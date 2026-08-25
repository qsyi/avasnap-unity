using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Qsyi.CameraGuide.Editor
{
    /// <summary>What gets written to disk for AvaSnap to read -- just
    /// enough for AvaSnap to draw its own 2D one-point-perspective guide
    /// over the live VRChat window: vertical FOV, pitch (camera tilt above/
    /// below the horizontal, degrees, positive = looking up), and roll
    /// (camera tilt around its own forward axis, degrees). World POSITION
    /// is deliberately not included -- AvaSnap has no 3D scene to place it
    /// in, so the guide it draws is centered wherever the camera currently
    /// is, not tied to a specific world point.</summary>
    [Serializable]
    public class CameraGuideExport
    {
        public double fov;
        public double pitch;
        public double roll;
        public string timestampUtc;
    }

    /// <summary>Editor-only, headless (no window, no Tools menu entry --
    /// nothing to configure): exports Camera.main's real FOV/pitch/roll to
    /// a JSON file AvaSnap reads, so AvaSnap's own 位置合わせモード guide
    /// overlay (drawn over the live VRChat window) can match whatever
    /// camera angle is currently being tested in Unity instead of requiring
    /// manual FOV input/horizon dragging.
    ///
    /// REQUEST-DRIVEN: only reads the camera and writes ExportPath when
    /// AvaSnap's own 取得 button touches RequestPath -- never continuously,
    /// so there's no per-frame camera work while nobody's asking. That
    /// response is driven by a FileSystemWatcher (near-instant when it
    /// fires) PLUS a cheap poll-fallback (RequestPollIntervalSeconds/
    /// OnEditorUpdate) that only stats RequestPath's own last-write time --
    /// never touching the camera unless a request is actually pending.
    /// Needed in practice, not just in theory: FileSystemWatcher alone
    /// never reacted to AvaSnap's requests on this exact AppData folder
    /// (confirmed by testing), matching AvaSnap's own already-documented
    /// experience with the same folder on its READ side, which is why it
    /// already carries the identical watcher+poll combo. Neither OneDrive
    /// redirection nor an obviously misconfigured watcher explains it here;
    /// Windows Defender real-time protection intercepting the small, quick
    /// writes is the most likely remaining culprit, but isn't something a
    /// portable package can safely work around (an AV exclusion is a
    /// per-machine setting, not something this script can or should touch).
    /// A named-pipe/loopback-socket based bridge would sidestep
    /// FileSystemWatcher's OS-notification reliability entirely and could
    /// drop the poll, but is meaningfully more code (connection lifecycle,
    /// cross-domain-reload handling) for what this is: a personal dev tool,
    /// not something facing untrusted users or scale. The 0.5s metadata-only
    /// stat this poll does instead is cheap enough that the tradeoff isn't
    /// worth it unless the watcher's unreliability turns out to be worse
    /// than observed so far.
    ///
    /// Works in both Edit mode and Play mode. Lives entirely under an
    /// "Editor" folder, so it never ships with the uploaded world, and only
    /// ever touches files in AvaSnap's own AppData folder -- no network, no
    /// scene hierarchy changes.</summary>
    [InitializeOnLoad]
    internal static class CameraCompositionGuideExporter
    {
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap");
        private static readonly string ExportPath = Path.Combine(AppDataDir, "unity_camera_guide.json");
        private const string RequestFileName = "unity_camera_guide_request.txt";
        private static readonly string RequestPath = Path.Combine(AppDataDir, RequestFileName);

        private static FileSystemWatcher s_requestWatcher;

        // FileSystemWatcher's Changed event can fire more than once for a
        // single logical write (a known Windows quirk), and its callback
        // runs on a ThreadPool thread, not the main thread -- this flag
        // (read/written from both) just collapses however many of those
        // land into a single EditorApplication.delayCall queued for the
        // next main-thread tick, instead of queuing one per event.
        private static volatile bool s_exportQueued;

        // Poll-fallback for when the watcher above just doesn't fire (see
        // this class's own doc comment) -- cheap on purpose: only stats
        // RequestPath's own last-write time, never reads/parses anything
        // and never touches the camera unless that time actually changed.
        private const double RequestPollIntervalSeconds = 0.5;
        private static double s_lastRequestPollTime;
        private static DateTime s_lastSeenRequestWriteTimeUtc;

        static CameraCompositionGuideExporter()
        {
            Directory.CreateDirectory(AppDataDir);

            var watcher = new FileSystemWatcher(AppDataDir, RequestFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnRequestFileTouched;
            watcher.Created += OnRequestFileTouched;
            watcher.EnableRaisingEvents = true;
            s_requestWatcher = watcher;

            // Seeded from whatever's already on disk (e.g. a leftover
            // request from a previous session) so the poll below doesn't
            // treat it as a brand-new request the moment polling starts --
            // same reasoning as AvaSnap's own UnityCameraGuideService.Start
            // seeding its side against the exact same kind of stale-file
            // false-positive.
            s_lastSeenRequestWriteTimeUtc = File.Exists(RequestPath) ? File.GetLastWriteTimeUtc(RequestPath) : DateTime.MinValue;
            EditorApplication.update += OnEditorUpdate;
        }

        /// <summary>Runs on FileSystemWatcher's own background thread --
        /// Camera/Transform access (in RunQueuedExport) throws if called
        /// from here directly, so this only ever queues the real work onto
        /// the main thread via EditorApplication.delayCall, the same
        /// marshal-to-main-thread pattern AvaSnap's OWN FileSystemWatcher
        /// uses in the opposite direction (Dispatcher.BeginInvoke).</summary>
        private static void OnRequestFileTouched(object sender, FileSystemEventArgs e) => QueueExport();

        /// <summary>The poll-fallback tick -- already on the main thread
        /// (EditorApplication.update), so no delayCall marshaling is
        /// strictly needed here, but routing through the same QueueExport
        /// keeps the watcher and poll paths from ever double-queuing if
        /// both happen to notice the same request.</summary>
        private static void OnEditorUpdate()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now - s_lastRequestPollTime < RequestPollIntervalSeconds) return;
            s_lastRequestPollTime = now;

            if (!File.Exists(RequestPath)) return;
            var writeTimeUtc = File.GetLastWriteTimeUtc(RequestPath);
            if (writeTimeUtc == s_lastSeenRequestWriteTimeUtc) return;
            s_lastSeenRequestWriteTimeUtc = writeTimeUtc;
            QueueExport();
        }

        private static void QueueExport()
        {
            if (s_exportQueued) return;
            s_exportQueued = true;
            EditorApplication.delayCall += RunQueuedExport;
        }

        private static void RunQueuedExport()
        {
            s_exportQueued = false;
            var target = Camera.main;
            if (target == null)
            {
                Debug.LogWarning("[AvaSnap連携] Camera.mainが見つからないため、カメラ構図補助線を送信できませんでした。");
                return;
            }
            ExportCamera(target);
        }

        /// <summary>Pitch/roll are derived from the camera's forward/up
        /// vectors rather than transform.eulerAngles, which doesn't cleanly
        /// separate into independent pitch/roll for an arbitrarily-yawed
        /// camera (gimbal-order artifacts). Pitch = angle of forward above/
        /// below the horizontal plane. Roll = signed angle, around the
        /// forward axis, between "up projected level" (what up WOULD be
        /// with zero roll) and the camera's actual up.</summary>
        private static void ExportCamera(Camera cam)
        {
            Vector3 forward = cam.transform.forward;
            double pitch = Math.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;

            Vector3 levelUp = Vector3.ProjectOnPlane(Vector3.up, forward);
            double roll = levelUp.sqrMagnitude > 1e-6f
                ? Vector3.SignedAngle(levelUp.normalized, cam.transform.up, forward)
                : 0.0;

            var export = new CameraGuideExport
            {
                fov = cam.fieldOfView,
                pitch = pitch,
                roll = roll,
                timestampUtc = DateTime.UtcNow.ToString("o"),
            };

            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(ExportPath, JsonUtility.ToJson(export, true));
            }
            catch (IOException)
            {
                // AvaSnap might have the file open for reading at the exact
                // wrong instant -- harmless, this request just goes
                // unanswered (the user can press 取得 again).
            }
        }
    }
}

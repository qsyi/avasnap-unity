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
    /// in, so the guide it draws is (like Unity's own version was) centered
    /// wherever the camera currently is, not tied to a specific world
    /// point.</summary>
    [Serializable]
    public class CameraGuideExport
    {
        public double fov;
        public double pitch;
        public double roll;
        public string timestampUtc;
    }

    /// <summary>Editor-only: exports the target Camera's real FOV/pitch/
    /// roll to a JSON file AvaSnap reads, so AvaSnap's own 位置合わせモード
    /// guide overlay (drawn over the live VRChat window, see AvaSnap's
    /// ControlPanelWindow) can match whatever camera angle is currently
    /// being tested in Unity instead of requiring manual FOV input/horizon
    /// dragging.
    ///
    /// REQUEST-DRIVEN, not continuous: earlier revisions polled the target
    /// Camera on a timer (EditorApplication.update) the whole time this was
    /// enabled, which meant Unity was doing SOME work (a per-tick check, or
    /// worse, a disk write) even while nobody was looking at the guide at
    /// all. This version does none of that -- it sits on a FileSystemWatcher
    /// waiting for AvaSnap's own "取得" button to touch RequestPath, and
    /// only then reads the camera and writes ExportPath, once. Genuinely
    /// zero background cost between requests (a FileSystemWatcher is an OS-
    /// level notification, not a poll), at the cost of losing the old
    /// "guide live-follows the camera as you drag it in Unity" behavior --
    /// AvaSnap now shows a snapshot from the last time its own button was
    /// pressed, not a continuous feed. Deliberate tradeoff, chosen over the
    /// old polling version for exactly that reason.
    ///
    /// Works in both Edit mode and Play mode. Lives entirely under an
    /// "Editor" folder, so it never ships with the uploaded world, and only
    /// ever touches files in AvaSnap's own AppData folder -- no network, no
    /// scene hierarchy changes.</summary>
    [InitializeOnLoad]
    public class CameraCompositionGuideWindow : EditorWindow
    {
        private const string EnabledPrefKey = "Qsyi.CameraCompositionGuide.Enabled";

        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap");
        private static readonly string ExportPath = Path.Combine(AppDataDir, "unity_camera_guide.json");
        private const string RequestFileName = "unity_camera_guide_request.txt";
        private static readonly string RequestPath = Path.Combine(AppDataDir, RequestFileName);

        private static bool s_enabled;
        private static Camera s_targetCameraOverride;
        private static FileSystemWatcher s_requestWatcher;

        // FileSystemWatcher's Changed event can fire more than once for a
        // single logical write (a known Windows quirk), and its callback
        // runs on a ThreadPool thread, not the main thread -- this flag
        // (read/written from both) just collapses however many of those
        // land into a single EditorApplication.delayCall queued for the
        // next main-thread tick, instead of queuing one per event.
        private static volatile bool s_exportQueued;

        private static string s_lastRequestStatus = "リクエスト待機中(AvaSnapの「取得」ボタンを押すと反応します)";

        /// <summary>EditorPrefs (not SessionState): persists across Editor
        /// restarts, defaults to true, global per-machine. Now also gates
        /// whether the FileSystemWatcher exists at all -- turning this off
        /// means Unity doesn't even listen for requests, not just "listens
        /// but ignores them".</summary>
        private static bool Enabled
        {
            get => s_enabled;
            set
            {
                if (s_enabled == value) return;
                s_enabled = value;
                EditorPrefs.SetBool(EnabledPrefKey, value);
                UpdateWatcher();
            }
        }

        static CameraCompositionGuideWindow()
        {
            s_enabled = EditorPrefs.GetBool(EnabledPrefKey, true);
            UpdateWatcher();
        }

        [MenuItem("Tools/qsyi/カメラ構図補助線 (AvaSnap連携)")]
        private static void Open() => GetWindow<CameraCompositionGuideWindow>("構図補助線");

        /// <summary>(Re)creates the watcher to match the current Enabled
        /// state -- called from the Enabled setter and the static
        /// constructor, the only two places that state can change.</summary>
        private static void UpdateWatcher()
        {
            s_requestWatcher?.Dispose();
            s_requestWatcher = null;
            if (!s_enabled) return;

            Directory.CreateDirectory(AppDataDir);
            var watcher = new FileSystemWatcher(AppDataDir, RequestFileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnRequestFileTouched;
            watcher.Created += OnRequestFileTouched;
            watcher.EnableRaisingEvents = true;
            s_requestWatcher = watcher;
        }

        /// <summary>Runs on FileSystemWatcher's own background thread --
        /// Camera/Transform access (in RunQueuedExport) throws if called
        /// from here directly, so this only ever queues the real work onto
        /// the main thread via EditorApplication.delayCall, the same
        /// marshal-to-main-thread pattern AvaSnap's OWN FileSystemWatcher
        /// uses in the opposite direction (Dispatcher.BeginInvoke).</summary>
        private static void OnRequestFileTouched(object sender, FileSystemEventArgs e)
        {
            if (s_exportQueued) return;
            s_exportQueued = true;
            EditorApplication.delayCall += RunQueuedExport;
        }

        private static void RunQueuedExport()
        {
            s_exportQueued = false;
            if (!s_enabled) return;
            var target = s_targetCameraOverride != null ? s_targetCameraOverride : Camera.main;
            s_lastRequestStatus = target != null
                ? $"最終応答: {DateTime.Now:HH:mm:ss} ({target.name})"
                : $"最終応答: {DateTime.Now:HH:mm:ss} (対象カメラが見つかりませんでした)";
            if (target == null) return;
            ExportCamera(target);
        }

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUILayout.Toggle("AvaSnapからの取得に応答", Enabled);
            if (EditorGUI.EndChangeCheck()) Enabled = enabled;

            s_targetCameraOverride = (Camera)EditorGUILayout.ObjectField(
                "対象カメラ (未指定ならCamera.main)", s_targetCameraOverride, typeof(Camera), true);

            // Manual test button: exercises the exact same ExportCamera path
            // a real AvaSnap request would, without needing AvaSnap running
            // to verify this side works.
            using (new EditorGUI.DisabledScope(!Enabled))
            {
                if (GUILayout.Button("今すぐ送信 (テスト用)"))
                {
                    var target = s_targetCameraOverride != null ? s_targetCameraOverride : Camera.main;
                    if (target != null) ExportCamera(target);
                }
            }

            EditorGUILayout.HelpBox(
                "AvaSnapの位置合わせモードで「取得」ボタンが押されるたびに、対象カメラの実際の" +
                "FOV・傾き(ピッチ/ロール)を一度だけファイルに書き出します(Unityのカメラを動かしても" +
                "自動では追従しません。押されるたびのスナップショット取得です)。\n\n" +
                "リクエストが来るまでUnity側は何も処理しません(常時監視のポーリングではなく、" +
                "OSのファイル変更通知を待つだけ)。\n\n" +
                s_lastRequestStatus + "\n\n" +
                "出力先: " + ExportPath,
                MessageType.Info);
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

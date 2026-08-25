using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Qsyi.CameraGuide.Editor
{
    [Serializable]
    public class CameraGuideExport
    {
        public double fov;
        public double pitch;
        public double roll;
        public string timestampUtc;
    }

    [InitializeOnLoad]
    internal static class CameraCompositionGuideExporter
    {
        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap");
        private static readonly string ExportPath = Path.Combine(AppDataDir, "unity_camera_guide.json");
        private const string RequestFileName = "unity_camera_guide_request.txt";
        private static readonly string RequestPath = Path.Combine(AppDataDir, RequestFileName);

        private static FileSystemWatcher s_requestWatcher;
        private static volatile bool s_exportQueued;

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

            s_lastSeenRequestWriteTimeUtc = File.Exists(RequestPath) ? File.GetLastWriteTimeUtc(RequestPath) : DateTime.MinValue;
            EditorApplication.update += OnEditorUpdate;
        }

        private static void OnRequestFileTouched(object sender, FileSystemEventArgs e) => QueueExport();

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
            }
        }
    }
}

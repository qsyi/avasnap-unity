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

    /// <summary>Editor-only: periodically exports the target Camera's real
    /// FOV/pitch/roll to a JSON file AvaSnap watches, so AvaSnap's own
    /// 位置合わせモード guide overlay (drawn over the live VRChat window,
    /// see AvaSnap's ControlPanelWindow) can auto-follow whatever camera
    /// angle is currently being tested in Unity instead of requiring manual
    /// FOV input/horizon dragging. Works in both Edit mode and Play mode
    /// (EditorApplication.update runs in both). Lives entirely under an
    /// "Editor" folder, so it never ships with the uploaded world, and only
    /// ever writes to a file in AvaSnap's own AppData folder -- no network,
    /// no scene hierarchy changes.</summary>
    [InitializeOnLoad]
    public class CameraCompositionGuideWindow : EditorWindow
    {
        private const string EnabledPrefKey = "Qsyi.CameraCompositionGuide.Enabled";

        // 5-10Hz is plenty smooth for a composition guide and keeps the
        // disk writes trivial -- no need to match render framerate for
        // numbers that only change as fast as someone can move a mouse/
        // keyboard-driven camera.
        private const double ExportIntervalSeconds = 0.15;

        private static bool s_enabled;
        private static Camera s_targetCameraOverride;
        private static double s_lastExportTime;
        private static readonly string ExportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvaSnap", "unity_camera_guide.json");

        /// <summary>EditorPrefs (not SessionState): persists across Editor
        /// restarts, defaults to true, global per-machine -- see the
        /// original Game-view-drawing version's own comment on this same
        /// choice (still applies, just now gating the file export instead
        /// of GL drawing).</summary>
        private static bool Enabled
        {
            get => s_enabled;
            set
            {
                if (s_enabled == value) return;
                s_enabled = value;
                EditorPrefs.SetBool(EnabledPrefKey, value);
            }
        }

        static CameraCompositionGuideWindow()
        {
            s_enabled = EditorPrefs.GetBool(EnabledPrefKey, true);
            EditorApplication.update += OnEditorUpdate;
        }

        [MenuItem("Tools/qsyi/カメラ構図補助線 (AvaSnap連携)")]
        private static void Open() => GetWindow<CameraCompositionGuideWindow>("構図補助線");

        private void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            bool enabled = EditorGUILayout.Toggle("AvaSnapへ送信", Enabled);
            if (EditorGUI.EndChangeCheck()) Enabled = enabled;

            s_targetCameraOverride = (Camera)EditorGUILayout.ObjectField(
                "対象カメラ (未指定ならCamera.main)", s_targetCameraOverride, typeof(Camera), true);

            EditorGUILayout.HelpBox(
                "プレイモード・エディタモードを問わず、対象カメラの実際のFOV・傾き(ピッチ/ロール)を" +
                $"約{1.0 / ExportIntervalSeconds:F0}Hzでファイルに書き出します。AvaSnapの位置合わせ" +
                "モードがこれを読み取り、遠近ガイド線として表示します(要AvaSnap側の対応)。\n\n" +
                "このUnityウィンドウがアクティブな間だけ書き出します(AvaSnap/VRChatなど他の" +
                "ウィンドウを見ている間は書き出しが止まり、ガイドはその時点の値のまま止まります)。\n\n" +
                "出力先: " + ExportPath,
                MessageType.Info);
        }

        /// <summary>Simple focus gate: only export while THIS Unity Editor
        /// instance is the OS-focused window. Note the known tradeoff --
        /// the guide freezes (or, if it never got a first export, stays
        /// blank) the moment you switch to VRChat/AvaSnap to actually look
        /// at it, since that's exactly when this instance loses focus.
        /// Deliberately kept anyway per explicit request for the simple
        /// version over the more correct-but-heavier "only defer to a
        /// DIFFERENT focused Unity instance" check (P/Invoke + process
        /// lookups) from an earlier revision.</summary>
        private static void OnEditorUpdate()
        {
            if (!s_enabled) return;
            if (!UnityEditorInternal.InternalEditorUtility.isApplicationActive) return;
            double now = EditorApplication.timeSinceStartup;
            if (now - s_lastExportTime < ExportIntervalSeconds) return;
            s_lastExportTime = now;

            var target = s_targetCameraOverride != null ? s_targetCameraOverride : Camera.main;
            if (target == null) return;
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
                string dir = Path.GetDirectoryName(ExportPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ExportPath, JsonUtility.ToJson(export, true));
            }
            catch (IOException)
            {
                // AvaSnap might have the file open for reading at the exact
                // wrong instant -- harmless, just skip this tick and retry
                // on the next one.
            }
        }
    }
}

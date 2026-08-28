using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Phone-form on-screen diagnostics overlay (IMGUI, zero UI assets).
    /// Shows fps, physics profile, pose_src_flip and heartbeat fields so the
    /// twitch-fix metrics are visible without pulling logcat. Attached only
    /// in BANXIA_PHONE builds by QuestMmdPlayerBootstrap.
    /// </summary>
    public sealed class PhoneDiagnosticsHud : MonoBehaviour
    {
        private const float MetricsRefreshInterval = 0.25f;
        private const float FpsSmoothing = 0.08f;

        private RuntimePerformanceMonitor monitor;
        private DiagnosticReporter reporter;
        private bool visible = true;
        private float smoothedFps;
        private float nextRefreshAt;
        private string line1 = string.Empty;
        private string line2 = string.Empty;
        private GUIStyle style;

        public void Bind(RuntimePerformanceMonitor performanceMonitor, DiagnosticReporter diagnosticsReporter)
        {
            monitor = performanceMonitor;
            reporter = diagnosticsReporter;
        }

        public void ToggleVisible()
        {
            visible = !visible;
        }

        private void Update()
        {
            var unscaled = Time.unscaledDeltaTime;
            if (unscaled > 0f)
            {
                var instant = 1f / unscaled;
                smoothedFps = smoothedFps <= 0f
                    ? instant
                    : Mathf.Lerp(smoothedFps, instant, FpsSmoothing);
            }

            if (Time.unscaledTime >= nextRefreshAt)
            {
                nextRefreshAt = Time.unscaledTime + MetricsRefreshInterval;
                RefreshLines();
            }
        }

        private void RefreshLines()
        {
            if (monitor == null)
            {
                line1 = $"fps={smoothedFps:F0}  (monitor not bound)";
                line2 = string.Empty;
                return;
            }

            line1 = string.Format(
                "fps={0:F0}/{1:F0}  p50={2:F1}ms  phys={3:F1}ms @{4}Hz×{5}",
                smoothedFps,
                monitor.currentFps,
                monitor.frameTimeP50Ms,
                monitor.mmdPhysicsMilliseconds,
                monitor.physicsFrequencyHz,
                monitor.physicsLastSubsteps);
            line2 = string.Format(
                "pose_src_flip={0}  phys_drops={1}(5s {2:F1}%)  {3}",
                monitor.physicsPoseSourceFlipFrames,
                monitor.physicsDroppedFrameCount,
                monitor.physicsDroppedFramePercent5s,
                monitor.modelLoaded ? "model=ok" : "model=none");
        }

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            if (style == null)
            {
                style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = Mathf.Max(18, Screen.height / 38),
                    normal = { textColor = new Color(0.75f, 0.95f, 0.85f) }
                };
            }

            const float pad = 12f;
            GUI.Box(new Rect(pad - 6f, pad - 6f, Screen.width * 0.62f, style.fontSize * 2.6f), GUIContent.none);
            GUI.Label(new Rect(pad, pad, Screen.width - pad * 2f, style.fontSize * 1.4f), line1, style);
            GUI.Label(new Rect(pad, pad + style.fontSize * 1.2f, Screen.width - pad * 2f, style.fontSize * 1.4f), line2, style);
        }
    }
}

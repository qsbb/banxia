using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Phone-form on-screen diagnostics overlay (IMGUI, zero UI assets).
    /// Shows performance telemetry and an optional framing-grid self-check.
    /// </summary>
    public sealed class PhoneDiagnosticsHud : MonoBehaviour
    {
        private const float MetricsRefreshInterval = 0.25f;
        private const float FpsSmoothing = 0.08f;
        private const float SecondaryLineRatio = 0.70f;

        private RuntimePerformanceMonitor monitor;
        private DiagnosticReporter reporter;
        private ICoPresenceDirector framingDirector;
        private bool visible = true;
        private bool framingGridVisible;
        private float smoothedFps;
        private float nextRefreshAt;
        private string line1 = string.Empty;
        private string line2 = string.Empty;
        private GUIStyle style;
        private GUIStyle framingStyle;
        private GUIStyle markerStyle;
        private int styleScreenHeight;

        public void Bind(RuntimePerformanceMonitor performanceMonitor, DiagnosticReporter diagnosticsReporter)
        {
            monitor = performanceMonitor;
            reporter = diagnosticsReporter;
        }

        public void BindFraming(ICoPresenceDirector director)
        {
            framingDirector = director;
        }

        public bool IsFramingGridVisible => framingGridVisible;

        public void SetFramingGridVisible(bool value)
        {
            framingGridVisible = value;
        }

        public void ToggleFramingGrid()
        {
            framingGridVisible = !framingGridVisible;
        }

        public void ToggleVisible()
        {
            visible = !visible;
        }

        /// <summary>当前是否显示（新 UI 壳层读取/控制）。</summary>
        public bool IsVisible => visible;

        /// <summary>显式设置显示状态（主界面/场景工具条切换时调用）。</summary>
        public void SetVisible(bool value)
        {
            visible = value;
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

        private void EnsureStyles()
        {
            if (style != null && styleScreenHeight == Screen.height)
            {
                return;
            }

            styleScreenHeight = Screen.height;
            var fontSize = Mathf.Max(18, Screen.height / 38);
            style = new GUIStyle(GUI.skin.label)
            {
                fontSize = fontSize,
                normal = { textColor = new Color(0.75f, 0.95f, 0.85f) }
            };
            framingStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(16, Screen.height / 48),
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = new Color(0.04f, 0.05f, 0.07f, 1f) }
            };
            markerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(14, Screen.height / 62),
                alignment = TextAnchor.MiddleLeft,
                normal = { textColor = Color.white }
            };
        }

        private void OnGUI()
        {
            if (!visible)
            {
                return;
            }

            EnsureStyles();
            const float pad = 12f;
            GUI.Box(new Rect(pad - 6f, pad - 6f, Screen.width * 0.62f, style.fontSize * 2.6f), GUIContent.none);
            GUI.Label(new Rect(pad, pad, Screen.width - pad * 2f, style.fontSize * 1.4f), line1, style);
            GUI.Label(new Rect(pad, pad + style.fontSize * 1.2f, Screen.width - pad * 2f, style.fontSize * 1.4f), line2, style);

            if (framingGridVisible)
            {
                DrawFramingGrid();
            }
        }

        private void DrawFramingGrid()
        {
            var snapshot = framingDirector != null
                ? framingDirector.CurrentFraming
                : default(CoPresenceFraming);
            var screenWidth = Screen.width;
            var screenHeight = Screen.height;
            var top = snapshot.Valid
                ? Mathf.Clamp(snapshot.TopPx, 0f, screenHeight)
                : screenHeight * CallFramingSolver.FrameBandTop;
            var bottom = snapshot.Valid
                ? Mathf.Clamp(snapshot.BottomPx, top, screenHeight)
                : screenHeight * CallFramingSolver.FrameBandBottom;
            var eyeLine = snapshot.Valid
                ? top + (bottom - top) * CallFramingSolver.PhoneVideoCallEyeLineRatio
                : screenHeight * CallFramingSolver.PhoneVideoCallEyeLineRatio;
            var secondaryLine = snapshot.Valid
                ? top + (bottom - top) * SecondaryLineRatio
                : screenHeight * SecondaryLineRatio;
            var margin = Mathf.Max(12f, screenWidth * 0.025f);
            var lineWidth = Mathf.Max(2f, screenHeight / 800f);

            var red = new Color(1f, 0.231f, 0.188f, 0.9f);
            var green = new Color(0.204f, 0.780f, 0.349f, 0.95f);
            DrawLine(margin, top, screenWidth - margin, top, red, lineWidth);
            DrawLine(margin, bottom, screenWidth - margin, bottom, red, lineWidth);
            DrawLine(margin, top, margin, bottom, red, lineWidth);
            DrawLine(screenWidth - margin, top, screenWidth - margin, bottom, red, lineWidth);
            DrawLine(margin, eyeLine, screenWidth - margin, eyeLine, green, lineWidth);
            DrawLine(margin, secondaryLine, screenWidth - margin, secondaryLine,
                new Color(0.204f, 0.780f, 0.349f, 0.55f), lineWidth);

            var readout = snapshot.Valid
                ? string.Format("frame d={0:F2} h={1:F2} eye={2:F1}% anchor={3}{4}",
                    snapshot.Distance,
                    snapshot.CameraY,
                    screenHeight > 0f ? eyeLine / screenHeight * 100f : 0f,
                    snapshot.HeadAnchor ? "head" : "bounds",
                    snapshot.Degraded ? " degraded" : string.Empty)
                : "frame unavailable · waiting for camera/model";
            var readoutRect = new Rect(margin + 8f, style.fontSize * 2.9f,
                Mathf.Min(screenWidth - margin * 2f - 16f, screenWidth * 0.92f),
                framingStyle.fontSize * 1.55f);
            var previousColor = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.88f);
            GUI.DrawTexture(readoutRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
            GUI.color = Color.black;
            GUI.Label(readoutRect, readout, framingStyle);
            GUI.color = previousColor;

            if (!snapshot.Valid || framingDirector == null || framingDirector.MainCamera == null)
            {
                return;
            }
            DrawMarker(framingDirector.MainCamera, snapshot.HeadTopWorld, "headTop", new Color(1f, 0.231f, 0.188f, 1f));
            DrawMarker(framingDirector.MainCamera, snapshot.EyeWorld, "eye", green);
            DrawMarker(framingDirector.MainCamera, snapshot.LowCutWorld, "waist", new Color(1f, 0.65f, 0.1f, 1f));
            DrawMarker(framingDirector.MainCamera, snapshot.FootWorld, "feet", Color.white);
        }

        private static void DrawLine(float x0, float y0, float x1, float y1, Color color, float width)
        {
            var previousColor = GUI.color;
            GUI.color = color;
            if (Mathf.Abs(y1 - y0) >= Mathf.Abs(x1 - x0))
            {
                GUI.DrawTexture(new Rect(x0 - width * 0.5f, Mathf.Min(y0, y1), width,
                    Mathf.Max(width, Mathf.Abs(y1 - y0))), Texture2D.whiteTexture);
            }
            else
            {
                GUI.DrawTexture(new Rect(Mathf.Min(x0, x1), y0 - width * 0.5f,
                    Mathf.Max(width, Mathf.Abs(x1 - x0)), width), Texture2D.whiteTexture);
            }
            GUI.color = previousColor;
        }

        private void DrawMarker(Camera camera, Vector3 world, string label, Color color)
        {
            var screen = camera.WorldToScreenPoint(world);
            if (screen.z <= 0f)
            {
                return;
            }
            var x = screen.x;
            var y = Screen.height - screen.y;
            var size = Mathf.Max(8f, Screen.height / 180f);
            DrawLine(x - size, y, x + size, y, color, Mathf.Max(2f, size * 0.22f));
            DrawLine(x, y - size, x, y + size, color, Mathf.Max(2f, size * 0.22f));
            var labelRect = new Rect(
                Mathf.Clamp(x + size + 4f, 0f, Screen.width - Screen.width * 0.22f),
                Mathf.Clamp(y - markerStyle.fontSize * 0.7f, 0f, Screen.height - markerStyle.fontSize * 1.5f),
                Screen.width * 0.22f,
                markerStyle.fontSize * 1.4f);
            var previousColor = GUI.color;
            GUI.color = color;
            GUI.Label(labelRect, label, markerStyle);
            GUI.color = previousColor;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// 手机端主界面（BANXIA_PHONE 专用，IMGUI 零 UI 资产）。
    /// 开机先进主界面：模型列表 / 进入场景 / 文件导入 / 设置 / 日志。
    /// 与 PhoneDiagnosticsHud 同构，不依赖 XR-only 的 CompanionWorldMenu。
    /// </summary>
    public sealed class PhoneHomeMenu : MonoBehaviour
    {
        private enum Page
        {
            Home,
            Settings,
            Logs,
        }

        private enum Mode
        {
            Menu,
            Scene,
        }

        private const string PrefsPrefix = "banxia.phone.";
        private const int LogPageLines = 200;

        private QuestMmdPlayerBootstrap owner;
        private RuntimeMmdModelLoader modelLoader;
        private QuestFileImportService fileImport;
        private RuntimePerformanceMonitor performance;
        private DiagnosticReporter diagnostics;
        private RuntimeDebugLog debugLog;
        private PhoneDiagnosticsHud hud;

        private Mode mode = Mode.Menu;
        private Page page = Page.Home;
        private bool sceneMenuOpen;
        private Vector2 listScroll;
        private Vector2 logScroll;
        private bool logScrollPinnedToBottom = true;

        private string importStatusLine = string.Empty;
        private string sceneEnterStatusLine = string.Empty;
        private float sceneEnterStatusUntil;
        private bool enteringScene;
        private IReadOnlyList<RuntimeMmdModelInfo> models = Array.Empty<RuntimeMmdModelInfo>();
        private float nextModelRefreshAt;

        private GUIStyle labelStyle;
        private GUIStyle smallStyle;
        private GUIStyle boldStyle;
        private GUIStyle buttonStyle;
        private GUIStyle boxStyle;
        private Texture2D whiteTexture;

        public void Bind(
            QuestMmdPlayerBootstrap bootstrap,
            RuntimeMmdModelLoader loader,
            QuestFileImportService importer,
            RuntimePerformanceMonitor performanceMonitor,
            DiagnosticReporter diagnosticsReporter,
            RuntimeDebugLog runtimeDebugLog)
        {
            owner = bootstrap;
            modelLoader = loader;
            fileImport = importer;
            performance = performanceMonitor;
            diagnostics = diagnosticsReporter;
            debugLog = runtimeDebugLog;
            if (fileImport != null)
            {
                fileImport.StatusChanged -= HandleImportStatusChanged;
                fileImport.StatusChanged += HandleImportStatusChanged;
            }
            RefreshModelList();
        }

        private void OnDestroy()
        {
            if (fileImport != null)
            {
                fileImport.StatusChanged -= HandleImportStatusChanged;
            }
        }

        private void HandleImportStatusChanged(string status)
        {
            importStatusLine = status;
            // 导入完成后刷新模型列表（发现新包）。
            nextModelRefreshAt = 0f;
        }

        private void Update()
        {
            if (Time.unscaledTime >= nextModelRefreshAt)
            {
                nextModelRefreshAt = Time.unscaledTime + 2f;
                RefreshModelList();
            }
        }

        private void RefreshModelList()
        {
            if (modelLoader == null)
            {
                return;
            }
            modelLoader.InvalidateInstalledModelCache();
            models = modelLoader.DiscoverInstalledModels();
        }

        private void OnGUI()
        {
            EnsureStyles();
            if (mode == Mode.Menu)
            {
                DrawMenu();
            }
            else
            {
                DrawSceneOverlay();
            }
        }

        private void EnterScene(RuntimeMmdModelInfo target)
        {
            if (enteringScene)
            {
                return;
            }
            enteringScene = true;
            SetTransient("正在加载模型…", 8f);
            _ = EnterSceneAsync(target);
        }

        private async System.Threading.Tasks.Task EnterSceneAsync(RuntimeMmdModelInfo target)
        {
            try
            {
                bool loaded;
                if (target != null)
                {
                    var avatar = await modelLoader.LoadFromFileAsync(target.Path, target.PackageRoot);
                    loaded = avatar != null;
                }
                else
                {
                    loaded = await modelLoader.RestoreLastModelAsync();
                }
                if (!loaded)
                {
                    SetTransient("模型加载失败，请看日志页", 4f);
                    return;
                }
                mode = Mode.Scene;
                sceneMenuOpen = false;
                if (hud != null)
                {
                    hud.SetVisible(false);
                }
            }
            catch (Exception exception)
            {
                SetTransient("加载异常: " + exception.Message, 5f);
                Debug.LogWarning("[PhoneHomeMenu] enter scene failed: " + exception.Message, this);
            }
            finally
            {
                enteringScene = false;
            }
        }

        private void ReturnToMenu()
        {
            mode = Mode.Menu;
            sceneMenuOpen = false;
            if (hud != null)
            {
                hud.SetVisible(PlayerPrefs.GetInt(PrefsPrefix + "hud", 0) == 1);
            }
            nextModelRefreshAt = 0f;
        }

        private void SetTransient(string message, float seconds)
        {
            sceneEnterStatusLine = message;
            sceneEnterStatusUntil = Time.unscaledTime + seconds;
        }

        // ─────────────────────────── 菜单模式 ───────────────────────────

        private void DrawMenu()
        {
            var safe = Screen.safeArea;
            GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), whiteTexture, ScaleMode.StretchToFill,
                false, 0f, new Color(0.035f, 0.045f, 0.065f, 1f), 0f, 0f);

            float top = safe.y + 16f;
            float width = safe.width - 32f;
            float x = safe.x + 16f;

            // 顶栏：应用名 + 配对状态点
            GUI.Label(new Rect(x, top, width * 0.6f, 44f), "伴夏 Banxia", boldStyle);
            var pairing = owner != null ? owner.Pairing : null;
            string pairingText = pairing != null && pairing.Status != null ? "● " + TruncateStatus(pairing.Status) : "○ 未配对";
            GUI.Label(new Rect(x + width * 0.6f, top, width * 0.4f, 44f), pairingText, smallStyle);
            top += 56f;

            // 导入状态行
            if (!string.IsNullOrEmpty(importStatusLine))
            {
                GUI.Label(new Rect(x, top, width, 30f), importStatusLine, smallStyle);
                top += 34f;
            }
            if (!string.IsNullOrEmpty(sceneEnterStatusLine) && Time.unscaledTime < sceneEnterStatusUntil)
            {
                GUI.Label(new Rect(x, top, width, 30f), sceneEnterStatusLine, smallStyle);
                top += 34f;
            }

            // 内容区（到 Tab 栏为止）
            float tabBarHeight = 108f;
            float contentHeight = safe.y + safe.height - tabBarHeight - top;
            switch (page)
            {
                case Page.Home:
                    DrawHomePage(x, top, width, contentHeight);
                    break;
                case Page.Settings:
                    DrawSettingsPage(x, top, width, contentHeight);
                    break;
                case Page.Logs:
                    DrawLogsPage(x, top, width, contentHeight);
                    break;
            }

            DrawTabBar(safe, tabBarHeight);
        }

        private void DrawHomePage(float x, float top, float width, float height)
        {
            float y = top;
            if (models == null || models.Count == 0)
            {
                GUI.Label(new Rect(x, y, width, 90f),
                    "还没有模型。\n点下面「导入模型」选择 PMX/ZIP 文件；\n或把文件放到 手机存储/Download/mmdmodel 再导入。", smallStyle);
                y += 100f;
            }
            else
            {
                float cardHeight = 116f;
                float listHeight = height - 132f;
                listScroll = GUI.BeginScrollView(
                    new Rect(x, y, width, Mathf.Max(120f, listHeight)), listScroll,
                    new Rect(0f, 0f, width - 24f, models.Count * (cardHeight + 12f)));
                for (int i = 0; i < models.Count; i++)
                {
                    DrawModelCard(models[i], 0f, i * (cardHeight + 12f), width - 24f, cardHeight);
                }
                GUI.EndScrollView();
                y += Mathf.Max(120f, listHeight) + 8f;
            }

            if (GUI.Button(new Rect(x, y, width, 84f),
                enteringScene ? "加载中…" : "进入场景（上次模型）", buttonStyle))
            {
                EnterScene(null);
            }
            y += 96f;
            if (GUI.Button(new Rect(x, y, width, 64f), "＋ 导入模型（PMX / ZIP / VMD）", buttonStyle))
            {
                if (fileImport != null && !fileImport.IsBusy)
                {
                    fileImport.OpenPicker();
                }
            }
        }

        private void DrawModelCard(RuntimeMmdModelInfo model, float cx, float cy, float w, float h)
        {
            var rect = new Rect(cx, cy, w, h);
            GUI.DrawTexture(rect, whiteTexture, ScaleMode.StretchToFill, false, 0f,
                new Color(1f, 1f, 1f, 0.06f), 0f, 0f);
            GUI.Label(new Rect(cx + 16f, cy + 10f, w - 200f, 40f), model.DisplayName, boldStyle);
            string summary = BuildModelSummary(model);
            GUI.Label(new Rect(cx + 16f, cy + 52f, w - 200f, 32f), summary, smallStyle);
            if (GUI.Button(new Rect(cx + w - 168f, cy + 26f, 152f, 64f), "进入 ▶", buttonStyle))
            {
                EnterScene(model);
            }
        }

        private static string BuildModelSummary(RuntimeMmdModelInfo model)
        {
            string sizeText = "未知大小";
            try
            {
                if (File.Exists(model.Path))
                {
                    long bytes = new FileInfo(model.Path).Length;
                    sizeText = bytes > 1024 * 1024
                        ? (bytes / 1024f / 1024f).ToString("F1") + " MB"
                        : (bytes / 1024f).ToString("F0") + " KB";
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            string root = string.IsNullOrEmpty(model.PackageRoot) ? string.Empty : Path.GetFileName(model.PackageRoot);
            return string.IsNullOrEmpty(root) ? "PMX · " + sizeText : root + " · " + sizeText;
        }

        private void DrawTabBar(Rect safe, float tabBarHeight)
        {
            var barRect = new Rect(safe.x, safe.y + safe.height - tabBarHeight, safe.width, tabBarHeight);
            GUI.DrawTexture(barRect, whiteTexture, ScaleMode.StretchToFill, false, 0f,
                new Color(0f, 0f, 0f, 0.35f), 0f, 0f);
            float w = barRect.width / 3f;
            DrawTabButton("主界面", Page.Home, barRect.x, barRect.y + 12f, w);
            DrawTabButton("设置", Page.Settings, barRect.x + w, barRect.y + 12f, w);
            DrawTabButton("日志", Page.Logs, barRect.x + w * 2f, barRect.y + 12f, w);
        }

        private void DrawTabButton(string label, Page target, float x, float y, float w)
        {
            bool active = page == target;
            var style = new GUIStyle(buttonStyle)
            {
                normal = { textColor = active ? new Color(0.49f, 0.78f, 0.85f) : new Color(0.7f, 0.75f, 0.78f) },
            };
            if (GUI.Button(new Rect(x + 6f, y, w - 12f, 72f), label, style))
            {
                page = target;
            }
        }

        // ─────────────────────────── 设置页 ───────────────────────────

        private void DrawSettingsPage(float x, float top, float width, float height)
        {
            float y = top;
            GUI.Label(new Rect(x, y, width, 40f), "设置", boldStyle);
            y += 52f;

            bool hudOn = PlayerPrefs.GetInt(PrefsPrefix + "hud", 0) == 1;
            bool newHud = GUI.Toggle(new Rect(x, y, width, 48f), hudOn, "  常显诊断 HUD（场景内性能摘要）", labelStyle);
            if (newHud != hudOn)
            {
                PlayerPrefs.SetInt(PrefsPrefix + "hud", newHud ? 1 : 0);
                PlayerPrefs.Save();
                if (hud != null)
                {
                    hud.SetVisible(newHud);
                }
            }
            y += 60f;

            int fps = PlayerPrefs.GetInt(PrefsPrefix + "fps", 60);
            GUI.Label(new Rect(x, y, width, 34f), "目标帧率: " + fps, labelStyle);
            int newFps = GUI.SelectionGrid(new Rect(x, y + 38f, width, 56f),
                fps >= 60 ? 1 : 0, new[] { "30", "60" }, 2, buttonStyle);
            newFps = newFps == 1 ? 60 : 30;
            if (newFps != fps)
            {
                PlayerPrefs.SetInt(PrefsPrefix + "fps", newFps);
                PlayerPrefs.Save();
                Application.targetFrameRate = newFps;
            }
            y += 110f;

            float volume = AudioListener.volume;
            GUI.Label(new Rect(x, y, width, 34f), "音量: " + volume.ToString("P0"), labelStyle);
            float newVolume = GUI.HorizontalSlider(new Rect(x, y + 42f, width, 24f), volume, 0f, 1f);
            if (Mathf.Abs(newVolume - volume) > 0.001f)
            {
                AudioListener.volume = newVolume;
            }
            y += 96f;

            GUI.Label(new Rect(x, y, width, 34f), "版本 " + Application.version, smallStyle);
            y += 40f;
            GUI.Label(new Rect(x, y, width, 34f), "设备 " + SystemInfo.deviceModel, smallStyle);
            y += 40f;
            GUI.Label(new Rect(x, y, width, 34f),
                "内存 " + (SystemInfo.systemMemorySize) + " MB", smallStyle);
        }

        // ─────────────────────────── 日志页 ───────────────────────────

        private void DrawLogsPage(float x, float top, float width, float height)
        {
            float y = top;
            GUI.Label(new Rect(x, y, width, 40f), "日志与诊断", boldStyle);
            if (GUI.Button(new Rect(x + width - 170f, y, 170f, 52f), "清空", buttonStyle) && debugLog != null)
            {
                debugLog.Clear();
            }
            y += 60f;

            if (performance != null)
            {
                GUI.Label(new Rect(x, y, width, 32f),
                    "FPS " + performance.currentFps.ToString("F1") +
                    " · p50 " + performance.frameTimeP50Ms.ToString("F1") + "ms" +
                    " · p95 " + performance.frameTimeP95Ms.ToString("F1") + "ms", smallStyle);
                y += 36f;
            }
            GUI.Label(new Rect(x, y, width, 32f),
                "物理 hz=" + (performance != null ? performance.physicsFrequencyHz.ToString() : "-") +
                " sub=" + (performance != null ? performance.physicsMaximumSubstepsPerFrame.ToString() : "-") +
                " swallow=" + (performance != null ? performance.lastSwallowedPoseBoneCount.ToString() : "-"), smallStyle);
            y += 44f;

            string text = debugLog != null ? debugLog.GetRecentText(LogPageLines) : "（日志服务不可用）";
            var content = new GUIContent(text);
            float textHeight = smallStyle.CalcHeight(content, width - 24f) + 16f;
            var viewRect = new Rect(x, y, width, Mathf.Max(120f, height - (y - top)));
            logScroll = GUI.BeginScrollView(viewRect, logScroll, new Rect(0f, 0f, width - 24f, textHeight));
            GUI.Label(new Rect(0f, 0f, width - 24f, textHeight), content, smallStyle);
            GUI.EndScrollView();
            if (logScrollPinnedToBottom && Event.current.type == EventType.Repaint)
            {
                logScroll.y = Mathf.Max(0f, textHeight - viewRect.height);
            }
        }

        // ─────────────────────────── 场景模式 ───────────────────────────

        private void DrawSceneOverlay()
        {
            var safe = Screen.safeArea;
            if (!string.IsNullOrEmpty(sceneEnterStatusLine) && Time.unscaledTime < sceneEnterStatusUntil)
            {
                GUI.Label(new Rect(safe.x + 16f, safe.y + 16f, safe.width - 160f, 36f),
                    sceneEnterStatusLine, smallStyle);
            }
            if (GUI.Button(new Rect(safe.x + safe.width - 108f, safe.y + 16f, 92f, 68f), "☰", buttonStyle))
            {
                sceneMenuOpen = !sceneMenuOpen;
            }
            if (sceneMenuOpen)
            {
                float panelWidth = Mathf.Min(430f, safe.width * 0.72f);
                float panelHeight = 330f;
                var panel = new Rect(safe.x + safe.width - panelWidth - 12f, safe.y + 96f, panelWidth, panelHeight);
                GUI.DrawTexture(panel, whiteTexture, ScaleMode.StretchToFill, false, 0f,
                    new Color(0.02f, 0.03f, 0.05f, 0.92f), 0f, 0f);
                float py = panel.y + 14f;
                if (GUI.Button(new Rect(panel.x + 14f, py, panelWidth - 28f, 60f), "返回主界面", buttonStyle))
                {
                    ReturnToMenu();
                }
                py += 70f;
                if (GUI.Button(new Rect(panel.x + 14f, py, panelWidth - 28f, 60f), "重置角色位置", buttonStyle))
                {
                    owner?.SendCommand(new AvatarCommand { name = "reset" });
                }
                py += 70f;
                bool hudOn = hud != null && hud.IsVisible;
                if (GUI.Button(new Rect(panel.x + 14f, py, panelWidth - 28f, 60f),
                    hudOn ? "关闭诊断 HUD" : "打开诊断 HUD", buttonStyle) && hud != null)
                {
                    hud.SetVisible(!hudOn);
                }
                py += 70f;
                if (GUI.Button(new Rect(panel.x + 14f, py, panelWidth - 28f, 60f), "关闭面板", buttonStyle))
                {
                    sceneMenuOpen = false;
                }
            }
        }

        // ─────────────────────────── 样式 ───────────────────────────

        private void EnsureStyles()
        {
            if (labelStyle != null)
            {
                return;
            }
            if (whiteTexture == null)
            {
                whiteTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                whiteTexture.SetPixel(0, 0, Color.white);
                whiteTexture.Apply();
                whiteTexture.hideFlags = HideFlags.DontSave;
            }
            int baseSize = Mathf.Clamp(Screen.height / 40, 20, 34);
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = baseSize,
                normal = { textColor = new Color(0.88f, 0.91f, 0.93f) },
                wordWrap = true,
            };
            smallStyle = new GUIStyle(labelStyle)
            {
                fontSize = Mathf.Max(16, baseSize - 6),
                normal = { textColor = new Color(0.62f, 0.72f, 0.75f) },
            };
            boldStyle = new GUIStyle(labelStyle)
            {
                fontStyle = FontStyle.Bold,
                fontSize = baseSize + 4,
            };
            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = baseSize,
                normal = { textColor = new Color(0.49f, 0.78f, 0.85f) },
                focused = { textColor = new Color(0.49f, 0.78f, 0.85f) },
                active = { textColor = new Color(0.3f, 0.5f, 0.55f) },
            };
            boxStyle = new GUIStyle(GUI.skin.box);
        }

        private static string TruncateStatus(string value)
        {
            value = value.Replace('\n', ' ').Trim();
            return value.Length > 14 ? value.Substring(0, 14) : value;
        }

        internal void BindHud(PhoneDiagnosticsHud diagnosticsHud)
        {
            hud = diagnosticsHud;
            if (hud != null && mode == Mode.Menu)
            {
                hud.SetVisible(PlayerPrefs.GetInt(PrefsPrefix + "hud", 0) == 1);
            }
        }
    }
}

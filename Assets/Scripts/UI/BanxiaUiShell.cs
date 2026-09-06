using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;
using UnityEngine.UIElements;

namespace QuestMmdPlayer
{
    /// <summary>
    /// 伴夏双端 UI Toolkit 壳层（iOS 风格深色设计）。
    /// Phone：全屏触屏面板；Quest：由 BanxiaQuestWorldUiHost 渲染到世界空间 RenderTexture。
    /// 业务调用保持平台无关，只把设备独占内容留给平台宿主。
    /// </summary>
    public sealed class BanxiaUiShell : MonoBehaviour
    {
        private enum Tab
        {
            Companion,
            Chat,
            Actions,
            Settings,
        }

        private enum UiMode
        {
            Menu,
            Scene,
        }

        private readonly struct SegmentChoice
        {
            public SegmentChoice(string label, Func<bool> isSelected, Action activate)
            {
                Label = label;
                IsSelected = isSelected;
                Activate = activate;
            }

            public string Label { get; }
            public Func<bool> IsSelected { get; }
            public Action Activate { get; }
        }

        private const string PrefsPrefix = "banxia.phone.";
        private const float PollIntervalSeconds = 0.5f;
        private const float ToastSeconds = 2.6f;
        private const int MaxChatBubbles = 24;

        private QuestMmdPlayerBootstrap owner;
        private RuntimeMmdModelLoader modelLoader;
        private QuestFileImportService fileImport;
        private RuntimeDebugLog debugLog;
        private PhoneDiagnosticsHud hud;
        private BanxiaUpdateChecker updateChecker;
        private bool worldSpaceHost;
        private VisualElement coPresenceSheet;
        private VisualElement coPresenceBackdrop;
        private VisualElement videoCallChrome;
        private VisualElement callTopChrome;
        private VisualElement callControls;
        private Label videoCallTimerLabel;
        private float lastChromeTopPx = -1f;
        private float lastChromeBottomPx = -1f;
        private float lastPairingKeyHeight = -1f;
        private bool pairingNumpadLayoutLogged;
        private Label videoCallSubtitleLabel;
        private VisualElement arPlaceHint;
        private bool arPlacedOnce;
        private double nextCallUiRefreshAt;
        private VisualElement settingsRootList;
        private readonly List<VisualElement> settingsDetailPages = new List<VisualElement>();
        private CoPresenceMode lastCoPresenceMode;
        private Action closeRequested;

        private UIDocument document;
        private VisualElement panelRoot;
        private VisualElement shellRoot;
        private VisualElement mainUi;
        private VisualElement content;
        private VisualElement tabBar;
        private VisualElement sceneToolbar;
        private VisualElement movePill;
        private VisualElement toast;
        private Label toastLabel;

        private readonly Dictionary<Tab, VisualElement> tabPages = new Dictionary<Tab, VisualElement>();
        private readonly List<RuntimeMmdModelInfo> models = new List<RuntimeMmdModelInfo>();
        private readonly List<Action> segmentRefreshers = new List<Action>();
        private readonly List<(bool user, string text)> chatHistory = new List<(bool user, string text)>();

        private UiMode mode = UiMode.Menu;
        private Tab currentTab = Tab.Companion;
        private float nextPollAt;
        private float toastHideAt = float.NegativeInfinity;
        private bool built;
        private bool enteringScene;
        private bool refreshingActions;

        public bool IsBuilt => built;

        // Dynamic UI handles.
        private VisualElement modelsListContainer;
        private VisualElement modelsEmptyContainer;
        private Label importStatusLabel;
        private VisualElement chatPairingCard;
        private ScrollView chatPairingScroll;
        private VisualElement chatConversationCard;
        private VisualElement chatSuggestionsRoot;
        private VisualElement chatInputBarRoot;
        private VisualElement chatVoiceToggle;
        private VisualElement chatHoldBar;
        private Label chatHoldLabel;
        private Label connectionBadge;
        private Label pairingStatusLabel;
        private TextField pairingServerField;
        private Label pairingCodeLabel;
        private VisualElement pairingDots;
        private VisualElement pairingNumpadSection;
        private Label pairingDisclosureChevron;
        private Label chatStateLabel;
        private ScrollView chatTranscript;
        private TextField chatInputField;
        private TouchScreenKeyboard activeTouchKeyboard;
        private TextField activeTouchField;
        private float lastKeyboardInsetPx = -1f;
        private bool keyboardInsetLogged;
        private Label voiceStatusLabel;
        private Label actionsStatusLabel;
        private VisualElement actionsListContainer;
        private Label idlePresetLabel;
        private Label settingsQualityStatus;
        private Label settingsPerformanceText;
        private Label settingsLogText;
        private Label updateStatusLabel;
        private VisualElement updateProgressRow;
        private VisualElement updateProgressFill;

        private string pairingCode = string.Empty;
        private string lastImportStatus = string.Empty;
        private string lastReplyText = string.Empty;
        private string lastTranscriptText = string.Empty;
        private string lastSuggestionsKey = string.Empty;
        private string lastPlayingActionId = string.Empty;
        private bool voiceInputMode;
        private float voiceHoldStartY;
        private bool voiceHoldCancelArmed;
        private bool pairingNumpadExpanded;

        public void ConfigureWorldSpace(Action onCloseRequested)
        {
            worldSpaceHost = true;
            closeRequested = onCloseRequested;
            if (built)
            {
                EnsureWorldCloseButton();
            }
        }

        public void Bind(
            QuestMmdPlayerBootstrap bootstrap,
            RuntimeMmdModelLoader loader,
            QuestFileImportService importer,
            RuntimeDebugLog runtimeDebugLog)
        {
            owner = bootstrap;
            modelLoader = loader;
            debugLog = runtimeDebugLog;

            if (fileImport != null)
            {
                fileImport.StatusChanged -= HandleImportStatus;
            }
            fileImport = importer;
            if (fileImport != null)
            {
                fileImport.StatusChanged += HandleImportStatus;
            }

            if (owner?.Pairing != null)
            {
                owner.Pairing.StatusChanged -= RefreshConnectionUi;
                owner.Pairing.StatusChanged += RefreshConnectionUi;
            }
            if (owner?.Conversation != null)
            {
                owner.Conversation.StateChanged -= HandleConversationStateChanged;
                owner.Conversation.StateChanged += HandleConversationStateChanged;
            }
            if (owner?.VmdActions != null)
            {
                owner.VmdActions.PlaybackChanged -= HandlePlaybackChanged;
                owner.VmdActions.PlaybackChanged += HandlePlaybackChanged;
            }
            if (owner?.Quality != null)
            {
                owner.Quality.QualityChanged -= HandleQualityChanged;
                owner.Quality.QualityChanged += HandleQualityChanged;
            }

            EnsureBuilt();
            if (owner?.Pairing != null && pairingServerField != null)
            {
                pairingServerField.SetValueWithoutNotify(owner.Pairing.PairingServerEndpoint ?? string.Empty);
            }
            RefreshModels(forceInvalidate: true);
            RefreshConnectionUi();
            RefreshChatUi();
            RefreshActionsFromLibrary();
            RefreshSettingsUi();
        }

        public void BindHud(PhoneDiagnosticsHud diagnosticsHud)
        {
            hud = diagnosticsHud;
            if (hud != null)
            {
                hud.BindFraming(owner?.CoPresence);
                hud.SetFramingGridVisible(PlayerPrefs.GetInt(PrefsPrefix + "framing-grid", 0) == 1);
                hud.SetVisible(false);
            }
        }

        private void Awake()
        {
            document = GetComponent<UIDocument>() ?? gameObject.AddComponent<UIDocument>();
            if (document.panelSettings == null)
            {
                document.panelSettings = Resources.Load<PanelSettings>("BanxiaPanelSettings");
                if (document.panelSettings == null)
                {
                    document.panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                    Debug.LogWarning("[BanxiaUi] BanxiaPanelSettings missing; using runtime fallback panel settings.", this);
                }
            }
            EnsureEventSystem();
            EnsureTextSettings(document.panelSettings);
        }

        /// <summary>
        /// 给 ScrollView 挂自实现的触摸拖拽滚动。
        /// 背景：内置 pan 手势在当前输入配置（activeInputHandler=旧版）下收不到
        /// pointer move 序列，导致页面滚不动（存量问题，配对表单迁入设置页后
        /// 内容超屏才暴露）。这里用 PointerDown/Move 事件手动滚 contentContainer。
        /// </summary>
        private static void EnableTouchDragScroll(ScrollView scrollView)
        {
            if (scrollView == null)
            {
                return;
            }
            // 拖拽阈值（屏幕像素）：超过才进入滚动捕获。
            // 没有 SlopVersion 时代的 CapturePointer 会把 PointerUp 一并抢走，
            // ScrollView 里的 Button/Toggle 永远收不到完整序列 → 点击失效。
            const float dragSlop = 12f;
            bool tracking = false;
            bool dragging = false;
            float lastY = 0f;
            scrollView.RegisterCallback<PointerDownEvent>(e =>
            {
                tracking = true;
                dragging = false;
                lastY = e.position.y;
            });
            scrollView.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!tracking)
                {
                    return;
                }
                float delta = e.position.y - lastY;
                if (!dragging)
                {
                    if (Mathf.Abs(delta) < dragSlop)
                    {
                        return;
                    }
                    dragging = true;
                    scrollView.CapturePointer(e.pointerId);
                }
                else
                {
                    lastY = e.position.y;
                }
                var scroller = scrollView.verticalScroller;
                float range = Mathf.Max(1f, scroller.highValue - scroller.lowValue);
                float viewport = Mathf.Max(1f, scrollView.contentContainer.worldBound.height - scrollView.worldBound.height);
                scroller.value -= delta * range / viewport;
            });
            scrollView.RegisterCallback<PointerUpEvent>(e =>
            {
                if (dragging)
                {
                    scrollView.ReleasePointer(e.pointerId);
                }
                tracking = false;
                dragging = false;
            });
            scrollView.RegisterCallback<PointerLeaveEvent>(e =>
            {
                tracking = false;
                dragging = false;
            });
        }

        // 屏幕空间的 UI Toolkit 面板（手机端）依赖 EventSystem 派发指针事件；
        // 项目只启用 Input System，老 StandaloneInputModule 会每帧抛
        // InvalidOperationException 且吞掉所有点击，这里兜底换成
        // InputSystemUIInputModule。世界空间端走 panel.Pick 自派发，不受影响。
        private static void EnsureEventSystem()
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem == null)
            {
                var existing = UnityEngine.Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>();
                eventSystem = existing;
                if (eventSystem == null)
                {
                    var go = new GameObject("BanxiaEventSystem");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    eventSystem = go.AddComponent<UnityEngine.EventSystems.EventSystem>();
                    Debug.Log("[BanxiaUi] Created EventSystem for UI Toolkit input.");
                }
            }

            if (eventSystem.GetComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>() == null)
            {
                var legacy = eventSystem.GetComponent<UnityEngine.EventSystems.StandaloneInputModule>();
                if (legacy != null)
                {
                    legacy.enabled = false;
                }
                eventSystem.gameObject.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
                Debug.Log("[BanxiaUi] InputSystemUIInputModule ensured.");
            }
        }

        // UI Toolkit 默认动态字体（LegacyRuntime/Liberation Sans）不含 CJK 字形，
        // 中文界面会整体不渲染；这里运行时优先挑一个系统 CJK 字体建动态
        // FontAsset。
        private static void EnsureTextSettings(PanelSettings panelSettings)
        {
            if (panelSettings == null || panelSettings.textSettings != null)
            {
                return;
            }

            PanelTextSettings textSettings;
            FontAsset appliedFontAsset;
            try
            {
                appliedFontAsset = CreateCjkFontAsset();
                if (appliedFontAsset == null)
                {
                    Debug.LogWarning("[BanxiaUi] No usable CJK font asset; falling back to LegacyRuntime.");
                    var builtin = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    appliedFontAsset = FontAsset.CreateFontAsset(
                        builtin, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024,
                        AtlasPopulationMode.Dynamic, true);
                }
                if (appliedFontAsset == null)
                {
                    Debug.LogWarning("[BanxiaUi] Font asset creation failed entirely.");
                    return;
                }

                textSettings = ScriptableObject.CreateInstance<PanelTextSettings>();
                var property = typeof(PanelTextSettings).GetProperty(
                    "defaultFontAsset",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(textSettings, appliedFontAsset);
                }
                else
                {
                    var field = typeof(PanelTextSettings).GetField(
                        "m_DefaultFontAsset",
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic);
                    field?.SetValue(textSettings, appliedFontAsset);
                }
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "ui.font.settings-probe");
                Debug.LogWarning("[BanxiaUi] Panel text settings fallback failed: " + exception.Message);
                return;
            }

            panelSettings.textSettings = textSettings;
            Debug.Log("[BanxiaUi] Panel text settings ready: font=" + appliedFontAsset.name);
        }

        // Android 系统自带 Noto CJK；这里直接用 FontAsset 的系统字体重载
        // CreateFontAsset(family, style, weight)（走 FontEngine 系统字体引用，
        // 而 CreateFontAsset(Font) 对 OS 动态字体拿不到字面数据，会报
        // “Unable to load font face”）。动态图集按需烘字形，这里只要求
        // 资产创建成功（字面能加载）；不能用 HasCharacter 判定——动态
        // 资产的字形字典在真正渲染前是空的。
        private static readonly (string Family, string Style)[] CjkFontCandidates =
        {
            ("Noto Sans CJK SC", "Regular"),
            ("MiSans", "Regular"),
            ("HarmonyOS Sans SC", "Regular"),
            ("Noto Sans SC", "Regular"),
            ("Noto Sans CJK JP", "Regular"),
            ("Noto Sans CJK HK", "Regular"),
            ("Noto Sans CJK KR", "Regular"),
            ("Droid Sans Fallback", "Regular"),
            ("Source Han Sans SC", "Regular"),
            ("Microsoft YaHei", "Regular"),
        };

        private static FontAsset CreateCjkFontAsset()
        {
            foreach (var (family, style) in CjkFontCandidates)
            {
                try
                {
                    var fontAsset = FontAsset.CreateFontAsset(family, style, 400);
                    if (fontAsset == null)
                    {
                        Debug.Log("[BanxiaUi] OS font asset null: " + family + "/" + style);
                        continue;
                    }
                    Debug.Log("[BanxiaUi] OS font asset accepted: " + family + "/" + style);
                    return fontAsset;
                }
                catch (Exception exception)
                {
                    QuestDebugMode.Report(exception, "ui.font.candidate-probe");
                    Debug.LogWarning("[BanxiaUi] OS font failed: " + family + "/" + style +
                                     " " + exception.Message);
                }
            }
            return null;
        }

        private void Start()
        {
            EnsureBuilt();
        }

        private void OnDestroy()
        {
            if (fileImport != null)
            {
                fileImport.StatusChanged -= HandleImportStatus;
            }
            if (owner?.Pairing != null)
            {
                owner.Pairing.StatusChanged -= RefreshConnectionUi;
            }
            if (owner?.Conversation != null)
            {
                owner.Conversation.StateChanged -= HandleConversationStateChanged;
            }
            if (owner?.VmdActions != null)
            {
                owner.VmdActions.PlaybackChanged -= HandlePlaybackChanged;
            }
            if (owner?.Quality != null)
            {
                owner.Quality.QualityChanged -= HandleQualityChanged;
            }
        }

        private void Update()
        {
            // World-space/动态 UIDocument 有时会在首帧才拿到 rootVisualElement；
            // 这里做幂等重试，避免“一次性构建失败后永远黑屏”。
            if (!built)
            {
                EnsureBuilt();
            }
            if (!built)
            {
                return;
            }
            if (toast.style.display == DisplayStyle.Flex && Time.unscaledTime >= toastHideAt)
            {
                toast.style.display = DisplayStyle.None;
            }
            // Android 上 UI Toolkit TextField 聚焦不会弹起系统软键盘（2022.3 已知行为），
            // 这里用 TouchScreenKeyboard 显式打开并在 Update 里回写文本。
            if (activeTouchKeyboard != null)
            {
                if (activeTouchField != null && activeTouchField.value != activeTouchKeyboard.text)
                {
                    activeTouchField.SetValueWithoutNotify(activeTouchKeyboard.text);
                }
                if (activeTouchKeyboard.status == TouchScreenKeyboard.Status.Done ||
                    activeTouchKeyboard.status == TouchScreenKeyboard.Status.Canceled ||
                    activeTouchKeyboard.status == TouchScreenKeyboard.Status.LostFocus)
                {
                    CloseTouchKeyboard();
                }
            }
            PushKeyboardInset();
            if (mode == UiMode.Scene && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                ReturnToMenu();
                return;
            }
            if (mode == UiMode.Scene)
            {
                HandleCoPresenceFrame();
                PushChromeInsets();
            }
            if (Time.unscaledTime < nextPollAt)
            {
                return;
            }
            nextPollAt = Time.unscaledTime + PollIntervalSeconds;
            RefreshConnectionUi();
            RefreshChatUi();
            RefreshVoiceUi();
            RefreshSettingsUi();
            RefreshActionPlaybackBadges();
            RefreshSceneToolbarState();
        }

        // ═══════════════════════ UI bootstrap ═══════════════════════

        private void EnsureBuilt()
        {
            if (built || document == null)
            {
                return;
            }
            panelRoot = document.rootVisualElement;
            if (panelRoot == null)
            {
                return;
            }
            panelRoot.Clear();
            var shellAsset = Resources.Load<VisualTreeAsset>("BanxiaShell");
            if (shellAsset != null)
            {
                shellAsset.CloneTree(panelRoot);
            }
            else
            {
                Debug.LogWarning("[BanxiaUi] BanxiaShell.uxml missing; using fallback visual tree.", this);
            }
            // 设计系统基座（vendored：sinanata/unity-ui-toolkit-design-system，MIT）。
            // 2022.3 不支持 USS @import，所以逐张挂载；顺序必须先 DS 令牌/组件，
            // 再挂 iOS 26 浅色覆盖层（BanxiaDsTheme），最后是 BanxiaTheme 既有层。
            foreach (var sheetName in new[]
                     {
                         "UI/Styles/DesignSystem/DesignTokens",
                         "UI/Styles/DesignSystem/Typography",
                         "UI/Styles/DesignSystem/Icons",
                         "UI/Styles/DesignSystem/Buttons",
                         "UI/Styles/DesignSystem/Inputs",
                         "UI/Styles/DesignSystem/TabsAndFilters",
                         "UI/Styles/DesignSystem/Cards",
                         "UI/Styles/DesignSystem/Navigation",
                         "UI/Styles/DesignSystem/Badges",
                         "UI/Styles/DesignSystem/Controls",
                         "UI/Styles/DesignSystem/Overlays",
                         "UI/Styles/DesignSystem/Feedback",
                         "UI/Styles/DesignSystem/Mobile",
                         "UI/Styles/DesignSystem/DropdownPopup",
                     })
            {
                var ds = Resources.Load<StyleSheet>(sheetName);
                if (ds != null)
                {
                    panelRoot.styleSheets.Add(ds);
                }
            }
            var dsTheme = Resources.Load<StyleSheet>("BanxiaDsTheme");
            if (dsTheme != null)
            {
                panelRoot.styleSheets.Add(dsTheme);
            }
            var theme = Resources.Load<StyleSheet>("BanxiaTheme");
            if (theme != null)
            {
                panelRoot.styleSheets.Add(theme);
            }

            shellRoot = panelRoot.Q<VisualElement>("root") ?? panelRoot;
            mainUi = shellRoot.Q<VisualElement>("main-ui") ?? shellRoot;
            content = shellRoot.Q<VisualElement>("content");
            tabBar = shellRoot.Q<VisualElement>("tab-bar");
            sceneToolbar = shellRoot.Q<VisualElement>("scene-toolbar");
            toast = shellRoot.Q<VisualElement>("toast");
            toastLabel = shellRoot.Q<Label>("toast-label");

            if (content == null || tabBar == null || sceneToolbar == null)
            {
                Debug.LogWarning("[BanxiaUi] Shell visual tree incomplete; falling back to runtime shell.", this);
                panelRoot.Clear();
                BuildFallbackShell(panelRoot);
                shellRoot = panelRoot.Q<VisualElement>("root") ?? panelRoot;
                mainUi = shellRoot.Q<VisualElement>("main-ui") ?? shellRoot;
                content = shellRoot.Q<VisualElement>("content");
                tabBar = shellRoot.Q<VisualElement>("tab-bar");
                sceneToolbar = shellRoot.Q<VisualElement>("scene-toolbar");
                toast = shellRoot.Q<VisualElement>("toast");
                toastLabel = shellRoot.Q<Label>("toast-label");
                if (content == null || tabBar == null || sceneToolbar == null)
                {
                    Debug.LogError("[BanxiaUi] Fallback shell is incomplete.", this);
                    return;
                }
            }

            BuildPages();
            SanitizeLabels(panelRoot);
            // 兜底：主界面必须不透明黑底。USS 的 var(--bg) 万一没生效
            // （老版本解析差异/加载时序），直接用内联样式钉死，
            // 避免“面板透明”的观感。
            if (mainUi != null)
            {
                mainUi.style.backgroundColor = new Color(0.949f, 0.949f, 0.969f, 1f); /* #F2F2F7 */
            }
            BindTab("tab-companion", Tab.Companion);
            BindTab("tab-chat", Tab.Chat);
            BindTab("tab-actions", Tab.Actions);
            BindTab("tab-settings", Tab.Settings);
            BindSceneToolbar();
            if (worldSpaceHost)
            {
                EnsureWorldCloseButton();
            }
            built = true;
            SelectTab(Tab.Companion);
            ApplyMode(UiMode.Menu);
        }

        private static void SanitizeLabels(VisualElement panelRoot)
        {
            // 空/NULL 文本的 Label 会触发 TextElement 渲染 NRE，导致整块 UI 黑屏。
            panelRoot.Query<Label>().ForEach(label =>
            {
                if (string.IsNullOrEmpty(label.text))
                {
                    label.text = " ";
                }
            });
        }

        private static void BuildFallbackShell(VisualElement panelRoot)
        {
            var root = new VisualElement { name = "root", pickingMode = PickingMode.Ignore };
            root.AddToClassList("screen-root");
            var main = new VisualElement { name = "main-ui", pickingMode = PickingMode.Position };
            main.AddToClassList("screen");
            var content = new VisualElement { name = "content" };
            content.style.flexGrow = 1f;
            var tabs = new VisualElement { name = "tab-bar" };
            tabs.AddToClassList("tab-bar");
            AddFallbackTab(tabs, "tab-companion", "伴夏", "首页");
            AddFallbackTab(tabs, "tab-chat", "对话", "对话");
            AddFallbackTab(tabs, "tab-actions", "动作", "动作");
            AddFallbackTab(tabs, "tab-settings", "设置", "设置");
            main.Add(content);
            main.Add(tabs);
            root.Add(main);
            var toolbar = new VisualElement { name = "scene-toolbar" };
            toolbar.AddToClassList("scene-toolbar");
            AddToolbarPill(toolbar, "pill-back", "主界面");
            AddToolbarPill(toolbar, "pill-move", "移动");
            AddToolbarPill(toolbar, "pill-mode", "环境");
            AddToolbarPill(toolbar, "pill-frame", "取景");
            AddToolbarPill(toolbar, "pill-hud", "HUD");
            root.Add(toolbar);
            var toast = new VisualElement { name = "toast" };
            toast.AddToClassList("toast");
            var label = new Label { name = "toast-label" };
            label.AddToClassList("toast-label");
            toast.Add(label);
            root.Add(toast);
            panelRoot.Add(root);
        }

        private static void AddFallbackTab(VisualElement parent, string name, string icon, string text)
        {
            var item = new VisualElement { name = name };
            item.AddToClassList("tab-item");
            var textLabel = new Label(text);
            textLabel.AddToClassList("tab-label");
            item.Add(textLabel);
            parent.Add(item);
        }

        private static void AddToolbarPill(VisualElement parent, string name, string text)
        {
            var pill = new VisualElement { name = name };
            pill.AddToClassList("pill");
            var label = new Label(text);
            label.AddToClassList("pill-label");
            pill.Add(label);
            parent.Add(pill);
        }

        private void BuildPages()
        {
            content.Clear();
            tabPages.Clear();
            segmentRefreshers.Clear();
            BuildCompanionPage();
            BuildChatPage();
            BuildActionsPage();
            BuildSettingsPage();
        }

        private void BindTab(string name, Tab tab)
        {
            var element = shellRoot.Q<VisualElement>(name);
            if (element == null)
            {
                return;
            }
            element.RegisterCallback<ClickEvent>(_ => SelectTab(tab));
        }

        private void SelectTab(Tab tab)
        {
            CloseTouchKeyboard();
            currentTab = tab;
            foreach (var pair in tabPages)
            {
                pair.Value.style.display = pair.Key == tab ? DisplayStyle.Flex : DisplayStyle.None;
            }
            SetTabSelected("tab-companion", tab == Tab.Companion);
            SetTabSelected("tab-chat", tab == Tab.Chat);
            SetTabSelected("tab-actions", tab == Tab.Actions);
            SetTabSelected("tab-settings", tab == Tab.Settings);
            if (tab == Tab.Companion)
            {
                RefreshModels(forceInvalidate: false);
            }
            if (tab == Tab.Actions && owner?.VmdActions != null && owner.VmdActions.Actions.Count == 0)
            {
                RefreshActionsAsync().Forget("ui.actions.refresh");
            }
            if (tab == Tab.Settings)
            {
                RefreshLogPreview();
            }
            RefreshConnectionUi();
        }

        private void SetTabSelected(string name, bool selected)
        {
            var element = shellRoot?.Q<VisualElement>(name);
            element?.EnableInClassList("selected", selected);
            element?.EnableInClassList("is-active", selected); // DS ds-bottom-nav__item 选中态
        }

        private void BindSceneToolbar()
        {
            sceneToolbar.Q<VisualElement>("pill-back")?.RegisterCallback<ClickEvent>(_ => ReturnToMenu());
            movePill = sceneToolbar.Q<VisualElement>("pill-move");
            movePill?.RegisterCallback<ClickEvent>(_ => ToggleMoveMode());
            sceneToolbar.Q<VisualElement>("pill-mode")?.RegisterCallback<ClickEvent>(_ => OnSceneModePillPressed());
            sceneToolbar.Q<VisualElement>("pill-frame")?.RegisterCallback<ClickEvent>(_ => ReframeCamera());
            sceneToolbar.Q<VisualElement>("pill-hud")?.RegisterCallback<ClickEvent>(_ => ToggleHud());
        }

        private void EnsureWorldCloseButton()
        {
            if (shellRoot == null || shellRoot.Q<VisualElement>("world-close") != null)
            {
                return;
            }
            var close = new VisualElement { name = "world-close", pickingMode = PickingMode.Position };
            close.AddToClassList("pill");
            close.style.position = Position.Absolute;
            close.style.top = 14;
            close.style.right = 14;
            close.style.paddingLeft = 16;
            close.style.paddingRight = 16;
            close.style.height = 42;
            var label = new Label("关闭");
            label.AddToClassList("pill-label");
            close.Add(label);
            close.RegisterCallback<ClickEvent>(_ => closeRequested?.Invoke());
            shellRoot.Add(close);
        }

        private void ApplyMode(UiMode nextMode)
        {
            mode = nextMode;
            bool menu = mode == UiMode.Menu;
            mainUi.style.display = menu ? DisplayStyle.Flex : DisplayStyle.None;
            sceneToolbar.style.display = menu ? DisplayStyle.None : DisplayStyle.Flex;
            if (hud != null && menu)
            {
                hud.SetVisible(false);
            }
            RefreshSceneToolbarState();
        }

        // ═══════════════════════ Companion / model page ═══════════════════════

        private void BuildCompanionPage()
        {
            var page = new VisualElement { style = { flexGrow = 1f } };
            page.Add(MakeNavBar("伴夏", "模型、导入与陪伴入口"));
            var scroll = new ScrollView();
            scroll.AddToClassList("scroll");
            EnableTouchDragScroll(scroll);
            modelsListContainer = new VisualElement();
            scroll.Add(modelsListContainer);

            modelsEmptyContainer = new VisualElement();
            modelsEmptyContainer.AddToClassList("empty-hint");
            var emptyLabel = new Label("还没有发现 PMX 模型。\n点击下面的导入按钮，或把模型放到 Download/mmdmodel 后再导入。");
            emptyLabel.AddToClassList("empty-hint-label");
            modelsEmptyContainer.Add(emptyLabel);
            scroll.Add(modelsEmptyContainer);

            importStatusLabel = new Label("模型和动作导入共用 Android 文件选择器");
            importStatusLabel.AddToClassList("status-line");
            scroll.Add(importStatusLabel);
            scroll.Add(MakeButton("导入模型 / 动作文件", false, () => OpenImportPicker("正在打开导入器…")));
            scroll.Add(MakeButton("刷新模型列表", false, () => RefreshModels(forceInvalidate: true)));

            // 快捷入口：填充首页纵向空间，同时把高频路径缩短为一跳。
            scroll.Add(MakeGroupHeader("快捷入口"));
            var grid = new VisualElement();
            grid.AddToClassList("tile-grid");
            grid.Add(MakeTile("对话", "和 TA 聊天 · 可拍一拍", () => SelectTab(Tab.Chat)));
            grid.Add(MakeTile("动作", "VMD · 待机 · 表情", () => SelectTab(Tab.Actions)));
            grid.Add(MakeTile("设置", "画质 · 摄像头 · 音量", () => SelectTab(Tab.Settings)));
            grid.Add(MakeTile("更新", "检查新版本并安装", () => CheckUpdateAsync().Forget("ui.update.check")));
            scroll.Add(grid);

            page.Add(scroll);
            tabPages[Tab.Companion] = page;
            content.Add(page);
        }

        private static VisualElement MakeTile(string title, string subtitle, Action onClick)
        {
            var tile = new VisualElement();
            tile.AddToClassList("tile");
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("tile-title");
            var subtitleLabel = new Label(subtitle);
            subtitleLabel.AddToClassList("tile-sub");
            tile.Add(titleLabel);
            tile.Add(subtitleLabel);
            tile.RegisterCallback<ClickEvent>(_ => onClick?.Invoke());
            return tile;
        }

        private void RefreshModels(bool forceInvalidate)
        {
            if (!built || modelLoader == null || modelsListContainer == null)
            {
                return;
            }
            if (forceInvalidate)
            {
                modelLoader.InvalidateInstalledModelCache();
            }
            models.Clear();
            try
            {
                var discovered = modelLoader.DiscoverInstalledModels();
                if (discovered != null)
                {
                    foreach (var model in discovered)
                    {
                        models.Add(model);
                    }
                }
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "ui.models.refresh");
                QuestDebugMode.RethrowIfEnabled(exception, "ui.models.refresh");
                ShowToast("模型列表读取失败：" + exception.Message);
            }

            modelsListContainer.Clear();
            modelsEmptyContainer.style.display = models.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            foreach (var model in models)
            {
                modelsListContainer.Add(MakeModelCard(model));
            }
        }

        private VisualElement MakeModelCard(RuntimeMmdModelInfo model)
        {
            var card = new VisualElement();
            card.AddToClassList("card");
            FillModelCard(card, model, confirmDelete: false);
            return card;
        }

        private void FillModelCard(VisualElement card, RuntimeMmdModelInfo model, bool confirmDelete)
        {
            card.Clear();
            if (confirmDelete)
            {
                var confirm = new VisualElement();
                confirm.AddToClassList("card-confirm");
                var confirmText = new Label("删除「" + model.DisplayName + "」？");
                confirmText.AddToClassList("card-confirm-text");
                confirm.Add(confirmText);
                confirm.Add(MakeSmallButton("取消", false, () => FillModelCard(card, model, false)));
                confirm.Add(MakeSmallButton("删除", true, () => DeleteModel(model)));
                card.Add(confirm);
                return;
            }

            var body = new VisualElement();
            body.AddToClassList("card-body");
            var title = new Label(model.DisplayName);
            title.AddToClassList("card-title");
            var subtitle = new Label(DescribeModel(model));
            subtitle.AddToClassList("card-subtitle");
            body.Add(title);
            body.Add(subtitle);
            if (IsCurrentModel(model))
            {
                var badge = new Label("使用中");
                badge.AddToClassList("card-badge");
                body.Add(badge);
            }

            var enter = MakeCardAction(IsCurrentModel(model) ? "查看" : "进入", () => EnterScene(model));
            var more = new VisualElement();
            more.AddToClassList("card-more");
            var moreLabel = new Label("删除");
            moreLabel.AddToClassList("card-more-label");
            more.Add(moreLabel);
            more.RegisterCallback<ClickEvent>(_ => FillModelCard(card, model, true));
            card.Add(body);
            card.Add(enter);
            card.Add(more);
        }

        private bool IsCurrentModel(RuntimeMmdModelInfo model)
        {
            return modelLoader != null && string.Equals(
                modelLoader.CurrentModelPath, model.Path, StringComparison.OrdinalIgnoreCase);
        }

        private void DeleteModel(RuntimeMmdModelInfo model)
        {
            if (modelLoader == null)
            {
                return;
            }
            bool deleted = false;
            try
            {
                deleted = modelLoader.DeleteInstalledPackage(model);
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "ui.models.delete");
                QuestDebugMode.RethrowIfEnabled(exception, "ui.models.delete");
                ShowToast("删除失败：" + exception.Message);
            }
            ShowToast(deleted ? "模型包已删除" : "当前模型正在使用或无法删除");
            RefreshModels(forceInvalidate: true);
        }

        private void OpenImportPicker(string status)
        {
            if (fileImport == null)
            {
                ShowToast("导入服务不可用");
                return;
            }
            if (fileImport.IsBusy)
            {
                ShowToast("导入正在进行");
                return;
            }
            lastImportStatus = status;
            if (importStatusLabel != null)
            {
                importStatusLabel.text = status;
            }
            if (!fileImport.OpenPicker())
            {
                ShowToast("无法打开文件选择器");
            }
        }

        private void HandleImportStatus(string status)
        {
            lastImportStatus = status ?? string.Empty;
            if (importStatusLabel != null)
            {
                importStatusLabel.text = lastImportStatus;
            }
            ShowToast(string.IsNullOrEmpty(lastImportStatus) ? "导入状态已更新" : lastImportStatus);
            RefreshModels(forceInvalidate: true);
            RefreshActionsAsync().Forget("ui.actions.refresh");
        }

        private static string DescribeModel(RuntimeMmdModelInfo model)
        {
            string sizeText = "未知大小";
            try
            {
                if (File.Exists(model.Path))
                {
                    long bytes = new FileInfo(model.Path).Length;
                    sizeText = bytes >= 1024 * 1024
                        ? (bytes / 1024f / 1024f).ToString("F1") + " MB"
                        : (bytes / 1024f).ToString("F0") + " KB";
                }
            }
            catch (IOException exception)
            {
                QuestDebugMode.Report(exception, "ui.models.size-probe");
            }
            catch (UnauthorizedAccessException exception)
            {
                QuestDebugMode.Report(exception, "ui.models.size-probe");
            }
            return "PMX · " + sizeText;
        }

        // ═══════════════════════ Chat / backend page ═══════════════════════

        private void BuildChatPage()
        {
            var page = new VisualElement { style = { flexGrow = 1f } };
            page.Add(MakeNavBar("伴夏", "和她说说话"));

            // ── 未连接：引导去设置绑定（配对表单已迁移到设置页「连接后端」）──
            chatPairingScroll = new ScrollView();
            chatPairingScroll.AddToClassList("scroll");
            EnableTouchDragScroll(chatPairingScroll);
            connectionBadge = new Label("○ 未连接");
            connectionBadge.AddToClassList("status-line");
            chatPairingScroll.Add(connectionBadge);

            chatPairingCard = new VisualElement();
            chatPairingCard.AddToClassList("chat-guide-card");
            var guideTitle = new Label("还没有连上伴夏");
            guideTitle.AddToClassList("chat-guide-title");
            chatPairingCard.Add(guideTitle);
            var guideBody = new Label("在「设置 → 连接后端」里填服务器地址并绑定，\n之后就能在这里文字与语音对话。");
            guideBody.AddToClassList("chat-guide-body");
            chatPairingCard.Add(guideBody);
            chatPairingCard.Add(MakeButton("去设置绑定", true, () => SelectTab(Tab.Settings)));
            chatPairingScroll.Add(chatPairingCard);
            page.Add(chatPairingScroll);

            // ── 已连接：状态卡 + 纯消息流 + 建议 + 输入条（微信/QQ 式对话窗）──
            chatConversationCard = new VisualElement();
            chatConversationCard.style.flexGrow = 1f;

            // 伴夏状态卡（对应效果图：名字 + 实时连接状态 + 麦克风摘要）
            var statusCard = new VisualElement();
            statusCard.AddToClassList("chat-status-card");
            var statusAvatar = new VisualElement();
            statusAvatar.AddToClassList("chat-status-avatar");
            statusCard.Add(statusAvatar);
            var statusTexts = new VisualElement();
            statusTexts.AddToClassList("chat-status-texts");
            var statusName = new Label("伴夏");
            statusName.AddToClassList("chat-status-name");
            statusTexts.Add(statusName);
            chatStateLabel = new Label("会话待命");
            chatStateLabel.AddToClassList("chat-status-sub");
            statusTexts.Add(chatStateLabel);
            voiceStatusLabel = new Label("麦克风待命");
            voiceStatusLabel.AddToClassList("chat-status-sub");
            statusTexts.Add(voiceStatusLabel);
            statusCard.Add(statusTexts);
            chatConversationCard.Add(statusCard);

            chatTranscript = new ScrollView();
            chatTranscript.AddToClassList("chat-scroll");
            EnableTouchDragScroll(chatTranscript);
            chatConversationCard.Add(chatTranscript);
            page.Add(chatConversationCard);

            BuildChatSuggestions(page);
            AddChatInputBar(page);

            tabPages[Tab.Chat] = page;
            content.Add(page);
        }

        private void BuildPairingNumpad(VisualElement parent)
        {
            var pad = new VisualElement();
            pad.AddToClassList("numpad");
            AddPairingNumpadRow(pad, "123");
            AddPairingNumpadRow(pad, "456");
            AddPairingNumpadRow(pad, "789");
            var utilityRow = new VisualElement();
            utilityRow.AddToClassList("numpad-row");
            utilityRow.Add(MakeNumpadKey("清", ClearPairingCode));
            utilityRow.Add(MakeNumpadKey("0", () => AppendPairingDigit("0")));
            utilityRow.Add(MakeNumpadKey("退", RemovePairingDigit));
            pad.Add(utilityRow);
            pad.RegisterCallback<GeometryChangedEvent>(_ => ApplyPairingNumpadLayout(pad));
            parent.Add(pad);
            ApplyPairingNumpadLayout(pad);
        }

        private void AddPairingNumpadRow(VisualElement pad, string digits)
        {
            var row = new VisualElement();
            row.AddToClassList("numpad-row");
            for (int i = 0; i < digits.Length; i++)
            {
                var digit = digits[i].ToString();
                row.Add(MakeNumpadKey(digit, () => AppendPairingDigit(digit)));
            }
            pad.Add(row);
        }

        private void ApplyPairingNumpadLayout(VisualElement pad)
        {
            if (pad == null)
            {
                return;
            }
            float panelHeight = ResolvePanelHeight(pad);
            if (panelHeight <= 1f)
            {
                return;
            }

            // Use panel logical units, then derive the physical target from the
            // actual panel-to-screen scale. This keeps the 1/16 screen-height rule
            // stable on both the 1440x3200 device and the 1080x2340 emulator.
            float physicalHeight = Screen.height;
            float pixelsPerPanelUnit = physicalHeight / panelHeight;
            if (pixelsPerPanelUnit <= 0f)
            {
                return;
            }
            float keyHeight = (physicalHeight / 16f) / pixelsPerPanelUnit;
            if (keyHeight <= 1f || Mathf.Abs(keyHeight - lastPairingKeyHeight) < 0.5f)
            {
                return;
            }
            lastPairingKeyHeight = keyHeight;
            pad.Query<VisualElement>(className: "numpad-key").ForEach(key =>
            {
                key.style.height = keyHeight;
                key.style.flexGrow = 1f;
                key.style.flexShrink = 1f;
                key.style.flexBasis = 0f;
                var radius = keyHeight * 0.5f;
                key.style.borderTopLeftRadius = radius;
                key.style.borderTopRightRadius = radius;
                key.style.borderBottomLeftRadius = radius;
                key.style.borderBottomRightRadius = radius;
                var label = key.Q<Label>(className: "numpad-key-label");
                if (label != null)
                {
                    label.style.fontSize = keyHeight * 0.42f;
                    label.style.unityTextAlign = TextAnchor.MiddleCenter;
                }
            });
            if (!pairingNumpadLayoutLogged)
            {
                pairingNumpadLayoutLogged = true;
                Debug.Log($"[M3] screenH={Screen.height} panelH={panelHeight:F1} keyH={keyHeight:F1}", this);
            }
        }

        /// <summary>
        /// 快速回复建议区：伴夏每次回复后由后端 LLM 生成（reply.suggestions），
        /// 最多 3 条，从上到下一、二、三排列；点击直接发送。无建议时整块隐藏，
        /// 不占用对话窗空间。
        /// </summary>
        private void BuildChatSuggestions(VisualElement parent)
        {
            chatSuggestionsRoot = new VisualElement();
            chatSuggestionsRoot.AddToClassList("chat-suggestions");
            chatSuggestionsRoot.style.display = DisplayStyle.None;
            parent.Add(chatSuggestionsRoot);
        }

        private void RenderChatSuggestions(IReadOnlyList<string> suggestions)
        {
            if (chatSuggestionsRoot == null)
            {
                return;
            }
            var key = suggestions == null || suggestions.Count == 0
                ? string.Empty
                : string.Join("\x1F", suggestions);
            if (key == lastSuggestionsKey)
            {
                return;
            }
            lastSuggestionsKey = key;
            chatSuggestionsRoot.Clear();
            if (string.IsNullOrEmpty(key))
            {
                chatSuggestionsRoot.style.display = DisplayStyle.None;
                return;
            }
            chatSuggestionsRoot.style.display = DisplayStyle.Flex;
            for (var index = 0; index < suggestions.Count && index < 3; index++)
            {
                var captured = suggestions[index];
                var item = new VisualElement();
                item.AddToClassList("chat-suggestion-item");
                var badge = new Label((index + 1).ToString());
                badge.AddToClassList("chat-suggestion-index");
                item.Add(badge);
                var text = new Label(captured);
                text.AddToClassList("chat-suggestion-text");
                item.Add(text);
                item.RegisterCallback<ClickEvent>(_ => SendChatMessage(captured));
                chatSuggestionsRoot.Add(item);
            }
        }

        /// <summary>
        /// Android 上 UI Toolkit TextField 聚焦不弹系统软键盘（2022.3 已知行为），
        /// 用 TouchScreenKeyboard 显式打开；文本在 Update 里逐帧回写。
        /// FocusIn 与 Click 双通道兜底：部分机型 FocusIn 被内部输入域吃掉时，
        /// ClickEvent 仍能到达。
        /// </summary>
        private void AttachTouchKeyboardFallback(TextField field)
        {
            void Open()
            {
                if (Application.platform != RuntimePlatform.Android)
                {
                    return;
                }
                if (activeTouchKeyboard != null && activeTouchKeyboard.active)
                {
                    return;
                }
                activeTouchField = field;
                activeTouchKeyboard = TouchScreenKeyboard.Open(
                    field.value ?? string.Empty, TouchScreenKeyboardType.Default,
                    false, false, false, false);
            }
            field.RegisterCallback<FocusInEvent>(_ => Open());
            field.RegisterCallback<ClickEvent>(_ => Open());
        }

        private void CloseTouchKeyboard()
        {
            if (activeTouchKeyboard != null)
            {
                activeTouchKeyboard.active = false;
            }
            activeTouchKeyboard = null;
            activeTouchField = null;
            lastKeyboardInsetPx = -1f;
            keyboardInsetLogged = false;
            ApplyKeyboardInset(0f);
            if (tabBar != null && mode == UiMode.Menu)
            {
                tabBar.style.display = DisplayStyle.Flex;
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus)
            {
                CloseTouchKeyboard();
            }
        }

        /// <summary>
        /// UI Toolkit does not resize the Unity surface for TouchScreenKeyboard on
        /// Android. Use the native keyboard rectangle as a bottom inset so the
        /// chat composer follows the IME like a native chat window.
        /// </summary>
        private void PushKeyboardInset()
        {
            // CloseTouchKeyboard already applies the zero inset. Skip the idle
            // path so non-phone frames do not allocate or query UI geometry.
            if (activeTouchKeyboard == null && lastKeyboardInsetPx <= 0f)
            {
                return;
            }
            var keyboardVisible = activeTouchKeyboard != null &&
                activeTouchKeyboard.active &&
                activeTouchKeyboard.status == TouchScreenKeyboard.Status.Visible &&
                !worldSpaceHost &&
                document != null && document.panelSettings != null &&
                document.panelSettings.targetTexture == null;
            var area = keyboardVisible ? TouchScreenKeyboard.area : new Rect(0f, 0f, 0f, 0f);
            var insetPx = ComputeKeyboardInsetPixels(area, Screen.height);
            if (Mathf.Abs(insetPx - lastKeyboardInsetPx) <= 0.5f)
            {
                return;
            }
            var panelHeight = ResolvePanelHeight(activeTouchField);
            if (panelHeight <= 1f || Screen.height <= 1)
            {
                return;
            }
            lastKeyboardInsetPx = insetPx;
            var insetPanelUnits = insetPx <= 0f
                ? 0f
                : insetPx * panelHeight / Screen.height + 24f;
            ApplyKeyboardInset(insetPanelUnits);
            if (insetPx > 0f && currentTab == Tab.Chat && chatTranscript != null)
            {
                chatTranscript.schedule.Execute(() =>
                {
                    if (chatTranscript.verticalScroller != null)
                    {
                        chatTranscript.verticalScroller.value = chatTranscript.verticalScroller.highValue;
                    }
                });
            }
            if (insetPx > 0f && !keyboardInsetLogged)
            {
                keyboardInsetLogged = true;
                Debug.Log($"[KbInset] screenH={Screen.height} panelH={panelHeight:F1} " +
                          $"insetPx={insetPx:F0} panelUnits={insetPanelUnits:F0}", this);
            }
            if (insetPx <= 0f)
            {
                keyboardInsetLogged = false;
            }
            if (tabBar != null && mode == UiMode.Menu)
            {
                tabBar.style.display = insetPx > 0f ? DisplayStyle.None : DisplayStyle.Flex;
            }
        }

        private float ResolvePanelHeight(VisualElement preferredElement)
        {
            var candidate = panelRoot == null ? 0f : panelRoot.worldBound.height;
            if (IsUsablePanelHeight(candidate))
            {
                return candidate;
            }

            candidate = preferredElement == null || preferredElement.panel == null
                ? 0f
                : preferredElement.panel.visualTree.worldBound.height;
            if (IsUsablePanelHeight(candidate))
            {
                return candidate;
            }

            candidate = panelRoot == null || panelRoot.panel == null
                ? 0f
                : panelRoot.panel.visualTree.worldBound.height;
            if (IsUsablePanelHeight(candidate))
            {
                return candidate;
            }

            // During the first GeometryChangedEvent UI Toolkit can expose NaN
            // bounds. A screen-sized fallback keeps the scale ratio finite until
            // the next layout pass supplies the real panel height.
            return Screen.height > 1 ? Screen.height : 0f;
        }

        private static bool IsUsablePanelHeight(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 1f;
        }

        internal static float ComputeKeyboardInsetPixels(Rect area, float screenHeight)
        {
            if (screenHeight <= 1f || area.width <= 0f || area.height <= 0f)
            {
                return 0f;
            }
            // area.height is the covered screen height and is independent of
            // whether the platform reports the Rect origin from the top or bottom.
            return Mathf.Clamp(area.height, 0f, screenHeight);
        }

        internal static float ComputeKeyboardInsetPanelUnits(Rect area, float screenHeight, float panelHeight)
        {
            if (panelHeight <= 1f || screenHeight <= 1f)
            {
                return 0f;
            }
            var insetPx = ComputeKeyboardInsetPixels(area, screenHeight);
            return insetPx <= 0f ? 0f : insetPx * panelHeight / screenHeight + 24f;
        }

        internal void ApplyKeyboardInset(float insetPanelUnits)
        {
            if (content == null)
            {
                return;
            }
            content.style.paddingBottom = Mathf.Max(0f, insetPanelUnits);
        }

        private void AddChatInputBar(VisualElement parent)
        {
            var bar = new VisualElement();
            bar.AddToClassList("chat-input-bar");
            bar.style.flexDirection = FlexDirection.Row;

            // 左：语音/键盘切换（微信式收纳语音入口，替代常驻“语音（可选）”面板）
            chatVoiceToggle = new VisualElement();
            chatVoiceToggle.AddToClassList("chat-camera");
            chatVoiceToggle.AddToClassList("chat-voice-toggle");
            var voiceToggleLabel = new Label("音");
            voiceToggleLabel.AddToClassList("chat-camera-label");
            chatVoiceToggle.Add(voiceToggleLabel);
            chatVoiceToggle.RegisterCallback<ClickEvent>(_ => SetVoiceInputMode(!voiceInputMode));
            bar.Add(chatVoiceToggle);

            // 键盘态：文本输入（min-width:0 + flexShrink 对抗长文本 min-width 溢出）
            chatInputField = new TextField { multiline = false };
            chatInputField.AddToClassList("ds-input");
            chatInputField.AddToClassList("field");
            chatInputField.style.minWidth = 0f;
            chatInputField.style.flexShrink = 1f;
            chatInputField.style.overflow = Overflow.Hidden;
            AttachTouchKeyboardFallback(chatInputField);
            bar.Add(chatInputField);

            // 语音态：按住说话条（按下录音，松开发送，上滑取消）
            chatHoldBar = new VisualElement();
            chatHoldBar.AddToClassList("chat-hold-bar");
            chatHoldBar.style.display = DisplayStyle.None;
            chatHoldLabel = new Label("按住 说话");
            chatHoldLabel.AddToClassList("chat-hold-label");
            chatHoldBar.Add(chatHoldLabel);
            BindHoldToTalkBar(chatHoldBar);
            bar.Add(chatHoldBar);

            var camera = new VisualElement();
            camera.AddToClassList("chat-camera");
            var cameraLabel = new Label("拍");
            cameraLabel.AddToClassList("chat-camera-label");
            camera.Add(cameraLabel);
            camera.RegisterCallback<ClickEvent>(_evt => SendChatWithCameraFrameAsync().Forget("ui.camera-frame"));
            bar.Add(camera);
            var send = new VisualElement();
            send.AddToClassList("chat-send");
            var label = new Label("发");
            label.AddToClassList("chat-send-label");
            send.Add(label);
            send.RegisterCallback<ClickEvent>(_ => SendChatText());
            bar.Add(send);
            chatInputBarRoot = bar;
            parent.Add(bar);
        }

        /// <summary>
        /// 摄像头单帧入口：默认关（设置页开关），拍摄前 toast 明示；帧只存在于
        /// 内存，拍完即发；拍摄失败时上送如实回执（must_not_claim_observed）。
        /// </summary>
        private async System.Threading.Tasks.Task SendChatWithCameraFrameAsync()
        {
            if (PlayerPrefs.GetInt(PrefsPrefix + "camera", 0) != 1)
            {
                ShowToast("请先在「设置 → 通用」开启摄像头单帧");
                return;
            }
            if (owner?.AstrBot == null || !owner.AstrBot.IsConnected || owner.Conversation == null)
            {
                ShowToast("真实后端尚未连接");
                return;
            }
            var userInput = chatInputField?.value?.Trim() ?? string.Empty;
            ShowToast("正在拍摄一帧（不会保存）…");
            var (frame, failureReason) = await PhoneRealityCameraSnapshot.CaptureSingleFrameAsync();
            if (frame != null)
            {
                var frameText = RealityCameraTurn.ComposeFrameText(userInput);
                var attachment = new TurnImageAttachment
                {
                    data_base64 = frame.JpegBase64,
                    purpose = frameText
                };
                AddChatBubble(true, frameText + "（附摄像头单帧）");
                chatInputField?.SetValueWithoutNotify(string.Empty);
                lastReplyText = string.Empty;
                lastTranscriptText = frameText;
                owner.Conversation.StartConversation(frameText, attachment);
                RefreshChatUi();
                return;
            }

            // 拍摄失败：上送如实回执，角色必须承认失败而不编造画面。
            var receipt = string.IsNullOrEmpty(userInput)
                ? RealityCameraTurn.ComposeFailureReceipt(failureReason)
                : userInput + "\n" + RealityCameraTurn.ComposeFailureReceipt(failureReason);
            AddChatBubble(true, receipt);
            chatInputField?.SetValueWithoutNotify(string.Empty);
            lastReplyText = string.Empty;
            lastTranscriptText = userInput;
            owner.Conversation.StartConversation(receipt);
            RefreshChatUi();
        }

        /// <summary>
        /// 语音输入模式（微信式）：输入条左侧切换钮在「键盘/语音」间切换。
        /// 语音态显示「按住 说话」条：按下开始录音，松开发送，上滑取消。
        /// 常开监听等配置项收进设置 → 通用；打断回复由新一轮输入自动 barge-in。
        /// </summary>
        private void SetVoiceInputMode(bool enabled)
        {
            if (enabled)
            {
                CloseTouchKeyboard();
            }
            voiceInputMode = enabled;
            if (chatVoiceToggle == null || chatHoldBar == null)
            {
                return;
            }
            var voiceLabel = chatVoiceToggle.Q<Label>(className: "chat-camera-label");
            if (voiceLabel != null)
            {
                voiceLabel.text = enabled ? "字" : "音";
            }
            chatHoldBar.style.display = enabled ? DisplayStyle.Flex : DisplayStyle.None;
            if (chatInputField != null)
            {
                chatInputField.style.display = enabled ? DisplayStyle.None : DisplayStyle.Flex;
            }
            SetChatInputButtonsVisible(!enabled);
        }

        private void SetChatInputButtonsVisible(bool visible)
        {
            if (chatInputBarRoot == null)
            {
                return;
            }
            var display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            // 注意：chat-voice-toggle 也挂了 chat-camera 类，须排除，只控制「拍/发」。
            chatInputBarRoot.Query<VisualElement>(className: "chat-camera").ForEach(element =>
            {
                if (!element.ClassListContains("chat-voice-toggle"))
                {
                    element.style.display = display;
                }
            });
            chatInputBarRoot.Query<VisualElement>(className: "chat-send").ForEach(element =>
            {
                element.style.display = display;
            });
        }

        private void BindHoldToTalkBar(VisualElement holdBar)
        {
            holdBar.RegisterCallback<PointerDownEvent>(evt =>
            {
                if (!voiceInputMode)
                {
                    return;
                }
                voiceHoldStartY = evt.position.y;
                voiceHoldCancelArmed = false;
                holdBar.CapturePointer(evt.pointerId);
                holdBar.AddToClassList("chat-hold-bar--active");
                UpdateHoldBarLabel();
                if (owner?.VoiceInput != null && !owner.VoiceInput.IsRecording)
                {
                    owner.VoiceInput.ToggleRecording();
                }
                evt.StopPropagation();
            });
            holdBar.RegisterCallback<PointerMoveEvent>(evt =>
            {
                if (!voiceInputMode || owner?.VoiceInput == null || !owner.VoiceInput.IsRecording)
                {
                    return;
                }
                var slideUp = voiceHoldStartY - evt.position.y;
                var shouldCancel = slideUp > 120f;
                if (shouldCancel != voiceHoldCancelArmed)
                {
                    voiceHoldCancelArmed = shouldCancel;
                    UpdateHoldBarLabel();
                }
            });
            holdBar.RegisterCallback<PointerUpEvent>(evt =>
            {
                if (!voiceInputMode || owner?.VoiceInput == null || !owner.VoiceInput.IsRecording)
                {
                    ResetHoldBarVisual();
                    return;
                }
                holdBar.ReleasePointer(evt.pointerId);
                if (voiceHoldCancelArmed)
                {
                    owner.VoiceInput.CancelRecording();
                    ShowToast("已取消本条语音");
                }
                else
                {
                    owner.VoiceInput.ToggleRecording(); // IsRecording → StopAndSend
                }
                ResetHoldBarVisual();
                RefreshVoiceUi();
                evt.StopPropagation();
            });
            holdBar.RegisterCallback<PointerLeaveEvent>(_ => ResetHoldBarVisual());
        }

        private void UpdateHoldBarLabel()
        {
            if (chatHoldLabel == null)
            {
                return;
            }
            var recording = owner?.VoiceInput != null && owner.VoiceInput.IsRecording;
            if (!recording)
            {
                chatHoldLabel.text = "按住 说话";
                return;
            }
            chatHoldLabel.text = voiceHoldCancelArmed
                ? "松开手指，取消发送"
                : "松开发送 · 上滑取消";
        }

        private void ResetHoldBarVisual()
        {
            voiceHoldCancelArmed = false;
            chatHoldBar?.RemoveFromClassList("chat-hold-bar--active");
            UpdateHoldBarLabel();
        }

        private void AppendPairingDigit(string digit)
        {
            if (owner?.Pairing != null && owner.Pairing.IsBusy)
            {
                return;
            }
            if (pairingCode.Length >= 6)
            {
                return;
            }
            pairingCode += digit;
            RefreshPairingCodeDisplay();
            if (pairingCode.Length == 6 && pairingNumpadExpanded)
            {
                // 输满即收起键盘：后续动作（点「连接后端」）不再需要 585px 的键盘。
                SetPairingNumpadExpanded(false);
            }
        }

        private void RemovePairingDigit()
        {
            if (pairingCode.Length == 0)
            {
                return;
            }
            pairingCode = pairingCode.Substring(0, pairingCode.Length - 1);
            RefreshPairingCodeDisplay();
        }

        private void ClearPairingCode()
        {
            pairingCode = string.Empty;
            RefreshPairingCodeDisplay();
        }

        private void RefreshPairingCodeDisplay()
        {
            if (pairingCodeLabel != null)
            {
                var chars = new char[6];
                for (int i = 0; i < 6; i++)
                {
                    chars[i] = i < pairingCode.Length ? '●' : '_';
                }
                pairingCodeLabel.text = $"{chars[0]} {chars[1]} {chars[2]}   {chars[3]} {chars[4]} {chars[5]}";
            }
            if (pairingDots != null)
            {
                pairingDots.Clear();
                for (int i = 0; i < 6; i++)
                {
                    var dot = new VisualElement();
                    dot.AddToClassList("code-dot");
                    dot.EnableInClassList("filled", i < pairingCode.Length);
                    pairingDots.Add(dot);
                }
            }
        }

        private void TryPair()
        {
            var pairing = owner?.Pairing;
            if (pairing == null)
            {
                ShowToast("配对控制器不可用");
                return;
            }
            var server = pairingServerField?.value?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(server) && !pairing.TrySetPairingServer(server, out var reason))
            {
                ShowToast(reason);
                RefreshConnectionUi();
                return;
            }
            if (pairingCode.Length != 6)
            {
                ShowToast("请输入完整的 6 位配对码");
                RefreshConnectionUi();
                return;
            }
            pairing.PairWithCode(pairingCode);
            ShowToast("正在配对…");
            RefreshConnectionUi();
        }

        private void ClearPairingConfiguration()
        {
            var bridge = owner?.AstrBot;
            if (bridge == null)
            {
                return;
            }
            try
            {
                if (File.Exists(bridge.ConfigurationPath))
                {
                    File.Delete(bridge.ConfigurationPath);
                }
                bridge.ReloadConfiguration();
                ShowToast("已解除后端绑定");
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "ui.pairing.clear");
                QuestDebugMode.RethrowIfEnabled(exception, "ui.pairing.clear");
                ShowToast("解除绑定失败：" + exception.Message);
            }
            RefreshConnectionUi();
        }

        private void RefreshConnectionUi()
        {
            if (!built || connectionBadge == null || owner == null)
            {
                return;
            }
            bool connected = owner.AstrBot != null && owner.AstrBot.IsConnected;
            connectionBadge.text = connected
                ? "● 已连接 · " + BanxiaUiText.LocalizeBridgeStatus(owner.AstrBot.Status)
                : "○ 未连接 · " + BanxiaUiText.LocalizeBridgeStatus(owner.AstrBot?.Status ?? string.Empty);
            if (chatPairingCard != null)
            {
                chatPairingCard.style.display = connected ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (chatPairingScroll != null)
            {
                chatPairingScroll.style.display = connected ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (chatConversationCard != null)
            {
                chatConversationCard.style.display = connected ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (chatSuggestionsRoot != null)
            {
                if (connected)
                {
                    // 显隐主要由 RenderChatSuggestions 依据建议内容控制。
                    if (!string.IsNullOrEmpty(lastSuggestionsKey))
                    {
                        chatSuggestionsRoot.style.display = DisplayStyle.Flex;
                    }
                }
                else
                {
                    // 断连即丢弃旧建议，避免重连后展示过期内容。
                    lastSuggestionsKey = string.Empty;
                    chatSuggestionsRoot.Clear();
                    chatSuggestionsRoot.style.display = DisplayStyle.None;
                }
            }
            if (chatInputBarRoot != null)
            {
                chatInputBarRoot.style.display = connected ? DisplayStyle.Flex : DisplayStyle.None;
            }
            var pairing = owner.Pairing;
            if (pairingStatusLabel != null)
            {
                var bridge = BanxiaUiText.LocalizeBridgeStatus(owner.AstrBot?.Status ?? string.Empty);
                var pairingText = BanxiaUiText.LocalizePairingStatus(pairing?.Status ?? "Pairing controller offline");
                var modeText = pairing != null && pairing.PrivateHttpAllowed ? "HTTP 默认（可填写 HTTPS）" : "仅 HTTPS";
                pairingStatusLabel.text = "实时连接：" + bridge + "\n配对：" + pairingText + "\n" + modeText;
            }
        }

        private void HandleConversationStateChanged(ConversationState _)
        {
            RefreshChatUi();
        }

        private void RefreshChatUi()
        {
            if (!built || owner?.Conversation == null || chatStateLabel == null)
            {
                return;
            }
            var conversation = owner.Conversation;
            chatStateLabel.text = "会话：" + BanxiaUiText.LocalizeConversationState(conversation.State) +
                                  " · " + BanxiaUiText.LocalizeBridgeStatus(conversation.TransportStatus);
            if (conversation.State == ConversationState.Error && !string.IsNullOrWhiteSpace(conversation.LastErrorMessage))
            {
                chatStateLabel.text += "\n错误：" + conversation.LastErrorMessage;
            }

            var transcript = conversation.Transcript ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(transcript) && transcript != lastTranscriptText)
            {
                lastTranscriptText = transcript;
                AddOrReplaceLatestUserBubble(transcript);
            }
            var reply = conversation.ReplyText ?? string.Empty;
            if (reply != lastReplyText)
            {
                lastReplyText = reply;
                if (!string.IsNullOrWhiteSpace(reply))
                {
                    AddOrReplaceLatestReplyBubble(reply);
                }
            }

            RenderChatSuggestions(conversation.SuggestedReplies);
        }

        private void SendChatText()
        {
            SendChatMessage(chatInputField?.value?.Trim() ?? string.Empty);
        }

        /// <summary>
        /// 统一发送入口：输入条「发」与建议条直发共用。发送即开启新回合，
        /// 控制器会清空旧建议（见 ConversationController.ApplySuggestions 注释）。
        /// </summary>
        private void SendChatMessage(string text)
        {
            text = text?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                ShowToast("请先输入内容");
                return;
            }
            if (owner?.AstrBot == null || !owner.AstrBot.IsConnected || owner.Conversation == null)
            {
                ShowToast("真实后端尚未连接，未切换到本地演示");
                return;
            }
            AddChatBubble(true, text);
            if (chatInputField != null)
            {
                chatInputField.SetValueWithoutNotify(string.Empty);
            }
            lastReplyText = string.Empty;
            lastTranscriptText = text;
            owner.Conversation.StartConversation(text);
            RefreshChatUi();
        }

        private void AddChatBubble(bool user, string text)
        {
            if (string.IsNullOrWhiteSpace(text) || chatTranscript == null)
            {
                return;
            }
            chatHistory.Add((user, text));
            while (chatHistory.Count > MaxChatBubbles)
            {
                chatHistory.RemoveAt(0);
            }
            RebuildChatBubbles();
        }

        private void AddOrReplaceLatestUserBubble(string text)
        {
            if (chatHistory.Count > 0 && chatHistory[chatHistory.Count - 1].user)
            {
                chatHistory[chatHistory.Count - 1] = (true, text);
            }
            else
            {
                chatHistory.Add((true, text));
            }
            while (chatHistory.Count > MaxChatBubbles)
            {
                chatHistory.RemoveAt(0);
            }
            RebuildChatBubbles();
        }

        private void AddOrReplaceLatestReplyBubble(string text)
        {
            if (chatHistory.Count > 0 && !chatHistory[chatHistory.Count - 1].user)
            {
                chatHistory[chatHistory.Count - 1] = (false, text);
            }
            else
            {
                chatHistory.Add((false, text));
            }
            while (chatHistory.Count > MaxChatBubbles)
            {
                chatHistory.RemoveAt(0);
            }
            RebuildChatBubbles();
        }

        private void RebuildChatBubbles()
        {
            chatTranscript.contentContainer.Clear();
            foreach (var bubble in chatHistory)
            {
                var rootBubble = new VisualElement();
                rootBubble.AddToClassList(bubble.user ? "bubble-user" : "bubble-reply");
                var label = new Label(bubble.text);
                label.AddToClassList("bubble-text");
                rootBubble.Add(label);
                chatTranscript.Add(rootBubble);
            }
        }

        private void RefreshVoiceUi()
        {
            if (voiceStatusLabel == null || owner?.VoiceInput == null)
            {
                return;
            }
            var voice = owner.VoiceInput;
            // 单行摘要（状态卡副标题位）：对话页不再保留整块语音面板。
            voiceStatusLabel.text = "麦克风 " + (voice.IsRecording ? "录音中" : "待命") +
                                    " · 常开 " + (voice.AlwaysListening ? "开" : "关") +
                                    " · 电平 " + voice.InputLevel.ToString("F2");
            UpdateHoldBarLabel();
        }

        // ═══════════════════════ Actions page ═══════════════════════

        private void BuildActionsPage()
        {
            var page = new VisualElement { style = { flexGrow = 1f } };
            page.Add(MakeNavBar("动作", "外部 VMD、待机与表情"));
            var scroll = new ScrollView();
            scroll.AddToClassList("scroll");
            EnableTouchDragScroll(scroll);

            idlePresetLabel = new Label("默认待机：—");
            idlePresetLabel.AddToClassList("status-line");
            scroll.Add(idlePresetLabel);

            scroll.Add(MakeButtonRow(
                MakeSmallButton("切换待机", false, () =>
                {
                    owner?.IdlePose?.CyclePreset(1);
                    RefreshActionsHeader();
                }),
                MakeSmallButton("停止动作", false, () =>
                {
                    owner?.VmdActions?.StopAndReturnToIdle();
                    owner?.Avatar?.PlayAction("idle");
                    RefreshActionPlaybackBadges();
                }),
                MakeSmallButton("暂停/继续", false, () => owner?.SendCommand(new AvatarCommand { name = "toggle_pause" }))));

            scroll.Add(MakeButtonRow(
                MakeSmallButton("表情轮换", false, CycleExpression),
                MakeSmallButton("刷新动作", false, () => RefreshActionsAsync().Forget("ui.actions.refresh")),
                MakeSmallButton("导入 VMD", false, () => OpenImportPicker("正在打开动作导入器…"))));

            actionsStatusLabel = new Label("外部动作列表待刷新");
            actionsStatusLabel.AddToClassList("status-line");
            scroll.Add(actionsStatusLabel);
            actionsListContainer = new VisualElement();
            scroll.Add(actionsListContainer);

            page.Add(scroll);
            tabPages[Tab.Actions] = page;
            content.Add(page);
        }

        private async Task RefreshActionsAsync()
        {
            if (refreshingActions || owner?.VmdActions == null)
            {
                return;
            }
            refreshingActions = true;
            try
            {
                if (actionsStatusLabel != null)
                {
                    actionsStatusLabel.text = "正在刷新外部动作…";
                }
                var refreshed = await owner.VmdActions.RefreshAsync();
                RebuildActionCards(refreshed ?? owner.VmdActions.Actions);
                if (actionsStatusLabel != null)
                {
                    actionsStatusLabel.text = "共 " + (refreshed?.Count ?? owner.VmdActions.Actions.Count) + " 个动作";
                }
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "ui.actions.refresh");
                QuestDebugMode.RethrowIfEnabled(exception, "ui.actions.refresh");
                if (actionsStatusLabel != null)
                {
                    actionsStatusLabel.text = "动作列表刷新失败：" + exception.Message;
                }
            }
            finally
            {
                refreshingActions = false;
            }
        }

        private void RefreshActionsFromLibrary()
        {
            if (owner?.VmdActions == null)
            {
                return;
            }
            RebuildActionCards(owner.VmdActions.Actions);
            RefreshActionsHeader();
        }

        private void RefreshActionsHeader()
        {
            if (idlePresetLabel != null)
            {
                idlePresetLabel.text = "默认待机：" + (owner?.IdlePose?.PresetDisplayName ?? "—");
            }
        }

        private void RebuildActionCards(IReadOnlyList<VmdActionInfo> source)
        {
            if (actionsListContainer == null)
            {
                return;
            }
            actionsListContainer.Clear();
            if (source == null || source.Count == 0)
            {
                var empty = new VisualElement();
                empty.AddToClassList("empty-hint");
                var label = new Label("还没有外部 VMD 动作。\n点击「导入 VMD」添加动作文件。");
                label.AddToClassList("empty-hint-label");
                empty.Add(label);
                actionsListContainer.Add(empty);
                lastPlayingActionId = string.Empty;
                return;
            }
            foreach (var action in source)
            {
                actionsListContainer.Add(MakeActionCard(action));
            }
            RefreshActionPlaybackBadges();
        }

        private VisualElement MakeActionCard(VmdActionInfo action)
        {
            var card = new VisualElement();
            card.AddToClassList("card");
            var body = new VisualElement();
            body.AddToClassList("card-body");
            var title = new Label(action.DisplayName);
            title.AddToClassList("card-title");
            var subtitle = new Label(action.DurationSeconds.ToString("F1") + "s · " +
                                     action.KeyframeCount + " 帧" +
                                     (action.HasFacialTrack ? " · 含表情" : string.Empty));
            subtitle.AddToClassList("card-subtitle");
            body.Add(title);
            body.Add(subtitle);
            var badge = new Label("播放中") { name = "badge-" + action.Id };
            badge.AddToClassList("card-badge");
            badge.style.display = DisplayStyle.None;
            body.Add(badge);
            card.Add(body);
            var actionId = action.Id;
            card.Add(MakeCardAction("播放", () => PlayOrStopAction(actionId).Forget("ui.actions.play-or-stop")));
            card.Add(MakeSmallButton("删除", true, () => DeleteAction(actionId).Forget("ui.actions.delete")));
            return card;
        }

        private async Task PlayOrStopAction(string actionId)
        {
            var library = owner?.VmdActions;
            if (library == null)
            {
                return;
            }
            if (library.IsPlaying && string.Equals(library.CurrentActionId, actionId, StringComparison.Ordinal))
            {
                library.StopAndReturnToIdle();
                owner?.Avatar?.PlayAction("idle");
            }
            else
            {
                bool ok = await library.PlayAsync(actionId);
                ShowToast(ok ? "动作已开始" : "动作播放失败");
            }
            RefreshActionPlaybackBadges();
        }

        private async Task DeleteAction(string actionId)
        {
            var library = owner?.VmdActions;
            if (library == null)
            {
                return;
            }
            bool ok = await library.DeleteActionAsync(actionId);
            ShowToast(ok ? "动作已删除" : "动作删除失败");
            await RefreshActionsAsync();
        }

        private void HandlePlaybackChanged()
        {
            RefreshActionPlaybackBadges();
        }

        private void RefreshActionPlaybackBadges()
        {
            if (owner?.VmdActions == null || actionsListContainer == null)
            {
                return;
            }
            var playingId = owner.VmdActions.IsPlaying ? owner.VmdActions.CurrentActionId : string.Empty;
            if (playingId == lastPlayingActionId && Time.unscaledTime >= nextPollAt)
            {
                return;
            }
            lastPlayingActionId = playingId;
            actionsListContainer.Query<Label>(null, "card-badge").ForEach(badge =>
            {
                var id = badge.name != null && badge.name.StartsWith("badge-", StringComparison.Ordinal)
                    ? badge.name.Substring("badge-".Length)
                    : string.Empty;
                badge.style.display = !string.IsNullOrEmpty(id) && id == playingId ? DisplayStyle.Flex : DisplayStyle.None;
            });
        }

        private int expressionIndex;
        private static readonly string[] ExpressionIds = { "neutral", "happy", "shy", "surprised", "sad" };
        private static readonly string[] ExpressionNames = { "模型默认", "轻笑", "害羞", "惊讶", "难过" };

        private void CycleExpression()
        {
            expressionIndex = (expressionIndex + 1) % ExpressionIds.Length;
            var expr = ExpressionIds[expressionIndex];
            owner?.Conversation?.SetManualExpression(expr);
            owner?.Avatar?.SetEmotion(expr);
            ShowToast("表情：" + ExpressionNames[expressionIndex]);
        }

        // ═══════════════════════ Settings page ═══════════════════════

        private void BuildSettingsPage()
        {
            var page = new VisualElement { style = { flexGrow = 1f } };
            settingsDetailPages.Clear();

            // ── 二级详情页（内容与旧版逐条一致，收进各自页面）──
            var connectionPage = MakeSettingsDetailPage("连接后端", "服务器地址与配对码", FillConnectionSection);
            var qualityPage = MakeSettingsDetailPage("画质与物理", "渲染画质 · MMD 物理", FillQualitySection);
            var generalPage = MakeSettingsDetailPage("通用", "HUD · 摄像头 · 帧率 · 音量", FillGeneralSection);
            var performancePage = MakeSettingsDetailPage("设备性能", "实时性能采样", FillPerformanceSection);
            var aboutPage = MakeSettingsDetailPage("关于", "版本 · 设备 · 内存", FillAboutSection);
            var updatePage = MakeSettingsDetailPage("软件更新", "检查 GitHub Releases", FillUpdateSection);
            var logPage = MakeSettingsDetailPage("运行诊断", "日志查看与清空", FillLogSection);
            settingsDetailPages.AddRange(new[]
            {
                connectionPage, qualityPage, generalPage, performancePage,
                aboutPage, updatePage, logPage,
            });

            // ── 一级：设置根列表（iOS Settings 式分组入口行）──
            var rootList = new VisualElement { style = { flexGrow = 1f } };
            rootList.Add(MakeNavBar("设置", "连接、画质与诊断"));
            var rootScroll = new ScrollView();
            rootScroll.AddToClassList("scroll");
            EnableTouchDragScroll(rootScroll);
            var sectionCard = new VisualElement();
            sectionCard.AddToClassList("group");
            sectionCard.AddToClassList("ds-card");
            sectionCard.Add(MakeSettingsRow("连接后端", "服务器地址与配对码", () => ShowSettingsPage(connectionPage)));
            sectionCard.Add(MakeSettingsRow("画质与物理", "渲染画质 · MMD 物理", () => ShowSettingsPage(qualityPage)));
            sectionCard.Add(MakeSettingsRow("通用", "调试模式 · HUD · 摄像头 · 帧率 · 音量", () => ShowSettingsPage(generalPage)));
            sectionCard.Add(MakeSettingsRow("设备性能", "实时性能采样", () => ShowSettingsPage(performancePage)));
            sectionCard.Add(MakeSettingsRow("关于", "版本 · 设备 · 内存", () => ShowSettingsPage(aboutPage)));
            sectionCard.Add(MakeSettingsRow("软件更新", "检查 GitHub Releases", () => ShowSettingsPage(updatePage)));
            sectionCard.Add(MakeSettingsRow("运行诊断", "日志查看与清空", () => ShowSettingsPage(logPage)));
            rootScroll.Add(sectionCard);
            rootList.Add(rootScroll);
            settingsRootList = rootList;

            page.Add(rootList);
            page.Add(connectionPage);
            page.Add(qualityPage);
            page.Add(generalPage);
            page.Add(performancePage);
            page.Add(aboutPage);
            page.Add(updatePage);
            page.Add(logPage);
            tabPages[Tab.Settings] = page;
            content.Add(page);
            RefreshPairingCodeDisplay();
        }

        /// <summary>显示设置某个二级页（其余隐藏，含根列表）。</summary>
        private void ShowSettingsPage(VisualElement page)
        {

            if (settingsRootList != null)
            {
                settingsRootList.style.display = DisplayStyle.None;
            }
            foreach (var detail in settingsDetailPages)
            {
                detail.style.display = detail == page ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        /// <summary>返回设置根列表。</summary>
        private void ShowSettingsRoot()
        {
            if (settingsRootList != null)
            {
                settingsRootList.style.display = DisplayStyle.Flex;
            }
            foreach (var detail in settingsDetailPages)
            {
                detail.style.display = DisplayStyle.None;
            }
        }

        /// <summary>设置二级页骨架：返回行 + 导航标题 + 滚动容器，内容由 fill 填充。</summary>
        private VisualElement MakeSettingsDetailPage(string title, string subtitle, Action<VisualElement> fill)
        {
            var detail = new VisualElement { style = { flexGrow = 1f, display = DisplayStyle.None } };
            // 返回行：VisualElement + ClickEvent（绕开 Button 控件的全部默认
            // USS——unity-button 的 flex 压缩曾把 120px 高度压成 16px）。
            var back = new VisualElement();
            back.AddToClassList("settings-back");
            back.style.height = 220f;
            back.style.flexShrink = 0f;
            back.style.alignSelf = Align.Stretch;
            back.style.paddingTop = 100f; // 顶部安全留白（其他页 navbar 从 y≈100 起）
            var backLabel = new Label("< 设置");
            backLabel.AddToClassList("settings-back-label");
            back.Add(backLabel);
            back.RegisterCallback<ClickEvent>(_ => ShowSettingsRoot());
            detail.Add(back);
            detail.Add(MakeNavBar(title, subtitle));
            var scroll = new ScrollView();
            scroll.AddToClassList("scroll");
            EnableTouchDragScroll(scroll);
            fill(scroll);
            detail.Add(scroll);
            return detail;
        }

        /// <summary>设置根列表的一级行（标题 + 灰色摘要 + chevron）。</summary>
        private static VisualElement MakeSettingsRow(string title, string subtitle, Action open)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var label = new Label(title);
            label.AddToClassList("row-label");
            row.Add(label);
            if (!string.IsNullOrEmpty(subtitle))
            {
                var hint = new Label(subtitle);
                hint.AddToClassList("row-value");
                row.Add(hint);
            }
            var chevron = new Label(">");
            chevron.AddToClassList("row-chevron");
            row.Add(chevron);
            row.RegisterCallback<ClickEvent>(_ => open?.Invoke());
            return row;
        }

        // ── 设置各二级页内容（从旧版单页逐条迁移）──

        private void FillConnectionSection(VisualElement scroll)
        {
            var pairingGroup = new VisualElement();
            pairingGroup.AddToClassList("group");
            pairingGroup.AddToClassList("ds-card");
            // 字段不再带内部 label：行标签「服务器」已表达语义，旧 label
            // （“服务器域名 / IP:端口”实测 573px）会把输入区顶出卡片右缘。
            pairingServerField = new TextField { multiline = false };
            pairingServerField.AddToClassList("ds-input");
            pairingServerField.AddToClassList("field");
            pairingServerField.style.minWidth = 0f;
            pairingServerField.style.flexShrink = 1f;
            pairingServerField.style.overflow = Overflow.Hidden;
            AttachTouchKeyboardFallback(pairingServerField);
            pairingGroup.Add(MakeElementRow("服务器", pairingServerField));
            var serverHint = new Label("域名或 IP:端口，如 192.168.5.55:25520");
            serverHint.AddToClassList("status-line");
            pairingGroup.Add(serverHint);
            pairingGroup.Add(MakeToggleRow("允许明文 HTTP（私网/远程）", owner?.Pairing?.PrivateHttpAllowed ?? false, value =>
            {
                owner?.Pairing?.SetPrivateHttpAllowed(value);
                RefreshConnectionUi();
            }));

            // 配对码 + 键盘收进折叠段：配对码录入是低频事件（首次/换绑），
            // 常驻 585px 键盘曾把「解除绑定」推出首屏。首次绑定自动展开。
            var disclosure = MakeDisclosureRow("配对码输入", () =>
                SetPairingNumpadExpanded(!pairingNumpadExpanded));
            pairingDisclosureChevron = disclosure.Q<Label>(className: "row-chevron");
            pairingGroup.Add(disclosure);
            pairingNumpadSection = new VisualElement();
            pairingCodeLabel = new Label("_ _ _   _ _ _");
            pairingCodeLabel.AddToClassList("status-line");
            pairingNumpadSection.Add(pairingCodeLabel);
            pairingDots = new VisualElement();
            pairingDots.AddToClassList("code-dots");
            pairingNumpadSection.Add(pairingDots);
            BuildPairingNumpad(pairingNumpadSection);
            pairingGroup.Add(pairingNumpadSection);
            pairingStatusLabel = new Label(string.Empty);
            pairingStatusLabel.AddToClassList("status-line");
            pairingGroup.Add(pairingStatusLabel);
            pairingGroup.Add(MakeButton("连接后端", true, TryPair));
            pairingGroup.Add(MakeButtonRow(
                MakeSmallButton("重新连接", false, () =>
                {
                    owner?.AstrBot?.ReloadConfiguration();
                    ShowToast("正在重新连接后端");
                }),
                MakeSmallButton("解除绑定", true, ClearPairingConfiguration)));
            scroll.Add(pairingGroup);
            // 首次（无已存服务器地址）自动展开键盘，已绑定用户默认收起。
            SetPairingNumpadExpanded(string.IsNullOrEmpty(owner?.Pairing?.PairingServerEndpoint));
        }

        /// <summary>折叠/展开配对码键盘段，并同步指示箭头。</summary>
        private void SetPairingNumpadExpanded(bool expanded)
        {
            pairingNumpadExpanded = expanded;
            if (pairingNumpadSection != null)
            {
                pairingNumpadSection.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (pairingDisclosureChevron != null)
            {
                pairingDisclosureChevron.text = expanded ? "▾" : "▸";
            }
        }

        /// <summary>折叠段标题行（与设置根列表行同款外观，点击回调）。</summary>
        private static VisualElement MakeDisclosureRow(string title, Action toggle)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var label = new Label(title);
            label.AddToClassList("row-label");
            row.Add(label);
            var chevron = new Label("▸");
            chevron.AddToClassList("row-chevron");
            row.Add(chevron);
            row.RegisterCallback<ClickEvent>(_ => toggle?.Invoke());
            return row;
        }

        private void FillQualitySection(VisualElement scroll)
        {
            var qualityGroup = new VisualElement();
            qualityGroup.AddToClassList("group");
            qualityGroup.AddToClassList("ds-card");
            qualityGroup.Add(MakeSegmentedRow("渲染画质",
                new SegmentChoice("性能", () => owner?.Quality?.CurrentPreset == QuestQualityPreset.Performance,
                    () => owner?.Quality?.ApplyPreset(QuestQualityPreset.Performance)),
                new SegmentChoice("平衡", () => owner?.Quality?.CurrentPreset == QuestQualityPreset.Balanced,
                    () => owner?.Quality?.ApplyPreset(QuestQualityPreset.Balanced)),
                new SegmentChoice("清晰", () => owner?.Quality?.CurrentPreset == QuestQualityPreset.Clear,
                    () => owner?.Quality?.ApplyPreset(QuestQualityPreset.Clear))));
            qualityGroup.Add(MakeSegmentedRow("MMD 物理",
                new SegmentChoice("性能", () => owner?.Quality?.CurrentPhysicsPreset == MmdPhysicsPreset.Performance,
                    () => owner?.Quality?.ApplyPhysicsPreset(MmdPhysicsPreset.Performance)),
                new SegmentChoice("平衡", () => owner?.Quality?.CurrentPhysicsPreset == MmdPhysicsPreset.Balanced,
                    () => owner?.Quality?.ApplyPhysicsPreset(MmdPhysicsPreset.Balanced)),
                new SegmentChoice("精细", () => owner?.Quality?.CurrentPhysicsPreset == MmdPhysicsPreset.Fine,
                    () => owner?.Quality?.ApplyPhysicsPreset(MmdPhysicsPreset.Fine))));
            scroll.Add(qualityGroup);
            settingsQualityStatus = new Label("画质尚未应用");
            settingsQualityStatus.AddToClassList("status-line");
            scroll.Add(settingsQualityStatus);
            scroll.Add(MakeButton("恢复默认画质", false, () =>
            {
                owner?.Quality?.ResetToDefault();
                RefreshSettingsUi();
            }));
        }

        private void FillGeneralSection(VisualElement scroll)
        {
            var generalGroup = new VisualElement();
            generalGroup.AddToClassList("group");
            generalGroup.AddToClassList("ds-card");
            generalGroup.Add(MakeToggleRow("场景诊断 HUD", PlayerPrefs.GetInt(PrefsPrefix + "hud", 0) == 1, value =>
            {
                PlayerPrefs.SetInt(PrefsPrefix + "hud", value ? 1 : 0);
                PlayerPrefs.Save();
                ShowToast(value ? "场景 HUD 已开启" : "场景 HUD 已关闭");
                if (hud != null && mode == UiMode.Scene)
                {
                    hud.SetVisible(value);
                }
            }));
            generalGroup.Add(MakeToggleRow("构图网格", PlayerPrefs.GetInt(PrefsPrefix + "framing-grid", 0) == 1, value =>
            {
                PlayerPrefs.SetInt(PrefsPrefix + "framing-grid", value ? 1 : 0);
                PlayerPrefs.Save();
                hud?.SetFramingGridVisible(value);
                ShowToast(value ? "构图网格已开启" : "构图网格已关闭");
            }));
            generalGroup.Add(MakeToggleRow("摄像头单帧（拍给 TA 看）", PlayerPrefs.GetInt(PrefsPrefix + "camera", 0) == 1, value =>
            {
                PlayerPrefs.SetInt(PrefsPrefix + "camera", value ? 1 : 0);
                PlayerPrefs.Save();
                // 授权能力化：默认关；开启即视为授予"按请求拍单帧"能力，
                // 关闭后拍摄入口直接拒绝。
                ShowToast(value ? "摄像头单帧已开启：对话页「拍」按钮可用" : "摄像头单帧已关闭");
            }));
            var debugModeRow = MakeToggleRow("调试模式（不拦截报错）", QuestDebugMode.Enabled, value =>
            {
                QuestDebugMode.SetEnabled(value);
                ShowToast(value ? "调试模式已开启：异常打印完整堆栈" : "调试模式已关闭");
            });
            generalGroup.Add(debugModeRow);
            segmentRefreshers.Add(() => debugModeRow.Q<Toggle>().SetValueWithoutNotify(QuestDebugMode.Enabled));
            generalGroup.Add(MakeInfoRow("摄像头单帧说明", "每次只拍一帧，不保存不录像"));
            // 语音配置收纳：对话页已微信化（输入条切换 + 按住说话），原
            // “语音（可选）”面板的配置项迁到此处。
            generalGroup.Add(MakeToggleRow("语音常开监听", owner?.VoiceInput?.AlwaysListening ?? false, value =>
            {
                owner?.VoiceInput?.ToggleAlwaysListening();
                RefreshVoiceUi();
                ShowToast(value ? "常开监听已开启" : "常开监听已关闭");
            }));
            generalGroup.Add(MakeButtonRow(
                MakeSmallButton("重启麦克风", false, () =>
                {
                    owner?.VoiceInput?.RestartMonitoring();
                    RefreshVoiceUi();
                    ShowToast("麦克风监控已重启");
                })));
            generalGroup.Add(MakeSegmentedRow("目标帧率",
                new SegmentChoice("30", () => CurrentTargetFps() == 30, () => SetTargetFps(30)),
                new SegmentChoice("60", () => CurrentTargetFps() == 60, () => SetTargetFps(60)),
                new SegmentChoice("120", () => CurrentTargetFps() == 120, () => SetTargetFps(120))));
            generalGroup.Add(MakeSliderRow("音量", AudioListener.volume, value =>
            {
                AudioListener.volume = value;
                PlayerPrefs.SetFloat(QuestQualitySettings.VolumePreferenceKey, value);
                PlayerPrefs.Save();
            }));
            scroll.Add(generalGroup);
        }

        private void FillPerformanceSection(VisualElement scroll)
        {
            var performanceGroup = new VisualElement();
            performanceGroup.AddToClassList("group");
            performanceGroup.AddToClassList("ds-card");
            settingsPerformanceText = new Label("性能采样待刷新");
            settingsPerformanceText.AddToClassList("status-line");
            performanceGroup.Add(settingsPerformanceText);
            scroll.Add(performanceGroup);
        }

        private void FillAboutSection(VisualElement scroll)
        {
            var aboutGroup = new VisualElement();
            aboutGroup.AddToClassList("group");
            aboutGroup.AddToClassList("ds-card");
            aboutGroup.Add(MakeInfoRow("版本", Application.version));
            aboutGroup.Add(MakeInfoRow("设备", string.IsNullOrEmpty(SystemInfo.deviceModel) ? SystemInfo.deviceName : SystemInfo.deviceModel));
            aboutGroup.Add(MakeInfoRow("内存", SystemInfo.systemMemorySize + " MB"));
            scroll.Add(aboutGroup);
        }

        private void FillUpdateSection(VisualElement scroll)
        {
            var updateGroup = new VisualElement();
            updateGroup.AddToClassList("group");
            updateGroup.AddToClassList("ds-card");
            updateStatusLabel = new Label("检查 GitHub Releases 上的新版本");
            updateStatusLabel.AddToClassList("status-line");
            updateGroup.Add(updateStatusLabel);
            updateProgressRow = new VisualElement();
            updateProgressRow.AddToClassList("progress-track");
            updateProgressFill = new VisualElement();
            updateProgressFill.AddToClassList("progress-fill");
            updateProgressFill.style.width = Length.Percent(0);
            updateProgressRow.Add(updateProgressFill);
            updateProgressRow.style.display = DisplayStyle.None;
            updateGroup.Add(updateProgressRow);
            updateGroup.Add(MakeButton("检查更新", true, () => CheckUpdateAsync().Forget("ui.update.check")));
            scroll.Add(updateGroup);
        }

        private void FillLogSection(VisualElement scroll)
        {
            var logGroup = new VisualElement();
            logGroup.AddToClassList("group");
            logGroup.AddToClassList("ds-card");
            settingsLogText = new Label("暂无日志");
            settingsLogText.AddToClassList("status-line");
            logGroup.Add(settingsLogText);
            logGroup.Add(MakeButtonRow(
                MakeSmallButton("刷新日志", false, RefreshLogPreview),
                MakeSmallButton("清空日志", true, () =>
                {
                    debugLog?.Clear();
                    RefreshLogPreview();
                })));
            scroll.Add(logGroup);
        }

        private int CurrentTargetFps()
        {
            return owner?.Quality == null
                ? QuestQualitySettings.ResolveStartupTargetFrameRate()
                : owner.Quality.ApplicationTargetFrameRate;
        }

        private void SetTargetFps(int fps)
        {
            if (owner?.Quality != null)
            {
                owner.Quality.SetUserTargetFrameRate(fps);
            }
            else
            {
                Application.targetFrameRate = fps;
                PlayerPrefs.SetInt(QuestQualitySettings.TargetFpsPreferenceKey, fps);
                PlayerPrefs.Save();
            }
            ShowToast("目标帧率：" + fps);
            RefreshSegments();
        }

        private void HandleQualityChanged(QuestQualityPreset _)
        {
            RefreshSettingsUi();
        }

        private void RefreshSettingsUi()
        {
            if (!built)
            {
                return;
            }
            if (settingsQualityStatus != null)
            {
                var quality = owner?.Quality;
                settingsQualityStatus.text = quality == null
                    ? "画质控制器不可用"
                    : "画质：" + QuestQualitySettings.GetDisplayName(quality.CurrentPreset) +
                      " · 物理：" + QuestQualitySettings.GetPhysicsDisplayName(quality.CurrentPhysicsPreset) +
                      "\n" + quality.Status;
            }
            if (settingsPerformanceText != null && currentTab == Tab.Settings)
            {
                var snapshot = RuntimeDiagnosticsBuilder.Capture(owner);
                settingsPerformanceText.text = RuntimeDiagnosticsFormatter.FormatPerformanceSummary(snapshot.Performance);
            }
            RefreshSegments();
        }

        private void RefreshLogPreview()
        {
            if (settingsLogText == null)
            {
                return;
            }
            settingsLogText.text = debugLog == null ? "诊断日志不可用" : debugLog.GetRecentText(12);
        }

        private async Task CheckUpdateAsync()
        {
            if (updateChecker == null)
            {
                updateChecker = gameObject.GetComponent<BanxiaUpdateChecker>() ?? gameObject.AddComponent<BanxiaUpdateChecker>();
            }
            updateStatusLabel.text = "正在检查 GitHub Releases…";
            updateProgressRow.style.display = DisplayStyle.None;
            var info = await updateChecker.CheckForUpdateAsync();
            if (string.IsNullOrEmpty(info.Version))
            {
                updateStatusLabel.text = "检查失败，请稍后重试";
                return;
            }
            if (!info.HasUpdate)
            {
                updateStatusLabel.text = "已是最新版本（" + Application.version + "）";
                ShowToast("已是最新版本");
                return;
            }
            updateStatusLabel.text = "发现新版本 " + info.Version + "，正在下载安装包…";
            updateProgressFill.style.width = Length.Percent(0f);
            updateProgressRow.style.display = DisplayStyle.Flex;
            string result = await updateChecker.DownloadAndInstallAsync(info, progress =>
            {
                updateProgressFill.style.width = Length.Percent(Mathf.Clamp01(progress) * 100f);
            });
            updateProgressRow.style.display = DisplayStyle.None;
            if (string.IsNullOrEmpty(result))
            {
                updateStatusLabel.text = "已请求系统安装（请在系统弹窗中确认）";
                ShowToast("请确认系统安装弹窗");
            }
            else
            {
                updateStatusLabel.text = result;
                ShowToast("更新流程需要手动确认");
            }
        }

        // ═══════════════════════ Scene mode ═══════════════════════

        private void EnterScene(RuntimeMmdModelInfo target)
        {
            if (enteringScene || modelLoader == null)
            {
                return;
            }
            enteringScene = true;
            EnterSceneAsync(target).Forget("ui.enter-scene");
        }

        private async Task EnterSceneAsync(RuntimeMmdModelInfo target)
        {
            try
            {
                ShowToast(target == null ? "正在恢复上次模型…" : "正在加载：" + target.DisplayName);
                bool loaded;
                if (target != null)
                {
                    var avatar = await modelLoader.LoadFromFileAsync(target.Path, target.PackageRoot);
                    loaded = avatar != null;
                }
                else
                {
                    loaded = await modelLoader.RestoreLastModelAsync();
                    // 无已安装模型时启动器已生成 fallback 角色（HandleLastModelRestoreCompleted），
                    // 直接放行进入场景，同框三模式不因“没装模型”被挡在门外。
                    if (!loaded && owner?.Avatar != null)
                    {
                        loaded = true;
                        if (CoPresenceDirector != null)
                        {
                            CoPresenceDirector.SetAvatar(owner.Avatar.transform);
                        }
                    }
                }
                if (!loaded)
                {
                    ShowToast("模型加载失败，请查看诊断日志");
                    RefreshLogPreview();
                    return;
                }
                if (worldSpaceHost)
                {
                    closeRequested?.Invoke();
                }
                else
                {
                    ApplyMode(UiMode.Scene);
                    var director = owner?.CoPresence;
                    if (director != null)
                    {
                        director.SetAvatar(owner.Avatar != null ? owner.Avatar.transform : null);
                        director.ApplyOnEnterScene();
                    }
                    arPlacedOnce = false;
                    UpdateCoPresenceChrome();
                    if (hud != null)
                    {
                        hud.SetVisible(PlayerPrefs.GetInt(PrefsPrefix + "hud", 0) == 1);
                    }
                }
                RefreshModels(forceInvalidate: false);
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "ui.enter-scene");
                QuestDebugMode.RethrowIfEnabled(exception, "ui.enter-scene");
                ShowToast("进入场景失败：" + exception.Message);
                Debug.LogWarning("[BanxiaUi] Enter scene failed: " + exception, this);
            }
            finally
            {
                enteringScene = false;
            }
        }

        // ═══════════════════════ 同框三模式 ═══════════════════════

        private ICoPresenceDirector CoPresenceDirector => owner?.CoPresence;

        private void HandleCoPresenceFrame()
        {
            var director = CoPresenceDirector;
            if (director == null)
            {
                return;
            }
            if (director.CurrentMode != lastCoPresenceMode)
            {
                // 覆盖一切模式转换（含 AR 权限拒绝后的异步回落），刷新 pill 文案与 chrome。
                lastCoPresenceMode = director.CurrentMode;
                UpdateCoPresenceChrome();
            }
            if (director.VideoCallActive && videoCallChrome != null
                && Time.unscaledTime >= nextCallUiRefreshAt)
            {
                nextCallUiRefreshAt = Time.unscaledTime + 0.5;
                if (videoCallTimerLabel != null)
                {
                    videoCallTimerLabel.text = director.CallDurationText;
                }
                if (videoCallSubtitleLabel != null)
                {
                    var reply = owner?.Conversation?.ReplyText ?? string.Empty;
                    videoCallSubtitleLabel.text = string.IsNullOrWhiteSpace(reply)
                        ? string.Empty
                        : "「" + reply.Trim() + "」";
                    videoCallSubtitleLabel.style.display =
                        string.IsNullOrWhiteSpace(reply) ? DisplayStyle.None : DisplayStyle.Flex;
                }
            }
            if (director.CurrentMode == CoPresenceMode.ArReality && !arPlacedOnce)
            {
                HandleArTapPlacement();
            }
        }

        private void HandleArTapPlacement()
        {
            // 与 PhoneOrbitCamera 同源的 legacy 触摸 API（项目 Active Input Handling = Both）
            if (Input.touchCount == 0)
            {
                return;
            }
            for (int i = 0; i < Input.touchCount; i++)
            {
                var touch = Input.GetTouch(i);
                if (touch.phase != UnityEngine.TouchPhase.Ended || touch.tapCount < 1)
                {
                    continue;
                }
                if (touch.deltaTime <= 0f || touch.deltaTime > 0.6f)
                {
                    continue;
                }
                if (touch.deltaPosition.sqrMagnitude > 24f * 24f)
                {
                    continue;
                }
                if (CoPresenceDirector == null ||
                    !CoPresenceDirector.PlaceAvatarAtScreenPoint(touch.position))
                {
                    continue;
                }
                arPlacedOnce = true;
                if (arPlaceHint != null)
                {
                    arPlaceHint.style.display = DisplayStyle.None;
                }
                ShowToast("已放置 · 拖动移动 · 双指缩放");
                return;
            }
        }

        private void OnSceneModePillPressed()
        {
            var director = CoPresenceDirector;
            if (director == null)
            {
                ShowToast("同框导演不可用");
                return;
            }
            if (director.CurrentMode == CoPresenceMode.VirtualScene)
            {
                ToggleEnvironmentSheet();
            }
            else
            {
                ToggleCoPresenceSheet();
            }
        }

        private void UpdateCoPresenceChrome()
        {
            var director = CoPresenceDirector;
            if (director == null)
            {
                return;
            }
            var modePill = sceneToolbar?.Q<VisualElement>("pill-mode");
            var modeLabel = modePill?.Q<Label>(className: "pill-label");
            if (modeLabel != null)
            {
                modeLabel.text = director.CurrentMode == CoPresenceMode.VirtualScene
                    ? "环境"
                    : "模式";
            }
            bool inScene = mode == UiMode.Scene;
            bool videoCall = director.CurrentMode == CoPresenceMode.VideoCall;
            // 通话/AR chrome 是惰性创建的：以记忆模式直接进场景时从未打开过
            // Sheet，overlays 不存在 → 必须在这里确保已建，否则 chrome 永不显示。
            if (inScene && videoCall)
            {
                EnsureCoPresenceOverlays();
            }
            if (sceneToolbar != null && inScene)
            {
                // 视频通话底部由通话控件行接管，隐藏场景工具条避免双层堆叠。
                sceneToolbar.style.display = videoCall ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (videoCallChrome != null)
            {
                videoCallChrome.style.display = inScene && videoCall ? DisplayStyle.Flex : DisplayStyle.None;
            }
            if (arPlaceHint != null)
            {
                arPlaceHint.style.display =
                    inScene && !videoCall
                    && director.CurrentMode == CoPresenceMode.ArReality && !arPlacedOnce
                        ? DisplayStyle.Flex
                        : DisplayStyle.None;
            }
            // 兜底（HOME 恢复 / 模式切换 / 选卡后）：弹层未打开时，任何 chrome 刷新
            // 都不得残留遮罩或控件让位状态——模态所有权只在 sheet 打开期间成立。
            bool sheetOpen = coPresenceSheet != null && coPresenceSheet.style.display == DisplayStyle.Flex;
            if (!sheetOpen)
            {
                if (coPresenceBackdrop != null)
                {
                    coPresenceBackdrop.style.display = DisplayStyle.None;
                }
                if (callControls != null)
                {
                    callControls.style.display = DisplayStyle.Flex;
                }
            }
            PushChromeInsets();
        }

        /// <summary>
        /// 将 UI Toolkit 面板逻辑坐标映射到主相机像素坐标。
        /// PanelSettings 使用参考分辨率，worldBound 不能直接与 Screen.height 混算。
        /// </summary>
        private void PushChromeInsets()
        {
            var director = CoPresenceDirector;
            if (director == null || !director.VideoCallActive ||
                panelRoot == null || callTopChrome == null || callControls == null ||
                callTopChrome.resolvedStyle.display != DisplayStyle.Flex ||
                callControls.resolvedStyle.display != DisplayStyle.Flex)
            {
                return;
            }

            var panelBounds = panelRoot.worldBound;
            var topBounds = callTopChrome.worldBound;
            var controlsBounds = callControls.worldBound;
            if (panelBounds.height <= 1f || topBounds.height <= 1f || controlsBounds.height <= 1f)
            {
                return;
            }

            var camera = director.MainCamera;
            var screenHeight = camera != null ? camera.pixelHeight : Screen.height;
            if (screenHeight <= 1)
            {
                return;
            }

            // worldBound 使用面板逻辑单位；相机输入使用物理像素。
            var panelToScreen = screenHeight / panelBounds.height;
            var topPx = Mathf.Clamp(
                (topBounds.yMax - panelBounds.yMin) * panelToScreen, 0f, screenHeight);
            var bottomPx = Mathf.Clamp(
                (controlsBounds.yMin - panelBounds.yMin) * panelToScreen, 0f, screenHeight);
            if (bottomPx <= topPx)
            {
                return;
            }
            if (Mathf.Abs(topPx - lastChromeTopPx) <= 0.5f &&
                Mathf.Abs(bottomPx - lastChromeBottomPx) <= 0.5f)
            {
                return;
            }

            lastChromeTopPx = topPx;
            lastChromeBottomPx = bottomPx;
            director.SetChromeInsets(topPx, bottomPx);
        }

        private void ToggleCoPresenceSheet()
        {
            EnsureCoPresenceOverlays();
            bool show = coPresenceSheet == null || coPresenceSheet.style.display != DisplayStyle.Flex;
            if (show)
            {
                ShowCoPresenceSheet(RebuildModeCards);
            }
            else
            {
                HideCoPresenceSheets();
            }
        }

        /// <summary>强制显示模式卡视图（pill 与「换种同框方式」共用）。</summary>
        private void ShowModeCards()
        {
            ShowCoPresenceSheet(RebuildModeCards);
        }

        private void ToggleEnvironmentSheet()
        {
            EnsureCoPresenceOverlays();
            var director = CoPresenceDirector;
            if (director == null)
            {
                return;
            }
            bool show = coPresenceSheet == null || coPresenceSheet.style.display != DisplayStyle.Flex;
            if (show)
            {
                ShowCoPresenceSheet(RebuildEnvironmentChips);
            }
            else
            {
                HideCoPresenceSheets();
            }
        }

        /// <summary>
        /// 打开弹层并建立模态所有权状态：通话控件让位、遮罩显示并可点击、sheet 置顶。
        /// 三个打开入口（模式 pill / 环境 pill / 「换种同框方式」）收敛于此。
        /// </summary>
        private void ShowCoPresenceSheet(Action rebuild)
        {
            EnsureCoPresenceOverlays();
            if (coPresenceSheet == null)
            {
                return;
            }
            // 先清残留（还原控件 / 隐藏遮罩 / 隐藏 sheet），再按目标内容填充。
            HideCoPresenceSheets();
            rebuild?.Invoke();
            // 模态所有权（INV-5）：控件不可见不可点，遮罩接管点击，sheet 在遮罩之上
            // （Add 顺序已在 EnsureCoPresenceOverlays 中固定为 chrome < scrim < sheet）。
            if (callControls != null)
            {
                callControls.style.display = DisplayStyle.None;
            }
            if (coPresenceBackdrop != null)
            {
                coPresenceBackdrop.style.display = DisplayStyle.Flex;
            }
            coPresenceSheet.style.display = DisplayStyle.Flex;
        }

        private void HideCoPresenceSheets()
        {
            if (coPresenceSheet != null)
            {
                coPresenceSheet.style.display = DisplayStyle.None;
            }
            if (coPresenceBackdrop != null)
            {
                coPresenceBackdrop.style.display = DisplayStyle.None;
            }
            // 关闭路径统一还原通话控件（回到 CallOnly 态）。
            if (callControls != null)
            {
                callControls.style.display = DisplayStyle.Flex;
            }
        }

        private void EnsureCoPresenceOverlays()
        {
            if (coPresenceSheet != null || shellRoot == null)
            {
                return;
            }
            coPresenceBackdrop = new VisualElement { name = "copresence-backdrop" };
            coPresenceBackdrop.AddToClassList("cp-backdrop");
            coPresenceBackdrop.style.display = DisplayStyle.None;
            coPresenceBackdrop.RegisterCallback<ClickEvent>(_ => HideCoPresenceSheets());
            shellRoot.Add(coPresenceBackdrop);

            coPresenceSheet = new VisualElement { name = "copresence-sheet" };
            coPresenceSheet.AddToClassList("cp-sheet");
            coPresenceSheet.style.display = DisplayStyle.None;
            shellRoot.Add(coPresenceSheet);

            // AR 放置提示（不拦截点击）
            arPlaceHint = new VisualElement { name = "ar-place-hint", pickingMode = PickingMode.Ignore };
            arPlaceHint.AddToClassList("ar-hint");
            var hintTitle = new Label("点按地面，把她放进来");
            hintTitle.AddToClassList("ar-hint-title");
            var hintSub = new Label("拖动移动 · 双指缩放 · 长按环绕");
            hintSub.AddToClassList("ar-hint-sub");
            arPlaceHint.Add(hintTitle);
            arPlaceHint.Add(hintSub);
            arPlaceHint.style.display = DisplayStyle.None;
            shellRoot.Add(arPlaceHint);

            // 视频通话 chrome。容器全屏铺开只为 space-between 定位胶囊与控件行，
            // 自身必须忽略点击：否则会盖在模式卡 Sheet 上拦截全部卡片点击
            // （chrome 在 shellRoot 中晚于 sheet 加入，层级更高）。
            videoCallChrome = new VisualElement { name = "video-call-chrome", pickingMode = PickingMode.Ignore };
            videoCallChrome.AddToClassList("call-chrome");
            videoCallChrome.style.display = DisplayStyle.None;

            callTopChrome = new VisualElement();
            var callTop = callTopChrome;
            callTop.AddToClassList("call-top");
            var callDot = new VisualElement();
            callDot.AddToClassList("call-dot");
            callTop.Add(callDot);
            var callName = new Label("伴夏");
            callName.AddToClassList("call-name");
            callTop.Add(callName);
            videoCallTimerLabel = new Label("00:00");
            videoCallTimerLabel.AddToClassList("call-timer");
            callTop.Add(videoCallTimerLabel);
            videoCallChrome.Add(callTop);
            callTopChrome.RegisterCallback<GeometryChangedEvent>(_ => PushChromeInsets());

            videoCallSubtitleLabel = new Label(string.Empty);
            videoCallSubtitleLabel.AddToClassList("call-subtitle");
            videoCallSubtitleLabel.style.display = DisplayStyle.None;
            videoCallChrome.Add(videoCallSubtitleLabel);

            callControls = new VisualElement();
            callControls.AddToClassList("call-controls");
            var hangup = new Label("挂断");
            hangup.AddToClassList("call-btn");
            hangup.AddToClassList("call-hangup");
            hangup.RegisterCallback<ClickEvent>(_ => ReturnToMenu());
            callControls.Add(hangup);
            var switchMode = new Label("模式");
            switchMode.AddToClassList("call-btn");
            switchMode.RegisterCallback<ClickEvent>(_ => ToggleCoPresenceSheet());
            callControls.Add(switchMode);
            var backChat = new Label("去聊天");
            backChat.AddToClassList("call-btn");
            backChat.RegisterCallback<ClickEvent>(_ => ReturnToMenu());
            callControls.Add(backChat);
            videoCallChrome.Add(callControls);
            callControls.RegisterCallback<GeometryChangedEvent>(_ => PushChromeInsets());

            shellRoot.Add(videoCallChrome);

            // 层级所有权（INV-5）：chrome(底) < arHint < 遮罩(scrim) < sheet(顶) < toast。
            // 旧实现里 chrome 最晚 Add、层级最高，会在弹层打开时盖住卡片（红丸压卡）。
            // 这里把 chrome 压到底，再把遮罩与 sheet 抬到其上；遮罩复用既有
            // coPresenceBackdrop（不另建 call-scrim），保证弹层打开时通话 chrome 不盖卡。
            videoCallChrome.SendToBack();
            arPlaceHint.PlaceBehind(coPresenceBackdrop);
            coPresenceBackdrop.PlaceBehind(coPresenceSheet);
            // Toast 在 UXML 里早于运行时 overlay 加入，会被弹层盖住；抬到最顶。
            toast?.BringToFront();
        }

        private void RebuildModeCards()
        {
            var director = CoPresenceDirector;
            if (coPresenceSheet == null || director == null)
            {
                return;
            }
            coPresenceSheet.Clear();
            var grabber = new VisualElement();
            grabber.AddToClassList("cp-grabber");
            grabber.RegisterCallback<ClickEvent>(_ => HideCoPresenceSheets());
            coPresenceSheet.Add(grabber);
            var title = new Label("和她同框");
            title.AddToClassList("cp-title");
            coPresenceSheet.Add(title);

            var modes = new (string title, string tag, string desc, CoPresenceMode value)[]
            {
                ("同框现实", "AR · 相机取景", "点按地面，把她放进你的房间", CoPresenceMode.ArReality),
                ("虚拟场景", "伪 AR · 虚拟环境", "夜街 / 星空 / 卧室 / 海边", CoPresenceMode.VirtualScene),
                ("视频通话", "半身 · 通话感", "胸像出镜 · 字幕 · 通话计时", CoPresenceMode.VideoCall),
            };
            foreach (var entry in modes)
            {
                bool current = director.CurrentMode == entry.value;
                bool disabled = entry.value == CoPresenceMode.ArReality
                    && !director.ArCameraAvailable;
                var card = new VisualElement();
                card.AddToClassList("cp-card");
                if (current)
                {
                    card.AddToClassList("current");
                }
                if (disabled)
                {
                    card.AddToClassList("disabled");
                }
                var head = new VisualElement();
                head.AddToClassList("cp-card-head");
                var cardTitle = new Label(entry.title);
                cardTitle.AddToClassList("cp-card-title");
                head.Add(cardTitle);
                var cardTag = new Label(entry.tag);
                cardTag.AddToClassList("cp-card-tag");
                head.Add(cardTag);
                card.Add(head);
                var cardDesc = new Label(entry.desc);
                cardDesc.AddToClassList("cp-card-desc");
                card.Add(cardDesc);
                if (!disabled)
                {
                    var target = entry.value;
                    card.RegisterCallback<ClickEvent>(_ =>
                    {
                        director.SwitchMode(target);
                        HideCoPresenceSheets();
                        UpdateCoPresenceChrome();
                        ShowToast("已切换：" + entry.title);
                    });
                }
                coPresenceSheet.Add(card);
            }
        }

        private void RebuildEnvironmentChips()
        {
            var director = CoPresenceDirector;
            if (coPresenceSheet == null || director == null)
            {
                return;
            }
            coPresenceSheet.Clear();
            var grabber = new VisualElement();
            grabber.AddToClassList("cp-grabber");
            grabber.RegisterCallback<ClickEvent>(_ => HideCoPresenceSheets());
            coPresenceSheet.Add(grabber);
            var title = new Label("虚拟环境");
            title.AddToClassList("cp-title");
            coPresenceSheet.Add(title);

            var envs = new (string name, VirtualEnvironment value)[]
            {
                ("夜街", VirtualEnvironment.NightStreet),
                ("星空", VirtualEnvironment.StarrySky),
                ("卧室", VirtualEnvironment.Bedroom),
                ("海边", VirtualEnvironment.Seaside),
            };
            var row = new VisualElement();
            row.AddToClassList("cp-chip-row");
            foreach (var entry in envs)
            {
                bool current = director.CurrentEnvironment == entry.value;
                var chip = new Label(entry.name);
                chip.AddToClassList("cp-chip");
                if (current)
                {
                    chip.AddToClassList("current");
                }
                var target = entry.value;
                chip.RegisterCallback<ClickEvent>(_ =>
                {
                    director.SwitchEnvironment(target);
                    RebuildEnvironmentChips();
                });
                row.Add(chip);
            }
            coPresenceSheet.Add(row);
            var note = new Label("环境光照自动匹配角色亮度 · 物理与画质跟随设置");
            note.AddToClassList("cp-note");
            coPresenceSheet.Add(note);
            // 虚拟场景是默认模式，从这里必须能到达模式选择，否则三模式成死路。
            // 注意：必须强制切换到模式卡视图（toggle 语义在 sheet 已开时会变成纯关闭）。
            var more = new Label("换种同框方式");
            more.AddToClassList("cp-more");
            more.RegisterCallback<ClickEvent>(_ => ShowModeCards());
            coPresenceSheet.Add(more);
        }

        private void ReturnToMenu()
        {
            owner?.CoPresence?.Suspend();
            HideCoPresenceSheets();
            ApplyMode(UiMode.Menu);
            // 必须在 ApplyMode 之后：chrome 显隐依赖 mode == Scene，
            // 早于 ApplyMode 调用会把通话 chrome 误判为 inScene 而重新显示。
            UpdateCoPresenceChrome();
            RefreshModels(forceInvalidate: true);
            RefreshLogPreview();
        }

        private void ToggleMoveMode()
        {
            var orbit = owner?.OrbitCamera;
            if (orbit == null)
            {
                ShowToast("相机控制器不可用");
                return;
            }
            orbit.SingleFingerMovesAvatar = !orbit.SingleFingerMovesAvatar;
            ShowToast(orbit.SingleFingerMovesAvatar ? "移动模式：单指拖动角色" : "移动模式已关闭");
            RefreshSceneToolbarState();
        }

        private void ResetAvatar()
        {
            owner?.SendCommand(new AvatarCommand { name = "reset" });
            ReframeCamera();
            ShowToast("角色已重置");
        }

        private void ReframeCamera()
        {
            owner?.OrbitCamera?.Reframe();
        }

        private void ToggleHud()
        {
            if (hud == null)
            {
                return;
            }
            hud.SetVisible(!hud.IsVisible);
            ShowToast(hud.IsVisible ? "HUD 已显示" : "HUD 已隐藏");
        }

        private void RefreshSceneToolbarState()
        {
            if (movePill == null)
            {
                return;
            }
            movePill.EnableInClassList("active", owner?.OrbitCamera != null && owner.OrbitCamera.SingleFingerMovesAvatar);
        }

        // ═══════════════════════ Control factories ═══════════════════════

        private static VisualElement MakeNavBar(string title, string subtitle)
        {
            var bar = new VisualElement();
            bar.AddToClassList("nav-bar");
            var titleLabel = new Label(title);
            titleLabel.AddToClassList("nav-title");
            bar.Add(titleLabel);
            var subtitleLabel = new Label(subtitle);
            subtitleLabel.AddToClassList("nav-subtitle");
            bar.Add(subtitleLabel);
            return bar;
        }

        private static Label MakeGroupHeader(string title)
        {
            var header = new Label(title);
            header.AddToClassList("ds-section__title");
            header.AddToClassList("group-header"); // 兼容既有边距微调
            return header;
        }

        private static VisualElement MakeButton(string text, bool primary, Action click, bool danger = false)
        {
            var button = new Button(() => click?.Invoke()) { text = text };
            button.AddToClassList("ds-btn");
            button.AddToClassList("ds-btn--block");
            if (primary)
            {
                button.AddToClassList("ds-btn--primary");
            }
            else if (danger)
            {
                button.AddToClassList("ds-btn--danger");
            }
            else
            {
                button.AddToClassList("ds-btn--secondary");
            }
            return button;
        }

        private static VisualElement MakeSmallButton(string text, bool danger, Action click)
        {
            var button = new Button(() => click?.Invoke()) { text = text };
            button.AddToClassList("ds-btn");
            button.AddToClassList("ds-btn--sm");
            button.AddToClassList(danger ? "ds-btn--danger" : "ds-btn--secondary");
            return button;
        }

        private static VisualElement MakeButtonRow(params VisualElement[] buttons)
        {
            var row = new VisualElement();
            row.AddToClassList("btn-row");
            foreach (var button in buttons)
            {
                // M5：等分修复。旧写法只设 flexGrow，Yoga 的 basis 仍是内容宽，
                // 两个按钮会按“内容+均分剩余”而不是“各半”分布；基础布局（ds-btn
                // 大按钮）下第二枚被挤成 144px 桩、标签不可见且行右溢出。
                // basis 归零后 grow 才是真正的等分。
                button.style.flexGrow = 1f;
                button.style.flexShrink = 1f;
                button.style.flexBasis = 0f;
                button.style.minWidth = 0f;
                row.Add(button);
            }
            return row;
        }

        private static VisualElement MakeCardAction(string text, Action click)
        {
            var action = new VisualElement();
            action.AddToClassList("card-action");
            var label = new Label(text);
            label.AddToClassList("card-action-label");
            action.Add(label);
            action.RegisterCallback<ClickEvent>(_ => click?.Invoke());
            return action;
        }

        private static VisualElement MakeNumpadKey(string text, Action click)
        {
            var key = new VisualElement();
            key.AddToClassList("numpad-key");
            var label = new Label(text);
            label.AddToClassList("numpad-key-label");
            key.Add(label);
            key.RegisterCallback<ClickEvent>(_ => click?.Invoke());
            return key;
        }

        private static VisualElement MakeInfoRow(string label, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var labelElement = new Label(label);
            labelElement.AddToClassList("row-label");
            var valueElement = new Label(value);
            valueElement.AddToClassList("row-value");
            row.Add(labelElement);
            row.Add(valueElement);
            return row;
        }

        private static VisualElement MakeElementRow(string label, VisualElement element)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var labelElement = new Label(label);
            labelElement.AddToClassList("row-label");
            row.Add(labelElement);
            element.style.flexGrow = 1f;
            // 对抗内容最小宽度：TextField 内部长文本会把自身 min-width 撑到
            // 全文本宽（Yoga min-width:auto），flex-shrink 无法收缩，行溢出。
            element.style.flexShrink = 1f;
            element.style.minWidth = 0f;
            element.style.overflow = Overflow.Hidden;
            row.Add(element);
            return row;
        }

        private static VisualElement MakeToggleRow(string label, bool initial, Action<bool> changed)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var labelElement = new Label(label);
            labelElement.AddToClassList("row-label");
            row.Add(labelElement);
            // ds-toggle：库的标准开关（Toggle + 手写 knob 子元素，:checked 驱动滑块位移）
            var toggle = new Toggle { value = initial };
            toggle.AddToClassList("ds-toggle");
            var knob = new VisualElement();
            knob.AddToClassList("ds-toggle__knob");
            toggle.Add(knob);
            toggle.RegisterValueChangedCallback(evt => changed?.Invoke(evt.newValue));
            toggle.style.flexShrink = 0f;
            row.Add(toggle);
            return row;
        }

        private VisualElement MakeSegmentedRow(string label, params SegmentChoice[] options)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var labelElement = new Label(label);
            labelElement.AddToClassList("row-label");
            row.Add(labelElement);
            var seg = new VisualElement();
            seg.AddToClassList("ds-tabs");
            seg.AddToClassList("seg"); // 复用既有 flexGrow 布局微调
            var items = new List<VisualElement>();
            Action refresh = () =>
            {
                for (int i = 0; i < options.Length; i++)
                {
                    bool selected = false;
                    try
                    {
                        selected = options[i].IsSelected?.Invoke() ?? false;
                    }
                    catch (NullReferenceException exception)
                    {
                        QuestDebugMode.Report(exception, "ui.settings.selection-probe");
                    }
                    items[i].EnableInClassList("is-active", selected);
                }
            };
            for (int i = 0; i < options.Length; i++)
            {
                int captured = i;
                var item = new Button { text = options[i].Label };
                item.AddToClassList("ds-tab");
                item.RegisterCallback<ClickEvent>(_ =>
                {
                    options[captured].Activate?.Invoke();
                    refresh();
                });
                items.Add(item);
                seg.Add(item);
            }
            segmentRefreshers.Add(refresh);
            refresh();
            row.Add(seg);
            return row;
        }

        private static VisualElement MakeSliderRow(string label, float initial, Action<float> changed)
        {
            var row = new VisualElement();
            row.AddToClassList("row");
            var labelElement = new Label(label);
            labelElement.AddToClassList("row-label");
            row.Add(labelElement);
            var slider = new Slider(0f, 1f) { value = Mathf.Clamp01(initial) };
            slider.style.flexGrow = 1f;
            slider.RegisterValueChangedCallback(evt => changed?.Invoke(evt.newValue));
            row.Add(slider);
            return row;
        }

        private void RefreshSegments()
        {
            foreach (var refresher in segmentRefreshers)
            {
                refresher?.Invoke();
            }
        }

        private void ShowToast(string message)
        {
            if (string.IsNullOrWhiteSpace(message) || toast == null || toastLabel == null)
            {
                return;
            }
            toastLabel.text = message;
            toast.style.display = DisplayStyle.Flex;
            toastHideAt = Time.unscaledTime + ToastSeconds;
        }
    }
}

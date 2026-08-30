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
        private Action closeRequested;

        private UIDocument document;
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
        private VisualElement chatConversationCard;
        private Label connectionBadge;
        private Label pairingStatusLabel;
        private TextField pairingServerField;
        private Label pairingCodeLabel;
        private VisualElement pairingDots;
        private Label chatStateLabel;
        private ScrollView chatTranscript;
        private TextField chatInputField;
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
        private string lastPlayingActionId = string.Empty;

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
            if (mode == UiMode.Scene && Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            {
                ReturnToMenu();
                return;
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
            var panelRoot = document.rootVisualElement;
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
                mainUi.style.backgroundColor = new Color(0f, 0f, 0f, 1f);
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
            AddToolbarPill(toolbar, "pill-reset", "重置");
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
            var iconLabel = new Label(icon);
            iconLabel.AddToClassList("tab-icon");
            var textLabel = new Label(text);
            textLabel.AddToClassList("tab-text");
            item.Add(iconLabel);
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
                _ = RefreshActionsAsync();
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
        }

        private void BindSceneToolbar()
        {
            sceneToolbar.Q<VisualElement>("pill-back")?.RegisterCallback<ClickEvent>(_ => ReturnToMenu());
            movePill = sceneToolbar.Q<VisualElement>("pill-move");
            movePill?.RegisterCallback<ClickEvent>(_ => ToggleMoveMode());
            sceneToolbar.Q<VisualElement>("pill-reset")?.RegisterCallback<ClickEvent>(_ => ResetAvatar());
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

            page.Add(scroll);
            tabPages[Tab.Companion] = page;
            content.Add(page);
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
            _ = RefreshActionsAsync();
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
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return "PMX · " + sizeText;
        }

        // ═══════════════════════ Chat / backend page ═══════════════════════

        private void BuildChatPage()
        {
            var page = new VisualElement { style = { flexGrow = 1f } };
            page.Add(MakeNavBar("对话", "AstrBot 配对、文字与语音"));
            var scroll = new ScrollView();
            scroll.AddToClassList("scroll");

            connectionBadge = new Label("○ 未连接");
            connectionBadge.AddToClassList("status-line");
            scroll.Add(connectionBadge);

            chatPairingCard = new VisualElement();
            chatPairingCard.Add(MakeGroupHeader("绑定后端"));
            var pairingGroup = new VisualElement();
            pairingGroup.AddToClassList("group");
            pairingServerField = new TextField("服务器域名 / IP:端口");
            pairingServerField.AddToClassList("field");
            pairingGroup.Add(MakeElementRow("服务器", pairingServerField));
            pairingGroup.Add(MakeToggleRow("允许私网 HTTP（局域网测试）", owner?.Pairing?.PrivateHttpAllowed ?? false, value =>
            {
                owner?.Pairing?.SetPrivateHttpAllowed(value);
                RefreshConnectionUi();
            }));
            chatPairingCard.Add(pairingGroup);

            pairingCodeLabel = new Label("_ _ _   _ _ _");
            pairingCodeLabel.AddToClassList("status-line");
            chatPairingCard.Add(pairingCodeLabel);
            pairingDots = new VisualElement();
            pairingDots.AddToClassList("code-dots");
            chatPairingCard.Add(pairingDots);
            BuildPairingNumpad(chatPairingCard);
            pairingStatusLabel = new Label(string.Empty);
            pairingStatusLabel.AddToClassList("status-line");
            chatPairingCard.Add(pairingStatusLabel);
            chatPairingCard.Add(MakeButton("连接后端", true, TryPair));
            chatPairingCard.Add(MakeButton("重新连接", false, () =>
            {
                owner?.AstrBot?.ReloadConfiguration();
                ShowToast("正在重新连接后端");
            }));
            chatPairingCard.Add(MakeButton("解除绑定", false, ClearPairingConfiguration, danger: true));
            scroll.Add(chatPairingCard);

            chatConversationCard = new VisualElement();
            chatStateLabel = new Label("会话待命");
            chatStateLabel.AddToClassList("status-line");
            chatConversationCard.Add(chatStateLabel);
            chatTranscript = new ScrollView();
            chatTranscript.AddToClassList("chat-scroll");
            chatConversationCard.Add(chatTranscript);
            AddQuickPhrases(chatConversationCard);
            AddChatInputBar(chatConversationCard);
            AddVoiceControls(chatConversationCard);
            scroll.Add(chatConversationCard);

            page.Add(scroll);
            tabPages[Tab.Chat] = page;
            content.Add(page);
            RefreshPairingCodeDisplay();
        }

        private void BuildPairingNumpad(VisualElement parent)
        {
            var pad = new VisualElement();
            pad.AddToClassList("numpad");
            for (int i = 1; i <= 9; i++)
            {
                int digit = i;
                pad.Add(MakeNumpadKey(digit.ToString(), () => AppendPairingDigit(digit.ToString())));
            }
            pad.Add(MakeNumpadKey("清", ClearPairingCode));
            pad.Add(MakeNumpadKey("0", () => AppendPairingDigit("0")));
            pad.Add(MakeNumpadKey("退", RemovePairingDigit));
            parent.Add(pad);
        }

        private void AddQuickPhrases(VisualElement parent)
        {
            var row = new VisualElement();
            row.AddToClassList("chips-row");
            foreach (var phrase in new[] { "你好", "你是谁", "现在几点", "还记得我吗", "跳个舞", "链路测试" })
            {
                string captured = phrase;
                row.Add(MakeChip(captured, () =>
                {
                    if (chatInputField != null)
                    {
                        chatInputField.value = captured;
                    }
                }));
            }
            parent.Add(row);
        }

        private void AddChatInputBar(VisualElement parent)
        {
            var bar = new VisualElement();
            bar.AddToClassList("chat-input-bar");
            chatInputField = new TextField { multiline = false };
            chatInputField.AddToClassList("field");
            bar.Add(chatInputField);
            var camera = new VisualElement();
            camera.AddToClassList("chat-camera");
            var cameraLabel = new Label("拍");
            cameraLabel.AddToClassList("chat-camera-label");
            camera.Add(cameraLabel);
            camera.RegisterCallback<ClickEvent>(_evt => { _ = SendChatWithCameraFrameAsync(); });
            bar.Add(camera);
            var send = new VisualElement();
            send.AddToClassList("chat-send");
            var label = new Label("发");
            label.AddToClassList("chat-send-label");
            send.Add(label);
            send.RegisterCallback<ClickEvent>(_ => SendChatText());
            bar.Add(send);
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

        private void AddVoiceControls(VisualElement parent)
        {
            parent.Add(MakeGroupHeader("语音"));
            var group = new VisualElement();
            group.AddToClassList("group");
            group.Add(MakeToggleRow("常开监听", owner?.VoiceInput?.AlwaysListening ?? false, value =>
            {
                owner?.VoiceInput?.ToggleAlwaysListening();
                RefreshVoiceUi();
            }));
            group.Add(MakeButtonRow(
                MakeSmallButton("开始/发送语音", false, () =>
                {
                    owner?.VoiceInput?.ToggleRecording();
                    RefreshVoiceUi();
                }),
                MakeSmallButton("取消本轮", false, CancelCurrentTurn),
                MakeSmallButton("重启麦克风", false, () => owner?.VoiceInput?.RestartMonitoring())));
            group.Add(MakeButtonRow(
                MakeSmallButton("打断回复", false, () => owner?.Conversation?.Interrupt()),
                MakeSmallButton("暂停动作", false, () => owner?.SendCommand(new AvatarCommand { name = "toggle_pause" }))));
            voiceStatusLabel = new Label("麦克风待命");
            voiceStatusLabel.AddToClassList("status-line");
            group.Add(voiceStatusLabel);
            parent.Add(group);
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
            if (chatConversationCard != null)
            {
                chatConversationCard.style.display = connected ? DisplayStyle.Flex : DisplayStyle.None;
            }
            var pairing = owner.Pairing;
            if (pairingStatusLabel != null)
            {
                var bridge = BanxiaUiText.LocalizeBridgeStatus(owner.AstrBot?.Status ?? string.Empty);
                var pairingText = BanxiaUiText.LocalizePairingStatus(pairing?.Status ?? "Pairing controller offline");
                var modeText = pairing != null && pairing.PrivateHttpAllowed ? "私网 HTTP 已允许" : "HTTPS 模式";
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
        }

        private void SendChatText()
        {
            var text = chatInputField?.value?.Trim() ?? string.Empty;
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
            chatInputField.SetValueWithoutNotify(string.Empty);
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

        private void CancelCurrentTurn()
        {
            if (owner?.VoiceInput != null && owner.VoiceInput.IsRecording)
            {
                owner.VoiceInput.CancelRecording();
            }
            else
            {
                owner?.Conversation?.Interrupt();
            }
            RefreshVoiceUi();
        }

        private void RefreshVoiceUi()
        {
            if (voiceStatusLabel == null || owner?.VoiceInput == null)
            {
                return;
            }
            var voice = owner.VoiceInput;
            voiceStatusLabel.text = "麦克风：" + voice.Status + "\n" +
                                    "监听 " + (voice.IsMonitoring ? "开" : "关") +
                                    " · 常开 " + (voice.AlwaysListening ? "开" : "关") +
                                    " · 录音 " + (voice.IsRecording ? "中" : "否") +
                                    " · 电平 " + voice.InputLevel.ToString("F3");
        }

        // ═══════════════════════ Actions page ═══════════════════════

        private void BuildActionsPage()
        {
            var page = new VisualElement { style = { flexGrow = 1f } };
            page.Add(MakeNavBar("动作", "外部 VMD、待机与表情"));
            var scroll = new ScrollView();
            scroll.AddToClassList("scroll");

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
                MakeSmallButton("刷新动作", false, () => _ = RefreshActionsAsync()),
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
            card.Add(MakeCardAction("播放", () => _ = PlayOrStopAction(actionId)));
            card.Add(MakeSmallButton("删除", true, () => _ = DeleteAction(actionId)));
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
            page.Add(MakeNavBar("设置", "画质、诊断与在线更新"));
            var scroll = new ScrollView();
            scroll.AddToClassList("scroll");

            scroll.Add(MakeGroupHeader("画质与物理"));
            var qualityGroup = new VisualElement();
            qualityGroup.AddToClassList("group");
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

            scroll.Add(MakeGroupHeader("通用"));
            var generalGroup = new VisualElement();
            generalGroup.AddToClassList("group");
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
            generalGroup.Add(MakeToggleRow("摄像头单帧（拍给 TA 看）", PlayerPrefs.GetInt(PrefsPrefix + "camera", 0) == 1, value =>
            {
                PlayerPrefs.SetInt(PrefsPrefix + "camera", value ? 1 : 0);
                PlayerPrefs.Save();
                // 授权能力化：默认关；开启即视为授予"按请求拍单帧"能力，
                // 关闭后拍摄入口直接拒绝。
                ShowToast(value ? "摄像头单帧已开启：对话页「拍」按钮可用" : "摄像头单帧已关闭");
            }));
            generalGroup.Add(MakeInfoRow("摄像头单帧说明", "每次只拍一帧，不保存不录像"));
            generalGroup.Add(MakeSegmentedRow("目标帧率",
                new SegmentChoice("30", () => Application.targetFrameRate == 30, () => SetTargetFps(30)),
                new SegmentChoice("60", () => Application.targetFrameRate == 60 || Application.targetFrameRate <= 0, () => SetTargetFps(60)),
                new SegmentChoice("120", () => Application.targetFrameRate == 120, () => SetTargetFps(120))));
            generalGroup.Add(MakeSliderRow("音量", AudioListener.volume, value => AudioListener.volume = value));
            scroll.Add(generalGroup);

            scroll.Add(MakeGroupHeader("设备性能"));
            var performanceGroup = new VisualElement();
            performanceGroup.AddToClassList("group");
            settingsPerformanceText = new Label("性能采样待刷新");
            settingsPerformanceText.AddToClassList("status-line");
            performanceGroup.Add(settingsPerformanceText);
            scroll.Add(performanceGroup);

            scroll.Add(MakeGroupHeader("关于"));
            var aboutGroup = new VisualElement();
            aboutGroup.AddToClassList("group");
            aboutGroup.Add(MakeInfoRow("版本", Application.version));
            aboutGroup.Add(MakeInfoRow("设备", string.IsNullOrEmpty(SystemInfo.deviceModel) ? SystemInfo.deviceName : SystemInfo.deviceModel));
            aboutGroup.Add(MakeInfoRow("内存", SystemInfo.systemMemorySize + " MB"));
            scroll.Add(aboutGroup);

            scroll.Add(MakeGroupHeader("软件更新"));
            var updateGroup = new VisualElement();
            updateGroup.AddToClassList("group");
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
            updateGroup.Add(MakeButton("检查更新", true, () => _ = CheckUpdateAsync()));
            scroll.Add(updateGroup);

            scroll.Add(MakeGroupHeader("运行诊断"));
            var logGroup = new VisualElement();
            logGroup.AddToClassList("group");
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

            page.Add(scroll);
            tabPages[Tab.Settings] = page;
            content.Add(page);
        }

        private void SetTargetFps(int fps)
        {
            Application.targetFrameRate = fps;
            PlayerPrefs.SetInt(PrefsPrefix + "fps", fps);
            PlayerPrefs.Save();
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
            _ = EnterSceneAsync(target);
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
                    if (hud != null)
                    {
                        hud.SetVisible(PlayerPrefs.GetInt(PrefsPrefix + "hud", 0) == 1);
                    }
                }
                RefreshModels(forceInvalidate: false);
            }
            catch (Exception exception)
            {
                ShowToast("进入场景失败：" + exception.Message);
                Debug.LogWarning("[BanxiaUi] Enter scene failed: " + exception, this);
            }
            finally
            {
                enteringScene = false;
            }
        }

        private void ReturnToMenu()
        {
            ApplyMode(UiMode.Menu);
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
            header.AddToClassList("group-header");
            return header;
        }

        private static VisualElement MakeButton(string text, bool primary, Action click, bool danger = false)
        {
            var button = new VisualElement();
            button.AddToClassList("btn");
            if (primary)
            {
                button.AddToClassList("btn-primary");
            }
            if (danger)
            {
                button.AddToClassList("btn-danger");
            }
            var label = new Label(text);
            label.AddToClassList("btn-label");
            button.Add(label);
            button.RegisterCallback<ClickEvent>(_ => click?.Invoke());
            return button;
        }

        private static VisualElement MakeSmallButton(string text, bool danger, Action click)
        {
            var button = new VisualElement();
            button.AddToClassList("btn-small");
            if (danger)
            {
                button.AddToClassList("btn-danger");
            }
            var label = new Label(text);
            label.AddToClassList("btn-label");
            button.Add(label);
            button.RegisterCallback<ClickEvent>(_ => click?.Invoke());
            return button;
        }

        private static VisualElement MakeButtonRow(params VisualElement[] buttons)
        {
            var row = new VisualElement();
            row.AddToClassList("btn-row");
            foreach (var button in buttons)
            {
                button.style.flexGrow = 1f;
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

        private static VisualElement MakeChip(string text, Action click)
        {
            var chip = new VisualElement();
            chip.AddToClassList("chip");
            var label = new Label(text);
            label.AddToClassList("chip-label");
            chip.Add(label);
            chip.RegisterCallback<ClickEvent>(_ => click?.Invoke());
            return chip;
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
            var knob = new VisualElement();
            knob.AddToClassList("switch-knob");
            var thumb = new VisualElement();
            thumb.AddToClassList("switch-thumb");
            knob.Add(thumb);
            bool state = initial;
            knob.EnableInClassList("on", state);
            knob.RegisterCallback<ClickEvent>(_ =>
            {
                state = !state;
                knob.EnableInClassList("on", state);
                changed?.Invoke(state);
            });
            row.Add(knob);
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
            seg.AddToClassList("seg");
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
                    catch (NullReferenceException) { }
                    items[i].EnableInClassList("selected", selected);
                }
            };
            for (int i = 0; i < options.Length; i++)
            {
                int captured = i;
                var item = new VisualElement();
                item.AddToClassList("seg-item");
                var itemLabel = new Label(options[i].Label);
                itemLabel.AddToClassList("seg-item-label");
                item.Add(itemLabel);
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

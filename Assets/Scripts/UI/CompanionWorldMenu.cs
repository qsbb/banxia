using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Keyboard = UnityEngine.InputSystem.Keyboard;
using UnityEngine.UI;
using UnityEngine.XR;

namespace QuestMmdPlayer
{
    [DisallowMultipleComponent]
    public sealed class CompanionWorldMenu : MonoBehaviour
    {
        [SerializeField] private float distanceFromHead = .9f;
        [SerializeField] private float verticalOffset = -.12f;
        [SerializeField] private float pointerLength = 3.5f;
        [SerializeField] private float triggerThreshold = .65f;

        private readonly PointerState leftPointer = new PointerState(XRNode.LeftHand);
        private readonly PointerState rightPointer = new PointerState(XRNode.RightHand);
        private readonly List<InputDevice> leftMenuDevices = new List<InputDevice>();
        private QuestMmdPlayerBootstrap owner;
        private Transform trackingSpace;
        private GameObject menuRoot;
        private Text statusText;
        private Text debugLogText;
        private bool debugMode;
        private Material pointerMaterial;
        private Font font;
        private GameObject mainLayer;
        private GameObject actionLayer;
        private GameObject actionListLayer;
        private readonly List<GameObject> actionListEntries = new List<GameObject>();
        private GameObject pairingLayer;
        private GameObject appearanceLayer;
        private GameObject modelLayer;
        private GameObject qualityLayer;
        private GameObject voiceLayer;
        private GameObject textInputLayer;
        private GameObject debugLayer;
        private Text qualityStatusText;
        private Text voiceStatusText;
        private Text voiceToggleText;
        private Text voiceRecordText;
        private Text conversationInputText;
        private Text conversationInputStatusText;
        private Text pairingServerText;
        private Text pairingCodeText;
        private Text pairingStatusText;
        private Text externalActionText;
        private Text idlePresetText;
        private Text outlineStatusText;
        private Text expressionButtonText;
        private Text modelStatusText;
        private readonly List<RuntimeMmdModelInfo> modelOptions = new List<RuntimeMmdModelInfo>();
        private int modelIndex;
        private TouchScreenKeyboard pairingKeyboard;
        private TouchScreenKeyboard conversationKeyboard;
        private GameObject pairingKeyboardLayer;
        private Text pairingKeyboardValueText;
        private string pairingKeyboardValue = string.Empty;
        private string pairingCode = string.Empty;
        private string conversationInputValue = string.Empty;
        private int expressionIndex;
        private static readonly string[] ExpressionPresets = { "neutral", "happy", "shy", "surprised", "sad" };
        private int externalActionIndex;
        private Coroutine pendingAvatarAction;
        private string transientStatus = string.Empty;
        private float transientStatusUntil;
        private bool previousMenuButton;
        private bool previousHandMenuGesture;
        private bool debugIntentHandled;
        private float nextStatusUpdate;
        private string lastLoggedMenuDevice;
        private Transform focusedInputLayer;
        private bool pointerReleaseRequired;
        private int pointerPressFrame = -1;

        private static readonly InputFeatureUsage<bool> MenuButtonAlias = new InputFeatureUsage<bool>("MenuButton");
        private static readonly InputFeatureUsage<bool> MenuButtonLowerAlias = new InputFeatureUsage<bool>("menuButton");

        public bool IsOpen => menuRoot != null && menuRoot.activeSelf;
        public RuntimeMenuLayer ActiveLayer => ResolveActiveLayer();
        public string Status { get; private set; } = "菜单已关闭";

        private sealed class PointerState
        {
            internal readonly XRNode node;
            internal readonly RaycastHit[] hits = new RaycastHit[24];
            internal LineRenderer line;
            internal CompanionMenuButtonTarget hovered;
            internal bool previousSelect;
            internal bool currentSelect;

            internal PointerState(XRNode node)
            {
                this.node = node;
            }
        }

        public void Initialize(QuestMmdPlayerBootstrap bootstrap)
        {
            if (owner?.FileImport != null)
            {
                owner.FileImport.StatusChanged -= HandleFileImportStatusChanged;
            }
            owner = bootstrap;
            if (owner?.FileImport != null)
            {
                owner.FileImport.StatusChanged += HandleFileImportStatusChanged;
            }
            if (menuRoot == null)
            {
                BuildMenu();
            }
            Hide();
            Debug.Log("[CompanionMenu] Ready; left controller menu button toggles the panel.", this);
        }

        private void Update()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (ConsumeAndroidDebugMenuCommand())
            {
                Toggle();
            }
#endif
            trackingSpace = QuestXrInputUtility.ResolveTrackingSpace(trackingSpace);
            var hasLeftHand = QuestXrInputUtility.TryGetTrackedHandPointer(
                XRNode.LeftHand, trackingSpace, out _, out var leftPinch);
            var hasRightHand = QuestXrInputUtility.TryGetTrackedHandPointer(
                XRNode.RightHand, trackingSpace, out _, out var rightPinch);
            var handMenuGesture = hasLeftHand && hasRightHand && leftPinch && rightPinch;
            var menuPressed = ReadLeftMenuButton();
#if UNITY_EDITOR
            menuPressed |= Input.GetKey(KeyCode.M);
#elif UNITY_ANDROID
            var keyboard = Keyboard.current;
            menuPressed |= keyboard != null && keyboard.f1Key.wasPressedThisFrame;
#endif
            var toggleRequested = (menuPressed && !previousMenuButton) ||
                (handMenuGesture && !previousHandMenuGesture);
            if (toggleRequested)
            {
                Toggle();
                SynchronizePointerSelection(
                    leftPointer,
                    hasLeftHand ? leftPinch : ReadSelect(InputDevices.GetDeviceAtXRNode(XRNode.LeftHand)));
                SynchronizePointerSelection(
                    rightPointer,
                    hasRightHand ? rightPinch : ReadSelect(InputDevices.GetDeviceAtXRNode(XRNode.RightHand)));
            }
            previousMenuButton = menuPressed;
            previousHandMenuGesture = handMenuGesture;

            if (!IsOpen)
            {
                SynchronizePointerSelection(
                    leftPointer,
                    hasLeftHand ? leftPinch : ReadSelect(InputDevices.GetDeviceAtXRNode(XRNode.LeftHand)));
                SynchronizePointerSelection(
                    rightPointer,
                    hasRightHand ? rightPinch : ReadSelect(InputDevices.GetDeviceAtXRNode(XRNode.RightHand)));
                return;
            }

            UpdatePairingKeyboard();
            UpdateConversationKeyboard();
            ClearHoverVisuals();
            UpdatePointer(leftPointer);
            UpdatePointer(rightPointer);
            ReleasePointerInputGateIfReady();
            ApplyHoverVisuals();

            if (Time.unscaledTime >= nextStatusUpdate)
            {
                nextStatusUpdate = Time.unscaledTime + .2f;
                UpdateStatusText();
            }
        }

        private bool ConsumeAndroidDebugMenuCommand()
        {
            if (debugIntentHandled)
            {
                return false;
            }

            debugIntentHandled = true;
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = activity.Call<AndroidJavaObject>("getIntent"))
                {
                    var command = intent.Call<string>("getStringExtra", "quest_debug_command");
                    if (string.Equals(command, "toggle_menu", StringComparison.Ordinal))
                    {
                        Debug.Log("[CompanionMenu] Consumed Android QA menu command.", this);
                        return true;
                    }
                    if (string.Equals(command, "open_import", StringComparison.Ordinal))
                    {
                        ImportFile();
                        return false;
                    }
                    if (string.Equals(command, nameof(AvatarTouchInteraction.SimulateContactForQa), StringComparison.Ordinal))
                    {
                        StartCoroutine(SimulateQaContactWhenAvatarReady(command));
                        return false;
                    }
                    if (string.Equals(command, "open_text_input", StringComparison.Ordinal))
                    {
                        StartCoroutine(OpenQaTextInputWhenReady());
                        return false;
                    }
                    if (string.Equals(command, "send_text", StringComparison.Ordinal))
                    {
                        var text = NormalizeConversationInput(
                            intent.Call<string>("getStringExtra", "quest_debug_text"));
                        if (!string.IsNullOrEmpty(text))
                        {
                            StartCoroutine(SendQaConversationWhenReady(text));
                        }
                        return false;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[CompanionMenu] Android QA command unavailable: {exception.Message}", this);
            }

            return false;
        }

        private IEnumerator SimulateQaContactWhenAvatarReady(string source)
        {
            var remaining = 5f;
            while (remaining > 0f && (owner == null || owner.TouchInteraction == null || owner.TouchInteraction.Avatar == null))
            {
                remaining -= Time.unscaledDeltaTime;
                yield return null;
            }

            owner?.TouchInteraction?.SimulateContactForQa(source);
        }

        private IEnumerator SendQaConversationWhenReady(string text)
        {
            var remaining = 20f;
            while (remaining > 0f &&
                   (owner == null || owner.Conversation == null || !owner.Conversation.IsRealBackendConnected))
            {
                remaining -= Time.unscaledDeltaTime;
                yield return null;
            }

            if (owner?.Conversation == null || !owner.Conversation.IsRealBackendConnected)
            {
                Debug.LogWarning("[CompanionMenu] Android QA text was not sent because the real backend is unavailable.", this);
                yield break;
            }

            owner.Conversation.StartConversation(text);
            Debug.Log("[CompanionMenu] Android QA text submitted through the real conversation transport.", this);
        }

        private IEnumerator OpenQaTextInputWhenReady()
        {
            var remaining = 10f;
            while (remaining > 0f && (menuRoot == null || Camera.main == null))
            {
                remaining -= Time.unscaledDeltaTime;
                yield return null;
            }
            if (menuRoot == null || Camera.main == null)
            {
                Debug.LogWarning("[CompanionMenu] Android QA text input could not open because the menu is unavailable.", this);
                yield break;
            }

            ShowInFront();
            ShowTextInputPanel();
            OpenConversationKeyboard();
            Debug.Log("[CompanionMenu] Android QA text input opened; keyboard_requested=" + (conversationKeyboard != null), this);
        }

        public void Toggle()
        {
            if (IsOpen) Hide();
            else ShowInFront();
        }

        public void ShowInFront()
        {
            if (menuRoot == null)
            {
                BuildMenu();
            }

            var camera = Camera.main;
            if (camera == null)
            {
                Status = "未找到头显相机";
                return;
            }

            var pose = CalculateMenuPose(
                new Pose(camera.transform.position, camera.transform.rotation),
                distanceFromHead,
                verticalOffset);
            menuRoot.transform.SetPositionAndRotation(pose.position, pose.rotation);
            menuRoot.SetActive(true);
            FocusInputLayer(mainLayer);
            Physics.SyncTransforms();
            SetPointerLinesVisible(true);
            UpdateStatusText();
            Status = "菜单已打开";
            Debug.Log($"[CompanionMenu] Opened at {pose.position:F3}.", this);
        }

        public void Hide()
        {
            if (menuRoot != null)
            {
                menuRoot.SetActive(false);
            }
            if (mainLayer != null) mainLayer.SetActive(true);
            if (actionLayer != null) actionLayer.SetActive(false);
            if (actionListLayer != null) actionListLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (modelLayer != null) modelLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            if (voiceLayer != null) voiceLayer.SetActive(false);
            if (textInputLayer != null) textInputLayer.SetActive(false);
            if (debugLayer != null) debugLayer.SetActive(false);
            debugMode = false;
            owner?.DebugLog?.SetDisplayEnabled(false);
            pairingCode = string.Empty;
            HidePairingKeyboard();
            CloseConversationKeyboard();
            FocusInputLayer(null);
            ClearHoverVisuals();
            leftPointer.hovered = null;
            rightPointer.hovered = null;
            SetPointerLinesVisible(false);
            Status = "菜单已关闭";
        }

        public static Pose CalculateMenuPose(Pose headPose, float distance, float downOffset)
        {
            var forward = QuestVrLocomotion.ProjectOnGround(headPose.rotation * Vector3.forward);
            if (forward.sqrMagnitude < .0001f)
            {
                forward = Vector3.forward;
            }

            var position = headPose.position + forward * Mathf.Max(.35f, distance) + Vector3.up * downOffset;
            return new Pose(position, Quaternion.LookRotation(forward, Vector3.up));
        }

        private void BuildMenu()
        {
            font = ResolveMenuFont();
            menuRoot = new GameObject("Companion World Menu", typeof(RectTransform), typeof(Canvas), typeof(CanvasRenderer), typeof(Image));
            var canvas = menuRoot.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 100;
            var rootRect = menuRoot.GetComponent<RectTransform>();
            rootRect.sizeDelta = new Vector2(720f, 680f);
            rootRect.localScale = Vector3.one * .001f;
            var background = menuRoot.GetComponent<Image>();
            background.color = new Color(.055f, .075f, .085f, .97f);
            background.raycastTarget = false;

            mainLayer = CreateUiObject("Main Menu Layer", menuRoot.transform, Vector2.zero, new Vector2(720f, 680f));
            CreateImage("Accent", mainLayer.transform, new Vector2(0f, 335f), new Vector2(720f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("陪伴", mainLayer.transform, new Vector2(0f, 286f), new Vector2(640f, 54f), 31, FontStyle.Bold, Color.white);
            CreateText("陪伴  /  对话  /  触碰", mainLayer.transform, new Vector2(0f, 244f), new Vector2(640f, 30f), 14, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));
            CreateButton("X", 316f, 286f, 48f, 48f, Hide, mainLayer.transform);
            // Source contract aliases: PAIR BACKEND / SET HOST PORT / AUTO COMPLETE PATH.

            var buttonWidth = 204f;
            var buttonHeight = 62f;
            var x = new[] { -224f, 0f, 224f };
            var y = new[] { 158f, 80f, 2f, -76f };
            CreateButton("挥手", x[0], y[0], buttonWidth, buttonHeight, () => RequestAvatarAction("wave", false), mainLayer.transform);
            CreateButton("语音", x[1], y[0], buttonWidth, buttonHeight, ShowVoicePanel, mainLayer.transform);
            CreateButton("打断回复", x[2], y[0], buttonWidth, buttonHeight, () => owner?.Conversation?.Interrupt(), mainLayer.transform);
            CreateButton("摸摸头", x[0], y[1], buttonWidth, buttonHeight, () => owner?.HumanInteraction?.SimulateInteraction(HumanInteractionKind.HeadPat), mainLayer.transform);
            CreateButton("握手", x[1], y[1], buttonWidth, buttonHeight, () => owner?.HumanInteraction?.SimulateInteraction(HumanInteractionKind.Handshake), mainLayer.transform);
            CreateButton("捏脸", x[2], y[1], buttonWidth, buttonHeight, () => owner?.HumanInteraction?.SimulateInteraction(HumanInteractionKind.CheekPinch), mainLayer.transform);
            CreateButton("重新放置", x[0], y[2], buttonWidth, buttonHeight, () => owner?.Placement?.RequestPlacement(), mainLayer.transform);
            CreateButton("站立校准", x[1], y[2], buttonWidth, buttonHeight, () => owner?.Placement?.CalibrateHeightAndPlace(), mainLayer.transform);
            CreateButton("彩色透视", x[2], y[2], buttonWidth, buttonHeight, () => owner?.Passthrough?.Toggle(), mainLayer.transform);
            CreateButton("绑定后端", x[0], y[3], buttonWidth, buttonHeight, ShowPairingPanel, mainLayer.transform);
            CreateButton("动作", x[1], y[3], buttonWidth, buttonHeight, ShowActionPanel, mainLayer.transform);
            CreateButton("外观", x[2], y[3], buttonWidth, buttonHeight, ShowAppearancePanel, mainLayer.transform);

            statusText = CreateText("", mainLayer.transform, new Vector2(0f, -177f), new Vector2(660f, 60f), 14, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            CreateButton("诊断", 286f, -278f, 108f, 44f, ShowDebugPanel, mainLayer.transform);
            CreateText("", mainLayer.transform, Vector2.zero, Vector2.zero, 1, FontStyle.Normal, Color.clear);

            pointerMaterial = CreatePointerMaterial(new Color(.25f, .86f, .66f, 1f));
            leftPointer.line = CreatePointerLine("Left Menu Pointer");
            rightPointer.line = CreatePointerLine("Right Menu Pointer");
            BuildActionPanel();
            BuildAppearancePanel();
            BuildModelPanel();
            BuildQualityPanel();
            BuildVoicePanel();
            BuildTextInputPanel();
            BuildDebugPanel();
        }

        private void BuildVoicePanel()
        {
            voiceLayer = CreateUiObject("Voice Layer", menuRoot.transform, Vector2.zero, new Vector2(720f, 680f));
            CreateImage("Accent", voiceLayer.transform, new Vector2(0f, 335f), new Vector2(720f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("实时语音", voiceLayer.transform, new Vector2(0f, 288f), new Vector2(640f, 50f), 29, FontStyle.Bold, Color.white);
            CreateText("监听、手动录音与链路恢复", voiceLayer.transform, new Vector2(0f, 250f), new Vector2(640f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));

            voiceToggleText = CreateButton("常开监听", -224f, 150f, 204f, 62f, ToggleVoiceListening, voiceLayer.transform).GetComponentInChildren<Text>();
            voiceRecordText = CreateButton("开始说话", 0f, 150f, 204f, 62f, ToggleVoiceRecording, voiceLayer.transform).GetComponentInChildren<Text>();
            CreateButton("重启麦克风", 224f, 150f, 204f, 62f, RestartVoiceMonitoring, voiceLayer.transform);
            CreateButton("文字对话", -112f, 72f, 204f, 62f, ShowTextInputPanel, voiceLayer.transform);
            CreateButton("取消本轮", 112f, 72f, 204f, 62f, CancelVoiceTurn, voiceLayer.transform);
            voiceStatusText = CreateText("", voiceLayer.transform, new Vector2(0f, -45f), new Vector2(650f, 180f), 15, FontStyle.Normal, new Color(.74f, .86f, .82f, 1f));
            voiceStatusText.alignment = TextAnchor.UpperLeft;
            CreateButton("返回主菜单", -112f, -214f, 204f, 62f, ShowMainPanel, voiceLayer.transform);
            CreateButton("关闭", 112f, -214f, 204f, 62f, Hide, voiceLayer.transform);
            voiceLayer.SetActive(false);
        }

        private void BuildTextInputPanel()
        {
            textInputLayer = CreateUiObject("Text Conversation Layer", menuRoot.transform, Vector2.zero, new Vector2(720f, 680f));
            CreateImage("Accent", textInputLayer.transform, new Vector2(0f, 335f), new Vector2(720f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("文字对话", textInputLayer.transform, new Vector2(0f, 288f), new Vector2(640f, 50f), 29, FontStyle.Bold, Color.white);
            CreateText("通过真实 AstrBot 链路发送，不使用本地演示", textInputLayer.transform, new Vector2(0f, 250f), new Vector2(640f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));

            conversationInputText = CreateText("点击打开键盘输入内容", textInputLayer.transform, new Vector2(0f, 188f), new Vector2(640f, 74f), 18, FontStyle.Normal, new Color(.78f, .94f, .86f, 1f));
            conversationInputText.alignment = TextAnchor.MiddleLeft;
            CreateButton("打开键盘", -224f, 112f, 204f, 58f, OpenConversationKeyboard, textInputLayer.transform);
            CreateButton("清空", 0f, 112f, 204f, 58f, ClearConversationInput, textInputLayer.transform);
            CreateButton("发送", 224f, 112f, 204f, 58f, SendConversationInput, textInputLayer.transform);

            CreateButton("你好", -224f, 38f, 204f, 54f, () => SetConversationInput("你好"), textInputLayer.transform);
            CreateButton("你是谁", 0f, 38f, 204f, 54f, () => SetConversationInput("你是谁？"), textInputLayer.transform);
            CreateButton("现在几点", 224f, 38f, 204f, 54f, () => SetConversationInput("现在几点？"), textInputLayer.transform);
            CreateButton("还记得我吗", -224f, -28f, 204f, 54f, () => SetConversationInput("你还记得我吗？"), textInputLayer.transform);
            CreateButton("跳个舞", 0f, -28f, 204f, 54f, () => SetConversationInput("请跳个舞。"), textInputLayer.transform);
            CreateButton("链路测试", 224f, -28f, 204f, 54f, () => SetConversationInput("这是一条客户端链路测试，请简短回复。"), textInputLayer.transform);

            conversationInputStatusText = CreateText("", textInputLayer.transform, new Vector2(0f, -104f), new Vector2(640f, 48f), 14, FontStyle.Normal, new Color(.74f, .86f, .82f, 1f));
            CreateButton("返回语音", -112f, -212f, 204f, 62f, ShowVoicePanel, textInputLayer.transform);
            CreateButton("关闭", 112f, -212f, 204f, 62f, Hide, textInputLayer.transform);
            textInputLayer.SetActive(false);
        }

        private void BuildDebugPanel()
        {
            debugLayer = CreateUiObject("Debug Layer", menuRoot.transform, new Vector2(-610f, 0f), new Vector2(440f, 680f));
            CreateImage("Sidebar Background", debugLayer.transform, Vector2.zero, new Vector2(440f, 680f), new Color(.035f, .055f, .06f, .985f));
            CreateImage("Accent", debugLayer.transform, new Vector2(0f, 335f), new Vector2(440f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("运行诊断", debugLayer.transform, new Vector2(0f, 292f), new Vector2(400f, 44f), 24, FontStyle.Bold, Color.white);
            CreateText("实时刷新  ·  主菜单可同时操作", debugLayer.transform, new Vector2(0f, 254f), new Vector2(400f, 26f), 12, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));
            debugLogText = CreateText("", debugLayer.transform, new Vector2(0f, 2f), new Vector2(410f, 454f), 11, FontStyle.Normal, new Color(.66f, .95f, .78f, 1f));
            debugLogText.alignment = TextAnchor.UpperLeft;
            CreateButton("清空记录", -132f, -282f, 120f, 48f, ClearDebugLog, debugLayer.transform);
            CreateButton("收起", 0f, -282f, 120f, 48f, ToggleDebugMode, debugLayer.transform);
            CreateButton("关闭菜单", 132f, -282f, 120f, 48f, Hide, debugLayer.transform);
            debugLayer.SetActive(false);
        }

        private void BuildActionPanel()
        {
            actionLayer = CreateUiObject("Action Presets Layer", menuRoot.transform, Vector2.zero, new Vector2(720f, 680f));
            CreateImage("Accent", actionLayer.transform, new Vector2(0f, 335f), new Vector2(720f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("动作预设", actionLayer.transform, new Vector2(0f, 288f), new Vector2(640f, 50f), 29, FontStyle.Bold, Color.white);
            CreateText("内置动作与本地 VMD", actionLayer.transform, new Vector2(0f, 250f), new Vector2(640f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));
            idlePresetText = CreateText("\u9ed8\u8ba4\u5f85\u673a\uff1a" + (owner?.IdlePose == null ? "\u672a\u7ed1\u5b9a" : owner.IdlePose.PresetDisplayName), actionLayer.transform, new Vector2(0f, 210f), new Vector2(640f, 28f), 14, FontStyle.Normal, new Color(.78f, .88f, .82f, 1f));


            CreateButton("切换待机", -224f, 150f, 204f, 62f, CycleIdlePreset, actionLayer.transform);
            CreateButton("挥手", 0f, 150f, 204f, 62f, () => PlayPresetAction("wave"), actionLayer.transform);
            CreateButton("鞠躬", 224f, 150f, 204f, 62f, () => PlayPresetAction("bow"), actionLayer.transform);
            CreateButton("点头", -224f, 72f, 204f, 62f, () => PlayPresetAction("nod"), actionLayer.transform);
            CreateButton("轻摆", 0f, 72f, 204f, 62f, () => PlayPresetAction("sway"), actionLayer.transform);
            CreateButton("停止动作", 224f, 72f, 204f, 62f, StopCurrentAction, actionLayer.transform);
            CreateButton("刷新外部动作", -224f, -6f, 204f, 62f, RefreshExternalActions, actionLayer.transform);
            CreateButton("上一个", 0f, -6f, 204f, 62f, () => SelectExternalAction(-1), actionLayer.transform);
            CreateButton("下一个", 224f, -6f, 204f, 62f, () => SelectExternalAction(1), actionLayer.transform);
            externalActionText = CreateText("外部动作 0 个", actionLayer.transform, new Vector2(0f, -67f), new Vector2(650f, 38f), 14, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            CreateButton("导入文件", -224f, -132f, 204f, 62f, ImportFile, actionLayer.transform);
            CreateButton("选择动作", 0f, -132f, 204f, 62f, ShowActionList, actionLayer.transform);
            CreateButton("播放选中", 224f, -132f, 204f, 62f, PlaySelectedExternalAction, actionLayer.transform);
            CreateButton("返回主菜单", 0f, -210f, 204f, 62f, ShowMainPanel, actionLayer.transform);
            BuildActionListLayer();
            actionLayer.SetActive(false);
        }

        private void ImportFile()
        {
            var importer = owner?.FileImport;
            if (importer == null)
            {
                SetTransientStatus("文件导入不可用", 3f);
                return;
            }

            importer.OpenPicker();
            HandleFileImportStatusChanged(importer.Status);
        }

        private void HandleFileImportStatusChanged(string message)
        {
            if (externalActionText != null && actionLayer != null && actionLayer.activeSelf)
            {
                externalActionText.text = message;
            }
            if (modelLayer != null && modelLayer.activeSelf)
            {
                RefreshInstalledModels();
            }
            Status = message ?? string.Empty;
        }

        private void BuildAppearancePanel()
        {
            appearanceLayer = CreateUiObject("Appearance Layer", menuRoot.transform, Vector2.zero, new Vector2(720f, 680f));
            CreateImage("Accent", appearanceLayer.transform, new Vector2(0f, 335f), new Vector2(720f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("角色外观与定位", appearanceLayer.transform, new Vector2(0f, 288f), new Vector2(640f, 50f), 29, FontStyle.Bold, Color.white);
            CreateText("描边  /  现实定位  /  连接诊断", appearanceLayer.transform, new Vector2(0f, 250f), new Vector2(640f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));

            CreateButton("细一点", -224f, 148f, 204f, 62f, DecreaseOutline, appearanceLayer.transform);
            CreateButton("描边开关", 0f, 148f, 204f, 62f, ToggleOutline, appearanceLayer.transform);
            CreateButton("粗一点", 224f, 148f, 204f, 62f, IncreaseOutline, appearanceLayer.transform);
            CreateButton("彩色透视", -224f, 70f, 204f, 62f, () => owner?.Passthrough?.Toggle(), appearanceLayer.transform);
            CreateButton("站立校准", 0f, 70f, 204f, 62f, () => owner?.Placement?.ResetHeightAndPlace(), appearanceLayer.transform);
            CreateButton("面对面放置", 224f, 70f, 204f, 62f, () => owner?.Placement?.FaceUserAndPlace(), appearanceLayer.transform);
            CreateButton("角色模型", 0f, -8f, 204f, 62f, ShowModelPanel, appearanceLayer.transform);
            CreateButton("\u626b\u63cf\u623f\u95f4", -224f, -8f, 204f, 62f, () => owner?.RoomUnderstanding?.RequestSceneCapture(), appearanceLayer.transform);
            CreateButton("\u753b\u8d28", 224f, -8f, 204f, 62f, ShowQualityPanel, appearanceLayer.transform);
            expressionButtonText = CreateButton("\u8868\u60c5\uff1a\u6a21\u578b\u9ed8\u8ba4", -112f, -78f, 204f, 52f, CycleExpression, appearanceLayer.transform).GetComponentInChildren<Text>();
            CreateButton("重连后端", 112f, -78f, 204f, 52f, ReconnectBackend, appearanceLayer.transform);

            outlineStatusText = CreateText("", appearanceLayer.transform, new Vector2(0f, -151f), new Vector2(650f, 72f), 13, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            CreateButton("返回主菜单", -112f, -234f, 204f, 56f, ShowMainPanel, appearanceLayer.transform);
            CreateButton("关闭", 112f, -234f, 204f, 56f, Hide, appearanceLayer.transform);
            appearanceLayer.SetActive(false);
        }

        private void BuildModelPanel()
        {
            modelLayer = CreateUiObject("Model Library Layer", menuRoot.transform, Vector2.zero, new Vector2(720f, 680f));
            CreateImage("Accent", modelLayer.transform, new Vector2(0f, 335f), new Vector2(720f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("角色模型", modelLayer.transform, new Vector2(0f, 288f), new Vector2(640f, 50f), 29, FontStyle.Bold, Color.white);
            CreateText("本机 PMX 模型导入与切换", modelLayer.transform, new Vector2(0f, 250f), new Vector2(640f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));

            CreateButton("上一个模型", -224f, 150f, 204f, 62f, () => SelectInstalledModel(-1), modelLayer.transform);
            CreateButton("加载选中", 0f, 150f, 204f, 62f, LoadSelectedModel, modelLayer.transform);
            CreateButton("下一个模型", 224f, 150f, 204f, 62f, () => SelectInstalledModel(1), modelLayer.transform);
            CreateButton("导入模型", -112f, 72f, 204f, 62f, ImportFile, modelLayer.transform);
            CreateButton("刷新列表", 112f, 72f, 204f, 62f, RefreshInstalledModels, modelLayer.transform);
            modelStatusText = CreateText("", modelLayer.transform, new Vector2(0f, -48f), new Vector2(650f, 170f), 15, FontStyle.Normal, new Color(.74f, .86f, .82f, 1f));
            modelStatusText.alignment = TextAnchor.UpperLeft;
            CreateButton("返回外观", -112f, -214f, 204f, 62f, ShowAppearancePanel, modelLayer.transform);
            CreateButton("关闭", 112f, -214f, 204f, 62f, Hide, modelLayer.transform);
            modelLayer.SetActive(false);
        }

        private void ShowModelPanel()
        {
            if (modelLayer == null)
            {
                BuildModelPanel();
            }
            if (mainLayer != null) mainLayer.SetActive(false);
            if (actionLayer != null) actionLayer.SetActive(false);
            if (actionListLayer != null) actionListLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            if (voiceLayer != null) voiceLayer.SetActive(false);
            if (textInputLayer != null) textInputLayer.SetActive(false);
            modelLayer.SetActive(true);
            FocusInputLayer(modelLayer);
            Physics.SyncTransforms();
            RefreshInstalledModels();
            Status = "角色模型面板已打开";
        }

        private void RefreshInstalledModels()
        {
            var loader = owner?.ModelLoader;
            modelOptions.Clear();
            if (loader != null)
            {
                var discovered = loader.DiscoverInstalledModels();
                for (var index = 0; index < discovered.Count; index++)
                {
                    modelOptions.Add(discovered[index]);
                }
            }
            if (modelOptions.Count == 0)
            {
                modelIndex = 0;
            }
            else
            {
                var currentPath = loader?.CurrentModelPath ?? string.Empty;
                var currentIndex = modelOptions.FindIndex(info =>
                    string.Equals(info.Path, currentPath, StringComparison.OrdinalIgnoreCase));
                modelIndex = currentIndex >= 0
                    ? currentIndex
                    : Mathf.Clamp(modelIndex, 0, modelOptions.Count - 1);
            }
            RefreshModelStatusText();
        }

        private void SelectInstalledModel(int direction)
        {
            if (modelOptions.Count == 0)
            {
                RefreshInstalledModels();
                return;
            }
            modelIndex = (modelIndex + direction + modelOptions.Count) % modelOptions.Count;
            RefreshModelStatusText();
        }

        private async void LoadSelectedModel()
        {
            var loader = owner?.ModelLoader;
            if (loader == null || loader.IsLoading || modelOptions.Count == 0)
            {
                RefreshModelStatusText();
                return;
            }
            modelIndex = Mathf.Clamp(modelIndex, 0, modelOptions.Count - 1);
            var selected = modelOptions[modelIndex];
            if (modelStatusText != null)
            {
                modelStatusText.text = "正在加载：" + selected.DisplayName;
            }
            owner?.DebugLog?.RecordStage("avatar_action", "processing", "model_switch_started");
            try
            {
                await loader.LoadInstalledModelAsync(selected);
                owner?.DebugLog?.RecordStage("avatar_action", "completed", "model_switch_completed");
                RefreshInstalledModels();
            }
            catch (Exception exception)
            {
                owner?.DebugLog?.RecordStage("avatar_action", "failed", "model_switch_failed");
                Debug.LogWarning("[CompanionMenu] Model switch failed: " + exception.Message, this);
                if (modelStatusText != null)
                {
                    modelStatusText.text = "模型加载失败，请查看诊断日志";
                }
            }
        }

        private void RefreshModelStatusText()
        {
            if (modelStatusText == null)
            {
                return;
            }
            var loader = owner?.ModelLoader;
            if (loader == null)
            {
                modelStatusText.text = "模型加载器不可用";
                return;
            }
            if (modelOptions.Count == 0)
            {
                modelStatusText.text = "没有发现可切换的 PMX 模型\n点击“导入模型”添加角色文件";
                return;
            }
            modelIndex = Mathf.Clamp(modelIndex, 0, modelOptions.Count - 1);
            var selected = modelOptions[modelIndex];
            var current = string.Equals(selected.Path, loader.CurrentModelPath, StringComparison.OrdinalIgnoreCase)
                ? "（当前）"
                : string.Empty;
            modelStatusText.text = $"{modelIndex + 1}/{modelOptions.Count}  {selected.DisplayName}{current}\n" +
                (loader.IsLoading ? "模型正在加载" : "选择后点击“加载选中”进行切换");
        }

        private void ToggleOutline()
        {
            owner?.Outline?.Toggle();
            RefreshAppearancePanel();
        }

        private void DecreaseOutline()
        {
            owner?.Outline?.DecreaseWidth();
            RefreshAppearancePanel();
        }

        private void IncreaseOutline()
        {
            owner?.Outline?.IncreaseWidth();
            RefreshAppearancePanel();
        }

        private void CycleExpression()
        {
            expressionIndex = (expressionIndex + 1) % ExpressionPresets.Length;
            var expression = ExpressionPresets[expressionIndex];
            owner?.Conversation?.SetManualExpression(expression);
            owner?.Avatar?.SetEmotion(expression);
            RefreshAppearancePanel();
            SetTransientStatus("表情已切换为" + ExpressionDisplayName(expression), 2f);
        }

        public static string ExpressionDisplayName(string expression)
        {
            switch (expression)
            {
                case "happy": return "轻笑";
                case "shy": return "害羞";
                case "surprised": return "惊讶";
                case "sad": return "难过";
                default: return "模型默认";
            }
        }

        private void RefreshAppearancePanel()
        {
            if (outlineStatusText == null || appearanceLayer == null || !appearanceLayer.activeSelf)
            {
                return;
            }
            var outline = owner?.Outline == null ? "描边控制不可用" : owner.Outline.Status;
            var placement = owner?.Placement == null ? "定位不可用" : LocalizePlacementStatus(owner.Placement.Status);
            var bridge = owner?.AstrBot == null ? "后端不可用" : LocalizeBridgeStatus(owner.AstrBot.Status);
            var room = owner?.RoomUnderstanding == null ? "房间识别不可用" : owner.RoomUnderstanding.ContextSummary;
            var expression = owner?.Conversation == null ? "neutral" : owner.Conversation.ManualExpression;
            if (expressionButtonText != null) expressionButtonText.text = "表情：" + ExpressionDisplayName(expression);
            outlineStatusText.text = outline + "  |  表情 " + ExpressionDisplayName(expression) + "\n定位 " + placement + "\n" + room + "  |  后端 " + bridge;
        }
        private void CycleIdlePreset()
        {
            owner?.IdlePose?.CyclePreset();
            RefreshIdlePresetText();
            SetTransientStatus("\u9ed8\u8ba4\u5f85\u673a\u5df2\u5207\u6362", 2f);
        }

        private void RefreshIdlePresetText()
        {
            if (idlePresetText == null)
            {
                return;
            }

            var value = owner?.IdlePose == null ? "\u672a\u7ed1\u5b9a" : owner.IdlePose.PresetDisplayName;
            idlePresetText.text = "\u9ed8\u8ba4\u5f85\u673a\uff1a" + value;
        }
        private void PlayPresetAction(string action)
        {
            RequestAvatarAction(action, true);
        }

        private void RequestAvatarAction(string action, bool returnToMain)
        {
            if (pendingAvatarAction != null)
            {
                StopCoroutine(pendingAvatarAction);
                pendingAvatarAction = null;
            }

            if (owner == null)
            {
                SetTransientStatus("角色控制器不可用", 3f);
                return;
            }

            if (owner.Avatar != null)
            {
                ExecuteAvatarAction(action, returnToMain);
                return;
            }

            SetTransientStatus("角色仍在加载，挥手会在就绪后播放", 3f);
            pendingAvatarAction = StartCoroutine(PlayActionWhenAvatarReady(action, returnToMain));
        }

        private IEnumerator PlayActionWhenAvatarReady(string action, bool returnToMain)
        {
            var remaining = 8f;
            while (remaining > 0f && (owner == null || owner.Avatar == null))
            {
                remaining -= Time.unscaledDeltaTime;
                yield return null;
            }

            pendingAvatarAction = null;
            if (owner == null || owner.Avatar == null)
            {
                SetTransientStatus("角色尚未就绪，动作未播放", 3f);
                yield break;
            }

            ExecuteAvatarAction(action, returnToMain);
        }

        private void ExecuteAvatarAction(string action, bool returnToMain)
        {
            var normalized = string.IsNullOrWhiteSpace(action) ? "idle" : action.ToLowerInvariant();
            owner.VmdActions?.StopAndReturnToIdle();
            owner.Avatar.PlayAction(normalized);
            if (returnToMain)
            {
                ShowMainPanel();
            }

            SetTransientStatus(ActionDisplayName(normalized) + "已触发", 2.8f);
            owner.DebugLog?.Record("AvatarAction", "手动选择内置动作：" + normalized);
            Debug.Log("[CompanionMenu] Action requested: " + normalized, this);
        }

        private void SetTransientStatus(string message, float seconds)
        {
            Status = message;
            transientStatus = message ?? string.Empty;
            transientStatusUntil = Time.unscaledTime + Mathf.Max(.5f, seconds);
            if (statusText != null && IsOpen)
            {
                UpdateStatusText();
            }
        }

        private static string ActionDisplayName(string action)
        {
            switch (action)
            {
                case "wave": return "挥手动作";
                case "bow": return "鞠躬动作";
                case "nod": return "点头动作";
                case "sway": return "轻摆动作";
                case "raise_hand": return "抬手动作";
                case "turn_half": return "转半圈动作";
                case "refuse": return "拒绝动作";
                case "step_back": return "后退动作";
                case "idle": return "待机动作";
                default: return "动作";
            }
        }

        private void StopCurrentAction()
        {
            owner?.VmdActions?.StopAndReturnToIdle();
            owner?.Avatar?.PlayAction("idle");
            RefreshExternalActionText();
        }

        private async void RefreshExternalActions()
        {
            var library = owner?.VmdActions;
            if (library == null)
            {
                RefreshExternalActionText();
                return;
            }
            if (externalActionText != null)
            {
                externalActionText.text = "正在刷新外部动作...";
            }
            await library.RefreshAsync();
            externalActionIndex = Mathf.Clamp(externalActionIndex, 0, Mathf.Max(0, library.Actions.Count - 1));
            RefreshActionList();
            RefreshExternalActionText();
        }

        private void SelectExternalAction(int direction)
        {
            var actions = owner?.VmdActions?.Actions;
            if (actions == null || actions.Count == 0)
            {
                externalActionIndex = 0;
                RefreshExternalActionText();
                return;
            }
            externalActionIndex = (externalActionIndex + direction + actions.Count) % actions.Count;
            RefreshExternalActionText();
        }

        private async void PlaySelectedExternalAction()
        {
            var library = owner?.VmdActions;
            if (library == null || library.Actions.Count == 0)
            {
                RefreshExternalActionText();
                return;
            }
            externalActionIndex = Mathf.Clamp(externalActionIndex, 0, library.Actions.Count - 1);
            if (externalActionText != null)
            {
                externalActionText.text = "正在加载 " + library.Actions[externalActionIndex].DisplayName;
            }
            var actionId = library.Actions[externalActionIndex].Id;
            var played = await library.PlayAsync(actionId);
            owner.DebugLog?.Record(
                "AvatarAction",
                "手动选择导入动作：" + actionId +
                (played ? "（已开始）" : "（播放失败）"));
            RefreshExternalActionText();
            RefreshActionList();
        }

        private void RefreshExternalActionText()
        {
            if (externalActionText == null)
            {
                return;
            }
            var library = owner?.VmdActions;
            if (library == null || library.Actions.Count == 0)
            {
                externalActionText.text = "外部动作 0 个";
                return;
            }
            externalActionIndex = Mathf.Clamp(externalActionIndex, 0, library.Actions.Count - 1);
            var selected = library.Actions[externalActionIndex];
            var state = library.IsPlaying && library.CurrentActionId == selected.Id ? "  播放中" : string.Empty;
            var facial = selected.HasFacialTrack ? "  + 表情" : string.Empty;
            externalActionText.text = $"{externalActionIndex + 1}/{library.Actions.Count}  {selected.DisplayName}  {selected.DurationSeconds:F1}s{facial}{state}";
        }

        private void BuildActionListLayer()
        {
            actionListLayer = CreateUiObject("Added Actions List", actionLayer.transform, Vector2.zero, new Vector2(680f, 590f));
            var background = CreateImage("List Background", actionListLayer.transform, Vector2.zero, new Vector2(680f, 590f), new Color(.035f, .06f, .065f, .98f));
            AddModalBlocker(background.gameObject, new Vector2(680f, 590f));
            CreateText("已添加动作", actionListLayer.transform, new Vector2(0f, 250f), new Vector2(620f, 42f), 24, FontStyle.Bold, Color.white);
            CreateButton("删除当前动作", -112f, -252f, 204f, 52f, DeleteSelectedExternalAction, actionListLayer.transform);
            CreateButton("关闭列表", 112f, -252f, 204f, 52f, HideActionList, actionListLayer.transform);
            actionListLayer.SetActive(false);
        }

        private void ShowActionList()
        {
            if (actionListLayer == null)
            {
                BuildActionListLayer();
            }
            RefreshActionList();
            actionListLayer.SetActive(true);
            SetDirectButtonColliders(actionLayer, false);
            FocusInputLayer(actionListLayer);
            Physics.SyncTransforms();
            Status = "已添加动作列表";
        }

        private void HideActionList()
        {
            if (actionListLayer != null)
            {
                actionListLayer.SetActive(false);
            }
            SetDirectButtonColliders(actionLayer, true);
            FocusInputLayer(actionLayer);
            Physics.SyncTransforms();
        }

        private void RefreshActionList()
        {
            if (actionListLayer == null || owner?.VmdActions == null)
            {
                return;
            }
            for (var index = 0; index < actionListEntries.Count; index++)
            {
                if (actionListEntries[index] != null)
                {
                    Destroy(actionListEntries[index]);
                }
            }
            actionListEntries.Clear();

            var actions = owner.VmdActions.Actions;
            if (actions == null || actions.Count == 0)
            {
                CreateText("还没有添加动作", actionListLayer.transform, new Vector2(0f, 100f), new Vector2(620f, 48f), 18, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
                return;
            }

            var visibleCount = Math.Min(8, actions.Count);
            var firstVisible = Mathf.Clamp(externalActionIndex - 3, 0, Mathf.Max(0, actions.Count - visibleCount));
            for (var index = 0; index < visibleCount; index++)
            {
                var actionIndex = firstVisible + index;
                var item = actions[actionIndex];
                var marker = actionIndex == externalActionIndex ? "> " : "  ";
                var label = marker + item.DisplayName + "  " + item.DurationSeconds.ToString("F1") + "s";
                var entry = CreateButton(
                    label,
                    0f,
                    190f - index * 48f,
                    610f,
                    42f,
                    () =>
                    {
                        externalActionIndex = actionIndex;
                        HideActionList();
                        RefreshExternalActionText();
                    },
                    actionListLayer.transform);
                actionListEntries.Add(entry);
            }

            if (actions.Count > visibleCount)
            {
                CreateText($"显示 {firstVisible + 1}-{firstVisible + visibleCount} / {actions.Count}", actionListLayer.transform, new Vector2(0f, -205f), new Vector2(620f, 30f), 12, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            }
        }

        private async void DeleteSelectedExternalAction()
        {
            var library = owner?.VmdActions;
            if (library == null || library.Actions.Count == 0)
            {
                HideActionList();
                return;
            }

            externalActionIndex = Mathf.Clamp(externalActionIndex, 0, library.Actions.Count - 1);
            var actionId = library.Actions[externalActionIndex].Id;
            var deleted = await library.DeleteActionAsync(actionId);
            if (deleted)
            {
                externalActionIndex = Mathf.Clamp(externalActionIndex, 0, Mathf.Max(0, library.Actions.Count - 1));
                SetTransientStatus("动作已删除", 2f);
                RefreshExternalActionText();
                RefreshActionList();
            }
            else
            {
                SetTransientStatus("动作删除失败", 2.5f);
            }
        }
        private void ShowActionPanel()
        {
            if (actionLayer == null)
            {
                BuildActionPanel();
            }
            mainLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (modelLayer != null) modelLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            if (voiceLayer != null) voiceLayer.SetActive(false);
            if (textInputLayer != null) textInputLayer.SetActive(false);
            actionLayer.SetActive(true);
            SetDirectButtonColliders(actionLayer, true);
            if (actionListLayer != null) actionListLayer.SetActive(false);
            FocusInputLayer(actionLayer);
            Physics.SyncTransforms();
            if (owner?.VmdActions == null || owner.VmdActions.Actions.Count == 0)
            {
                RefreshExternalActions();
            }
            else
            {
                RefreshActionList();
                RefreshExternalActionText();
            }
            RefreshIdlePresetText();
            Status = "动作预设已打开";
        }

        private void ShowAppearancePanel()
        {
            if (appearanceLayer == null)
            {
                BuildAppearancePanel();
            }
            if (mainLayer != null) mainLayer.SetActive(false);
            if (actionLayer != null) actionLayer.SetActive(false);
            if (actionListLayer != null) actionListLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (modelLayer != null) modelLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            if (voiceLayer != null) voiceLayer.SetActive(false);
            if (textInputLayer != null) textInputLayer.SetActive(false);
            appearanceLayer.SetActive(true);
            FocusInputLayer(appearanceLayer);
            Physics.SyncTransforms();
            RefreshAppearancePanel();
            Status = "外观面板已打开";
        }
        private void ShowPairingPanel()
        {
            if (pairingLayer == null)
            {
                BuildPairingPanel();
            }
            pairingCode = string.Empty;
            mainLayer.SetActive(false);
            if (actionLayer != null) actionLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (modelLayer != null) modelLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            if (voiceLayer != null) voiceLayer.SetActive(false);
            if (textInputLayer != null) textInputLayer.SetActive(false);
            pairingLayer.SetActive(true);
            HidePairingKeyboard();
            FocusInputLayer(pairingLayer);
            Physics.SyncTransforms();
            RefreshPairingPanel();
            Status = "后端绑定面板已打开";
        }

        private void ShowMainPanel()
        {
            HidePairingKeyboard();
            pairingCode = string.Empty;
            if (actionLayer != null) actionLayer.SetActive(false);
            if (actionListLayer != null) actionListLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (modelLayer != null) modelLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            if (voiceLayer != null) voiceLayer.SetActive(false);
            if (textInputLayer != null) textInputLayer.SetActive(false);
            if (mainLayer != null) mainLayer.SetActive(true);
            FocusInputLayer(mainLayer);
            Physics.SyncTransforms();
            UpdateStatusText();
        }

        private void ShowVoicePanel()
        {
            if (voiceLayer == null)
            {
                BuildVoicePanel();
            }
            if (mainLayer != null) mainLayer.SetActive(false);
            if (actionLayer != null) actionLayer.SetActive(false);
            if (actionListLayer != null) actionListLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (modelLayer != null) modelLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            if (textInputLayer != null) textInputLayer.SetActive(false);
            voiceLayer.SetActive(true);
            FocusInputLayer(voiceLayer);
            Physics.SyncTransforms();
            RefreshVoicePanel();
            Status = "实时语音面板已打开";
        }

        private void ToggleVoiceListening()
        {
            owner?.VoiceInput?.ToggleAlwaysListening();
            RefreshVoicePanel();
        }

        private void ToggleVoiceRecording()
        {
            owner?.VoiceInput?.ToggleRecording();
            RefreshVoicePanel();
        }

        private void RestartVoiceMonitoring()
        {
            owner?.VoiceInput?.RestartMonitoring();
            RefreshVoicePanel();
        }

        private void CancelVoiceTurn()
        {
            if (owner?.VoiceInput != null && owner.VoiceInput.IsRecording)
            {
                owner.VoiceInput.CancelRecording();
            }
            else if (owner?.Conversation != null && owner.Conversation.State != ConversationState.Idle)
            {
                owner.Conversation.Interrupt();
            }
            RefreshVoicePanel();
        }

        private void RefreshVoicePanel()
        {
            if (voiceLayer == null || !voiceLayer.activeSelf)
            {
                return;
            }
            var voice = owner?.VoiceInput;
            var conversation = owner?.Conversation;
            if (voiceToggleText != null)
            {
                voiceToggleText.text = "常开监听：" + (voice != null && voice.AlwaysListening ? "开" : "关");
            }
            if (voiceRecordText != null)
            {
                voiceRecordText.text = voice != null && voice.IsRecording ? "发送语音" : "开始说话";
            }
            if (voiceStatusText == null)
            {
                return;
            }
            var backend = conversation != null && conversation.IsRealBackendConnected ? "已连接" : "未连接";
            var error = conversation == null || string.IsNullOrEmpty(conversation.LastErrorCode)
                ? "无"
                : conversation.LastErrorCode;
            voiceStatusText.text = voice == null
                ? "麦克风组件不可用"
                : $"状态：{voice.Status}\n" +
                  $"监听={Flag(voice.IsMonitoring)}  录音={Flag(voice.IsRecording)}  常开={Flag(voice.AlwaysListening)}  后端={backend}\n" +
                  $"输入电平 {voice.InputLevel:F4} / 阈值 {voice.ActivationThreshold:F4}  激活 {voice.ActivationProgress * 100f:F0}%\n" +
                  $"上轮 {voice.LastTurnCaptureSeconds:F2}s  {voice.LastTurnChunkCount} 块  {voice.LastTurnPcmBytes} B\n" +
                   $"ASR {conversation?.TranscriptCharacters ?? 0} 字  回复 {conversation?.ReplyTextCharacters ?? 0} 字  错误={error}";
        }

        private void ShowTextInputPanel()
        {
            if (textInputLayer == null)
            {
                BuildTextInputPanel();
            }
            if (mainLayer != null) mainLayer.SetActive(false);
            if (actionLayer != null) actionLayer.SetActive(false);
            if (actionListLayer != null) actionListLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (modelLayer != null) modelLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            if (voiceLayer != null) voiceLayer.SetActive(false);
            textInputLayer.SetActive(true);
            FocusInputLayer(textInputLayer);
            Physics.SyncTransforms();
            RefreshConversationInputPanel();
            Status = "文字对话面板已打开";
        }

        private void OpenConversationKeyboard()
        {
            CloseConversationKeyboard();
            conversationKeyboard = TouchScreenKeyboard.Open(
                conversationInputValue,
                TouchScreenKeyboardType.Default,
                false,
                true,
                false,
                false,
                "输入要说的话",
                512);
            if (conversationInputStatusText != null)
            {
                conversationInputStatusText.text = conversationKeyboard == null
                    ? "系统键盘不可用，请使用下方测试短句"
                    : "系统键盘已请求打开";
            }
        }

        private void UpdateConversationKeyboard()
        {
            if (conversationKeyboard == null)
            {
                return;
            }
            conversationInputValue = NormalizeConversationInput(conversationKeyboard.text);
            RefreshConversationInputPanel();
            if (conversationKeyboard.status == TouchScreenKeyboard.Status.Visible)
            {
                return;
            }
            var status = conversationKeyboard.status;
            conversationKeyboard = null;
            Debug.Log("[CompanionMenu] Conversation keyboard closed with status=" + status + ".", this);
            if (conversationInputStatusText != null)
            {
                conversationInputStatusText.text = status == TouchScreenKeyboard.Status.Done
                    ? "输入完成，可以发送"
                    : status == TouchScreenKeyboard.Status.Canceled
                        ? "已取消输入"
                        : "系统键盘已关闭";
            }
        }

        private void CloseConversationKeyboard()
        {
            if (conversationKeyboard == null)
            {
                return;
            }
            conversationKeyboard.active = false;
            conversationKeyboard = null;
        }

        private void ClearConversationInput()
        {
            conversationInputValue = string.Empty;
            RefreshConversationInputPanel();
        }

        private void SetConversationInput(string value)
        {
            conversationInputValue = NormalizeConversationInput(value);
            RefreshConversationInputPanel();
        }

        private void SendConversationInput()
        {
            var text = NormalizeConversationInput(conversationInputValue);
            if (string.IsNullOrEmpty(text))
            {
                if (conversationInputStatusText != null) conversationInputStatusText.text = "请先输入内容";
                return;
            }
            var conversation = owner?.Conversation;
            if (conversation == null || !conversation.IsRealBackendConnected)
            {
                if (conversationInputStatusText != null) conversationInputStatusText.text = "真实后端尚未连接，未切换到本地演示";
                return;
            }
            conversation.StartConversation(text);
            if (conversationInputStatusText != null) conversationInputStatusText.text = "已通过真实 AstrBot 链路发送";
        }

        private void RefreshConversationInputPanel()
        {
            if (conversationInputText != null)
            {
                conversationInputText.text = string.IsNullOrEmpty(conversationInputValue)
                    ? "点击打开键盘输入内容"
                    : conversationInputValue;
            }
            if (conversationInputStatusText == null || textInputLayer == null || !textInputLayer.activeSelf)
            {
                return;
            }
            var conversation = owner?.Conversation;
            if (conversation != null && !string.IsNullOrEmpty(conversation.LastErrorCode))
            {
                conversationInputStatusText.text = "链路错误：" + RuntimeDebugLog.CodeLabel(conversation.LastErrorCode);
            }
        }

        public static string NormalizeConversationInput(string value)
        {
            var normalized = (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();
            return normalized.Length <= 512 ? normalized : normalized.Substring(0, 512);
        }

        private void ShowDebugPanel()
        {
            if (debugLayer == null)
            {
                BuildDebugPanel();
            }
            var primaryFocus = focusedInputLayer == null || !focusedInputLayer.gameObject.activeInHierarchy
                ? mainLayer
                : focusedInputLayer.gameObject;
            if (primaryFocus == null)
            {
                return;
            }
            debugLayer.SetActive(true);
            debugMode = true;
            owner?.DebugLog?.SetDisplayEnabled(true);
            FocusInputLayer(primaryFocus);
            Physics.SyncTransforms();
            UpdateDebugLogText();
            Status = "运行诊断已打开";
        }

        private void DisableDebugDisplay()
        {
            if (!debugMode)
            {
                return;
            }
            debugMode = false;
            owner?.DebugLog?.SetDisplayEnabled(false);
        }

        private void ClearDebugLog()
        {
            owner?.DebugLog?.Clear();
            UpdateDebugLogText();
        }

        private void BuildQualityPanel()
        {
            qualityLayer = CreateUiObject("Quality Layer", menuRoot.transform, Vector2.zero, new Vector2(720f, 680f));
            CreateImage("Accent", qualityLayer.transform, new Vector2(0f, 335f), new Vector2(720f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("画质设置", qualityLayer.transform, new Vector2(0f, 288f), new Vector2(640f, 50f), 29, FontStyle.Bold, Color.white);
            CreateText("Quest 渲染比例与抗锯齿", qualityLayer.transform, new Vector2(0f, 250f), new Vector2(640f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));

            CreateButton("性能", -224f, 150f, 204f, 62f, () => ApplyQualityPreset(QuestQualityPreset.Performance), qualityLayer.transform);
            CreateButton("平衡", 0f, 150f, 204f, 62f, () => ApplyQualityPreset(QuestQualityPreset.Balanced), qualityLayer.transform);
            CreateButton("清晰", 224f, 150f, 204f, 62f, () => ApplyQualityPreset(QuestQualityPreset.Clear), qualityLayer.transform);
            CreateButton("恢复默认", -112f, 72f, 204f, 62f, ResetQualityPreset, qualityLayer.transform);
            qualityStatusText = CreateText("", qualityLayer.transform, new Vector2(0f, -30f), new Vector2(650f, 110f), 14, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            CreateButton("返回外观", -112f, -182f, 204f, 62f, ShowAppearancePanel, qualityLayer.transform);
            CreateButton("关闭", 112f, -182f, 204f, 62f, Hide, qualityLayer.transform);
            qualityLayer.SetActive(false);
        }

        private void ApplyQualityPreset(QuestQualityPreset preset)
        {
            owner?.Quality?.ApplyPreset(preset);
            RefreshQualityPanel();
        }

        private void ResetQualityPreset()
        {
            owner?.Quality?.ResetToDefault();
            RefreshQualityPanel();
        }

        private void ShowQualityPanel()
        {
            if (qualityLayer == null)
            {
                BuildQualityPanel();
            }
            if (mainLayer != null) mainLayer.SetActive(false);
            if (actionLayer != null) actionLayer.SetActive(false);
            if (actionListLayer != null) actionListLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (modelLayer != null) modelLayer.SetActive(false);
            if (voiceLayer != null) voiceLayer.SetActive(false);
            if (textInputLayer != null) textInputLayer.SetActive(false);
            qualityLayer.SetActive(true);
            FocusInputLayer(qualityLayer);
            Physics.SyncTransforms();
            RefreshQualityPanel();
            Status = "画质设置已打开";
        }

        private void RefreshQualityPanel()
        {
            if (qualityStatusText == null || qualityLayer == null || !qualityLayer.activeSelf)
            {
                return;
            }
            var quality = owner?.Quality;
            qualityStatusText.text = quality == null
                ? "画质控制不可用"
                : "当前档位：" + QuestQualitySettings.GetDisplayName(quality.CurrentPreset) + "\n" + quality.Status + "\n切换会立即生效，并保存到本机";
        }

        private void BuildPairingPanel()
        {
            pairingLayer = CreateUiObject("Backend Pairing Layer", menuRoot.transform, Vector2.zero, new Vector2(720f, 680f));
            CreateImage("Accent", pairingLayer.transform, new Vector2(0f, 335f), new Vector2(720f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("绑定后端", pairingLayer.transform, new Vector2(0f, 288f), new Vector2(640f, 50f), 29, FontStyle.Bold, Color.white);
            CreateText("域名或 IP:端口  /  6 位配对码  /  路径自动补全", pairingLayer.transform, new Vector2(0f, 250f), new Vector2(640f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));

            pairingServerText = CreateText("尚未设置服务器", pairingLayer.transform, new Vector2(0f, 207f), new Vector2(650f, 34f), 13, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            CreateButton("输入域名 / IP:端口", -160f, 161f, 300f, 52f, OpenPairingKeyboard, pairingLayer.transform);
            CreateButton("局域网 HTTP", 160f, 161f, 300f, 52f, TogglePrivateHttp, pairingLayer.transform);
            CreateText("请输入 6 位配对码", pairingLayer.transform, new Vector2(0f, 114f), new Vector2(620f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));
            pairingCodeText = CreateText("_ _ _   _ _ _", pairingLayer.transform, new Vector2(0f, 72f), new Vector2(620f, 54f), 30, FontStyle.Bold, Color.white);

            var digits = new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9" };
            for (var index = 0; index < digits.Length; index++)
            {
                var digit = digits[index];
                var column = index % 3;
                var row = index / 3;
                CreateButton(digit, -124f + column * 124f, 18f - row * 58f, 108f, 48f, () => AppendPairingDigit(digit[0]), pairingLayer.transform);
            }
            CreateButton("清空", -124f, -156f, 108f, 48f, ClearPairingCode, pairingLayer.transform);
            CreateButton("0", 0f, -156f, 108f, 48f, () => AppendPairingDigit('0'), pairingLayer.transform);
            CreateButton("退格", 124f, -156f, 108f, 48f, RemovePairingDigit, pairingLayer.transform);

            CreateButton("返回", -180f, -226f, 160f, 54f, ShowMainPanel, pairingLayer.transform);

            CreateButton("重连", 0f, -226f, 160f, 54f, ReconnectBackend, pairingLayer.transform);
            CreateButton("连接", 180f, -226f, 160f, 54f, ConnectPairingCode, pairingLayer.transform);
            pairingStatusText = CreateText("", pairingLayer.transform, new Vector2(0f, -294f), new Vector2(660f, 92f), 13, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            BuildPairingKeyboardLayer();
            pairingLayer.SetActive(false);
        }

        private void AppendPairingDigit(char digit)
        {
            if (owner?.Pairing == null || owner.Pairing.IsBusy || pairingCode.Length >= 6) return;
            pairingCode += digit;
            RefreshPairingPanel();
        }

        private void RemovePairingDigit()
        {
            if (owner?.Pairing != null && owner.Pairing.IsBusy) return;
            if (pairingCode.Length > 0) pairingCode = pairingCode.Substring(0, pairingCode.Length - 1);
            RefreshPairingPanel();
        }

        private void ClearPairingCode()
        {
            if (owner?.Pairing != null && owner.Pairing.IsBusy) return;
            pairingCode = string.Empty;
            RefreshPairingPanel();
        }

        private void ConnectPairingCode()
        {
            owner?.Pairing?.PairWithCode(pairingCode);
            RefreshPairingPanel();
        }

        private void ReconnectBackend()
        {
            var bridge = owner?.AstrBot;
            if (bridge == null)
            {
                SetTransientStatus("后端控制器不可用", 3f);
                return;
            }
            var configured = bridge.ReloadConfiguration();
            SetTransientStatus(configured ? "正在重新连接后端" : LocalizeBridgeStatus(bridge.Status), 3f);
            RefreshPairingPanel();
            RefreshAppearancePanel();
        }
        private void TogglePrivateHttp()
        {
            var pairing = owner?.Pairing;
            if (pairing == null || pairing.IsBusy)
            {
                return;
            }
            pairing.SetPrivateHttpAllowed(!pairing.PrivateHttpAllowed);
            RefreshPairingPanel();
        }

        private void OpenPairingKeyboard()
        {
            if (pairingKeyboardLayer == null)
            {
                BuildPairingKeyboardLayer();
            }
            pairingKeyboardValue = BackendPairingProtocol.GetServerEntry(
                owner?.Pairing?.PairingServerEndpoint);
            pairingKeyboardLayer.SetActive(true);
            SetDirectButtonColliders(pairingLayer, false);
            FocusInputLayer(pairingKeyboardLayer);
            RefreshPairingKeyboard();
            Physics.SyncTransforms();
        }

        private void UpdatePairingKeyboard()
        {
            // Quest system keyboards are unreliable over immersive world-space
            // canvases. The modal keypad below owns this input path.
        }

        private void BuildPairingKeyboardLayer()
        {
            pairingKeyboardLayer = CreateUiObject(
                "Pairing Server Keyboard",
                pairingLayer.transform,
                Vector2.zero,
                new Vector2(690f, 620f));
            var background = CreateImage(
                "Keyboard Background",
                pairingKeyboardLayer.transform,
                Vector2.zero,
                new Vector2(690f, 620f),
                new Color(.025f, .045f, .05f, .995f));
            AddModalBlocker(background.gameObject, new Vector2(690f, 620f));
            CreateText(
                "\u8f93\u5165\u57df\u540d\u6216 IP:\u7aef\u53e3",
                pairingKeyboardLayer.transform,
                new Vector2(0f, 270f),
                new Vector2(640f, 42f),
                24,
                FontStyle.Bold,
                Color.white);
            pairingKeyboardValueText = CreateText(
                string.Empty,
                pairingKeyboardLayer.transform,
                new Vector2(0f, 218f),
                new Vector2(640f, 48f),
                20,
                FontStyle.Normal,
                new Color(.72f, .94f, .84f, 1f));

            var rows = new[]
            {
                "1234567890",
                "qwertyuiop",
                "asdfghjkl",
                "zxcvbnm.-:[]"
            };
            for (var row = 0; row < rows.Length; row++)
            {
                var keys = rows[row];
                var firstX = -(keys.Length - 1) * 29f;
                for (var column = 0; column < keys.Length; column++)
                {
                    var key = keys[column].ToString();
                    CreateButton(
                        key,
                        firstX + column * 58f,
                        148f - row * 54f,
                        52f,
                        44f,
                        () => AppendPairingServerCharacter(key[0]),
                        pairingKeyboardLayer.transform);
                }
            }

            CreateButton("\u9000\u683c", -225f, -124f, 136f, 48f, RemovePairingServerCharacter, pairingKeyboardLayer.transform);
            CreateButton("\u6e05\u7a7a", -75f, -124f, 136f, 48f, ClearPairingServerEntry, pairingKeyboardLayer.transform);
            CreateButton("\u53d6\u6d88", 75f, -124f, 136f, 48f, HidePairingKeyboard, pairingKeyboardLayer.transform);
            CreateButton("\u786e\u5b9a", 225f, -124f, 136f, 48f, AcceptPairingServerEntry, pairingKeyboardLayer.transform);
            CreateText(
                "\u8def\u5f84\u4f1a\u81ea\u52a8\u8865\u5168\uff1b\u5c40\u57df\u7f51 IP \u9700\u5148\u5f00\u542f HTTP",
                pairingKeyboardLayer.transform,
                new Vector2(0f, -184f),
                new Vector2(640f, 30f),
                13,
                FontStyle.Normal,
                new Color(.62f, .72f, .75f, 1f));
            pairingKeyboardLayer.SetActive(false);
        }

        private void AppendPairingServerCharacter(char value)
        {
            if (pairingKeyboardValue.Length >= 128) return;
            pairingKeyboardValue += value;
            RefreshPairingKeyboard();
        }

        private void RemovePairingServerCharacter()
        {
            if (pairingKeyboardValue.Length > 0)
            {
                pairingKeyboardValue = pairingKeyboardValue.Substring(0, pairingKeyboardValue.Length - 1);
            }
            RefreshPairingKeyboard();
        }

        private void ClearPairingServerEntry()
        {
            pairingKeyboardValue = string.Empty;
            RefreshPairingKeyboard();
        }

        private void AcceptPairingServerEntry()
        {
            var pairing = owner?.Pairing;
            if (pairing == null)
            {
                return;
            }
            if (!pairing.TrySetPairingServer(pairingKeyboardValue, out _))
            {
                RefreshPairingPanel();
                return;
            }
            HidePairingKeyboard();
            RefreshPairingPanel();
        }

        private void HidePairingKeyboard()
        {
            if (pairingKeyboard != null) pairingKeyboard.active = false;
            pairingKeyboard = null;
            if (pairingKeyboardLayer != null)
            {
                pairingKeyboardLayer.SetActive(false);
            }
            SetDirectButtonColliders(pairingLayer, true);
            if (pairingLayer != null && pairingLayer.activeInHierarchy)
            {
                FocusInputLayer(pairingLayer);
            }
        }

        private void RefreshPairingKeyboard()
        {
            if (pairingKeyboardValueText == null) return;
            pairingKeyboardValueText.text = string.IsNullOrEmpty(pairingKeyboardValue)
                ? "_"
                : pairingKeyboardValue;
        }
        private void RefreshPairingPanel()
        {
            if (pairingLayer == null || !pairingLayer.activeSelf) return;
            var pairing = owner?.Pairing;
            var endpoint = pairing?.PairingServerEndpoint ?? string.Empty;
            if (pairingServerText != null)
            {
                pairingServerText.text = string.IsNullOrEmpty(endpoint)
                    ? "尚未设置服务器"
                    : "服务器  " + BackendPairingProtocol.GetServerEntry(endpoint) + "  （路径已自动补全）";
            }
            if (pairingCodeText != null)
            {
                var padded = pairingCode.PadRight(6, '_');
                pairingCodeText.text = padded.Substring(0, 3) + "   " + padded.Substring(3, 3);
            }
            if (pairingStatusText != null)
            {
                const string entryHint = "只需填写域名或 IP:端口";
                var connectionMode = pairing != null && pairing.PrivateHttpAllowed
                    ? "连接模式：仅私网 IP 的 HTTP（测试）"
                    : "连接模式：HTTPS";
                var bridge = LocalizeBridgeStatus(owner?.AstrBot?.Status ?? "AstrBot configuration not loaded");
                pairingStatusText.text = "实时连接：" + bridge + "\n配对：" + LocalizePairingStatus(pairing?.Status ?? "Pairing controller offline") + "\n" + entryHint + "   |   " + connectionMode;
            }
        }

        private static Font ResolveMenuFont()
        {
            var preferred = new[] { "Noto Sans CJK SC", "Noto Sans SC", "Microsoft YaHei UI", "Microsoft YaHei", "Droid Sans Fallback", "sans-serif" };
            var dynamicFont = Font.CreateDynamicFontFromOSFont(preferred, 32);
            return dynamicFont != null ? dynamicFont : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }

        private static string LocalizePairingStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "配对状态不可用";
            switch (value)
            {
                case "Enter pairing server and 6-digit code": return "请输入配对服务器和 6 位配对码";
                case "Pairing server ready": return "配对服务器已就绪";
                case "Enter all 6 pairing digits": return "请输入完整的 6 位配对码";
                case "Set the HTTPS pairing server first": return "请先设置 HTTPS 配对服务器";
                case "Set the pairing server first": return "请先设置配对服务器";
                case "Private-LAN HTTP enabled for this pairing session": return "已临时允许私网 IP 的 HTTP 配对";
                case "HTTPS pairing required": return "已恢复为仅允许 HTTPS 配对";
                case "Enable private-LAN HTTP before using a private IP address": return "检测到私网 IP，请先开启局域网 HTTP";
                case "Pairing request is already running": return "配对请求正在进行";
                case "QR scanner is unavailable": return "二维码扫描暂不可用";
                case "Exchanging one-time pairing credential...": return "正在交换一次性配对凭据……";
                case "Pairing response is invalid or incompatible": return "配对响应无效或版本不兼容";
                case "Configuration saved, but AstrBot reconnect could not start": return "配置已保存，但 AstrBot 无法开始重连";
                case "Backend paired; AstrBot is connecting": return "后端绑定成功，AstrBot 正在连接";
                case "Pairing controller offline": return "配对控制器离线";
                case "Meta OpenXR 1.0.2 cannot expose passthrough camera frames; use the 6-digit code.": return "当前 SDK 无法读取相机画面，请使用 6 位配对码";
            }
            if (value.StartsWith("Pairing exchange failed (HTTP ", StringComparison.Ordinal))
                return "配对失败：" + value.Substring("Pairing exchange failed ".Length);
            return value;
        }

        private static string LocalizeBridgeStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "状态未知";
            if (value.StartsWith("AstrBot config missing:", StringComparison.Ordinal)) return "尚未绑定";
            if (value.StartsWith("AstrBot config invalid:", StringComparison.Ordinal)) return "绑定配置无效";
            if (value.IndexOf("bridge_service_disabled", StringComparison.Ordinal) >= 0) return "后端服务已关闭";
            if (value.StartsWith("Health check failed (HTTP 0)", StringComparison.Ordinal)) return "无法连接服务器";
            if (value.StartsWith("Health check failed (HTTP 401)", StringComparison.Ordinal)) return "认证失败";
            if (value.StartsWith("Health check failed", StringComparison.Ordinal)) return "健康检查失败";
            if (value.StartsWith("Connection failed", StringComparison.Ordinal)) return "无法连接服务器";
            const string readyPrefix = "AstrBot session ready (";
            if (value.StartsWith(readyPrefix, StringComparison.Ordinal) &&
                value.EndsWith(")", StringComparison.Ordinal))
            {
                var chain = value.Substring(readyPrefix.Length, value.Length - readyPrefix.Length - 1);
                return "会话已建立 · " + LocalizeBackendChainStatus(chain);
            }
            switch (value)
            {
                case "AstrBot configuration not loaded": return "尚未载入配置";
                case "AstrBot config loaded": return "配置已载入";
                case "AstrBot config could not be read": return "无法读取配置";
                case "Checking AstrBot health": return "正在检查服务";
                case "AstrBot health check ready": return "服务可用";
                case "AstrBot health response is incompatible": return "服务协议不兼容";
                case "Starting AstrBot session": return "正在建立会话";
                case "AstrBot session ready": return "会话已建立";
                case "Connecting AstrBot SSE": return "正在连接实时事件";
                case "AstrBot SSE connected": return "实时连接正常";
                case "AstrBot session expired; recreating": return "会话已过期，正在重建";
                case "AstrBot session closed": return "会话已关闭";
                case "AstrBot SSE disconnected": return "实时连接已断开";
            }
            return value;
        }

        private static string LocalizeBackendChainStatus(string value)
        {
            switch (value)
            {
                case "EventBus ready": return "AstrBot 链路正常";
                case "EventBus eligible": return "AstrBot 链路可用";
                case "direct provider fallback": return "直连模型兼容回退";
                case "owner_not_configured":
                case "quest_identity_not_allowlisted": return "原始账号尚未在“序”中绑定";
                case "invalid_bot_id":
                case "invalid_user_id":
                case "missing_bot_id":
                case "missing_user_id": return "配对中的用户或机器人身份无效";
                case "client_id_mismatch":
                case "invalid_client_id":
                case "missing_client_id":
                case "trusted_client_id_missing": return "Quest 客户端身份不匹配";
                case "missing_platform_id":
                case "trusted_platform_id_missing":
                case "trusted_platform_not_configured":
                case "trusted_platform_unavailable": return "AstrBot 消息平台未配置或不可用";
                case "authorization_timeout": return "身份授权检查超时";
                case "authorization_denied":
                case "authorization_error":
                case "protected_context_denied": return "AstrBot 链路未授权";
                default: return "链路状态未知";
            }
        }

        private static string LocalizePlacementStatus(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "状态未知";
            if (value.StartsWith("Searching for a tracked floor", StringComparison.Ordinal)) return "正在寻找地面";
            if (value.StartsWith("Placed on tracked floor", StringComparison.Ordinal)) return "已放在追踪地面";
            if (value.StartsWith("Placed at tracking-floor fallback", StringComparison.Ordinal)) return "已使用地面回退";
            if (value == "Head pose is unavailable") return "头部追踪不可用";
            if (value == "Waiting for avatar") return "等待角色";
            return value;
        }
        private static string TruncateMiddle(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maximum) return value ?? string.Empty;
            var side = (maximum - 3) / 2;
            return value.Substring(0, side) + "..." + value.Substring(value.Length - side);
        }

        private void UpdatePointer(PointerState pointer)
        {
            var device = InputDevices.GetDeviceAtXRNode(pointer.node);
            var usingHand = QuestXrInputUtility.TryGetTrackedHandPointer(
                pointer.node,
                trackingSpace,
                out var pose,
                out var handPinch);
            var select = usingHand ? handPinch : ReadSelect(device);
            var selectDown = select && !pointer.previousSelect;
            pointer.currentSelect = select;
            pointer.previousSelect = select;

            if (!usingHand && !QuestXrInputUtility.TryGetWorldPose(pointer.node, trackingSpace, out pose))
            {
                if (pointer.line != null) pointer.line.enabled = false;
                pointer.hovered = null;
                return;
            }

            var ray = new Ray(pose.position, pose.rotation * Vector3.forward);
            var hitCount = Physics.RaycastNonAlloc(ray, pointer.hits, pointerLength, ~0, QueryTriggerInteraction.Collide);
            var bestDistance = float.MaxValue;
            var end = ray.origin + ray.direction * pointerLength;
            CompanionMenuButtonTarget best = null;
            for (var i = 0; i < hitCount; i++)
            {
                var target = pointer.hits[i].collider == null
                    ? null
                    : pointer.hits[i].collider.GetComponent<CompanionMenuButtonTarget>();
                if (target == null ||
                    !target.IsInteractive ||
                    !IsTargetInFocusedLayer(target) ||
                    pointer.hits[i].distance >= bestDistance)
                {
                    continue;
                }

                best = target;
                bestDistance = pointer.hits[i].distance;
                end = pointer.hits[i].point;
            }

            pointer.hovered = best;
            pointer.line.enabled = true;
            pointer.line.SetPosition(0, ray.origin);
            pointer.line.SetPosition(1, end);
            if (CanDispatchPointerPress(best, selectDown))
            {
                best.Press();
                if (!usingHand)
                {
                    device.SendHapticImpulse(0u, .35f, .06f);
                }
            }
        }

        private static void SynchronizePointerSelection(PointerState pointer, bool select)
        {
            pointer.currentSelect = select;
            pointer.previousSelect = select;
        }

        private void FocusInputLayer(GameObject layer)
        {
            focusedInputLayer = layer == null ? null : layer.transform;
            pointerReleaseRequired = layer != null;
            ClearHoverVisuals();
            leftPointer.hovered = null;
            rightPointer.hovered = null;
        }

        private void ReleasePointerInputGateIfReady()
        {
            if (pointerReleaseRequired && !leftPointer.currentSelect && !rightPointer.currentSelect)
            {
                pointerReleaseRequired = false;
            }
        }

        private bool IsTargetInFocusedLayer(CompanionMenuButtonTarget target)
        {
            var inFocusedLayer = target != null &&
                   focusedInputLayer != null &&
                   focusedInputLayer.gameObject.activeInHierarchy &&
                   target.gameObject.activeInHierarchy &&
                   target.transform.IsChildOf(focusedInputLayer);
            var inDiagnostics = debugMode && debugLayer != null && debugLayer.activeInHierarchy &&
                target != null && target.gameObject.activeInHierarchy && target.transform.IsChildOf(debugLayer.transform);
            return inFocusedLayer || inDiagnostics;
        }

        private bool CanDispatchPointerPress(CompanionMenuButtonTarget target, bool selectDown)
        {
            if (!selectDown || pointerReleaseRequired || !IsTargetInFocusedLayer(target))
            {
                return false;
            }

            if (pointerPressFrame == Time.frameCount)
            {
                return false;
            }

            pointerPressFrame = Time.frameCount;
            return true;
        }

        private void UpdateStatusText()
        {
            if (owner == null)
            {
                return;
            }
            if (pairingLayer != null && pairingLayer.activeSelf)
            {
                RefreshPairingPanel();
                return;
            }
            if (actionLayer != null && actionLayer.activeSelf)
            {
                return;
            }
            if (appearanceLayer != null && appearanceLayer.activeSelf)
            {
                RefreshAppearancePanel();
                return;
            }
            if (modelLayer != null && modelLayer.activeSelf)
            {
                RefreshModelStatusText();
                return;
            }
            if (voiceLayer != null && voiceLayer.activeSelf)
            {
                RefreshVoicePanel();
                return;
            }
            if (textInputLayer != null && textInputLayer.activeSelf)
            {
                RefreshConversationInputPanel();
                return;
            }
            if (debugLayer != null && debugLayer.activeSelf)
            {
                UpdateDebugLogText();
                return;
            }
            if (statusText == null)
            {
                return;
            }

            var action = Time.unscaledTime < transientStatusUntil && !string.IsNullOrWhiteSpace(transientStatus)
                ? transientStatus
                : "动作 " + ActionDisplayName(owner.Avatar == null ? "idle" : owner.Avatar.CurrentAction);
            var passthrough = owner.Passthrough != null && owner.Passthrough.State == PassthroughState.Enabled ? "开启" : "关闭";
            var avatar = owner.Avatar == null ? "加载中" : "就绪";
            var placement = owner.Placement == null || !owner.Placement.HasPlacement ? "等待放置" : "已放置";
            var microphoneStatus = owner.VoiceInput == null ? "OFF" : owner.VoiceInput.ShortStatus;
            var microphone = microphoneStatus == "REC" ? "录音中" :
                microphoneStatus == "LIVE" ? "常开监听" :
                microphoneStatus == "READY" ? "就绪" : "关闭";
            var backend = owner.Conversation != null && owner.Conversation.IsUsingMockTransport ? "\u672c\u5730\u6f14\u793a" : owner.Conversation != null && owner.Conversation.IsRealBackendConnected ? "\u5728\u7ebf" : owner.AstrBot != null && owner.AstrBot.IsConfigured ? "\u6b63\u5728\u8fde\u63a5" : "\u672a\u7ed1\u5b9a";
            var height = owner.Placement != null && owner.Placement.HasHeightCalibration ? $"{owner.Placement.EstimatedUserHeight:F2}m" : "未定位";
            statusText.text = $"{action}   |   彩透 {passthrough}   |   角色 {avatar}   |   {placement}\n站立身高估算 {height}   |   麦克风 {microphone}   |   后端 {backend}";
            UpdateDebugLogText();
        }

        private void ToggleDebugMode()
        {
            if (debugLayer != null && debugLayer.activeSelf)
            {
                debugLayer.SetActive(false);
                DisableDebugDisplay();
                Physics.SyncTransforms();
                return;
            }

            ShowDebugPanel();
        }

        private void UpdateDebugLogText()
        {
            if (!debugMode || debugLogText == null || owner == null)
            {
                return;
            }

            var snapshot = RuntimeDiagnosticsBuilder.Capture(owner);
            var voice = snapshot.Voice;
            var conversation = snapshot.Conversation;
            var backend = snapshot.Backend;
            var audio = snapshot.Audio;
            var error = owner.Conversation == null || string.IsNullOrEmpty(owner.Conversation.LastErrorCode)
                ? string.Empty
                : "，错误=" + RuntimeDebugLog.CodeLabel(owner.Conversation.LastErrorCode);
            var rootCause = owner.DebugLog == null
                ? "诊断组件不可用"
                : owner.DebugLog.CurrentRootCause;
            var timeline = owner.DebugLog == null
                ? string.Empty
                : owner.DebugLog.GetRecentTimelineText(11);
            debugLogText.text =
                $"当前根因：{rootCause}\n" +
                $"链路：{backend.ChainStatus}，连接={OnOff(backend.Connected)}，等待回复={OnOff(conversation.AwaitingBackendResponse)}{error}\n" +
                $"麦克风：监听={OnOff(voice.Monitoring)}，录音={OnOff(voice.Recording)}，常开={OnOff(voice.AlwaysListening)}，说话={OnOff(voice.SpeechDetected)}\n" +
                $"输入：{voice.InputLevel:F4}/{voice.ActivationThreshold:F4}，上轮 {voice.LastTurnCaptureSeconds:F2}s · {voice.LastTurnChunkCount}块/{voice.LastTurnPcmBytes}B\n" +
                $"耗时：事件 {Ms(conversation.FirstEventMs)} · 文字 {Ms(conversation.FirstTextMs)} · 音频 {Ms(conversation.FirstAudioMs)} · 结束 {Ms(conversation.ReplyEndMs)}\n" +
                $"播放：缓冲 {audio.BufferedSeconds:F2}s · 欠载 {audio.UnderflowCount} · 上传={OnOff(backend.AudioUploadInProgress)}\n" +
                "阶段时间线（最新在下）" +
                (string.IsNullOrEmpty(timeline) ? "\n暂无记录" : "\n" + timeline);
        }

        private RuntimeMenuLayer ResolveActiveLayer()
        {
            if (menuRoot == null)
            {
                return RuntimeMenuLayer.Unavailable;
            }
            if (!IsOpen)
            {
                return RuntimeMenuLayer.Closed;
            }
            if (focusedInputLayer == null)
            {
                return RuntimeMenuLayer.Unknown;
            }

            switch (focusedInputLayer.name)
            {
                case "Pairing Server Keyboard": return RuntimeMenuLayer.PairingKeyboard;
                case "Added Actions List": return RuntimeMenuLayer.ActionList;
                case "Quality Layer": return RuntimeMenuLayer.Quality;
                case "Voice Layer": return RuntimeMenuLayer.Voice;
                case "Text Conversation Layer": return RuntimeMenuLayer.TextInput;
                case "Debug Layer": return RuntimeMenuLayer.Debug;
                case "Appearance Layer": return RuntimeMenuLayer.Appearance;
                case "Model Library Layer": return RuntimeMenuLayer.Models;
                case "Backend Pairing Layer": return RuntimeMenuLayer.Pairing;
                case "Action Presets Layer": return RuntimeMenuLayer.Actions;
                case "Main Menu Layer": return RuntimeMenuLayer.Main;
                default: return RuntimeMenuLayer.Unknown;
            }
        }

        private static string Flag(bool value)
        {
            return value ? "1" : "0";
        }

        private static string OnOff(bool value)
        {
            return value ? "是" : "否";
        }

        private static string ConversationStateName(ConversationState state)
        {
            switch (state)
            {
                case ConversationState.Idle: return "空闲";
                case ConversationState.Listening: return "聆听中";
                case ConversationState.Thinking: return "处理中";
                case ConversationState.Speaking: return "回复中";
                case ConversationState.Interrupted: return "已打断";
                case ConversationState.Error: return "错误";
                default: return state.ToString();
            }
        }

        private static string Ms(int value)
        {
            return value < 0 ? "-" : value + "ms";
        }
        private static void SetDirectButtonColliders(GameObject layer, bool enabled)
        {
            if (layer == null) return;
            var targets = layer.GetComponentsInChildren<CompanionMenuButtonTarget>(true);
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                if (target != null && target.transform.parent == layer.transform)
                {
                    target.SetInteractive(enabled);
                }
            }
        }

        private static void AddModalBlocker(GameObject background, Vector2 size)
        {
            if (background == null)
            {
                return;
            }
            var collider = background.AddComponent<BoxCollider>();
            collider.size = new Vector3(size.x, size.y, 8f);
        }
        private void ClearHoverVisuals()
        {
            leftPointer.hovered?.SetHovered(false);
            if (rightPointer.hovered != leftPointer.hovered)
            {
                rightPointer.hovered?.SetHovered(false);
            }
        }

        private void ApplyHoverVisuals()
        {
            leftPointer.hovered?.SetHovered(true);
            if (rightPointer.hovered != leftPointer.hovered)
            {
                rightPointer.hovered?.SetHovered(true);
            }
        }

        private void SetPointerLinesVisible(bool visible)
        {
            if (leftPointer.line != null) leftPointer.line.enabled = visible;
            if (rightPointer.line != null) rightPointer.line.enabled = visible;
        }

        private LineRenderer CreatePointerLine(string objectName)
        {
            var pointerObject = new GameObject(objectName);
            pointerObject.transform.SetParent(transform, false);
            var line = pointerObject.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.startWidth = .0035f;
            line.endWidth = .002f;
            line.numCapVertices = 4;
            line.material = pointerMaterial;
            line.startColor = Color.white;
            line.endColor = new Color(.25f, .86f, .66f, .75f);
            line.enabled = false;
            return line;
        }

        private GameObject CreateButton(string label, float x, float y, float width, float height, Action action, Transform parent = null)
        {
            var buttonObject = CreateUiObject(label, parent ?? menuRoot.transform, new Vector2(x, y), new Vector2(width, height));
            var image = buttonObject.AddComponent<Image>();
            var normal = new Color(.105f, .14f, .15f, 1f);
            var hover = new Color(.18f, .48f, .39f, 1f);
            image.color = normal;
            var button = buttonObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.onClick.AddListener(() => action?.Invoke());
            var collider = buttonObject.AddComponent<BoxCollider>();
            collider.size = new Vector3(width, height, 16f);
            var target = buttonObject.AddComponent<CompanionMenuButtonTarget>();
            target.Configure(button, image, normal, hover);
            CreateText(label, buttonObject.transform, Vector2.zero, new Vector2(width - 12f, height - 8f), 18, FontStyle.Bold, Color.white);
            return buttonObject;
        }

        private GameObject CreateUiObject(string objectName, Transform parent, Vector2 position, Vector2 size)
        {
            var result = new GameObject(objectName, typeof(RectTransform), typeof(CanvasRenderer));
            var rect = result.GetComponent<RectTransform>();
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.pivot = new Vector2(.5f, .5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            return result;
        }

        private Image CreateImage(string objectName, Transform parent, Vector2 position, Vector2 size, Color color)
        {
            var result = CreateUiObject(objectName, parent, position, size).AddComponent<Image>();
            result.color = color;
            result.raycastTarget = false;
            return result;
        }

        private Text CreateText(string value, Transform parent, Vector2 position, Vector2 size, int fontSize, FontStyle style, Color color)
        {
            var result = CreateUiObject("Label", parent, position, size).AddComponent<Text>();
            result.font = font;
            result.fontSize = fontSize;
            result.fontStyle = style;
            result.alignment = TextAnchor.MiddleCenter;
            result.color = color;
            result.text = value;
            result.supportRichText = false;
            result.raycastTarget = false;
            return result;
        }

        private static Material CreatePointerMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            var material = new Material(shader);
            material.color = color;
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            return material;
        }

        private bool ReadSelect(InputDevice device)
        {
            if (!device.isValid)
            {
                return false;
            }
            if (ReadButton(device, CommonUsages.triggerButton))
            {
                return true;
            }
            return device.TryGetFeatureValue(CommonUsages.trigger, out var value) && value >= triggerThreshold;
        }

        private static bool ReadButton(InputDevice device, InputFeatureUsage<bool> usage)
        {
            return device.TryGetFeatureValue(usage, out var value) && value;
        }

        private bool ReadLeftMenuButton()
        {
            var nodeDevice = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            if (TryReadMenuButton(nodeDevice))
            {
                LogMenuDevice(nodeDevice);
                return true;
            }

            leftMenuDevices.Clear();
            InputDevices.GetDevicesWithCharacteristics(
                InputDeviceCharacteristics.Left | InputDeviceCharacteristics.Controller,
                leftMenuDevices);
            var pressed = false;
            for (var i = 0; i < leftMenuDevices.Count; i++)
            {
                var device = leftMenuDevices[i];
                if (TryReadMenuButton(device)) pressed = true;
                LogMenuDevice(device);
            }
            return pressed;
        }

        private static bool TryReadMenuButton(InputDevice device)
        {
            if (!device.isValid)
            {
                return false;
            }

            return ReadButton(device, CommonUsages.menuButton) ||
                   ReadButton(device, MenuButtonAlias) ||
                   ReadButton(device, MenuButtonLowerAlias);
        }

        private void LogMenuDevice(InputDevice device)
        {
            if (!device.isValid)
            {
                return;
            }

            var signature = device.name + "|" + device.characteristics;
            if (string.Equals(signature, lastLoggedMenuDevice, StringComparison.Ordinal))
            {
                return;
            }

            lastLoggedMenuDevice = signature;
            Debug.Log("[CompanionMenu] Left controller: " + signature + "; menu aliases=menuButton/MenuButton/menuButton.", this);
        }

        private void OnDestroy()
        {
            if (owner?.FileImport != null)
            {
                owner.FileImport.StatusChanged -= HandleFileImportStatusChanged;
            }
            if (pendingAvatarAction != null)
            {
                StopCoroutine(pendingAvatarAction);
                pendingAvatarAction = null;
            }
            HidePairingKeyboard();
            if (pointerMaterial != null)
            {
                Destroy(pointerMaterial);
            }
            if (menuRoot != null)
            {
                Destroy(menuRoot);
            }
        }
    }

    internal sealed class CompanionMenuButtonTarget : MonoBehaviour
    {
        private Button button;
        private Image image;
        private Color normal;
        private Color hover;

        internal void Configure(Button sourceButton, Image sourceImage, Color normalColor, Color hoverColor)
        {
            button = sourceButton;
            image = sourceImage;
            normal = normalColor;
            hover = hoverColor;
        }

        internal bool IsInteractive => button != null && button.interactable && GetComponent<Collider>() != null && GetComponent<Collider>().enabled;

        internal void SetInteractive(bool enabled)
        {
            if (button != null) button.interactable = enabled;
            var collider = GetComponent<Collider>();
            if (collider != null) collider.enabled = enabled;
        }
        internal void SetHovered(bool hovered)
        {
            if (image != null)
            {
                image.color = hovered ? hover : normal;
            }
        }

        internal void Press()
        {
            if (button != null && button.interactable)
            {
                button.onClick.Invoke();
            }
        }
    }
}

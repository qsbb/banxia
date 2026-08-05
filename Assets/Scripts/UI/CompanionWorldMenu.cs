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
        private Material pointerMaterial;
        private Font font;
        private GameObject mainLayer;
        private GameObject actionLayer;
        private GameObject pairingLayer;
        private GameObject appearanceLayer;
        private GameObject qualityLayer;
        private Text qualityStatusText;
        private Text pairingServerText;
        private Text pairingCodeText;
        private Text pairingStatusText;
        private Text externalActionText;
        private Text outlineStatusText;
        private TouchScreenKeyboard pairingKeyboard;
        private string pairingCode = string.Empty;
        private int externalActionIndex;
        private Coroutine pendingAvatarAction;
        private string transientStatus = string.Empty;
        private float transientStatusUntil;
        private bool previousMenuButton;
        private bool previousHandMenuGesture;
        private bool debugIntentHandled;
        private float nextStatusUpdate;
        private string lastLoggedMenuDevice;

        private static readonly InputFeatureUsage<bool> MenuButtonAlias = new InputFeatureUsage<bool>("MenuButton");
        private static readonly InputFeatureUsage<bool> MenuButtonLowerAlias = new InputFeatureUsage<bool>("menuButton");

        public bool IsOpen => menuRoot != null && menuRoot.activeSelf;
        public string Status { get; private set; } = "菜单已关闭";

        private sealed class PointerState
        {
            internal readonly XRNode node;
            internal readonly RaycastHit[] hits = new RaycastHit[24];
            internal LineRenderer line;
            internal CompanionMenuButtonTarget hovered;
            internal bool previousSelect;

            internal PointerState(XRNode node)
            {
                this.node = node;
            }
        }

        public void Initialize(QuestMmdPlayerBootstrap bootstrap)
        {
            owner = bootstrap;
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
                leftPointer.previousSelect = hasLeftHand ? leftPinch :
                    ReadSelect(InputDevices.GetDeviceAtXRNode(XRNode.LeftHand));
                rightPointer.previousSelect = hasRightHand ? rightPinch :
                    ReadSelect(InputDevices.GetDeviceAtXRNode(XRNode.RightHand));
            }
            previousMenuButton = menuPressed;
            previousHandMenuGesture = handMenuGesture;

            if (!IsOpen)
            {
                leftPointer.previousSelect = hasLeftHand ? leftPinch :
                    ReadSelect(InputDevices.GetDeviceAtXRNode(XRNode.LeftHand));
                rightPointer.previousSelect = hasRightHand ? rightPinch :
                    ReadSelect(InputDevices.GetDeviceAtXRNode(XRNode.RightHand));
                return;
            }

            UpdatePairingKeyboard();
            ClearHoverVisuals();
            UpdatePointer(leftPointer);
            UpdatePointer(rightPointer);
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
                    if (string.Equals(command, nameof(AvatarTouchInteraction.SimulateContactForQa), StringComparison.Ordinal))
                    {
                        StartCoroutine(SimulateQaContactWhenAvatarReady(command));
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
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            pairingCode = string.Empty;
            if (pairingKeyboard != null) pairingKeyboard.active = false;
            pairingKeyboard = null;
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
            // Source contract aliases: PAIR BACKEND / SCAN QR / SET HTTPS SERVER.

            var buttonWidth = 204f;
            var buttonHeight = 62f;
            var x = new[] { -224f, 0f, 224f };
            var y = new[] { 158f, 80f, 2f, -76f };
            CreateButton("挥手", x[0], y[0], buttonWidth, buttonHeight, () => RequestAvatarAction("wave", false), mainLayer.transform);
            CreateButton("说话 / 发送", x[1], y[0], buttonWidth, buttonHeight, () => owner?.VoiceInput?.ToggleRecording(), mainLayer.transform);
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

            statusText = CreateText("", mainLayer.transform, new Vector2(0f, -194f), new Vector2(660f, 74f), 14, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            CreateText("", mainLayer.transform, Vector2.zero, Vector2.zero, 1, FontStyle.Normal, Color.clear);

            pointerMaterial = CreatePointerMaterial(new Color(.25f, .86f, .66f, 1f));
            leftPointer.line = CreatePointerLine("Left Menu Pointer");
            rightPointer.line = CreatePointerLine("Right Menu Pointer");
            BuildActionPanel();
            BuildAppearancePanel();
            BuildQualityPanel();
        }

        private void BuildActionPanel()
        {
            actionLayer = CreateUiObject("Action Presets Layer", menuRoot.transform, Vector2.zero, new Vector2(720f, 680f));
            CreateImage("Accent", actionLayer.transform, new Vector2(0f, 335f), new Vector2(720f, 10f), new Color(.25f, .86f, .66f, 1f));
            CreateText("动作预设", actionLayer.transform, new Vector2(0f, 288f), new Vector2(640f, 50f), 29, FontStyle.Bold, Color.white);
            CreateText("内置动作与本地 VMD", actionLayer.transform, new Vector2(0f, 250f), new Vector2(640f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));

            CreateButton("自然待机", -224f, 150f, 204f, 62f, () => PlayPresetAction("idle"), actionLayer.transform);
            CreateButton("挥手", 0f, 150f, 204f, 62f, () => PlayPresetAction("wave"), actionLayer.transform);
            CreateButton("鞠躬", 224f, 150f, 204f, 62f, () => PlayPresetAction("bow"), actionLayer.transform);
            CreateButton("点头", -224f, 72f, 204f, 62f, () => PlayPresetAction("nod"), actionLayer.transform);
            CreateButton("轻摆", 0f, 72f, 204f, 62f, () => PlayPresetAction("sway"), actionLayer.transform);
            CreateButton("停止动作", 224f, 72f, 204f, 62f, StopCurrentAction, actionLayer.transform);
            CreateButton("刷新外部动作", -224f, -6f, 204f, 62f, RefreshExternalActions, actionLayer.transform);
            CreateButton("上一个", 0f, -6f, 204f, 62f, () => SelectExternalAction(-1), actionLayer.transform);
            CreateButton("下一个", 224f, -6f, 204f, 62f, () => SelectExternalAction(1), actionLayer.transform);
            externalActionText = CreateText("外部动作 0 个", actionLayer.transform, new Vector2(0f, -67f), new Vector2(650f, 38f), 14, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            CreateButton("播放选中", -112f, -132f, 204f, 62f, PlaySelectedExternalAction, actionLayer.transform);
            CreateButton("返回主菜单", 112f, -132f, 204f, 62f, ShowMainPanel, actionLayer.transform);
            actionLayer.SetActive(false);
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
            CreateButton("重连后端", 0f, -8f, 204f, 62f, ReconnectBackend, appearanceLayer.transform);
            CreateButton("\u626b\u63cf\u623f\u95f4", -224f, -8f, 204f, 62f, () => owner?.RoomUnderstanding?.RequestSceneCapture(), appearanceLayer.transform);
            CreateButton("\u753b\u8d28", 224f, -8f, 204f, 62f, ShowQualityPanel, appearanceLayer.transform);

            outlineStatusText = CreateText("", appearanceLayer.transform, new Vector2(0f, -96f), new Vector2(650f, 92f), 14, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            CreateButton("返回主菜单", -112f, -182f, 204f, 62f, ShowMainPanel, appearanceLayer.transform);
            CreateButton("关闭", 112f, -182f, 204f, 62f, Hide, appearanceLayer.transform);
            appearanceLayer.SetActive(false);
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
            outlineStatusText.text = outline + "\n\u5b9a\u4f4d " + placement + "\n" + room + "\n\u540e\u7aef " + bridge;
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
            await library.PlayAsync(library.Actions[externalActionIndex].Id);
            RefreshExternalActionText();
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

        private void ShowActionPanel()
        {
            if (actionLayer == null)
            {
                BuildActionPanel();
            }
            mainLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            actionLayer.SetActive(true);
            Physics.SyncTransforms();
            RefreshExternalActions();
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
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            appearanceLayer.SetActive(true);
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
            if (qualityLayer != null) qualityLayer.SetActive(false);
            pairingLayer.SetActive(true);
            Physics.SyncTransforms();
            RefreshPairingPanel();
            Status = "后端绑定面板已打开";
        }

        private void ShowMainPanel()
        {
            if (pairingKeyboard != null) pairingKeyboard.active = false;
            pairingKeyboard = null;
            pairingCode = string.Empty;
            if (actionLayer != null) actionLayer.SetActive(false);
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            if (qualityLayer != null) qualityLayer.SetActive(false);
            if (mainLayer != null) mainLayer.SetActive(true);
            Physics.SyncTransforms();
            UpdateStatusText();
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
            if (pairingLayer != null) pairingLayer.SetActive(false);
            if (appearanceLayer != null) appearanceLayer.SetActive(false);
            qualityLayer.SetActive(true);
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
            CreateText("一次性配对码  /  HTTPS  /  自动连接", pairingLayer.transform, new Vector2(0f, 250f), new Vector2(640f, 28f), 13, FontStyle.Normal, new Color(.62f, .72f, .75f, 1f));

            pairingServerText = CreateText("尚未设置服务器", pairingLayer.transform, new Vector2(0f, 207f), new Vector2(650f, 34f), 13, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
            CreateButton("设置服务器地址", -160f, 161f, 300f, 52f, OpenPairingKeyboard, pairingLayer.transform);
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

            CreateButton("返回", -255f, -226f, 150f, 54f, ShowMainPanel, pairingLayer.transform);
            CreateButton("扫码", -85f, -226f, 150f, 54f, () => owner?.Pairing?.BeginQrScan(), pairingLayer.transform);
            CreateButton("重连", 85f, -226f, 150f, 54f, ReconnectBackend, pairingLayer.transform);
            CreateButton("连接", 255f, -226f, 150f, 54f, ConnectPairingCode, pairingLayer.transform);
            pairingStatusText = CreateText("", pairingLayer.transform, new Vector2(0f, -294f), new Vector2(660f, 92f), 13, FontStyle.Normal, new Color(.74f, .82f, .84f, 1f));
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
            var initial = owner?.Pairing?.PairingServerEndpoint ?? string.Empty;
            pairingKeyboard = TouchScreenKeyboard.Open(
                initial,
                TouchScreenKeyboardType.URL,
                false,
                false,
                false,
                false,
                "请输入 Quest Bridge 服务器地址");
            if (pairingKeyboard == null && pairingStatusText != null)
            {
                pairingStatusText.text = "系统键盘不可用";
            }
        }

        private void UpdatePairingKeyboard()
        {
            if (pairingKeyboard == null) return;
            if (pairingKeyboard.status == TouchScreenKeyboard.Status.Visible) return;
            var keyboard = pairingKeyboard;
            pairingKeyboard = null;
            if (keyboard.status == TouchScreenKeyboard.Status.Done)
            {
                owner?.Pairing?.TrySetPairingServer(keyboard.text, out _);
            }
            RefreshPairingPanel();
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
                    : "服务器  " + TruncateMiddle(endpoint, 72);
            }
            if (pairingCodeText != null)
            {
                var padded = pairingCode.PadRight(6, '_');
                pairingCodeText.text = padded.Substring(0, 3) + "   " + padded.Substring(3, 3);
            }
            if (pairingStatusText != null)
            {
                var scanner = pairing != null && pairing.ScannerAvailable ? "二维码扫描可用" : "当前请使用 6 位配对码";
                var connectionMode = pairing != null && pairing.PrivateHttpAllowed
                    ? "连接模式：仅私网 IP 的 HTTP（测试）"
                    : "连接模式：HTTPS";
                var bridge = LocalizeBridgeStatus(owner?.AstrBot?.Status ?? "AstrBot configuration not loaded");
                pairingStatusText.text = "实时连接：" + bridge + "\n配对：" + LocalizePairingStatus(pairing?.Status ?? "Pairing controller offline") + "\n" + scanner + "   |   " + connectionMode;
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
            if (value.StartsWith("Health check failed (HTTP 0)", StringComparison.Ordinal)) return "无法连接服务器";
            if (value.StartsWith("Health check failed (HTTP 401)", StringComparison.Ordinal)) return "认证失败";
            if (value.StartsWith("Health check failed", StringComparison.Ordinal)) return "健康检查失败";
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
            }
            return value;
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
                if (target == null || pointer.hits[i].distance >= bestDistance)
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
            if (selectDown && best != null)
            {
                best.Press();
                if (!usingHand)
                {
                    device.SendHapticImpulse(0u, .35f, .06f);
                }
            }
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
            var microphone = microphoneStatus == "REC" ? "录音中" : microphoneStatus == "READY" ? "就绪" : "关闭";
            var backend = owner.Conversation != null && owner.Conversation.IsRealBackendConnected ? "在线" : "本地模式";
            var height = owner.Placement != null && owner.Placement.HasHeightCalibration ? $"{owner.Placement.EstimatedUserHeight:F2}m" : "未定位";
            statusText.text = $"{action}   |   彩透 {passthrough}   |   角色 {avatar}   |   {placement}\n站立身高估算 {height}   |   麦克风 {microphone}   |   后端 {backend}";
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

        private void CreateButton(string label, float x, float y, float width, float height, Action action, Transform parent = null)
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
            if (pendingAvatarAction != null)
            {
                StopCoroutine(pendingAvatarAction);
                pendingAvatarAction = null;
            }
            if (pairingKeyboard != null) pairingKeyboard.active = false;
            pairingKeyboard = null;
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

using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Add this component to one empty GameObject in a scene. It creates enough
    /// runtime content for an editor preview and for the first Quest smoke test.
    /// </summary>
    public sealed class QuestMmdPlayerBootstrap : MonoBehaviour
    {
        public const string AndroidTaskLabel = "\u4F34\u590F";

        [SerializeField] private bool createCameraIfMissing = true;
        [SerializeField] private bool createLightIfMissing = true;
        [SerializeField] private bool createFallbackAvatar = true;
        [SerializeField] private bool createPrototypeHud = true;
        [SerializeField] private bool createTouchInteraction = true;
        [SerializeField] private bool createHumanInteraction = true;
        [SerializeField] private bool createConversationPrototype = true;
        [SerializeField] private bool createVrLocomotion = true;
        [SerializeField] private bool createAvatarPlacement = true;
        [SerializeField] private Vector3 avatarStartPosition = new Vector3(0f, 0f, 1.35f);

        public AvatarController Avatar { get; private set; }
        public AvatarNaturalIdlePose IdlePose { get; private set; }
        public AvatarTouchInteraction TouchInteraction { get; private set; }
        public AvatarHumanInteraction HumanInteraction { get; private set; }
        public QuestTrackedHandVisualizer TrackedHands { get; private set; }
        public AvatarMmdPhysicsAdapter HandPhysics { get; private set; }
        public QuestAvatarRayInteraction AvatarRayInteraction { get; private set; }
        public CompanionWorldMenu Menu { get; private set; }
        public ConversationController Conversation { get; private set; }
        public QuestMicrophoneInput VoiceInput { get; private set; }
        public QuestVrLocomotion Locomotion { get; private set; }
        public AvatarPlacementService Placement { get; private set; }
        public RoomUnderstandingService RoomUnderstanding { get; private set; }
        public AvatarOutlineController Outline { get; private set; }
        public AvatarPresence Presence { get; private set; }
        public PassthroughFacade Passthrough { get; private set; }
        public AstrBotBridge AstrBot { get; private set; }
        public BackendPairingController Pairing { get; private set; }
        public VmdActionLibrary VmdActions { get; private set; }
        public RuntimeMmdModelLoader ModelLoader => runtimeMmdLoader;
        public QuestQualitySettings Quality { get; private set; }
        public QuestFileImportService FileImport { get; private set; }
        public RuntimeDebugLog DebugLog { get; private set; }
        public RuntimePerformanceMonitor Performance { get; private set; }

        private RuntimeMmdModelLoader runtimeMmdLoader;
        private AvatarController fallbackAvatar;
        private bool androidTaskLabelLogged;

        private void Awake()
        {
            ApplyAndroidTaskLabel();
            DebugLog = gameObject.GetComponent<RuntimeDebugLog>() ?? gameObject.AddComponent<RuntimeDebugLog>();
            Performance = gameObject.GetComponent<RuntimePerformanceMonitor>() ??
                gameObject.AddComponent<RuntimePerformanceMonitor>();
            EnsureCamera();
            EnsureLight();

            Passthrough = gameObject.GetComponent<PassthroughFacade>() ?? gameObject.AddComponent<PassthroughFacade>();
            AstrBot = gameObject.GetComponent<AstrBotBridge>() ?? gameObject.AddComponent<AstrBotBridge>();
            Pairing = gameObject.GetComponent<BackendPairingController>() ?? gameObject.AddComponent<BackendPairingController>();
            Pairing.Initialize(AstrBot);
            IdlePose = gameObject.GetComponent<AvatarNaturalIdlePose>() ?? gameObject.AddComponent<AvatarNaturalIdlePose>();
            if (createTouchInteraction)
            {
                TouchInteraction = gameObject.GetComponent<AvatarTouchInteraction>() ?? gameObject.AddComponent<AvatarTouchInteraction>();
            }
            if (createHumanInteraction)
            {
                HumanInteraction = gameObject.GetComponent<AvatarHumanInteraction>() ?? gameObject.AddComponent<AvatarHumanInteraction>();
            }
            TrackedHands = gameObject.GetComponent<QuestTrackedHandVisualizer>() ?? gameObject.AddComponent<QuestTrackedHandVisualizer>();
            HandPhysics = gameObject.GetComponent<AvatarMmdPhysicsAdapter>() ?? gameObject.AddComponent<AvatarMmdPhysicsAdapter>();
            if (createConversationPrototype)
            {
                Conversation = gameObject.GetComponent<ConversationController>() ?? gameObject.AddComponent<ConversationController>();
            }
            if (createConversationPrototype)
            {
                VoiceInput = gameObject.GetComponent<QuestMicrophoneInput>() ?? gameObject.AddComponent<QuestMicrophoneInput>();
                VoiceInput.Bind(Conversation);
            }
            if (createVrLocomotion)
            {
                Locomotion = gameObject.GetComponent<QuestVrLocomotion>() ?? gameObject.AddComponent<QuestVrLocomotion>();
            }
            RoomUnderstanding = gameObject.GetComponent<RoomUnderstandingService>() ?? gameObject.AddComponent<RoomUnderstandingService>();
            AstrBot.BindSpatialContext(RoomUnderstanding);
            if (createAvatarPlacement)
            {
                Placement = gameObject.GetComponent<AvatarPlacementService>() ?? gameObject.AddComponent<AvatarPlacementService>();
            }
            Presence = gameObject.GetComponent<AvatarPresence>() ?? gameObject.AddComponent<AvatarPresence>();
            Outline = gameObject.GetComponent<AvatarOutlineController>() ?? gameObject.AddComponent<AvatarOutlineController>();
            Quality = gameObject.GetComponent<QuestQualitySettings>() ?? gameObject.AddComponent<QuestQualitySettings>();
            runtimeMmdLoader = GetComponent<RuntimeMmdModelLoader>();
            VmdActions = gameObject.GetComponent<VmdActionLibrary>() ?? gameObject.AddComponent<VmdActionLibrary>();
            _ = VmdActions.RefreshAsync();
            FileImport = gameObject.GetComponent<QuestFileImportService>() ?? gameObject.AddComponent<QuestFileImportService>();
            FileImport.Initialize(runtimeMmdLoader, VmdActions);

            Avatar = FindObjectOfType<AvatarController>();
            if (Avatar == null && runtimeMmdLoader == null && createFallbackAvatar)
            {
                fallbackAvatar = FallbackAvatarFactory.Create(avatarStartPosition);
                Avatar = fallbackAvatar;
                BindInteractions();
            }

            BindInteractions();

            if (createPrototypeHud)
            {
                Menu = gameObject.GetComponent<CompanionWorldMenu>() ?? gameObject.AddComponent<CompanionWorldMenu>();
                Menu.Initialize(this);
#if UNITY_EDITOR
                var hud = gameObject.GetComponent<PrototypeHud>() ?? gameObject.AddComponent<PrototypeHud>();
                hud.Initialize(this);
#endif
                BindInteractions();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                ApplyAndroidTaskLabel();
            }
        }

        private void ApplyAndroidTaskLabel()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    activity.Call("runOnUiThread", new AndroidJavaRunnable(() =>
                    {
                        try
                        {
                            using (var callbackPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                            using (var callbackActivity = callbackPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                            using (var description = new AndroidJavaObject(
                                "android.app.ActivityManager$TaskDescription",
                                AndroidTaskLabel))
                            {
                                callbackActivity.Call("setTitle", AndroidTaskLabel);
                                callbackActivity.Call("setTaskDescription", description);
                            }

                            if (!androidTaskLabelLogged)
                            {
                                androidTaskLabelLogged = true;
                                Debug.Log("[Banxia] Android activity title and task label configured.");
                            }
                        }
                        catch (System.Exception exception)
                        {
                            Debug.LogWarning($"[Banxia] Android task label failed: {exception.Message}");
                        }
                    }));
                }
            }
            catch (System.Exception exception)
            {
                Debug.LogWarning($"[Banxia] Android task label scheduling failed: {exception.Message}");
            }
#endif
        }

        private void OnEnable()
        {
            if (AstrBot != null)
            {
                AstrBot.CommandReceived += HandleCommand;
            }

            if (runtimeMmdLoader != null)
            {
                runtimeMmdLoader.AvatarLoaded += HandleAvatarLoaded;
                runtimeMmdLoader.ModelWillUnload += HandleModelWillUnload;
                runtimeMmdLoader.LoadFailed += HandleMmdLoadFailed;
                runtimeMmdLoader.ProgressChanged += HandleMmdProgress;
                runtimeMmdLoader.LastModelRestoreCompleted += HandleLastModelRestoreCompleted;
            }
        }

        private void OnDisable()
        {
            if (AstrBot != null)
            {
                AstrBot.CommandReceived -= HandleCommand;
            }

            if (runtimeMmdLoader != null)
            {
                runtimeMmdLoader.AvatarLoaded -= HandleAvatarLoaded;
                runtimeMmdLoader.ModelWillUnload -= HandleModelWillUnload;
                runtimeMmdLoader.LoadFailed -= HandleMmdLoadFailed;
                runtimeMmdLoader.ProgressChanged -= HandleMmdProgress;
                runtimeMmdLoader.LastModelRestoreCompleted -= HandleLastModelRestoreCompleted;
            }
        }

        private void HandleAvatarLoaded(AvatarController avatar)
        {
            if (fallbackAvatar != null && fallbackAvatar != avatar)
            {
                Destroy(fallbackAvatar.gameObject);
                fallbackAvatar = null;
            }

            Avatar = avatar;
            var avatarObject = avatar == null ? null : avatar.gameObject;
            if (avatarObject != null)
            {
                avatarObject.SetActive(false);
            }
            VmdActions?.BindModel(
                runtimeMmdLoader == null ? null : runtimeMmdLoader.CurrentMmdModel,
                runtimeMmdLoader == null || runtimeMmdLoader.CurrentModel == null
                    ? null
                    : runtimeMmdLoader.CurrentModel.transform,
                avatar,
                runtimeMmdLoader == null
                    ? string.Empty
                    : runtimeMmdLoader.CurrentModelContentSha256);
            BindInteractions();
            if (avatarObject != null)
            {
                avatarObject.SetActive(true);
            }
            Debug.Log($"[Banxia] PMX avatar ready: {avatar.name}");
        }

        private void HandleModelWillUnload()
        {
            VmdActions?.ClearModel();
        }

        private void HandleMmdLoadFailed(string message)
        {
            Debug.LogWarning($"[Banxia] PMX load failed: {message}");
            if (Avatar == null && createFallbackAvatar)
            {
                fallbackAvatar = FallbackAvatarFactory.Create(avatarStartPosition);
                Avatar = fallbackAvatar;
                BindInteractions();
            }
        }

        private void HandleLastModelRestoreCompleted(bool restored)
        {
            if (restored || Avatar != null || !createFallbackAvatar ||
                (runtimeMmdLoader != null && runtimeMmdLoader.IsLoading))
            {
                return;
            }

            fallbackAvatar = FallbackAvatarFactory.Create(avatarStartPosition);
            Avatar = fallbackAvatar;
            BindInteractions();
            Debug.Log("[Banxia] No installed model was restored; using the fallback avatar.");
        }

        private void HandleMmdProgress(string stage)
        {
            Debug.Log($"[Banxia] PMX load: {stage}");
        }

        private void BindInteractions()
        {
            if (IdlePose != null)
            {
                IdlePose.Bind(Avatar);
            }
            // IdlePose has just written the relaxed stance. Capture exactly that
            // visible pose instead of restoring AvatarController's import T-pose.
            Avatar?.CaptureCurrentActionPose();
            if (TouchInteraction != null)
            {
                TouchInteraction.Bind(Avatar);
            }
            if (HumanInteraction != null)
            {
                HumanInteraction.Bind(Avatar);
            }
            TrackedHands?.Bind(HumanInteraction);
            HandPhysics?.Bind(Avatar, TrackedHands);
            Quality?.ApplyHandContactPolicy(HandPhysics);
            if (AvatarRayInteraction != null)
            {
                AvatarRayInteraction.Bind(Avatar, HumanInteraction, Menu);
            }
            if (Conversation != null)
            {
                Conversation.Bind(Avatar, HumanInteraction);
            }
            if (Presence != null)
            {
                Presence.Bind(Avatar);
            }
            if (Placement != null)
            {
                Placement.Bind(Avatar);
            }
            if (Outline != null)
            {
                Outline.Bind(Avatar);
            }
        }
        private void EnsureCamera()
        {
            if (!createCameraIfMissing || Camera.main != null)
            {
                return;
            }

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, 1.6f, 0f);
            cameraObject.transform.rotation = Quaternion.identity;
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.045f, 0.065f, 0f);
            cameraObject.AddComponent<AudioListener>();
        }

        private void EnsureLight()
        {
            if (!createLightIfMissing || FindObjectOfType<Light>() != null)
            {
                return;
            }

            var lightObject = new GameObject("Key Light");
            lightObject.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
            var light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
        }

        private void HandleCommand(AvatarCommand command)
        {
            if (Avatar == null || command == null)
            {
                return;
            }

            switch ((command.name ?? string.Empty).ToLowerInvariant())
            {
                case "play_motion":
                case "play":
                    PlayCommandAction(command.motionId);
                    break;
                case "toggle_pause":
                case "pause":
                    Avatar.TogglePlayback();
                    break;
                case "set_emotion":
                case "emotion":
                    Avatar.SetEmotion(command.emotion);
                    break;
                case "reset":
                    if (Placement == null || !Placement.ResetAvatarToStanding())
                    {
                        Avatar.ResetTransform();
                    }
                    break;
                case "place":
                case "place_avatar":
                case "recenter_avatar":
                    Placement?.RequestPlacement();
                    break;
                case "move":
                    Avatar.Move(command.vector);
                    break;
                case "rotate":
                    Avatar.Rotate(Mathf.Approximately(command.value, 0f) ? command.vector.y : command.value);
                    break;
                case "scale":
                    Avatar.Scale(Mathf.Approximately(command.value, 0f) ? 1f : command.value);
                    break;
                case "set_action":
                case "action":
                    PlayCommandAction(command.motionId ?? command.text);
                    break;
                case "handshake":
                    HumanInteraction?.SimulateInteraction(HumanInteractionKind.Handshake);
                    break;
                case "head_pat":
                case "headpat":
                case "pat":
                    HumanInteraction?.SimulateInteraction(HumanInteractionKind.HeadPat);
                    break;
                case "cheek_pinch":
                case "cheekpinch":
                case "pinch":
                    HumanInteraction?.SimulateInteraction(HumanInteractionKind.CheekPinch);
                    break;
                default:
                    Debug.LogWarning($"[Banxia] unsupported command: {command.name}");
                    break;
            }
        }

        private void PlayCommandAction(string action)
        {
            var normalized = string.IsNullOrWhiteSpace(action) ? "idle" : action.Trim().ToLowerInvariant();
            var restingAction = normalized == "lie" ? "lie_down" : normalized;
            if ((restingAction == "sit" || restingAction == "lie_down") &&
                Placement != null && Placement.TryExecuteRestingAction(restingAction))
            {
                return;
            }
            if (Placement != null && Placement.IsRestingOrAligning &&
                Placement.TryReturnToStanding(normalized))
            {
                return;
            }
            Avatar?.PlayActionFromSource(normalized, AvatarActionSource.Backend);
        }
    }
}

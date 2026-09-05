using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>Result of handling one Flutter command: either data JSON or an error string.</summary>
    public readonly struct FlutterCommandResult
    {
        public FlutterCommandResult(string dataJson, string error)
        {
            DataJson = dataJson ?? string.Empty;
            Error = error ?? string.Empty;
        }

        public string DataJson { get; }
        public string Error { get; }
        public bool Ok => string.IsNullOrEmpty(Error);

        public static FlutterCommandResult Success(string dataJson = null) => new FlutterCommandResult(dataJson, null);
        public static FlutterCommandResult Failure(string error) => new FlutterCommandResult(null, error);
    }

    /// <summary>
    /// Maps Flutter commands onto the platform-independent Unity services and
    /// republishes service events as Flutter events. It holds no engine state of
    /// its own beyond tiny diffs used to emit change notifications; every value
    /// is read live from the owning services, keeping business logic platform
    /// independent (see CLAUDE.md and docs/plans/flutter-ui-module-design.md).
    ///
    /// Commands that complete asynchronously (model.load, action.refresh,
    /// update.check, ...) return an immediate <c>ok</c> reply and report their
    /// result through the matching <c>*.updated</c>/<c>toast</c> events.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FlutterUiFacade : MonoBehaviour
    {
        private const string PrefsPrefix = "banxia.phone.";
        private const string CameraPrefKey = PrefsPrefix + "camera";
        private const string HudPrefKey = PrefsPrefix + "hud";
        private const string FramingGridPrefKey = PrefsPrefix + "framing-grid";
        private const string FpsPrefKey = PrefsPrefix + "fps";

        private const float PollIntervalSeconds = 0.25f;
        private const float PerformanceIntervalSeconds = 2f;
        private const int LogRefreshLineCount = 12;
        private const int PairingCodeLength = 6;

        private static readonly string[] ExpressionIds = { "neutral", "happy", "shy", "surprised", "sad" };

        private BanxiaFlutterBridge bridge;
        private QuestMmdPlayerBootstrap owner;

        // Pairing numpad state (M3): the facade owns the 6-digit buffer.
        private string pairingCodeBuffer = string.Empty;

        // Update-check state retained between check and install.
        private BanxiaUpdateChecker.UpdateInfo lastUpdateInfo;

        // Diff state for polled events.
        private int lastConnectionState = -1;
        private string lastBridgeStatus = string.Empty;
        private string lastTranscript = string.Empty;
        private string lastReplyText = string.Empty;
        private string lastSuggestionsKey = string.Empty;
        private bool lastMonitoring;
        private bool lastAlwaysListening;
        private bool lastRecording;
        private float lastVoiceLevel;
        private bool lastVideoCallActive;
        private string lastCallDurationText = string.Empty;
        private bool lastArPlaced;
        private bool lastFramingValid;
        private string lastFramingSignature = string.Empty;
        private int lastScreenWidth;
        private int lastScreenHeight;
        private int expressionIndex;
        private float nextPollAt;
        private float nextPerformanceAt;

        // Convenience accessors so every handler reads live service state.
        private AstrBotBridge AstrBot => owner == null ? null : owner.AstrBot;
        private BackendPairingController Pairing => owner == null ? null : owner.Pairing;
        private ConversationController Conversation => owner == null ? null : owner.Conversation;
        private QuestMicrophoneInput VoiceInput => owner == null ? null : owner.VoiceInput;
        private RuntimeMmdModelLoader ModelLoader => owner == null ? null : owner.ModelLoader;
        private VmdActionLibrary VmdActions => owner == null ? null : owner.VmdActions;
        private QuestQualitySettings Quality => owner == null ? null : owner.Quality;
        private QuestFileImportService FileImport => owner == null ? null : owner.FileImport;
        private BanxiaUpdateChecker UpdateChecker => owner == null ? null : EnsureUpdateChecker();
        private ICoPresenceDirector CoPresence => owner == null ? null : owner.CoPresence;
        private PhoneOrbitCamera OrbitCamera => owner == null ? null : owner.OrbitCamera;
        private PhoneDiagnosticsHud PhoneHud => owner == null ? null : owner.PhoneHud;
        private RuntimePerformanceMonitor Performance => owner == null ? null : owner.Performance;
        private RuntimeDebugLog DebugLog => owner == null ? null : owner.DebugLog;
        private AvatarNaturalIdlePose IdlePose => owner == null ? null : owner.IdlePose;

        private void Update()
        {
            if (bridge == null)
            {
                return;
            }
            if (Time.unscaledTime >= nextPollAt)
            {
                nextPollAt = Time.unscaledTime + PollIntervalSeconds;
                PollConnection();
                PollConversationText();
                PollVoice();
                PollCallTimer();
                PollArPlacement();
                PollFraming();
                PollScreenGeometry();
            }
            if (Time.unscaledTime >= nextPerformanceAt)
            {
                nextPerformanceAt = Time.unscaledTime + PerformanceIntervalSeconds;
                PollPerformance();
            }
        }

        /// <summary>
        /// Binds the bridge and the composition root, subscribes to service
        /// events and resets all diff state. Call once at startup, after both
        /// the bridge and the bootstrap exist (they share one GameObject).
        /// </summary>
        public void Bind(BanxiaFlutterBridge uiBridge, QuestMmdPlayerBootstrap uiOwner)
        {
            UnsubscribeAll();
            bridge = uiBridge;
            owner = uiOwner;
            ResetDiffState();
            if (owner != null)
            {
                SubscribeToServices();
                PublishInitialState();
            }
        }

        public void BindBridge(BanxiaFlutterBridge uiBridge)
        {
            bridge = uiBridge;
        }

        /// <summary>Stops event subscriptions before the composition root is destroyed.</summary>
        public void Unbind()
        {
            UnsubscribeAll();
            bridge = null;
            owner = null;
            ResetDiffState();
        }

        // ------------------------------------------------------------------
        // Command dispatch
        // ------------------------------------------------------------------

        /// <summary>Routes a Flutter command to the matching Unity service.</summary>
        public FlutterCommandResult HandleCommand(string name, string payloadJson)
        {
            switch (name)
            {
                case FlutterCommands.ModelDiscover: return DiscoverModels();
                case FlutterCommands.ModelLoad: return HandleModelLoad(payloadJson);
                case FlutterCommands.ModelDelete: return HandleModelDelete(payloadJson);
                case FlutterCommands.ModelImport: return HandleModelImport();

                case FlutterCommands.ActionRefresh: return HandleActionRefresh();
                case FlutterCommands.ActionPlay: return HandleActionPlay(payloadJson);
                case FlutterCommands.ActionStop: return HandleActionStop();
                case FlutterCommands.ActionDelete: return HandleActionDelete(payloadJson);

                case FlutterCommands.IdleCycle: return HandleIdleCycle();
                case FlutterCommands.ExpressionCycle: return HandleExpressionCycle();
                case FlutterCommands.AvatarCommand: return HandleAvatarCommand(payloadJson);

                case FlutterCommands.ConversationSend: return HandleConversationSend(payloadJson);
                case FlutterCommands.ConversationSendWithCamera: return HandleConversationSendWithCamera(payloadJson);
                case FlutterCommands.ConversationInterrupt: return HandleConversationInterrupt();

                case FlutterCommands.VoiceToggleListen: return HandleVoiceToggleListen();
                case FlutterCommands.VoiceToggleRecord: return HandleVoiceToggleRecord();
                case FlutterCommands.VoiceRestart: return HandleVoiceRestart();
                case FlutterCommands.VoiceCancel: return HandleVoiceCancel();

                case FlutterCommands.PairingSetServer: return HandlePairingSetServer(payloadJson);
                case FlutterCommands.PairingSetPrivateHttp: return HandlePairingSetPrivateHttp(payloadJson);
                case FlutterCommands.PairingDigit: return HandlePairingDigit(payloadJson);
                case FlutterCommands.PairingPair: return HandlePairingPair();
                case FlutterCommands.PairingReconnect: return HandlePairingReconnect();
                case FlutterCommands.PairingClearBinding: return HandlePairingClearBinding();

                case FlutterCommands.QualityApplyPreset: return HandleQualityApplyPreset(payloadJson);
                case FlutterCommands.QualityApplyPhysics: return HandleQualityApplyPhysics(payloadJson);
                case FlutterCommands.QualityReset: return HandleQualityReset();

                case FlutterCommands.SettingsTargetFps: return HandleSettingsTargetFps(payloadJson);
                case FlutterCommands.SettingsVolume: return HandleSettingsVolume(payloadJson);
                case FlutterCommands.SettingsToggle: return HandleSettingsToggle(payloadJson);

                case FlutterCommands.CopresenceEnterScene: return HandleCopresenceEnterScene(payloadJson);
                case FlutterCommands.CopresenceReturnToMenu: return HandleCopresenceReturnToMenu();
                case FlutterCommands.CopresenceSwitchMode: return HandleCopresenceSwitchMode(payloadJson);
                case FlutterCommands.CopresenceSwitchEnvironment: return HandleCopresenceSwitchEnvironment(payloadJson);
                case FlutterCommands.CopresenceSetChromeInsets: return HandleCopresenceSetChromeInsets(payloadJson);
                case FlutterCommands.CopresenceArPlace: return HandleCopresenceArPlace(payloadJson);

                case FlutterCommands.SceneMoveMode: return HandleSceneMoveMode();
                case FlutterCommands.SceneReframe: return HandleSceneReframe();
                case FlutterCommands.SceneHud: return HandleSceneHud();

                case FlutterCommands.UpdateCheck: return HandleUpdateCheckCommand();
                case FlutterCommands.UpdateInstall: return HandleUpdateInstallCommand();

                case FlutterCommands.LogRefresh: return HandleLogRefresh();
                case FlutterCommands.LogClear: return HandleLogClear();

                case FlutterCommands.QaCommand: return HandleQaCommand(payloadJson);

                default:
                    return FlutterCommandResult.Failure("Unknown Flutter command: " + name);
            }
        }

        // ------------------------------------------------------------------
        // Model commands
        // ------------------------------------------------------------------

        private FlutterCommandResult DiscoverModels()
        {
            var payload = new FlutterModelListPayload
            {
                models = BuildModelInfoDtos()
            };
            if (!FlutterMessageProtocol.TrySerializePayload(payload, out var json, out var error))
            {
                return FlutterCommandResult.Failure(error);
            }
            return FlutterCommandResult.Success(json);
        }

        private FlutterCommandResult HandleModelLoad(string payloadJson)
        {
            var loader = ModelLoader;
            if (loader == null)
            {
                return FlutterCommandResult.Failure("模型加载器不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<ModelLoadPayload>(payloadJson);
            var path = payload == null ? string.Empty : payload.path;
            if (string.IsNullOrWhiteSpace(path))
            {
                return FlutterCommandResult.Failure("缺少模型路径");
            }
            if (FindModel(path) == null)
            {
                return FlutterCommandResult.Failure("未找到指定模型");
            }
            _ = LoadModelAsync(path);
            return FlutterCommandResult.Success();
        }

        private async Task<bool> LoadModelAsync(string path)
        {
            try
            {
                var loader = ModelLoader;
                if (loader == null)
                {
                    PublishToast("模型加载器不可用");
                    return false;
                }
                var model = FindModel(path);
                if (model != null)
                {
                    await loader.LoadInstalledModelAsync(model);
                    return true;
                }
                await loader.LoadFromFileAsync(path);
                return true;
            }
            catch (Exception exception)
            {
                PublishToast("模型加载失败：" + exception.Message);
                return false;
            }
        }

        private FlutterCommandResult HandleModelDelete(string payloadJson)
        {
            var payload = FlutterMessageProtocol.DeserializePayload<ModelDeletePayload>(payloadJson);
            var loader = ModelLoader;
            var model = FindModel(payload == null ? null : payload.path);
            if (loader == null)
            {
                return FlutterCommandResult.Failure("模型加载器不可用");
            }
            if (model == null)
            {
                return FlutterCommandResult.Failure("未找到指定模型");
            }
            var deleted = loader.DeleteInstalledPackage(model);
            PublishToast(deleted ? "已删除模型" : "删除模型失败");
            if (deleted)
            {
                PublishModelUpdated();
            }
            return deleted ? FlutterCommandResult.Success() : FlutterCommandResult.Failure("删除模型失败");
        }

        private FlutterCommandResult HandleModelImport()
        {
            if (FileImport == null)
            {
                return FlutterCommandResult.Failure("文件导入不可用");
            }
            if (!FileImport.OpenPicker())
            {
                return FlutterCommandResult.Failure("无法打开系统文件选择器");
            }
            return FlutterCommandResult.Success();
        }

        // ------------------------------------------------------------------
        // Action commands
        // ------------------------------------------------------------------

        private FlutterCommandResult HandleActionRefresh()
        {
            if (VmdActions == null)
            {
                return FlutterCommandResult.Failure("动作库不可用");
            }
            _ = VmdActions.RefreshAsync();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleActionPlay(string payloadJson)
        {
            if (VmdActions == null)
            {
                return FlutterCommandResult.Failure("动作库不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<ActionPlayPayload>(payloadJson);
            if (string.IsNullOrWhiteSpace(payload == null ? null : payload.id))
            {
                return FlutterCommandResult.Failure("缺少动作 id");
            }
            _ = VmdActions.PlayAsync(payload.id);
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleActionStop()
        {
            if (VmdActions == null)
            {
                return FlutterCommandResult.Failure("动作库不可用");
            }
            VmdActions.StopAndReturnToIdle();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleActionDelete(string payloadJson)
        {
            if (VmdActions == null)
            {
                return FlutterCommandResult.Failure("动作库不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<ActionDeletePayload>(payloadJson);
            if (payload == null || string.IsNullOrWhiteSpace(payload.id))
            {
                return FlutterCommandResult.Failure("缺少动作 id");
            }
            _ = VmdActions.DeleteActionAsync(payload.id);
            return FlutterCommandResult.Success();
        }

        // ------------------------------------------------------------------
        // Idle / expression / avatar commands
        // ------------------------------------------------------------------

        private FlutterCommandResult HandleIdleCycle()
        {
            if (IdlePose == null || owner == null || owner.Avatar == null)
            {
                return FlutterCommandResult.Failure("待机控制不可用");
            }
            IdlePose.CyclePreset(1);
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleExpressionCycle()
        {
            if (Conversation == null)
            {
                return FlutterCommandResult.Failure("表情控制不可用");
            }
            expressionIndex = (expressionIndex + 1) % ExpressionIds.Length;
            Conversation.SetManualExpression(ExpressionIds[expressionIndex]);
            PublishToast("表情：" + ExpressionIds[expressionIndex]);
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleAvatarCommand(string payloadJson)
        {
            var payload = FlutterMessageProtocol.DeserializePayload<AvatarCommandPayload>(payloadJson);
            if (owner == null || payload == null || string.IsNullOrWhiteSpace(payload.name))
            {
                return FlutterCommandResult.Failure("缺少 avatar.command name");
            }
            if (owner.Avatar == null)
            {
                return FlutterCommandResult.Failure("模型尚未加载");
            }
            owner.SendCommand(new AvatarCommand { name = payload.name });
            return FlutterCommandResult.Success();
        }

        // ------------------------------------------------------------------
        // Conversation / voice commands
        // ------------------------------------------------------------------

        private FlutterCommandResult HandleConversationSend(string payloadJson)
        {
            var payload = FlutterMessageProtocol.DeserializePayload<ConversationSendPayload>(payloadJson);
            if (Conversation == null)
            {
                return FlutterCommandResult.Failure("对话控制器不可用");
            }
            if (!Conversation.IsRealBackendConnected && !Conversation.IsUsingMockTransport)
            {
                return FlutterCommandResult.Failure("对话后端尚未连接");
            }
            var text = payload == null ? string.Empty : payload.text;
            if (text.Length > 4000)
            {
                return FlutterCommandResult.Failure("消息长度超过限制");
            }
            var attachment = payload == null ? string.Empty : payload.attachment;
            if (!string.IsNullOrWhiteSpace(attachment))
            {
                if (!TryValidateImageAttachment(attachment, out var attachmentError))
                {
                    return FlutterCommandResult.Failure(attachmentError);
                }
                Conversation.StartConversation(text, new TurnImageAttachment
                {
                    data_base64 = attachment,
                    purpose = RealityCameraTurn.ComposeFrameText(text)
                });
            }
            else
            {
                Conversation.StartConversation(text);
            }
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleConversationSendWithCamera(string payloadJson)
        {
#if !BANXIA_PHONE
            return FlutterCommandResult.Failure("随身摄像头单帧仅支持手机端");
#else
            var payload = FlutterMessageProtocol.DeserializePayload<ConversationSendPayload>(payloadJson);
            var input = payload == null ? string.Empty : payload.text;
            if (Conversation == null || AstrBot == null || !AstrBot.IsConnected)
            {
                return FlutterCommandResult.Failure("真实后端尚未连接");
            }
            if (PlayerPrefs.GetInt(CameraPrefKey, 0) != 1)
            {
                return FlutterCommandResult.Failure("请先在设置中开启摄像头单帧");
            }
            _ = CaptureCameraFrameAndSendAsync(input);
            return FlutterCommandResult.Success();
#endif
        }

#if BANXIA_PHONE
        private async Task CaptureCameraFrameAndSendAsync(string input)
        {
            try
            {
                PublishToast("正在拍摄一帧（不会保存）…");
                var (frame, failureReason) = await PhoneRealityCameraSnapshot.CaptureSingleFrameAsync();
                if (frame != null)
                {
                    var frameText = RealityCameraTurn.ComposeFrameText(input);
                    Conversation?.StartConversation(frameText, new TurnImageAttachment
                    {
                        data_base64 = frame.JpegBase64,
                        purpose = frameText
                    });
                    return;
                }
                var receipt = string.IsNullOrEmpty(input)
                    ? RealityCameraTurn.ComposeFailureReceipt(failureReason)
                    : input + "\n" + RealityCameraTurn.ComposeFailureReceipt(failureReason);
                Conversation?.StartConversation(receipt);
            }
            catch (Exception exception)
            {
                PublishToast("摄像头单帧失败：" + exception.Message);
            }
        }
#endif

        private FlutterCommandResult HandleConversationInterrupt()
        {
            if (Conversation == null)
            {
                return FlutterCommandResult.Failure("对话控制器不可用");
            }
            Conversation.Interrupt();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleVoiceToggleListen()
        {
            if (VoiceInput == null)
            {
                return FlutterCommandResult.Failure("语音输入不可用");
            }
            VoiceInput.ToggleAlwaysListening();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleVoiceToggleRecord()
        {
            if (VoiceInput == null)
            {
                return FlutterCommandResult.Failure("语音输入不可用");
            }
            VoiceInput.ToggleRecording();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleVoiceRestart()
        {
            if (VoiceInput == null)
            {
                return FlutterCommandResult.Failure("语音输入不可用");
            }
            VoiceInput.RestartMonitoring();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleVoiceCancel()
        {
            if (VoiceInput == null && Conversation == null)
            {
                return FlutterCommandResult.Failure("语音输入不可用");
            }
            if (VoiceInput != null && VoiceInput.IsRecording)
            {
                VoiceInput.CancelRecording();
            }
            else
            {
                Conversation?.CancelVoiceInput();
            }
            return FlutterCommandResult.Success();
        }

        // ------------------------------------------------------------------
        // Pairing commands
        // ------------------------------------------------------------------

        private FlutterCommandResult HandlePairingSetServer(string payloadJson)
        {
            if (Pairing == null)
            {
                return FlutterCommandResult.Failure("配对控制器不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<PairingSetServerPayload>(payloadJson);
            var server = payload == null ? string.Empty : payload.server;
            if (Pairing.TrySetPairingServer(server, out var reason))
            {
                PublishPairingStatus();
                return FlutterCommandResult.Success();
            }
            return FlutterCommandResult.Failure(reason);
        }

        private FlutterCommandResult HandlePairingSetPrivateHttp(string payloadJson)
        {
            if (Pairing == null)
            {
                return FlutterCommandResult.Failure("配对控制器不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<PairingSetPrivateHttpPayload>(payloadJson);
            if (payload == null)
            {
                return FlutterCommandResult.Failure("缺少 privateHttp 设置值");
            }
            Pairing.SetPrivateHttpAllowed(payload.enabled);
            PublishPairingStatus();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandlePairingDigit(string payloadJson)
        {
            if (Pairing == null)
            {
                return FlutterCommandResult.Failure("配对控制器不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<PairingDigitPayload>(payloadJson);
            if (payload == null || string.IsNullOrWhiteSpace(payload.op))
            {
                return FlutterCommandResult.Failure("缺少配对键盘操作");
            }
            switch (payload.op)
            {
                case "append":
                    if (string.IsNullOrEmpty(payload.digit) || payload.digit.Length != 1 ||
                        payload.digit[0] < '0' || payload.digit[0] > '9')
                    {
                        return FlutterCommandResult.Failure("配对数字必须是单个 0-9 字符");
                    }
                    if (pairingCodeBuffer.Length >= PairingCodeLength)
                    {
                        return FlutterCommandResult.Failure("配对码最多 6 位");
                    }
                    pairingCodeBuffer += payload.digit[0];
                    break;
                case "remove":
                    if (pairingCodeBuffer.Length > 0)
                    {
                        pairingCodeBuffer = pairingCodeBuffer.Substring(0, pairingCodeBuffer.Length - 1);
                    }
                    break;
                case "clear":
                    pairingCodeBuffer = string.Empty;
                    break;
                default:
                    return FlutterCommandResult.Failure("未知配对键盘操作：" + payload.op);
            }
            PublishPairingStatus();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandlePairingPair()
        {
            if (Pairing == null)
            {
                return FlutterCommandResult.Failure("配对控制器不可用");
            }
            if (pairingCodeBuffer.Length != PairingCodeLength)
            {
                return FlutterCommandResult.Failure("请输入完整的 6 位配对码");
            }
            if (Pairing.IsBusy)
            {
                return FlutterCommandResult.Failure("配对请求正在进行");
            }
            if (string.IsNullOrWhiteSpace(Pairing.PairingServerEndpoint))
            {
                return FlutterCommandResult.Failure("请先设置配对服务器");
            }
            Pairing.PairWithCode(pairingCodeBuffer);
            pairingCodeBuffer = string.Empty;
            PublishPairingStatus();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandlePairingReconnect()
        {
            if (AstrBot == null)
            {
                return FlutterCommandResult.Failure("后端桥不可用");
            }
            if (!AstrBot.ReloadConfiguration())
            {
                return FlutterCommandResult.Failure("后端配置不可用");
            }
            PublishToast("正在重新连接后端");
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandlePairingClearBinding()
        {
            if (AstrBot == null)
            {
                return FlutterCommandResult.Failure("后端桥不可用");
            }
            try
            {
                var path = AstrBot.ConfigurationPath;
                if (string.IsNullOrWhiteSpace(path))
                {
                    return FlutterCommandResult.Failure("后端配置路径为空");
                }
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                var legacyPath = Path.Combine(
                    Application.persistentDataPath,
                    AstrBotBridge.LegacyConfigurationFileName);
                if (!string.Equals(path, legacyPath, StringComparison.Ordinal) &&
                    File.Exists(legacyPath))
                {
                    File.Delete(legacyPath);
                }
            }
            catch (Exception exception)
            {
                PublishToast("解除绑定失败：" + exception.Message);
                return FlutterCommandResult.Failure("解除绑定失败");
            }
            AstrBot.ReloadConfiguration();
            Pairing?.ClearPairingServer();
            pairingCodeBuffer = string.Empty;
            PublishPairingStatus();
            PublishToast("已解除后端绑定");
            return FlutterCommandResult.Success();
        }

        // ------------------------------------------------------------------
        // Quality / settings commands
        // ------------------------------------------------------------------

        private FlutterCommandResult HandleQualityApplyPreset(string payloadJson)
        {
            if (Quality == null)
            {
                return FlutterCommandResult.Failure("画质设置不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<QualityApplyPresetPayload>(payloadJson);
            if (TryParseQualityPreset(payload == null ? string.Empty : payload.preset, out var preset))
            {
                Quality.ApplyPreset(preset);
                return FlutterCommandResult.Success();
            }
            return FlutterCommandResult.Failure("未知画质预设：" + (payload == null ? string.Empty : payload.preset));
        }

        private FlutterCommandResult HandleQualityApplyPhysics(string payloadJson)
        {
            if (Quality == null)
            {
                return FlutterCommandResult.Failure("画质设置不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<QualityApplyPhysicsPayload>(payloadJson);
            if (TryParsePhysicsPreset(payload == null ? string.Empty : payload.preset, out var preset))
            {
                Quality.ApplyPhysicsPreset(preset);
                return FlutterCommandResult.Success();
            }
            return FlutterCommandResult.Failure("未知物理预设：" + (payload == null ? string.Empty : payload.preset));
        }

        private FlutterCommandResult HandleSettingsTargetFps(string payloadJson)
        {
            var payload = FlutterMessageProtocol.DeserializePayload<SettingsTargetFpsPayload>(payloadJson);
            var fps = payload == null ? 0 : payload.fps;
            if (fps != 30 && fps != 60 && fps != 120)
            {
                return FlutterCommandResult.Failure("目标帧率仅支持 30/60/120");
            }
            Application.targetFrameRate = fps;
            PlayerPrefs.SetInt(FpsPrefKey, fps);
            PlayerPrefs.Save();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleSettingsVolume(string payloadJson)
        {
            var payload = FlutterMessageProtocol.DeserializePayload<SettingsVolumePayload>(payloadJson);
            if (payload == null || float.IsNaN(payload.v) || float.IsInfinity(payload.v) ||
                payload.v < 0f || payload.v > 1f)
            {
                return FlutterCommandResult.Failure("音量必须是 0 到 1 之间的数值");
            }
            AudioListener.volume = payload.v;
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleQualityReset()
        {
            if (Quality == null)
            {
                return FlutterCommandResult.Failure("画质设置不可用");
            }
            Quality.ResetToDefault();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleSettingsToggle(string payloadJson)
        {
            var payload = FlutterMessageProtocol.DeserializePayload<SettingsTogglePayload>(payloadJson);
            if (payload == null || string.IsNullOrWhiteSpace(payload.key))
            {
                return FlutterCommandResult.Failure("缺少设置项 key");
            }
#if !BANXIA_PHONE
            if (payload.key == "hud" || payload.key == "framingGrid" || payload.key == "camera")
            {
                return FlutterCommandResult.Failure("该设置仅支持手机端");
            }
#endif
            var value = payload.value;
            switch (payload.key)
            {
                case "hud":
                    if (PhoneHud == null)
                    {
                        return FlutterCommandResult.Failure("HUD 不可用");
                    }
                    PlayerPrefs.SetInt(HudPrefKey, value ? 1 : 0);
                    PlayerPrefs.Save();
                    PhoneHud.SetVisible(value);
                    return FlutterCommandResult.Success();
                case "framingGrid":
                    if (PhoneHud == null)
                    {
                        return FlutterCommandResult.Failure("构图网格不可用");
                    }
                    PlayerPrefs.SetInt(FramingGridPrefKey, value ? 1 : 0);
                    PlayerPrefs.Save();
                    PhoneHud.SetFramingGridVisible(value);
                    return FlutterCommandResult.Success();
                case "camera":
                    PlayerPrefs.SetInt(CameraPrefKey, value ? 1 : 0);
                    PlayerPrefs.Save();
                    return FlutterCommandResult.Success();
                default:
                    return FlutterCommandResult.Failure("未知设置项：" + payload.key);
            }
        }

        // ------------------------------------------------------------------
        // Co-presence / scene commands
        // ------------------------------------------------------------------

        private FlutterCommandResult HandleCopresenceEnterScene(string payloadJson)
        {
            if (CoPresence == null)
            {
                return FlutterCommandResult.Failure("同框导演不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<CopresenceEnterScenePayload>(payloadJson);
            var path = payload == null ? string.Empty : payload.path;
            if (!string.IsNullOrWhiteSpace(path))
            {
                if (FindModel(path) == null)
                {
                    return FlutterCommandResult.Failure("未找到指定模型");
                }
                _ = LoadModelThenEnterSceneAsync(path);
                return FlutterCommandResult.Success();
            }
            CoPresence.ApplyOnEnterScene();
            PublishCopresenceMode();
            PollArPlacement();
            PollCallTimer();
            PollFraming();
            return FlutterCommandResult.Success();
        }

        private async Task LoadModelThenEnterSceneAsync(string path)
        {
            if (await LoadModelAsync(path))
            {
                CoPresence?.ApplyOnEnterScene();
            }
        }

        private FlutterCommandResult HandleCopresenceReturnToMenu()
        {
            if (CoPresence == null)
            {
                return FlutterCommandResult.Failure("同框导演不可用");
            }
            CoPresence.Suspend();
            PublishCopresenceMode();
            PublishPlacementChanged(false);
            PollCallTimer();
            PublishEvent(FlutterEvents.FramingAnchors, new FlutterFramingAnchorsPayload { valid = false });
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleCopresenceSwitchMode(string payloadJson)
        {
            if (CoPresence == null)
            {
                return FlutterCommandResult.Failure("同框导演不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<CopresenceSwitchModePayload>(payloadJson);
            if (!TryParseCopresenceMode(payload == null ? string.Empty : payload.mode, out var mode))
            {
                return FlutterCommandResult.Failure("未知同框模式：" + (payload == null ? string.Empty : payload.mode));
            }
            if (!CoPresence.SwitchMode(mode))
            {
                return FlutterCommandResult.Failure("同框模式切换不可用：相机未就绪或操作正在进行");
            }
            PublishCopresenceMode();
            PublishPlacementChanged(CoPresence.ArPlaced);
            PollCallTimer();
            PollFraming();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleCopresenceSwitchEnvironment(string payloadJson)
        {
            if (CoPresence == null)
            {
                return FlutterCommandResult.Failure("同框导演不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<CopresenceSwitchEnvironmentPayload>(payloadJson);
            if (!TryParseVirtualEnvironment(payload == null ? string.Empty : payload.env, out var environment))
            {
                return FlutterCommandResult.Failure("未知虚拟场景：" + (payload == null ? string.Empty : payload.env));
            }
            CoPresence.SwitchEnvironment(environment);
            PublishCopresenceMode();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleCopresenceSetChromeInsets(string payloadJson)
        {
            if (CoPresence == null)
            {
                return FlutterCommandResult.Failure("同框导演不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<CopresenceSetChromeInsetsPayload>(payloadJson);
            var camera = CoPresence.MainCamera;
            var screenHeight = camera != null && camera.pixelHeight > 1 ? camera.pixelHeight : Screen.height;
            if (payload == null || float.IsNaN(payload.top) || float.IsInfinity(payload.top) ||
                float.IsNaN(payload.bottom) || float.IsInfinity(payload.bottom) ||
                screenHeight <= 1f || payload.top < 0f || payload.bottom <= payload.top ||
                payload.bottom > screenHeight)
            {
                return FlutterCommandResult.Failure("通话安全区参数无效");
            }
            CoPresence.SetChromeInsets(payload.top, payload.bottom);
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleCopresenceArPlace(string payloadJson)
        {
            if (CoPresence == null)
            {
                return FlutterCommandResult.Failure("同框导演不可用");
            }
            var payload = FlutterMessageProtocol.DeserializePayload<CopresenceArPlacePayload>(payloadJson);
            var camera = CoPresence.MainCamera;
            var screenWidth = camera != null && camera.pixelWidth > 1 ? camera.pixelWidth : Screen.width;
            var screenHeight = camera != null && camera.pixelHeight > 1 ? camera.pixelHeight : Screen.height;
            if (payload == null || float.IsNaN(payload.x) || float.IsInfinity(payload.x) ||
                float.IsNaN(payload.y) || float.IsInfinity(payload.y) ||
                screenWidth <= 1f || screenHeight <= 1f || payload.x < 0f || payload.y < 0f ||
                payload.x > screenWidth || payload.y > screenHeight)
            {
                return FlutterCommandResult.Failure("放置坐标超出相机视口");
            }
            // Flutter sends top-origin physical pixels; Unity ScreenPointToRay uses
            // bottom-origin pixels, so convert only the vertical axis here.
            var unityY = screenHeight - payload.y;
            if (!CoPresence.PlaceAvatarAtScreenPoint(new Vector2(payload.x, unityY)))
            {
                return FlutterCommandResult.Failure("当前同框模式无法放置角色");
            }
            PublishPlacementChanged(true);
            PublishCopresenceMode();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleSceneMoveMode()
        {
#if !BANXIA_PHONE
            return FlutterCommandResult.Failure("场景移动模式仅支持手机端");
#else
            if (OrbitCamera == null)
            {
                return FlutterCommandResult.Failure("轨道相机不可用");
            }
            OrbitCamera.SingleFingerMovesAvatar = !OrbitCamera.SingleFingerMovesAvatar;
            return FlutterCommandResult.Success();
#endif
        }

        private FlutterCommandResult HandleSceneReframe()
        {
#if !BANXIA_PHONE
            return FlutterCommandResult.Failure("场景取景仅支持手机端");
#else
            if (OrbitCamera == null)
            {
                return FlutterCommandResult.Failure("轨道相机不可用");
            }
            OrbitCamera.Reframe();
            return FlutterCommandResult.Success();
#endif
        }

        private FlutterCommandResult HandleSceneHud()
        {
#if !BANXIA_PHONE
            return FlutterCommandResult.Failure("场景 HUD 仅支持手机端");
#else
            if (PhoneHud == null)
            {
                return FlutterCommandResult.Failure("HUD 不可用");
            }
            PhoneHud.ToggleVisible();
            return FlutterCommandResult.Success();
#endif
        }

        // ------------------------------------------------------------------
        // Update / log / QA commands
        // ------------------------------------------------------------------

        private FlutterCommandResult HandleUpdateCheckCommand()
        {
            if (UpdateChecker == null)
            {
                return FlutterCommandResult.Failure("更新检查不可用");
            }
            _ = CheckForUpdateAsync();
            return FlutterCommandResult.Success();
        }

        private async Task CheckForUpdateAsync()
        {
            var checker = UpdateChecker;
            if (checker == null)
            {
                return;
            }
            PublishUpdateStatus("checking", 0f);
            try
            {
                lastUpdateInfo = await checker.CheckForUpdateAsync();
                if (lastUpdateInfo != null && lastUpdateInfo.HasUpdate)
                {
                    PublishToast("发现新版本 " + lastUpdateInfo.Version);
                }
                else
                {
                    PublishToast("已是最新版本");
                }
                PublishUpdateStatus("idle", 0f);
            }
            catch (Exception exception)
            {
                PublishToast("检查更新失败：" + exception.Message);
                PublishUpdateStatus("idle", 0f);
            }
        }

        private FlutterCommandResult HandleUpdateInstallCommand()
        {
            if (UpdateChecker == null)
            {
                return FlutterCommandResult.Failure("更新检查不可用");
            }
            if (lastUpdateInfo == null)
            {
                return FlutterCommandResult.Failure("请先检查更新");
            }
            _ = InstallUpdateAsync(lastUpdateInfo);
            return FlutterCommandResult.Success();
        }

        private async Task InstallUpdateAsync(BanxiaUpdateChecker.UpdateInfo update)
        {
            var checker = UpdateChecker;
            if (checker == null || update == null)
            {
                return;
            }
            try
            {
                PublishUpdateStatus("downloading", 0f);
                var error = await checker.DownloadAndInstallAsync(
                    update,
                    progress => PublishUpdateStatus("downloading", progress));
                if (!string.IsNullOrWhiteSpace(error))
                {
                    PublishToast(error);
                    PublishUpdateStatus("idle", 0f);
                }
            }
            catch (Exception exception)
            {
                PublishToast("安装更新失败：" + exception.Message);
                PublishUpdateStatus("idle", 0f);
            }
        }

        private FlutterCommandResult HandleLogClear()
        {
            if (DebugLog == null)
            {
                return FlutterCommandResult.Failure("诊断日志不可用");
            }
            DebugLog.Clear();
            return FlutterCommandResult.Success();
        }

        private FlutterCommandResult HandleLogRefresh()
        {
            if (DebugLog == null)
            {
                return FlutterCommandResult.Failure("诊断日志不可用");
            }
            var text = DebugLog.GetRecentText(LogRefreshLineCount);
            var lines = string.IsNullOrEmpty(text) ? new string[0] : text.Split('\n');
            if (!FlutterMessageProtocol.TrySerializePayload(
                new FlutterLogUpdatedPayload { lines = lines }, out var json, out var error))
            {
                return FlutterCommandResult.Failure(error);
            }
            return FlutterCommandResult.Success(json);
        }

        private FlutterCommandResult HandleQaCommand(string payloadJson)
        {
            var payload = FlutterMessageProtocol.DeserializePayload<QaCommandPayload>(payloadJson);
            var name = payload == null ? string.Empty : payload.name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return FlutterCommandResult.Failure("缺少 QA 命令名称");
            }
            Debug.Log("[BanxiaQA] qa.command=" + name);
            switch (name)
            {
                case FlutterQaCommands.LoadFirstModel:
                    if (ModelLoader == null)
                    {
                        return FlutterCommandResult.Failure("模型加载器不可用");
                    }
                    if (ModelLoader.DiscoverInstalledModels().Count == 0)
                    {
                        return FlutterCommandResult.Failure("未发现可用模型");
                    }
                    _ = LoadFirstModelForQaAsync();
                    return FlutterCommandResult.Success();
                case FlutterQaCommands.OpenImport:
                    return HandleModelImport();
                case FlutterQaCommands.SendText:
                {
                    if (Conversation == null)
                    {
                        return FlutterCommandResult.Failure("对话控制器不可用");
                    }
                    var args = FlutterMessageProtocol.DeserializePayload<QaSendTextArgs>(payload == null ? null : payload.args);
                    var text = args == null ? string.Empty : args.text;
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        return FlutterCommandResult.Failure("QA 文本不能为空");
                    }
                    if (!Conversation.IsRealBackendConnected && !Conversation.IsUsingMockTransport)
                    {
                        return FlutterCommandResult.Failure("对话后端尚未连接");
                    }
                    Conversation.StartConversation(text);
                    return FlutterCommandResult.Success();
                }
                case FlutterQaCommands.RunVmdQa:
                    if (VmdActions == null)
                    {
                        return FlutterCommandResult.Failure("动作库不可用");
                    }
                    _ = VmdActions.PlayRecommendedDanceAsync();
                    return FlutterCommandResult.Success();
                case FlutterQaCommands.ToggleMenu:
                    return FlutterCommandResult.Failure("QA 菜单命令需通过 Quest 硬件菜单入口触发");
                case FlutterQaCommands.OpenModelList:
                case FlutterQaCommands.CaptureFirstModel:
                case FlutterQaCommands.SimulateContactForQa:
                case FlutterQaCommands.OpenWorldUi:
                case FlutterQaCommands.OpenTextInput:
                case FlutterQaCommands.RunPerformanceQa:
                    return FlutterCommandResult.Failure("该 QA 命令尚未接入 Flutter 运行时路由");
                default:
                    return FlutterCommandResult.Failure("未知 QA 命令：" + name);
            }
        }

        private async Task LoadFirstModelForQaAsync()
        {
            var loader = ModelLoader;
            if (loader == null)
            {
                return;
            }
            var models = loader.DiscoverInstalledModels();
            if (models.Count == 0)
            {
                PublishToast("未发现可用模型");
                return;
            }
            try
            {
                await loader.LoadInstalledModelAsync(models[0]);
            }
            catch (Exception exception)
            {
                PublishToast("QA 模型加载失败：" + exception.Message);
            }
        }

        // ------------------------------------------------------------------
        // Service event subscriptions → Flutter events
        // ------------------------------------------------------------------

        private void SubscribeToServices()
        {
            if (Pairing != null)
            {
                Pairing.StatusChanged -= HandlePairingStatusChanged;
                Pairing.StatusChanged += HandlePairingStatusChanged;
            }
            if (Conversation != null)
            {
                Conversation.StateChanged -= HandleConversationStateChanged;
                Conversation.StateChanged += HandleConversationStateChanged;
            }
            if (ModelLoader != null)
            {
                ModelLoader.AvatarLoaded -= HandleAvatarLoaded;
                ModelLoader.AvatarLoaded += HandleAvatarLoaded;
                ModelLoader.LoadFailed -= HandleLoadFailed;
                ModelLoader.LoadFailed += HandleLoadFailed;
                ModelLoader.ProgressChanged -= HandleModelProgress;
                ModelLoader.ProgressChanged += HandleModelProgress;
            }
            if (VmdActions != null)
            {
                VmdActions.ActionsChanged -= HandleActionsChanged;
                VmdActions.ActionsChanged += HandleActionsChanged;
                VmdActions.PlaybackChanged -= HandlePlaybackChanged;
                VmdActions.PlaybackChanged += HandlePlaybackChanged;
                VmdActions.OperationFailed -= HandleActionOperationFailed;
                VmdActions.OperationFailed += HandleActionOperationFailed;
            }
            if (Quality != null)
            {
                Quality.QualityChanged -= HandleQualityChanged;
                Quality.QualityChanged += HandleQualityChanged;
            }
            if (CoPresence != null)
            {
                CoPresence.ModeChanged -= HandleCopresenceModeChanged;
                CoPresence.ModeChanged += HandleCopresenceModeChanged;
                CoPresence.EnvironmentChanged -= HandleCopresenceEnvironmentChanged;
                CoPresence.EnvironmentChanged += HandleCopresenceEnvironmentChanged;
            }
            if (FileImport != null)
            {
                FileImport.StatusChanged -= HandleImportStatusChanged;
                FileImport.StatusChanged += HandleImportStatusChanged;
            }
        }

        private BanxiaUpdateChecker EnsureUpdateChecker()
        {
            if (owner == null)
            {
                return null;
            }
            return owner.GetComponent<BanxiaUpdateChecker>() ?? owner.gameObject.AddComponent<BanxiaUpdateChecker>();
        }

        private void UnsubscribeAll()
        {
            if (Pairing != null)
            {
                Pairing.StatusChanged -= HandlePairingStatusChanged;
            }
            if (Conversation != null)
            {
                Conversation.StateChanged -= HandleConversationStateChanged;
            }
            if (ModelLoader != null)
            {
                ModelLoader.AvatarLoaded -= HandleAvatarLoaded;
                ModelLoader.LoadFailed -= HandleLoadFailed;
                ModelLoader.ProgressChanged -= HandleModelProgress;
            }
            if (VmdActions != null)
            {
                VmdActions.ActionsChanged -= HandleActionsChanged;
                VmdActions.PlaybackChanged -= HandlePlaybackChanged;
                VmdActions.OperationFailed -= HandleActionOperationFailed;
            }
            if (Quality != null)
            {
                Quality.QualityChanged -= HandleQualityChanged;
            }
            if (CoPresence != null)
            {
                CoPresence.ModeChanged -= HandleCopresenceModeChanged;
                CoPresence.EnvironmentChanged -= HandleCopresenceEnvironmentChanged;
            }
            if (FileImport != null)
            {
                FileImport.StatusChanged -= HandleImportStatusChanged;
            }
        }

        private void HandlePairingStatusChanged()
        {
            PublishPairingStatus();
            PollConnection();
        }

        private void HandleConversationStateChanged(ConversationState state)
        {
            PublishEvent(FlutterEvents.ConversationState, new FlutterConversationStatePayload
            {
                state = state.ToString(),
                transportStatus = Conversation == null ? string.Empty : Conversation.TransportStatus,
                lastError = Conversation == null ? string.Empty : (Conversation.LastErrorCode ?? string.Empty)
            });
        }

        private void HandleAvatarLoaded(AvatarController avatar)
        {
            // Flutter model.load can enter the scene after the loader callback;
            // make the final presentation own the loaded model rather than a
            // camera target left over from the previous avatar.
            if (avatar != null && OrbitCamera != null)
            {
                OrbitCamera.SetTrackedAvatar(avatar.transform);
                OrbitCamera.FrameModel(avatar.gameObject);
                CoPresence?.SetAvatar(avatar.transform);
            }
            PublishModelUpdated();
            PublishToast("模型已加载");
        }

        private void HandleLoadFailed(string message)
        {
            PublishImportStatus(message ?? "模型加载失败");
            PublishToast(message ?? "模型加载失败");
        }

        private void HandleModelProgress(string message)
        {
            PublishImportStatus(message);
        }

        private void HandleActionsChanged()
        {
            PublishActionUpdated();
        }

        private void HandlePlaybackChanged()
        {
            PublishEvent(FlutterEvents.ActionPlaybackChanged, new FlutterPlaybackChangedPayload
            {
                playingId = VmdActions == null ? string.Empty : VmdActions.CurrentActionId
            });
        }

        private void HandleActionOperationFailed(string message)
        {
            PublishToast(message ?? "动作操作失败");
        }

        private void HandleQualityChanged(QuestQualityPreset preset)
        {
            PublishQualityChanged();
        }

        private void HandleCopresenceModeChanged(CoPresenceMode mode)
        {
            PublishCopresenceMode();
        }

        private void HandleCopresenceEnvironmentChanged(VirtualEnvironment environment)
        {
            PublishCopresenceMode();
        }

        private void HandleImportStatusChanged(string status)
        {
            status = string.IsNullOrWhiteSpace(status) && FileImport != null ? FileImport.Status : status;
            PublishImportStatus(status);
            if (!string.IsNullOrWhiteSpace(status))
            {
                PublishToast(status);
            }
        }

        // ------------------------------------------------------------------
        // Event publishers
        // ------------------------------------------------------------------

        private void PublishEvent<T>(string eventName, T payload)
        {
            bridge?.PublishEvent(eventName, payload);
        }

        private void PublishToast(string message)
        {
            bridge?.PublishToast(message);
        }

        private void PublishPairingStatus()
        {
            PublishEvent(FlutterEvents.PairingStatus, new FlutterPairingStatusPayload
            {
                status = Pairing == null ? string.Empty : Pairing.Status,
                server = Pairing == null ? string.Empty :
                    BackendPairingProtocol.GetServerEntry(Pairing.PairingServerEndpoint),
                privateHttp = Pairing != null && Pairing.PrivateHttpAllowed,
                codeLen = pairingCodeBuffer.Length
            });
        }

        private void PublishModelUpdated()
        {
            PublishEvent(FlutterEvents.ModelUpdated, new FlutterModelUpdatedPayload
            {
                models = BuildModelInfoDtos(),
                currentPath = ModelLoader == null ? string.Empty : ModelLoader.CurrentModelPath
            });
        }

        private void PublishActionUpdated()
        {
            PublishEvent(FlutterEvents.ActionUpdated, new FlutterActionUpdatedPayload
            {
                actions = BuildActionInfoDtos()
            });
        }

        private void PublishQualityChanged()
        {
            var quality = Quality;
            PublishEvent(FlutterEvents.QualityChanged, new FlutterQualityChangedPayload
            {
                renderPreset = quality == null ? string.Empty : ToQualityPresetWire(quality.CurrentPreset),
                physicsPreset = quality == null ? string.Empty : ToPhysicsPresetWire(quality.CurrentPhysicsPreset),
                status = quality == null ? string.Empty : quality.Status
            });
        }

        private void PublishCopresenceMode()
        {
            var director = CoPresence;
            PublishEvent(FlutterEvents.CopresenceMode, new FlutterCopresenceModePayload
            {
                mode = director == null ? string.Empty : ToCopresenceModeWire(director.CurrentMode),
                environment = director == null ? string.Empty : ToVirtualEnvironmentWire(director.CurrentEnvironment),
                videoCallActive = director != null && director.VideoCallActive,
                arAvailable = director != null && director.ArCameraAvailable,
                arPlaced = director != null && director.ArPlaced
            });
        }

        private void PublishPlacementChanged(bool arPlaced)
        {
            PublishEvent(FlutterEvents.CopresencePlacementChanged,
                new FlutterPlacementChangedPayload { arPlaced = arPlaced });
        }

        public void PublishInitialState()
        {
            if (bridge == null || owner == null)
            {
                return;
            }
            PublishPairingStatus();
            PublishModelUpdated();
            PublishActionUpdated();
            PublishQualityChanged();
            PublishCopresenceMode();
            PollConnection();
            PollConversationText();
            PollVoice();
            PollCallTimer();
            PollArPlacement();
            PollFraming();
            PollScreenGeometry();
            PollPerformance();
            PublishUpdateStatus("idle", 0f);
        }

        private void PublishImportStatus(string status)
        {
            PublishEvent(FlutterEvents.ModelImportStatus, new FlutterImportStatusPayload
            {
                status = status ?? string.Empty
            });
        }

        private void PublishUpdateStatus(string phase, float progress)
        {
            PublishEvent(FlutterEvents.UpdateStatus, new FlutterUpdateStatusPayload
            {
                phase = phase ?? string.Empty,
                progress = Mathf.Clamp01(progress)
            });
        }

        // ------------------------------------------------------------------
        // Polled (event-less) state
        // ------------------------------------------------------------------

        private void PollConnection()
        {
            var connected = AstrBot != null && AstrBot.IsConnected;
            var status = AstrBot == null ? "no bridge" : AstrBot.Status;
            var state = connected ? 1 : 0;
            if (state != lastConnectionState || !string.Equals(status, lastBridgeStatus, StringComparison.Ordinal))
            {
                lastConnectionState = state;
                lastBridgeStatus = status;
                PublishEvent(FlutterEvents.ConnectionChanged, new FlutterConnectionChangedPayload
                {
                    connected = connected,
                    bridgeStatus = status
                });
            }
        }

        private void PollConversationText()
        {
            var transcript = Conversation == null ? string.Empty : Conversation.Transcript;
            var reply = Conversation == null ? string.Empty : Conversation.ReplyText;
            if (!string.Equals(transcript, lastTranscript, StringComparison.Ordinal))
            {
                lastTranscript = transcript;
                PublishEvent(FlutterEvents.ConversationTranscript, new FlutterTextPayload { text = transcript });
            }
            if (!string.Equals(reply, lastReplyText, StringComparison.Ordinal))
            {
                lastReplyText = reply;
                PublishEvent(FlutterEvents.ConversationReply, new FlutterTextPayload { text = reply });
            }

            var suggestions = Conversation == null
                ? Array.Empty<string>()
                : Conversation.SuggestedReplies;
            var suggestionsKey = string.Join("\u001f", suggestions);
            if (!string.Equals(suggestionsKey, lastSuggestionsKey, StringComparison.Ordinal))
            {
                lastSuggestionsKey = suggestionsKey;
                PublishEvent(FlutterEvents.ConversationSuggestions,
                    new FlutterConversationSuggestionsPayload
                    {
                        suggestions = suggestions == null ? new string[0] :
                            new List<string>(suggestions).ToArray()
                    });
            }
        }

        private void PollVoice()
        {
            var voice = VoiceInput;
            var monitoring = voice != null && voice.IsMonitoring;
            var alwaysListening = voice != null && voice.AlwaysListening;
            var recording = voice != null && voice.IsRecording;
            var level = voice == null ? 0f : voice.InputLevel;
            if (monitoring != lastMonitoring || alwaysListening != lastAlwaysListening ||
                recording != lastRecording || Mathf.Abs(level - lastVoiceLevel) > 0.01f)
            {
                lastMonitoring = monitoring;
                lastAlwaysListening = alwaysListening;
                lastRecording = recording;
                lastVoiceLevel = level;
                PublishEvent(FlutterEvents.VoiceStatus, new FlutterVoiceStatusPayload
                {
                    monitoring = monitoring,
                    alwaysListening = alwaysListening,
                    recording = recording,
                    level = level
                });
            }
        }

        private void PollCallTimer()
        {
            var director = CoPresence;
            if (director == null)
            {
                return;
            }
            var videoCallActive = director.VideoCallActive;
            var durationText = videoCallActive ? director.CallDurationText : string.Empty;
            if (videoCallActive != lastVideoCallActive || !string.Equals(durationText, lastCallDurationText, StringComparison.Ordinal))
            {
                lastVideoCallActive = videoCallActive;
                lastCallDurationText = durationText;
                PublishEvent(FlutterEvents.CopresenceCallTimer,
                    new FlutterCallTimerPayload { durationText = durationText });
            }
        }

        private void PollArPlacement()
        {
            var director = CoPresence;
            if (director == null || director.ArPlaced == lastArPlaced)
            {
                return;
            }
            lastArPlaced = director.ArPlaced;
            PublishPlacementChanged(lastArPlaced);
        }

        private void PollFraming()
        {
            var director = CoPresence;
            if (director == null)
            {
                return;
            }

            var snapshot = director.CurrentFraming;
            var camera = director.MainCamera;
            var screenHeight = snapshot.ScreenHeight > 0f
                ? snapshot.ScreenHeight
                : (camera != null && camera.pixelHeight > 1 ? camera.pixelHeight : Screen.height);
            var screenWidth = camera != null && camera.pixelWidth > 1 ? camera.pixelWidth : Screen.width;
            if (!snapshot.Valid || camera == null || screenHeight <= 1f || screenWidth <= 1f)
            {
                if (lastFramingValid || !string.Equals(lastFramingSignature, "invalid", StringComparison.Ordinal))
                {
                    lastFramingValid = false;
                    lastFramingSignature = "invalid";
                    PublishEvent(FlutterEvents.FramingAnchors, new FlutterFramingAnchorsPayload
                    {
                        valid = false,
                        screenWidthPx = screenWidth,
                        screenHeightPx = screenHeight
                    });
                }
                return;
            }

            var top = Mathf.Clamp(snapshot.TopPx, 0f, screenHeight);
            var bottom = Mathf.Clamp(snapshot.BottomPx, top, screenHeight);
            var eyeLine = top + (bottom - top) * CallFramingSolver.PhoneVideoCallEyeLineRatio;
            var eyeLinePct = screenHeight > 0f ? eyeLine / screenHeight * 100f : 0f;
            var anchorKind = snapshot.HeadAnchor ? "head" : "bounds";
            if (!TryProjectAnchor(camera, snapshot.HeadTopWorld, out var headTop) ||
                !TryProjectAnchor(camera, snapshot.EyeWorld, out var eye) ||
                !TryProjectAnchor(camera, snapshot.LowCutWorld, out var waist) ||
                !TryProjectAnchor(camera, snapshot.FootWorld, out var feet))
            {
                if (lastFramingValid || !string.Equals(lastFramingSignature, "projection-invalid", StringComparison.Ordinal))
                {
                    lastFramingValid = false;
                    lastFramingSignature = "projection-invalid";
                    PublishEvent(FlutterEvents.FramingAnchors, new FlutterFramingAnchorsPayload
                    {
                        valid = false,
                        screenWidthPx = screenWidth,
                        screenHeightPx = screenHeight,
                        topPx = top,
                        bottomPx = bottom,
                        anchorKind = anchorKind,
                        headAnchor = snapshot.HeadAnchor,
                        degraded = true
                    });
                }
                return;
            }

            var signature = string.Concat(
                anchorKind, "|",
                snapshot.Distance.ToString("F3", CultureInfo.InvariantCulture), "|",
                snapshot.CameraY.ToString("F3", CultureInfo.InvariantCulture), "|",
                eyeLinePct.ToString("F2", CultureInfo.InvariantCulture), "|",
                headTop.x.ToString("F3", CultureInfo.InvariantCulture), ",",
                headTop.y.ToString("F3", CultureInfo.InvariantCulture), "|",
                eye.x.ToString("F3", CultureInfo.InvariantCulture), ",",
                eye.y.ToString("F3", CultureInfo.InvariantCulture), "|",
                waist.x.ToString("F3", CultureInfo.InvariantCulture), ",",
                waist.y.ToString("F3", CultureInfo.InvariantCulture), "|",
                feet.x.ToString("F3", CultureInfo.InvariantCulture), ",",
                feet.y.ToString("F3", CultureInfo.InvariantCulture));
            if (lastFramingValid && string.Equals(signature, lastFramingSignature, StringComparison.Ordinal))
            {
                return;
            }

            lastFramingValid = true;
            lastFramingSignature = signature;
            PublishEvent(FlutterEvents.FramingAnchors, new FlutterFramingAnchorsPayload
            {
                valid = true,
                screenWidthPx = screenWidth,
                screenHeightPx = screenHeight,
                topPx = top,
                bottomPx = bottom,
                anchorKind = anchorKind,
                eyeLinePct = eyeLinePct,
                d = snapshot.Distance,
                h = snapshot.CameraY,
                distance = snapshot.Distance,
                cameraY = snapshot.CameraY,
                headAnchor = snapshot.HeadAnchor,
                degraded = snapshot.Degraded,
                anchors = new FlutterFramingAnchorSetDto
                {
                    headTop = headTop,
                    eye = eye,
                    waist = waist,
                    feet = feet
                }
            });
        }

        private static bool TryProjectAnchor(
            Camera camera,
            Vector3 world,
            out FlutterFramingAnchorDto anchor)
        {
            anchor = new FlutterFramingAnchorDto();
            if (camera == null || !IsFinite(world.x) || !IsFinite(world.y) || !IsFinite(world.z))
            {
                return false;
            }
            var pixelRect = camera.pixelRect;
            if (pixelRect.width <= 1f || pixelRect.height <= 1f)
            {
                return false;
            }
            var projected = camera.WorldToScreenPoint(world);
            if (!IsFinite(projected.x) || !IsFinite(projected.y) || !IsFinite(projected.z) || projected.z <= 0f)
            {
                return false;
            }
            var x = (projected.x - pixelRect.x) / pixelRect.width;
            var y = 1f - (projected.y - pixelRect.y) / pixelRect.height;
            if (!IsFinite(x) || !IsFinite(y))
            {
                return false;
            }
            anchor.x = Mathf.Clamp01(x);
            anchor.y = Mathf.Clamp01(y);
            return true;
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void PollScreenGeometry()
        {
            if (Screen.width == lastScreenWidth && Screen.height == lastScreenHeight)
            {
                return;
            }
            var first = lastScreenWidth == 0 && lastScreenHeight == 0;
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;
            // Ask Flutter to re-measure chrome insets T/B on resolution change
            // (design §M1, PushChromeInsets / GeometryChangedEvent equivalent).
            if (!first)
            {
                PublishEvent(FlutterEvents.CopresenceChromeInsetsNeeded, new FlutterTextPayload());
            }
        }

        private void PollPerformance()
        {
            var perf = Performance;
            if (perf == null)
            {
                return;
            }
            PublishEvent(FlutterEvents.PerformanceSnapshot, new FlutterPerformanceSnapshotPayload
            {
                fps5s = perf.fps5Seconds,
                fps30s = perf.fps30Seconds,
                frameP50Ms = perf.frameTimeP50Ms,
                frameP95Ms = perf.frameTimeP95Ms,
                physicsDropS = perf.physicsSessionDroppedSeconds,
                poseSrcFlip = perf.physicsPoseSourceFlipFrames
            });
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private FlutterModelInfoDto[] BuildModelInfoDtos()
        {
            var loader = ModelLoader;
            if (loader == null)
            {
                return new FlutterModelInfoDto[0];
            }
            var source = loader.DiscoverInstalledModels();
            var result = new FlutterModelInfoDto[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                var model = source[i];
                result[i] = new FlutterModelInfoDto
                {
                    displayName = model.DisplayName,
                    path = model.Path,
                    packageRoot = model.PackageRoot ?? string.Empty,
                    size = FormatByteSize(model.ByteLength),
                    inUse = ModelLoader.CurrentModelPath != null &&
                        string.Equals(ModelLoader.CurrentModelPath, model.Path, StringComparison.OrdinalIgnoreCase)
                };
            }
            return result;
        }

        private FlutterVmdActionInfoDto[] BuildActionInfoDtos()
        {
            if (VmdActions == null)
            {
                return new FlutterVmdActionInfoDto[0];
            }
            var source = VmdActions.Actions;
            var result = new FlutterVmdActionInfoDto[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                var action = source[i];
                result[i] = new FlutterVmdActionInfoDto
                {
                    id = action.Id,
                    displayName = action.DisplayName,
                    byteLength = action.ByteLength,
                    keyframeCount = action.KeyframeCount,
                    lastFrame = action.LastFrame,
                    durationSeconds = action.DurationSeconds,
                    hasFacialTrack = action.HasFacialTrack
                };
            }
            return result;
        }

        private RuntimeMmdModelInfo FindModel(string path)
        {
            var loader = ModelLoader;
            if (loader == null || string.IsNullOrWhiteSpace(path))
            {
                return null;
            }
            foreach (var model in loader.DiscoverInstalledModels())
            {
                if (string.Equals(model.Path, path, StringComparison.Ordinal))
                {
                    return model;
                }
            }
            return null;
        }

        private void ResetDiffState()
        {
            pairingCodeBuffer = string.Empty;
            lastUpdateInfo = null;
            lastConnectionState = -1;
            lastBridgeStatus = string.Empty;
            lastTranscript = string.Empty;
            lastReplyText = string.Empty;
            lastSuggestionsKey = string.Empty;
            lastMonitoring = false;
            lastAlwaysListening = false;
            lastRecording = false;
            lastVoiceLevel = 0f;
            lastVideoCallActive = false;
            lastCallDurationText = string.Empty;
            lastFramingValid = false;
            lastFramingSignature = string.Empty;
            lastScreenWidth = 0;
            lastScreenHeight = 0;
            expressionIndex = 0;
            nextPollAt = 0f;
            nextPerformanceAt = 0f;
        }

        private static string FormatByteSize(long bytes)
        {
            if (bytes <= 0)
            {
                return string.Empty;
            }
            if (bytes >= 1024L * 1024L)
            {
                return (bytes / (1024f * 1024f)).ToString("F1") + " MB";
            }
            return Math.Max(1L, bytes / 1024L) + " KB";
        }

        private static string ToQualityPresetWire(QuestQualityPreset preset)
        {
            switch (preset)
            {
                case QuestQualityPreset.Performance: return "performance";
                case QuestQualityPreset.Clear: return "clear";
                default: return "balanced";
            }
        }

        private static string ToPhysicsPresetWire(MmdPhysicsPreset preset)
        {
            switch (preset)
            {
                case MmdPhysicsPreset.Performance: return "performance";
                case MmdPhysicsPreset.Fine: return "fine";
                default: return "balanced";
            }
        }

        private static string ToCopresenceModeWire(CoPresenceMode mode)
        {
            switch (mode)
            {
                case CoPresenceMode.ArReality: return "arReality";
                case CoPresenceMode.VideoCall: return "videoCall";
                default: return "virtualScene";
            }
        }

        private static string ToVirtualEnvironmentWire(VirtualEnvironment environment)
        {
            switch (environment)
            {
                case VirtualEnvironment.StarrySky: return "starrySky";
                case VirtualEnvironment.Bedroom: return "bedroom";
                case VirtualEnvironment.Seaside: return "seaside";
                default: return "nightStreet";
            }
        }

        private static bool TryValidateImageAttachment(string base64, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(base64) || base64.Length > (1 << 20))
            {
                error = "图片附件超过大小限制";
                return false;
            }
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                error = "图片附件编码无效";
                return false;
            }
            if (bytes.Length == 0 || bytes.Length > 768 * 1024)
            {
                error = "图片附件超过大小限制";
                return false;
            }
            var jpeg = bytes.Length >= 3 && bytes[0] == 0xff && bytes[1] == 0xd8 && bytes[2] == 0xff;
            var png = bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 &&
                bytes[2] == 0x4e && bytes[3] == 0x47 && bytes[4] == 0x0d &&
                bytes[5] == 0x0a && bytes[6] == 0x1a && bytes[7] == 0x0a;
            if (!jpeg && !png)
            {
                error = "仅支持 JPEG 或 PNG 图片附件";
                return false;
            }
            return true;
        }

        private static bool TryParseQualityPreset(string value, out QuestQualityPreset preset)
        {
            switch (value)
            {
                case "performance": preset = QuestQualityPreset.Performance; return true;
                case "balanced": preset = QuestQualityPreset.Balanced; return true;
                case "clear": preset = QuestQualityPreset.Clear; return true;
                default: preset = QuestQualityPreset.Balanced; return false;
            }
        }

        private static bool TryParsePhysicsPreset(string value, out MmdPhysicsPreset preset)
        {
            switch (value)
            {
                case "performance": preset = MmdPhysicsPreset.Performance; return true;
                case "balanced": preset = MmdPhysicsPreset.Balanced; return true;
                case "fine": preset = MmdPhysicsPreset.Fine; return true;
                default: preset = MmdPhysicsPreset.Balanced; return false;
            }
        }

        private static bool TryParseCopresenceMode(string value, out CoPresenceMode mode)
        {
            switch (value)
            {
                case "arReality": mode = CoPresenceMode.ArReality; return true;
                case "virtualScene": mode = CoPresenceMode.VirtualScene; return true;
                case "videoCall": mode = CoPresenceMode.VideoCall; return true;
                default: mode = CoPresenceMode.VirtualScene; return false;
            }
        }

        private static bool TryParseVirtualEnvironment(string value, out VirtualEnvironment environment)
        {
            switch (value)
            {
                case "nightStreet": environment = VirtualEnvironment.NightStreet; return true;
                case "starrySky": environment = VirtualEnvironment.StarrySky; return true;
                case "bedroom": environment = VirtualEnvironment.Bedroom; return true;
                case "seaside": environment = VirtualEnvironment.Seaside; return true;
                default: environment = VirtualEnvironment.NightStreet; return false;
            }
        }
    }

    // ------------------------------------------------------------------
    // Command payload DTOs (field names match the camelCase JSON keys).
    // ------------------------------------------------------------------

    [Serializable] public sealed class ModelLoadPayload { public string path = string.Empty; public string packageRoot = string.Empty; }
    [Serializable] public sealed class ModelDeletePayload { public string path = string.Empty; }
    [Serializable] public sealed class ActionPlayPayload { public string id = string.Empty; }
    [Serializable] public sealed class ActionDeletePayload { public string id = string.Empty; }
    [Serializable] public sealed class AvatarCommandPayload { public string name = string.Empty; }
    [Serializable] public sealed class ConversationSendPayload { public string text = string.Empty; public string attachment = string.Empty; }
    [Serializable] public sealed class PairingSetServerPayload { public string server = string.Empty; }
    [Serializable] public sealed class PairingSetPrivateHttpPayload { public bool enabled; }
    [Serializable] public sealed class PairingDigitPayload { public string op = string.Empty; public string digit = string.Empty; }
    [Serializable] public sealed class QualityApplyPresetPayload { public string preset = string.Empty; }
    [Serializable] public sealed class QualityApplyPhysicsPayload { public string preset = string.Empty; }
    [Serializable] public sealed class SettingsTargetFpsPayload { public int fps; }
    [Serializable] public sealed class SettingsVolumePayload { public float v; }
    [Serializable] public sealed class SettingsTogglePayload { public string key = string.Empty; public bool value; }
    [Serializable] public sealed class CopresenceEnterScenePayload { public string path = string.Empty; }
    [Serializable] public sealed class CopresenceSwitchModePayload { public string mode = string.Empty; }
    [Serializable] public sealed class CopresenceSwitchEnvironmentPayload { public string env = string.Empty; }
    [Serializable] public sealed class CopresenceSetChromeInsetsPayload { public float top; public float bottom; }
    [Serializable] public sealed class CopresenceArPlacePayload { public float x; public float y; }
    [Serializable] public sealed class QaCommandPayload { public string name = string.Empty; public string args = string.Empty; }
    [Serializable] public sealed class QaSendTextArgs { public string text = string.Empty; }
}

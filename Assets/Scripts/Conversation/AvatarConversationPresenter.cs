using System;
using System.Collections.Generic;
using System.Threading;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer
{
    // UMT, imported VMD, and physical reactions write the authored pose first.
    // Gaze is the final additive pass except while a real hand contact is
    // active, so contact remains the authoritative owner of the head.
    [DefaultExecutionOrder(11200)]
    public sealed class AvatarConversationPresenter : MonoBehaviour
    {
        private struct Viseme
        {
            public SkinnedMeshRenderer Renderer;
            public int Index;
            public float BaseWeight;
            public int Group;
            public float LastSpeechWeight;
            public float LastCompositeWeight;
            public bool HasSpeechComposite;
        }

        private struct ExpressionMorph
        {
            public SkinnedMeshRenderer Renderer;
            public int Index;
            public string Name;
            public float BaseWeight;
            public float CurrentWeight;
            public float LastExpressionWeight;
            public float LastCompositeWeight;
            public bool HasExpressionComposite;
        }

        private readonly List<Viseme> visemes = new List<Viseme>();
        private readonly List<ExpressionMorph> expressions = new List<ExpressionMorph>();
        private readonly AvatarBehaviorCoordinator behavior = new AvatarBehaviorCoordinator();
        private AvatarController avatar;
        private AvatarHumanInteraction humanInteraction;
        private Pcm16StreamAudioPlayer audioPlayer;
        private VmdActionLibrary vmdActions;
        private AvatarMouthLatePass mouthLatePass;
        private AvatarPlacementService placement;
        private RuntimeDebugLog diagnostics;
        private readonly SemaphoreSlim danceRequestGate = new SemaphoreSlim(1, 1);
        private Transform head;
        private Transform jaw;
        private Quaternion headBaseRotation;
        private Quaternion jawBaseRotation;
        private ConversationState state;
        private string lookAtMode = "none";
        [SerializeField] private bool gazeAtUserWhileIdle = true;
        [SerializeField] private bool gazeAtUserDuringConversation = true;
        [SerializeField, Range(.5f, 6f)] private float idleGazeBlendSpeed = 2.25f;
        [SerializeField, Range(2f, 20f)] private float gazeTrackingSpeed = 8f;
        private float gazeBlend;
        // Offset from the pose authored by the current action/VMD. Keeping it
        // separate prevents gaze from erasing head choreography or accumulating.
        private Quaternion smoothedHeadRotation;
        private bool hasSmoothedHeadRotation;
        private Quaternion authoredHeadRotation = Quaternion.identity;
        private Quaternion presentedHeadRotation = Quaternion.identity;
        private bool hasPresentedHeadRotation;
        private bool mouthWasActive;
        private float smoothedMouthAmount;
        private float lastAudibleRms;
        private float lastVisibleMouthAmount;
        private float lastTimelinePositionMs;
        private float lastTimelinePeak;
        private string lastGazeDiagnostic = string.Empty;
        private int fallbackVisemeGroup = -1;
        private string targetEmotion = "neutral";
        private float targetEmotionIntensity;
        private string manualExpression = "neutral";
        private string lastLoggedAction = "idle";
        private AvatarActionExecutionContext activeTrackedAction;
        private bool activeTrackedActionAccepted;
        private bool activeTrackedActionStarted;
        private float activeTrackedActionAcceptedAt;
        private Coroutine timedActionCompletion;
        private Coroutine pendingDanceExpiry;
        private AvatarActionExecutionContext pendingDanceContext;
        private bool pendingDanceRequest;
        private bool pendingDanceSelectNext;
        private Coroutine pendingBootstrapExpiry;
        private AvatarActionExecutionContext pendingBootstrapContext;
        private string pendingBootstrapEmotion;
        private string pendingBootstrapGesture;
        private string pendingBootstrapLookAt;
        private float pendingBootstrapIntensity;
        private int pendingBootstrapDurationMs;
        private AvatarActionParameters pendingBootstrapParameters;
        private AvatarActionTransition pendingBootstrapTransition;
        private string pendingBootstrapSource;
        private bool pendingBootstrapIntent;
        private SpeechVisemeCue[] speechTimeline = Array.Empty<SpeechVisemeCue>();
        private bool speechTimelineMatchesAvatar;
        private readonly float[] visemeInfluences = new float[5];
        private readonly float[] targetVisemeInfluences = new float[5];
        [SerializeField, Range(1f, 12f)] private float expressionBlendSpeed = 6f;
        [SerializeField, Range(1f, 24f)] private float mouthAttackSpeed = 11f;
        [SerializeField, Range(1f, 24f)] private float mouthReleaseSpeed = 7f;
        [SerializeField, Range(1f, 30f)] private float visemeCrossfadeSpeed = 14f;

        public int MatchedVisemeCount => visemes.Count;
        public float LastAudibleRms => lastAudibleRms;
        public float SmoothedMouthAmount => smoothedMouthAmount;
        public float LastVisibleMouthAmount => lastVisibleMouthAmount;
        public float LastTimelinePositionMs => lastTimelinePositionMs;
        public float LastTimelinePeak => lastTimelinePeak;
        public bool SpeechTimelineActive => speechTimelineMatchesAvatar && speechTimeline.Length > 0;
        public string ManualExpression => manualExpression;
        public string Status => avatar == null
            ? "Waiting for avatar"
            : $"{state} | gesture:{behavior.LastGesture} visemes:{visemes.Count} jaw:{(jaw == null ? "no" : "yes")}";
        public event Action<AvatarActionExecutionUpdate> ActionExecutionChanged;

        public void Bind(AvatarController target, AvatarHumanInteraction human, Pcm16StreamAudioPlayer streamPlayer)
        {
            var preservePendingAction = activeTrackedAction != null &&
                ((pendingDanceRequest && ReferenceEquals(activeTrackedAction, pendingDanceContext)) ||
                 (pendingBootstrapIntent && ReferenceEquals(activeTrackedAction, pendingBootstrapContext)));
            if (activeTrackedAction != null && !preservePendingAction)
            {
                EmitActionUpdate(
                    activeTrackedAction,
                    AvatarActionReceiptPhase.Interrupted,
                    "runtime",
                    "system_interrupted");
            }
            if (avatar != null)
            {
                avatar.ActionChanged -= HandleAvatarActionChanged;
            }
            UnsubscribeVmdLifecycle();
            RestoreMouth();
            RestoreExpressions();
            RestoreJaw();
            RestoreHead();
            avatar = target;
            humanInteraction = human;
            audioPlayer = streamPlayer;
            vmdActions = GetComponent<VmdActionLibrary>();
            mouthLatePass = GetComponent<AvatarMouthLatePass>();
            if (mouthLatePass == null)
            {
                mouthLatePass = gameObject.AddComponent<AvatarMouthLatePass>();
            }
            mouthLatePass.Initialize(this);
            SubscribeVmdLifecycle();
            placement = GetComponent<AvatarPlacementService>();
            diagnostics = GetComponent<RuntimeDebugLog>();
            head = null;
            jaw = null;
            visemes.Clear();
            expressions.Clear();
            targetEmotion = "neutral";
            targetEmotionIntensity = 0f;
            manualExpression = "neutral";
            gazeBlend = 0f;
            hasSmoothedHeadRotation = false;
            authoredHeadRotation = Quaternion.identity;
            presentedHeadRotation = Quaternion.identity;
            hasPresentedHeadRotation = false;
            lookAtMode = "none";
            mouthWasActive = false;
            smoothedMouthAmount = 0f;
            lastAudibleRms = 0f;
            lastVisibleMouthAmount = 0f;
            lastTimelinePositionMs = 0f;
            lastTimelinePeak = 0f;
            fallbackVisemeGroup = -1;
            ResetVisemeInfluences();
            ClearSpeechTimeline();
            behavior.Reset(Time.unscaledTime, UnityEngine.Random.value);

            if (avatar == null)
            {
                return;
            }

            lastLoggedAction = avatar.CurrentAction;
            avatar.ActionChanged += HandleAvatarActionChanged;

            head = FindHead(avatar);
            if (head != null)
            {
                headBaseRotation = head.localRotation;
                smoothedHeadRotation = Quaternion.identity;
                hasSmoothedHeadRotation = true;
            }
            jaw = FindJaw(avatar);
            if (jaw != null)
            {
                jawBaseRotation = jaw.localRotation;
            }
            CacheVisemes();
            CacheExpressions();
            FlushPendingDanceRequest();
            FlushPendingBootstrapIntent();
            Debug.Log($"[ConversationPresenter] Bound mouth: visemes={visemes.Count}, expressions={expressions.Count}, jaw={(jaw == null ? "no" : "yes")}.", this);
        }

        public void SetConversationState(ConversationState next)
        {
            if (state != ConversationState.Idle && next == ConversationState.Idle)
            {
                behavior.DeferIdle(Time.unscaledTime, UnityEngine.Random.value);
            }
            state = next;
        }

        public bool ApplyIntent(
            string emotion,
            string gesture,
            string lookAt,
            float intensity = 1f,
            int durationMs = 2000,
            AvatarActionExecutionContext executionContext = null,
            AvatarActionParameters actionParameters = null,
            AvatarActionTransition actionTransition = null,
            string actionSource = "backend")
        {
            if (avatar == null)
            {
                var earlyGesture = string.IsNullOrWhiteSpace(gesture)
                    ? string.Empty
                    : gesture.Trim().ToLowerInvariant();
                if (executionContext != null &&
                    (earlyGesture == "dance" || earlyGesture == "dance_next"))
                {
                    // The backend can beat asynchronous PMX binding by a few
                    // frames. Preserve an explicit dance instead of turning
                    // a transient bootstrap state into invalid_state.
                    ActivateTrackedAction(executionContext);
                    QueuePendingDanceRequest(earlyGesture == "dance_next", executionContext);
                    Debug.Log(
                        earlyGesture == "dance_next"
                            ? "[ConversationPresenter] dance_waiting_model_next: avatar binding not ready."
                            : "[ConversationPresenter] dance_waiting_model: avatar binding not ready.",
                        this);
                    return true;
                }
                if (executionContext != null && IsQueueableBootstrapGesture(earlyGesture))
                {
                    QueuePendingBootstrapIntent(
                        emotion,
                        earlyGesture,
                        lookAt,
                        intensity,
                        durationMs,
                        executionContext,
                        actionParameters,
                        actionTransition,
                        actionSource);
                    return true;
                }
                ReportRejected(executionContext, "avatar_unavailable");
                return false;
            }

            emotion = AstrBotProtocol.SanitizeEmotion(emotion);
            var requestedGesture = string.IsNullOrWhiteSpace(gesture) ? string.Empty : gesture.Trim().ToLowerInvariant();
            // World-placement actions remain exact enum values. Free-form text
            // never reaches AvatarPlacementService.
            gesture = requestedGesture == "sit" || requestedGesture == "lie" || requestedGesture == "lie_down"
                ? requestedGesture == "lie" ? "lie_down" : requestedGesture
                : AstrBotProtocol.SanitizeGesture(gesture);
            lookAt = AstrBotProtocol.SanitizeLookAt(lookAt);
            avatar.SetEmotion(emotion);
            targetEmotion = emotion;
            targetEmotionIntensity = Mathf.Clamp01(intensity);
            lookAtMode = lookAt == "hand" ? "none" : lookAt;
            var reactionSeconds = Mathf.Clamp(durationMs <= 0 ? 2f : durationMs / 1000f, .25f, 8f);
            var semanticContact = humanInteraction != null && humanInteraction.HasSemanticContact;
            if (!behavior.TryAcceptIntent(
                    gesture,
                    semanticContact,
                    IsImportedMotionBusy(),
                    Time.unscaledTime,
                    out var acceptedGesture))
            {
                diagnostics?.Record("AvatarAction", "后端动作意图被本地仲裁阻止：" + gesture);
                diagnostics?.RecordStage("avatar_action", "blocked", "action_arbitration_blocked");
                ReportRejected(executionContext, "blocked");
                return false;
            }

            diagnostics?.Record(
                "AvatarAction",
                "后端动作意图已接受：" + acceptedGesture +
                " source=" + (string.IsNullOrWhiteSpace(actionSource) ? "backend" : actionSource));
            diagnostics?.RecordStage(
                "avatar_action",
                "processing",
                "backend_intent_accepted");

            if (acceptedGesture == "crouch" && !avatar.SupportsCrouch)
            {
                diagnostics?.RecordStage("avatar_action", "limited", "crouch_rig_missing");
                ReportRejected(executionContext, "asset_missing");
                return false;
            }

            if (acceptedGesture == "sit" || acceptedGesture == "lie_down")
            {
                if (placement == null || !placement.TryExecuteRestingAction(acceptedGesture))
                {
                    diagnostics?.RecordStage("avatar_action", "limited", "rest_target_unavailable");
                    ReportRejected(executionContext, "asset_missing");
                    return false;
                }
                ActivateTrackedAction(executionContext);
                return true;
            }
            else if (placement != null && placement.IsRestingOrAligning && acceptedGesture != "talk")
            {
                PrepareTrackedAction(executionContext);
                var returning = placement.TryReturnToStanding(acceptedGesture);
                if (returning) ReportAccepted(executionContext);
                else ReportRejected(executionContext, "invalid_state");
                return returning;
            }
            else if (acceptedGesture == "handshake")
            {
                if (humanInteraction == null)
                {
                    ReportRejected(executionContext, "invalid_state");
                    return false;
                }
                humanInteraction.PlayReaction(HumanInteractionKind.Handshake, reactionSeconds);
                ActivateTrackedAction(executionContext);
                StartTimedTrackedAction(executionContext, reactionSeconds, "interaction");
                return true;
            }
            else if (acceptedGesture == "head_pat")
            {
                if (humanInteraction == null)
                {
                    ReportRejected(executionContext, "invalid_state");
                    return false;
                }
                humanInteraction.PlayReaction(HumanInteractionKind.HeadPat, reactionSeconds);
                ActivateTrackedAction(executionContext);
                StartTimedTrackedAction(executionContext, reactionSeconds, "interaction");
                return true;
            }
            else if (acceptedGesture == "cheek_pinch")
            {
                if (humanInteraction == null)
                {
                    ReportRejected(executionContext, "invalid_state");
                    return false;
                }
                humanInteraction.PlayReaction(HumanInteractionKind.CheekPinch, reactionSeconds);
                ActivateTrackedAction(executionContext);
                StartTimedTrackedAction(executionContext, reactionSeconds, "interaction");
                return true;
            }
            else if (acceptedGesture == "wave" || acceptedGesture == "bow" ||
                acceptedGesture == "dance" || acceptedGesture == "dance_next" ||
                acceptedGesture == "nod" || acceptedGesture == "sway" ||
                acceptedGesture == "raise_hand" || acceptedGesture == "raise_leg" || acceptedGesture == "turn_half" ||
                acceptedGesture == "crouch" ||
                acceptedGesture == "refuse" || acceptedGesture == "step_back" ||
                acceptedGesture == "idle")
            {
                if (acceptedGesture == "dance" || acceptedGesture == "dance_next")
                {
                    ActivateTrackedAction(executionContext);
                    if (executionContext != null && ShouldWaitForDanceBinding())
                    {
                        QueuePendingDanceRequest(acceptedGesture == "dance_next", executionContext);
                        return true;
                    }
                    _ = PlayRecommendedDance(acceptedGesture == "dance_next", executionContext);
                    return true;
                }
                else
                {
                    PrepareTrackedAction(executionContext);
                    var played = avatar.PlayActionFromSource(
                        acceptedGesture,
                        AvatarActionSource.Backend,
                        actionParameters,
                        actionTransition,
                        actionSource);
                    if (played)
                    {
                        ReportAccepted(executionContext);
                        ReportStarted(executionContext, "avatar_controller");
                    }
                    else
                    {
                        ReportRejected(executionContext, "busy");
                    }
                    return played;
                }
            }
            ReportRejected(executionContext, "unsupported");
            return false;
        }

        public void SetManualExpression(string expression)
        {
            manualExpression = NormalizeExpression(expression);
            targetEmotion = manualExpression;
            targetEmotionIntensity = manualExpression == "neutral" ? 0f : .42f;
            avatar?.SetEmotion(manualExpression);
        }

        public void SetSpeechTimeline(SpeechVisemeCue[] timeline)
        {
            if (timeline == null || timeline.Length == 0)
            {
                ClearSpeechTimeline();
                return;
            }

            speechTimeline = new SpeechVisemeCue[timeline.Length];
            var cueGroups = new HashSet<int>();
            for (var index = 0; index < timeline.Length; index++)
            {
                var cue = timeline[index];
                speechTimeline[index] = new SpeechVisemeCue
                {
                    Symbol = cue == null ? string.Empty : cue.Symbol,
                    StartMs = cue == null ? 0 : cue.StartMs,
                    EndMs = cue == null ? 0 : cue.EndMs,
                    Weight = cue == null ? 0f : Mathf.Clamp01(cue.Weight)
                };
                var group = GetVisemeGroup(speechTimeline[index].Symbol);
                if (group >= 0) cueGroups.Add(group);
            }
            speechTimelineMatchesAvatar = visemes.Exists(viseme =>
                viseme.Group >= 0 && cueGroups.Contains(viseme.Group));
        }

        public void ClearSpeechTimeline()
        {
            speechTimeline = Array.Empty<SpeechVisemeCue>();
            speechTimelineMatchesAvatar = false;
        }

        public void PlayLocalAction(string action)
        {
            var normalized = string.IsNullOrWhiteSpace(action)
                ? "idle"
                : action.Trim().ToLowerInvariant();
            diagnostics?.Record("AvatarAction", "本地动作回退执行：" + normalized);
            diagnostics?.RecordStage("avatar_action", "processing", "local_action_fallback");
            if (normalized == "sit" || normalized == "lie_down")
            {
                if (placement == null || !placement.TryExecuteRestingAction(normalized))
                {
                    diagnostics?.RecordStage("avatar_action", "limited", "rest_target_unavailable");
                }
                return;
            }
            if (placement != null && placement.IsRestingOrAligning)
            {
                placement.TryReturnToStanding(normalized);
                return;
            }
            if (normalized == "dance" || normalized == "dance_next")
            {
                _ = PlayRecommendedDance(normalized == "dance_next");
                return;
            }

            if (normalized == "crouch" && (avatar == null || !avatar.SupportsCrouch))
            {
                diagnostics?.RecordStage("avatar_action", "limited", "crouch_rig_missing");
                return;
            }

            if (avatar != null)
            {
                avatar.PlayActionFromSource(normalized, AvatarActionSource.Manual);
            }
        }

        private async System.Threading.Tasks.Task PlayRecommendedDance(
            bool selectNext = false,
            AvatarActionExecutionContext executionContext = null)
        {
            var queued = danceRequestGate.CurrentCount == 0;
            if (queued)
            {
                diagnostics?.Record(
                    "AvatarAction",
                    selectNext
                        ? "下一支舞蹈请求已排队，等待当前动作加载完成"
                        : "舞蹈请求已排队，等待当前动作加载完成");
                diagnostics?.RecordStage(
                    "avatar_action",
                    "queued",
                    selectNext ? "dance_next_queued" : "dance_queued");
            }

            await danceRequestGate.WaitAsync();
            try
            {
                if (executionContext != null && !IsTrackedActionCurrent(executionContext))
                {
                    return;
                }
                diagnostics?.RecordStage(
                    "avatar_action",
                    "processing",
                    selectNext ? "dance_next_started" : "dance_started");
                await PlayRecommendedDanceCore(selectNext, executionContext);
            }
            finally
            {
                danceRequestGate.Release();
            }
        }

        private async System.Threading.Tasks.Task PlayRecommendedDanceCore(
            bool selectNext,
            AvatarActionExecutionContext executionContext)
        {
            diagnostics?.Record("AvatarAction", selectNext
                ? "开始查找下一支可播放的自定义舞蹈动作"
                : "开始查找可播放的自定义舞蹈动作");
            if (vmdActions != null && vmdActions.BoundModel)
            {
                try
                {
                    var played = selectNext
                        ? await vmdActions.PlayNextDanceAsync()
                        : await vmdActions.PlayRecommendedDanceAsync();
                    diagnostics?.Record(
                        "AvatarAction",
                        played
                            ? "舞蹈请求已播放导入动作：" + vmdActions.CurrentActionId
                            : "舞蹈请求未找到可播放的导入动作");
                    diagnostics?.RecordStage(
                        "avatar_action",
                        played ? "completed" : "limited",
                        played ? "custom_dance_started" : "custom_dance_unavailable");
                    if (played)
                    {
                        if (executionContext != null && !IsTrackedActionCurrent(executionContext))
                        {
                            vmdActions.StopAndReturnToIdle();
                            return;
                        }
                        ReportStarted(executionContext, "imported_vmd");
                        return;
                    }
                }
                catch (Exception exception)
                {
                    diagnostics?.Record("AvatarAction", "导入舞蹈播放失败：" + exception.Message);
                    Debug.LogWarning("[ConversationPresenter] Dance motion fallback failed: " + exception.Message, this);
                }
            }
            else
            {
                // The intent can arrive while the avatar bootstrap is still
                // binding the model and its VMD library. Keep the explicit
                // action alive until that binding completes instead of
                // falling through to a silent no-op.
                if (ShouldWaitForDanceBinding() && executionContext != null)
                {
                    QueuePendingDanceRequest(selectNext, executionContext);
                    return;
                }
                diagnostics?.Record(
                    "AvatarAction",
                    vmdActions == null
                        ? "自定义动作库未绑定，改用内置舞蹈"
                        : "角色模型未绑定到自定义动作库，改用内置舞蹈");
            }

            // A built-in fallback keeps the explicit request visible even when
            // no imported dance is installed or the VMD is not compatible.
            diagnostics?.Record("AvatarAction", "播放内置舞蹈回退动作");
            var fallbackPlayed = avatar != null &&
                avatar.PlayActionFromSource("dance", AvatarActionSource.Backend);
            if (!fallbackPlayed) ReportRejected(executionContext, "asset_missing");
        }

        private void QueuePendingDanceRequest(
            bool selectNext,
            AvatarActionExecutionContext executionContext)
        {
            if (executionContext == null)
            {
                return;
            }
            pendingDanceRequest = true;
            pendingDanceSelectNext = selectNext;
            pendingDanceContext = executionContext;
            diagnostics?.Record(
                "AvatarAction",
                selectNext
                    ? "舞蹈请求等待角色模型绑定：下一支导入舞蹈"
                    : "舞蹈请求等待角色模型绑定：导入舞蹈");
            diagnostics?.RecordStage(
                "avatar_action",
                "queued",
                selectNext ? "dance_waiting_model_next" : "dance_waiting_model");
            Debug.Log(
                selectNext
                    ? "[ConversationPresenter] dance_waiting_model_next: preserving action until model binding."
                    : "[ConversationPresenter] dance_waiting_model: preserving action until model binding.",
                this);
            if (pendingDanceExpiry != null)
            {
                StopCoroutine(pendingDanceExpiry);
            }
            pendingDanceExpiry = StartCoroutine(ExpirePendingDanceRequest(
                executionContext,
                20f));
        }

        private System.Collections.IEnumerator ExpirePendingDanceRequest(
            AvatarActionExecutionContext executionContext,
            float timeoutSeconds)
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(.1f, timeoutSeconds));
            pendingDanceExpiry = null;
            if (!pendingDanceRequest || !ReferenceEquals(pendingDanceContext, executionContext))
            {
                yield break;
            }

            pendingDanceRequest = false;
            pendingDanceContext = null;
            diagnostics?.RecordStage("avatar_action", "limited", "dance_model_bind_timeout");
            ReportRejected(executionContext, "asset_missing");
        }

        private void FlushPendingDanceRequest()
        {
            if (!pendingDanceRequest || vmdActions == null || !vmdActions.BoundModel)
            {
                return;
            }

            var selectNext = pendingDanceSelectNext;
            var context = pendingDanceContext;
            pendingDanceRequest = false;
            pendingDanceContext = null;
            if (pendingDanceExpiry != null)
            {
                StopCoroutine(pendingDanceExpiry);
                pendingDanceExpiry = null;
            }

            if (!IsTrackedActionCurrent(context))
            {
                diagnostics?.RecordStage("avatar_action", "skipped", "dance_waiting_model_stale");
                return;
            }

            diagnostics?.RecordStage(
                "avatar_action",
                "processing",
                selectNext ? "dance_model_bound_resume_next" : "dance_model_bound_resume");
            Debug.Log(
                selectNext
                    ? "[ConversationPresenter] dance_model_bound_resume_next: starting imported dance."
                    : "[ConversationPresenter] dance_model_bound_resume: starting imported dance.",
                this);
            _ = PlayRecommendedDance(selectNext, context);
        }

        private bool ShouldWaitForDanceBinding()
        {
            return vmdActions == null || !vmdActions.BoundModel || avatar == null;
        }

        private static bool IsQueueableBootstrapGesture(string gesture)
        {
            switch (gesture)
            {
                case "wave":
                case "bow":
                case "nod":
                case "sway":
                case "raise_hand":
                case "raise_leg":
                case "turn_half":
                case "crouch":
                case "sit":
                case "lie":
                case "lie_down":
                case "handshake":
                case "head_pat":
                case "cheek_pinch":
                    return true;
                default:
                    return false;
            }
        }

        private void QueuePendingBootstrapIntent(
            string emotion,
            string gesture,
            string lookAt,
            float intensity,
            int durationMs,
            AvatarActionExecutionContext executionContext,
            AvatarActionParameters actionParameters,
            AvatarActionTransition actionTransition,
            string actionSource)
        {
            if (executionContext == null)
            {
                return;
            }

            PrepareTrackedAction(executionContext);
            ReportAccepted(executionContext);
            pendingBootstrapIntent = true;
            pendingBootstrapContext = executionContext;
            pendingBootstrapEmotion = emotion;
            pendingBootstrapGesture = gesture;
            pendingBootstrapLookAt = lookAt;
            pendingBootstrapIntensity = intensity;
            pendingBootstrapDurationMs = durationMs;
            pendingBootstrapParameters = actionParameters;
            pendingBootstrapTransition = actionTransition;
            pendingBootstrapSource = actionSource;
            diagnostics?.RecordStage("avatar_action", "queued", "action_waiting_model");
            Debug.Log("[ConversationPresenter] action_waiting_model: preserving action until model binding.", this);

            if (pendingBootstrapExpiry != null)
            {
                StopCoroutine(pendingBootstrapExpiry);
            }
            pendingBootstrapExpiry = StartCoroutine(ExpirePendingBootstrapIntent(executionContext, 20f));
        }

        private System.Collections.IEnumerator ExpirePendingBootstrapIntent(
            AvatarActionExecutionContext executionContext,
            float timeoutSeconds)
        {
            yield return new WaitForSecondsRealtime(Mathf.Max(.1f, timeoutSeconds));
            pendingBootstrapExpiry = null;
            if (!pendingBootstrapIntent || !ReferenceEquals(pendingBootstrapContext, executionContext))
            {
                yield break;
            }

            pendingBootstrapIntent = false;
            pendingBootstrapContext = null;
            diagnostics?.RecordStage("avatar_action", "limited", "action_model_bind_timeout");
            ReportRejected(executionContext, "asset_missing");
        }

        private void FlushPendingBootstrapIntent()
        {
            if (!pendingBootstrapIntent || avatar == null)
            {
                return;
            }

            var context = pendingBootstrapContext;
            var emotion = pendingBootstrapEmotion;
            var gesture = pendingBootstrapGesture;
            var lookAt = pendingBootstrapLookAt;
            var intensity = pendingBootstrapIntensity;
            var durationMs = pendingBootstrapDurationMs;
            var parameters = pendingBootstrapParameters;
            var transition = pendingBootstrapTransition;
            var source = pendingBootstrapSource;
            pendingBootstrapIntent = false;
            pendingBootstrapContext = null;
            pendingBootstrapParameters = null;
            pendingBootstrapTransition = null;
            pendingBootstrapExpiry = null;

            diagnostics?.RecordStage("avatar_action", "processing", "action_model_bound_resume");
            ApplyIntent(
                emotion,
                gesture,
                lookAt,
                intensity,
                durationMs,
                context,
                parameters,
                transition,
                source);
        }

        public void InterruptTrackedAction(string turnId, string reasonCode = "user_interrupted")
        {
            if (activeTrackedAction == null ||
                !string.Equals(activeTrackedAction.TurnId, turnId, StringComparison.Ordinal))
            {
                return;
            }

            var context = activeTrackedAction;
            EmitActionUpdate(context, AvatarActionReceiptPhase.Interrupted, "runtime", reasonCode);
            if (timedActionCompletion != null)
            {
                StopCoroutine(timedActionCompletion);
                timedActionCompletion = null;
            }
            if (vmdActions != null &&
                (vmdActions.IsLoading || vmdActions.IsPlaying || vmdActions.IsHoldingEndPose || vmdActions.IsBlendingOut))
            {
                vmdActions.StopAndReturnToIdle();
            }
            else if (avatar != null && avatar.CurrentAction != "idle")
            {
                avatar.PlayActionFromSource("idle", AvatarActionSource.System);
            }
        }

        private void ActivateTrackedAction(AvatarActionExecutionContext context)
        {
            PrepareTrackedAction(context);
            ReportAccepted(context);
        }

        private void PrepareTrackedAction(AvatarActionExecutionContext context)
        {
            if (context == null) return;
            if (activeTrackedAction != null && !IsTrackedActionCurrent(context))
            {
                EmitActionUpdate(
                    activeTrackedAction,
                    AvatarActionReceiptPhase.Interrupted,
                    "runtime",
                    "superseded");
                if (timedActionCompletion != null)
                {
                    StopCoroutine(timedActionCompletion);
                    timedActionCompletion = null;
                }
            }
            activeTrackedAction = context;
            activeTrackedActionAccepted = false;
            activeTrackedActionStarted = false;
            activeTrackedActionAcceptedAt = Time.unscaledTime;
        }

        private void ReportAccepted(AvatarActionExecutionContext context)
        {
            if (!IsTrackedActionCurrent(context) || activeTrackedActionAccepted) return;
            activeTrackedActionAccepted = true;
            EmitActionUpdate(context, AvatarActionReceiptPhase.Accepted, "backend", string.Empty);
        }

        private void StartTimedTrackedAction(
            AvatarActionExecutionContext context,
            float durationSeconds,
            string source)
        {
            if (context == null) return;
            ReportStarted(context, source);
            if (timedActionCompletion != null) StopCoroutine(timedActionCompletion);
            timedActionCompletion = StartCoroutine(CompleteTrackedActionAfter(
                context,
                Mathf.Max(.01f, durationSeconds),
                source));
        }

        private System.Collections.IEnumerator CompleteTrackedActionAfter(
            AvatarActionExecutionContext context,
            float durationSeconds,
            string source)
        {
            yield return new WaitForSecondsRealtime(durationSeconds);
            timedActionCompletion = null;
            ReportCompleted(context, source);
        }

        private bool IsTrackedActionCurrent(AvatarActionExecutionContext context)
        {
            return context != null && activeTrackedAction != null &&
                string.Equals(context.TurnId, activeTrackedAction.TurnId, StringComparison.Ordinal) &&
                string.Equals(context.ActionId, activeTrackedAction.ActionId, StringComparison.Ordinal);
        }

        private void ReportStarted(AvatarActionExecutionContext context, string source)
        {
            if (!IsTrackedActionCurrent(context) || !activeTrackedActionAccepted || activeTrackedActionStarted) return;
            activeTrackedActionStarted = true;
            EmitActionUpdate(context, AvatarActionReceiptPhase.Started, source, string.Empty);
        }

        private void ReportCompleted(AvatarActionExecutionContext context, string source)
        {
            if (!IsTrackedActionCurrent(context) || !activeTrackedActionStarted) return;
            EmitActionUpdate(context, AvatarActionReceiptPhase.Completed, source, string.Empty);
        }

        private void ReportRejected(AvatarActionExecutionContext context, string reasonCode)
        {
            if (context == null) return;
            if (activeTrackedAction != null && !IsTrackedActionCurrent(context))
            {
                ActionExecutionChanged?.Invoke(new AvatarActionExecutionUpdate
                {
                    Context = context,
                    Phase = AvatarActionReceiptPhase.Rejected,
                    Source = "runtime",
                    ReasonCode = reasonCode,
                    ElapsedMs = 0
                });
                return;
            }
            if (activeTrackedAction == null)
            {
                activeTrackedAction = context;
                activeTrackedActionAccepted = false;
                activeTrackedActionAcceptedAt = Time.unscaledTime;
            }
            if (!IsTrackedActionCurrent(context)) return;
            EmitActionUpdate(context, AvatarActionReceiptPhase.Rejected, "runtime", reasonCode);
        }

        private void SubscribeVmdLifecycle()
        {
            if (vmdActions == null) return;
            vmdActions.PlaybackChanged -= HandleVmdPlaybackChanged;
            vmdActions.PlaybackChanged += HandleVmdPlaybackChanged;
        }

        private void UnsubscribeVmdLifecycle()
        {
            if (vmdActions == null) return;
            vmdActions.PlaybackChanged -= HandleVmdPlaybackChanged;
        }

        private void HandleVmdPlaybackChanged()
        {
            if (activeTrackedAction == null ||
                (activeTrackedAction.Gesture != "dance" && activeTrackedAction.Gesture != "dance_next"))
            {
                return;
            }

            if (vmdActions.PlaybackPhase == VmdPlaybackPhase.Playing)
            {
                ReportStarted(activeTrackedAction, "imported_vmd");
            }
            else if (vmdActions.PlaybackPhase == VmdPlaybackPhase.Idle && activeTrackedActionStarted)
            {
                ReportCompleted(activeTrackedAction, "imported_vmd");
            }
        }

        private void EmitActionUpdate(
            AvatarActionExecutionContext context,
            AvatarActionReceiptPhase phase,
            string source,
            string reasonCode)
        {
            if (context == null) return;
            var terminal = phase == AvatarActionReceiptPhase.Completed ||
                phase == AvatarActionReceiptPhase.Rejected ||
                phase == AvatarActionReceiptPhase.Interrupted;
            ActionExecutionChanged?.Invoke(new AvatarActionExecutionUpdate
            {
                Context = context,
                Phase = phase,
                Source = source,
                ReasonCode = reasonCode,
                ElapsedMs = Mathf.Max(0, Mathf.RoundToInt(
                    (Time.unscaledTime - activeTrackedActionAcceptedAt) * 1000f))
            });
            if (terminal && IsTrackedActionCurrent(context))
            {
                activeTrackedAction = null;
                activeTrackedActionAccepted = false;
                activeTrackedActionStarted = false;
            }
        }

        public static string NormalizeExpression(string expression)
        {
            var value = string.IsNullOrWhiteSpace(expression) ? "neutral" : expression.Trim().ToLowerInvariant();
            return value == "happy" || value == "shy" || value == "surprised" || value == "sad"
                ? value
                : "neutral";
        }

        private void LateUpdate()
        {
            if (avatar == null)
            {
                return;
            }

            // The action/physics writers have produced this frame's authored
            // pose by now. Restore the previous authored pose only when the
            // head still contains our last presented value. If an action or
            // physics writer replaced it this frame, that new value is already
            // the correct base and must not be multiplied by the old inverse.
            PrepareAuthoredHeadPose();

            var semanticContact = humanInteraction != null && humanInteraction.HasSemanticContact;
            var idleAttention = ShouldUseIdleUserGaze(state, semanticContact, gazeAtUserWhileIdle);
            var conversationAttention = ShouldUseConversationUserGaze(
                state,
                semanticContact,
                gazeAtUserDuringConversation);
            var wantsAttention = lookAtMode != "none" || idleAttention || conversationAttention;
            if (semanticContact)
            {
                // A real hand response remains the authored base, but contact
                // must not cancel eye contact during a live conversation. A
                // reduced additive weight keeps the hand-directed reaction
                // readable while avoiding the visibly frozen gaze reported on
                // the device.
                var contactGazeWeight = GetContactGazeWeight(state, semanticContact);
                gazeBlend = Mathf.MoveTowards(
                    gazeBlend,
                    contactGazeWeight,
                    Time.unscaledDeltaTime * 3.5f);
                var contactGazeMode = contactGazeWeight > .001f ? "user" : "physical_contact";
                RecordGazeDiagnostic(true, contactGazeWeight > .001f, contactGazeMode);
                if (contactGazeWeight > .001f)
                {
                    ApplyGaze(gazeBlend, "user");
                }
                else
                {
                    smoothedHeadRotation = Quaternion.identity;
                    hasSmoothedHeadRotation = false;
                }
            }
            else
            {
                gazeBlend = Mathf.MoveTowards(
                    gazeBlend,
                    wantsAttention ? 1f : 0f,
                    Time.unscaledDeltaTime * (idleAttention ? idleGazeBlendSpeed : 3.5f));
                var gazeMode = ResolveGazeMode(state, idleAttention, conversationAttention, lookAtMode);
                RecordGazeDiagnostic(false, wantsAttention, gazeMode);
                ApplyGaze(gazeBlend, gazeMode);
            }

            ApplyExpressions();
            UpdateIdleBehavior(semanticContact);
        }

        internal void ApplyMouthLatePass()
        {
            if (avatar == null) return;
            var speechLevel = state == ConversationState.Speaking && audioPlayer != null
                ? audioPlayer.AudibleRms
                : 0f;
            lastAudibleRms = Mathf.Max(0f, speechLevel);
            ApplyMouth(speechLevel);
        }

        private void UpdateIdleBehavior(bool semanticContact)
        {
            if (behavior.TryTakeIdleBehavior(
                    state,
                    semanticContact,
                    IsImportedMotionBusy(),
                    avatar.CurrentAction,
                    Time.unscaledTime,
                    UnityEngine.Random.value,
                    out var gesture))
            {
                diagnostics?.Record("AvatarAction", "自动待机动作：" + gesture);
                avatar.PlayActionFromSource(gesture, AvatarActionSource.Idle);
            }
        }

        private bool IsImportedMotionBusy()
        {
            return vmdActions != null &&
                (vmdActions.IsLoading || vmdActions.IsPlaying ||
                    vmdActions.IsHoldingEndPose || vmdActions.IsBlendingOut);
        }

        private void HandleAvatarActionChanged(string nextAction)
        {
            var normalized = string.IsNullOrWhiteSpace(nextAction) ? "idle" : nextAction;
            diagnostics?.Record(
                "AvatarAction",
                "\u52a8\u4f5c\u5207\u6362: " + lastLoggedAction + " -> " + normalized +
                " source=" + (avatar == null ? AvatarActionSource.Unknown : avatar.CurrentActionSource));
            diagnostics?.RecordStage("avatar_action", "completed", "action_state_changed");
            lastLoggedAction = normalized;

            if (activeTrackedAction == null)
            {
                return;
            }

            var requested = activeTrackedAction.Gesture;
            var isDance = requested == "dance" || requested == "dance_next";
            var isRequestedState = string.Equals(normalized, requested, StringComparison.Ordinal) ||
                (requested == "lie" && normalized == "lie_down") ||
                (isDance && (normalized == "vmd" || normalized == "dance"));
            if (isRequestedState)
            {
                var context = activeTrackedAction;
                ReportStarted(context, normalized == "vmd" ? "imported_vmd" : "avatar_controller");
                // A resting pose becomes an accomplished world action once its
                // alignment service commits the matching avatar state. The pose
                // may remain active indefinitely, but the transition is complete.
                if (requested == "sit" || requested == "lie" || requested == "lie_down")
                {
                    ReportCompleted(context, "placement");
                }
                return;
            }

            if (normalized == "idle")
            {
                if (activeTrackedActionStarted)
                {
                    ReportCompleted(activeTrackedAction, "avatar_controller");
                }
                return;
            }

            EmitActionUpdate(
                activeTrackedAction,
                AvatarActionReceiptPhase.Interrupted,
                "runtime",
                "superseded");
        }

        private void RecordGazeDiagnostic(bool semanticContact, bool wantsAttention, string gazeMode)
        {
            var cameraAvailable = Camera.main != null;
            var owner = semanticContact
                ? "physical_contact"
                : !cameraAvailable
                    ? "camera_missing"
                    : wantsAttention
                        ? "conversation_or_idle"
                        : "released";
            var action = avatar == null ? "none" : avatar.CurrentAction;
            var signature = $"owner={owner}|state={state}|mode={gazeMode}|action={action}|" +
                $"semantic_contact={semanticContact}|camera={cameraAvailable}";
            if (string.Equals(signature, lastGazeDiagnostic, StringComparison.Ordinal))
            {
                return;
            }
            lastGazeDiagnostic = signature;
            diagnostics?.Record(
                "ConversationPresenter",
                "Gaze " + signature.Replace('|', ' ') + $" blend={gazeBlend:F2}");
        }

        private void OnDestroy()
        {
            UnsubscribeVmdLifecycle();
            if (avatar != null)
            {
                avatar.ActionChanged -= HandleAvatarActionChanged;
            }
        }

        public static bool ShouldUseIdleUserGaze(ConversationState conversationState, bool semanticContact, bool enabled)
        {
            return enabled && !semanticContact &&
                (conversationState == ConversationState.Idle ||
                    conversationState == ConversationState.Thinking);
        }

        public static string ResolveGazeMode(
            ConversationState conversationState,
            bool idleAttention,
            string requestedMode)
        {
            return ResolveGazeMode(
                conversationState,
                idleAttention,
                ShouldUseConversationUserGaze(conversationState, false, true),
                requestedMode);
        }

        public static string ResolveGazeMode(
            ConversationState conversationState,
            bool idleAttention,
            bool conversationAttention,
            string requestedMode)
        {
            var normalized = string.IsNullOrWhiteSpace(requestedMode)
                ? "none"
                : requestedMode.Trim().ToLowerInvariant();
            if (idleAttention && conversationState == ConversationState.Thinking)
            {
                return "user";
            }
            // A stale or expressive backend look_at must not cancel eye contact
            // in the middle of a live conversation. Physical contact still
            // disables conversationAttention before this resolver is called.
            if (conversationAttention) return "user";
            return idleAttention && normalized == "none" ? "user" : normalized;
        }

        public static bool ShouldUseConversationUserGaze(
            ConversationState conversationState,
            bool semanticContact,
            bool enabled)
        {
            // Physical contact reduces the additive weight in LateUpdate but
            // does not cancel conversational attention while listening,
            // thinking, or speaking.
            return enabled &&
                (conversationState == ConversationState.Listening ||
                    conversationState == ConversationState.Thinking ||
                    conversationState == ConversationState.Speaking);
        }

        public static float GetContactGazeWeight(
            ConversationState conversationState,
            bool semanticContact)
        {
            if (!semanticContact)
            {
                return 0f;
            }

            return conversationState == ConversationState.Listening ||
                conversationState == ConversationState.Thinking ||
                conversationState == ConversationState.Speaking
                ? .28f
                : 0f;
        }

        public static bool ShouldSuspendGazeForAction(string action)
        {
            // Compatibility/query helper: true means that this action has
            // authored head motion and therefore receives a reduced gaze
            // weight. Gaze is no longer completely disabled for the action.
            return GetActionGazeWeight(action) < .999f;
        }

        public static float GetActionGazeWeight(string action)
        {
            switch (string.IsNullOrWhiteSpace(action) ? string.Empty : action.Trim().ToLowerInvariant())
            {
                case "bow":
                case "nod":
                    return .18f;
                case "refuse":
                    return .25f;
                case "dance":
                case "dance_next":
                case "vmd":
                    return .35f;
                case "turn_half":
                    return .45f;
                case "sit":
                case "lie_down":
                    return .55f;
                case "wave":
                case "sway":
                case "raise_hand":
                case "raise_leg":
                case "crouch":
                case "step_back":
                    return .78f;
                default:
                    return 1f;
            }
        }

        public static float GetActionGazeWeight(string action, ConversationState conversationState)
        {
            var authoredWeight = GetActionGazeWeight(action);
            var isConversation = conversationState == ConversationState.Listening ||
                conversationState == ConversationState.Thinking ||
                conversationState == ConversationState.Speaking;
            // During a live turn the avatar must visibly keep attending to the
            // user. The authored pose remains the base, but cannot reduce gaze
            // below this readable conversational weight.
            return isConversation ? Mathf.Max(.65f, authoredWeight) : authoredWeight;
        }

        public static Quaternion SmoothGazeRotation(
            Quaternion current,
            Quaternion target,
            float deltaTime,
            float trackingSpeed)
        {
            var amount = 1f - Mathf.Exp(-Mathf.Max(.01f, trackingSpeed) * Mathf.Max(0f, deltaTime));
            return Quaternion.Slerp(current, target, amount);
        }

        private void ApplyGaze(float amount, string mode)
        {
            if (head == null)
            {
                hasSmoothedHeadRotation = false;
                return;
            }
            // XR cameras can be unavailable during reconnect. Do not erase
            // the current action pose just because a target is missing.
            if (Camera.main == null)
            {
                // No gaze offset is presented this frame. Treat the pose
                // authored by the action/VMD writer as the next base instead
                // of retaining a stale base from the previous camera frame.
                authoredHeadRotation = head.localRotation;
                hasPresentedHeadRotation = false;
                return;
            }
            if (amount <= .001f)
            {
                if (!hasSmoothedHeadRotation)
                {
                    smoothedHeadRotation = Quaternion.identity;
                    hasSmoothedHeadRotation = true;
                }
                smoothedHeadRotation = SmoothGazeRotation(
                    smoothedHeadRotation,
                    Quaternion.identity,
                    Time.unscaledDeltaTime,
                    gazeTrackingSpeed);
                // Keep composing the decaying local offset for this frame.
                // Returning before this write would snap an authored action
                // directly to its base pose when attention is released.
                ApplyHeadGazeOffset();
                return;
            }

            var direction = Camera.main.transform.position - head.position;
            if (direction.sqrMagnitude <= .0001f)
            {
                return;
            }
            var localDirection = avatar.transform.InverseTransformDirection(direction.normalized);
            var userYaw = Mathf.Clamp(Mathf.Atan2(localDirection.x, localDirection.z) * Mathf.Rad2Deg, -24f, 24f);
            var yaw = mode == "away" ? (userYaw >= 0f ? -18f : 18f) : userYaw;
            var pitch = mode == "away"
                ? 2f
                : Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(localDirection.y, -1f, 1f)) * Mathf.Rad2Deg, -14f, 14f);
            var desired = Quaternion.Slerp(
                Quaternion.identity,
                Quaternion.Euler(pitch, yaw * .65f, 0f),
                Mathf.Clamp01(amount * (avatar == null
                    ? 1f
                    : GetActionGazeWeight(avatar.CurrentAction, state))));
            if (!hasSmoothedHeadRotation)
            {
                smoothedHeadRotation = Quaternion.identity;
                hasSmoothedHeadRotation = true;
            }
            smoothedHeadRotation = SmoothGazeRotation(
                smoothedHeadRotation,
                desired,
                Time.unscaledDeltaTime,
                gazeTrackingSpeed);
            // The base for this frame is the pose written by the active action
            // or imported VMD. Compose the attention offset on top of it.
            ApplyHeadGazeOffset();
        }

        private void PrepareAuthoredHeadPose()
        {
            if (head == null)
            {
                return;
            }

            if (!hasPresentedHeadRotation)
            {
                // A contact writer or an action/VMD writer may have produced
                // a pose while gaze was inactive. That pose is authoritative.
                authoredHeadRotation = head.localRotation;
                return;
            }

            // Quaternion.Angle is intentionally tiny: an unchanged Transform
            // compares exactly, while a VMD/action rewrite should be treated as
            // an authored replacement even when the new pose is close by.
            if (Quaternion.Angle(head.localRotation, presentedHeadRotation) <= .01f)
            {
                head.localRotation = authoredHeadRotation;
            }
            else
            {
                // The earlier writer replaced the previous presented value.
                // Adopt that live pose instead of composing the new gaze over
                // the previous frame's authored base.
                authoredHeadRotation = head.localRotation;
            }
            hasPresentedHeadRotation = false;
        }

        private void ApplyHeadGazeOffset()
        {
            if (head == null)
            {
                return;
            }

            authoredHeadRotation = head.localRotation;
            head.localRotation = authoredHeadRotation * smoothedHeadRotation;
            presentedHeadRotation = head.localRotation;
            hasPresentedHeadRotation = true;
        }

        private void ApplyMouth(float rms)
        {
            ApplyMouth(rms, Time.unscaledDeltaTime);
        }

        private void ApplyMouth(float rms, float deltaTime)
        {
            lastAudibleRms = Mathf.Max(0f, rms);
            if (visemes.Count == 0 && jaw == null)
            {
                lastVisibleMouthAmount = 0f;
                lastTimelinePositionMs = 0f;
                lastTimelinePeak = 0f;
                return;
            }

            var targetAmount = Mathf.Clamp01((rms - .0025f) * 22f);
            var amount = SmoothMouthAmount(
                smoothedMouthAmount,
                targetAmount,
                deltaTime,
                mouthAttackSpeed,
                mouthReleaseSpeed);
            smoothedMouthAmount = amount;
            var useTimeline = speechTimelineMatchesAvatar && audioPlayer != null &&
                audioPlayer.PlaybackStarted;
            var timelinePositionMs = useTimeline
                ? audioPlayer.AudiblePlaybackSeconds * 1000f
                : 0f;
            var timelinePeak = useTimeline ? TimelinePeak(timelinePositionMs) : 1f;
            var totalInfluence = visemes.Count == 0
                ? 1f
                : BuildVisemeInfluences(
                    useTimeline,
                    timelinePositionMs,
                    targetAmount <= .001f,
                    deltaTime);
            var visibleAmount = amount * Mathf.Clamp01(totalInfluence);
            lastTimelinePositionMs = timelinePositionMs;
            lastTimelinePeak = timelinePeak;
            lastVisibleMouthAmount = visibleAmount;
            if (visibleAmount <= .001f)
            {
                if (mouthWasActive)
                {
                    RestoreMouth();
                    RestoreJaw();
                    mouthWasActive = false;
                }
                if (amount <= .001f)
                {
                    ResetVisemeInfluences();
                }
                return;
            }

            mouthWasActive = true;
            for (var i = 0; i < visemes.Count; i++)
            {
                var viseme = visemes[i];
                var influence = viseme.Group >= 0 && viseme.Group < visemeInfluences.Length
                    ? NormalizeVisemeInfluence(visemeInfluences[viseme.Group], totalInfluence)
                    : 0f;
                var add = amount * influence * 68f;
                if (viseme.Renderer != null)
                {
                    var current = viseme.Renderer.GetBlendShapeWeight(viseme.Index);
                    var authored = ResolveMorphLayerBaseWeight(
                        current,
                        viseme.LastCompositeWeight,
                        viseme.LastSpeechWeight,
                        viseme.HasSpeechComposite,
                        viseme.BaseWeight);
                    var composite = ComposeMorphLayerWeight(authored, add);
                    viseme.Renderer.SetBlendShapeWeight(viseme.Index, composite);
                    viseme.LastSpeechWeight = Mathf.Max(0f, composite - authored);
                    viseme.LastCompositeWeight = composite;
                    viseme.HasSpeechComposite = add > .001f;
                    visemes[i] = viseme;
                }
            }
            // A blend-shape vowel already opens the mouth. Rotating a jaw bone
            // on top of it doubles the deformation on avatars that expose both.
            if (jaw != null && visemes.Count == 0)
            {
                jaw.localRotation = Quaternion.Slerp(
                    jawBaseRotation,
                    jawBaseRotation * Quaternion.Euler(visibleAmount * 13f, 0f, 0f),
                    visibleAmount);
            }
        }

        private float TimelinePeak(float positionMs)
        {
            var peak = 0f;
            for (var index = 0; index < speechTimeline.Length; index++)
            {
                var cue = speechTimeline[index];
                if (cue != null && GetVisemeGroup(cue.Symbol) >= 0)
                {
                    peak = Mathf.Max(peak, SpeechCueEnvelope(cue, positionMs));
                }
            }
            return peak;
        }

        private float TimelineInfluence(int group, float positionMs)
        {
            if (group < 0) return 0f;
            var peak = 0f;
            for (var index = 0; index < speechTimeline.Length; index++)
            {
                var cue = speechTimeline[index];
                if (cue != null && GetVisemeGroup(cue.Symbol) == group)
                {
                    peak = Mathf.Max(peak, SpeechCueEnvelope(cue, positionMs));
                }
            }
            return peak;
        }

        private float BuildVisemeInfluences(
            bool useTimeline,
            float positionMs,
            bool preserveCurrentDistribution,
            float deltaTime)
        {
            Array.Clear(targetVisemeInfluences, 0, targetVisemeInfluences.Length);
            if (useTimeline)
            {
                for (var group = 0; group < targetVisemeInfluences.Length; group++)
                {
                    targetVisemeInfluences[group] = TimelineInfluence(group, positionMs);
                }
            }
            else if (preserveCurrentDistribution)
            {
                // Reply completion clears the timeline before the RMS envelope
                // has fully released. Retaining the last vowel while the mouth
                // closes avoids a visible last-frame jump back to the fallback A.
                Array.Copy(
                    visemeInfluences,
                    targetVisemeInfluences,
                    visemeInfluences.Length);
            }
            else if (fallbackVisemeGroup >= 0 &&
                     fallbackVisemeGroup < targetVisemeInfluences.Length)
            {
                targetVisemeInfluences[fallbackVisemeGroup] = 1f;
            }

            var targetTotal = 0f;
            for (var group = 0; group < targetVisemeInfluences.Length; group++)
            {
                targetTotal += Mathf.Max(0f, targetVisemeInfluences[group]);
            }
            if (targetTotal > 1f)
            {
                for (var group = 0; group < targetVisemeInfluences.Length; group++)
                {
                    targetVisemeInfluences[group] /= targetTotal;
                }
            }

            var total = 0f;
            for (var group = 0; group < visemeInfluences.Length; group++)
            {
                visemeInfluences[group] = SmoothVisemeInfluence(
                    visemeInfluences[group],
                    targetVisemeInfluences[group],
                    deltaTime,
                    visemeCrossfadeSpeed);
                total += visemeInfluences[group];
            }
            return total;
        }

        public static float SmoothVisemeInfluence(
            float current,
            float target,
            float deltaTime,
            float speed)
        {
            var safeCurrent = Mathf.Clamp01(current);
            var safeTarget = Mathf.Clamp01(target);
            var blend = 1f - Mathf.Exp(
                -Mathf.Max(.01f, speed) * Mathf.Max(0f, deltaTime));
            return Mathf.Lerp(safeCurrent, safeTarget, blend);
        }

        private void ResetVisemeInfluences()
        {
            Array.Clear(visemeInfluences, 0, visemeInfluences.Length);
            Array.Clear(targetVisemeInfluences, 0, targetVisemeInfluences.Length);
        }

        public static float NormalizeVisemeInfluence(float influence, float totalInfluence)
        {
            var value = Mathf.Max(0f, influence);
            var total = Mathf.Max(0f, totalInfluence);
            return total > 1f ? value / total : value;
        }

        public static float ResolveMorphLayerBaseWeight(
            float currentWeight,
            float previousCompositeWeight,
            float previousContribution,
            bool hasPreviousComposite,
            float fallbackWeight = 0f)
        {
            var current = float.IsNaN(currentWeight) || float.IsInfinity(currentWeight)
                ? Mathf.Clamp(fallbackWeight, 0f, 100f)
                : Mathf.Clamp(currentWeight, 0f, 100f);
            if (!hasPreviousComposite ||
                Mathf.Abs(current - previousCompositeWeight) > .05f)
            {
                // An upstream owner (for example VMD facial animation) wrote a
                // fresh value this frame. Compose on top of that authored pose.
                return current;
            }
            return Mathf.Clamp(current - Mathf.Max(0f, previousContribution), 0f, 100f);
        }

        public static float ComposeMorphLayerWeight(float authoredWeight, float contribution)
        {
            var authored = float.IsNaN(authoredWeight) || float.IsInfinity(authoredWeight)
                ? 0f
                : authoredWeight;
            var added = float.IsNaN(contribution) || float.IsInfinity(contribution)
                ? 0f
                : contribution;
            return Mathf.Clamp(authored + Mathf.Max(0f, added), 0f, 100f);
        }

        public static float SpeechCueEnvelope(
            SpeechVisemeCue cue,
            float positionMs,
            float transitionMs = 65f)
        {
            if (cue == null || cue.EndMs <= cue.StartMs) return 0f;
            var ramp = Mathf.Max(1f, transitionMs);
            var weight = Mathf.Clamp01(cue.Weight);
            if (positionMs < cue.StartMs - ramp || positionMs > cue.EndMs + ramp) return 0f;
            if (positionMs < cue.StartMs)
                return weight * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(cue.StartMs - ramp, cue.StartMs, positionMs));
            if (positionMs > cue.EndMs)
                return weight * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(cue.EndMs, cue.EndMs + ramp, positionMs)));
            return weight;
        }

        public static float SmoothMouthAmount(
            float current,
            float target,
            float deltaTime,
            float attackSpeed,
            float releaseSpeed)
        {
            var safeCurrent = Mathf.Clamp01(current);
            var safeTarget = Mathf.Clamp01(target);
            var speed = safeTarget > safeCurrent
                ? Mathf.Max(.01f, attackSpeed)
                : Mathf.Max(.01f, releaseSpeed);
            return Mathf.MoveTowards(safeCurrent, safeTarget, Mathf.Max(0f, deltaTime) * speed);
        }

        private void CacheVisemes()
        {
            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var r = 0; r < renderers.Length; r++)
            {
                var mesh = renderers[r].sharedMesh;
                if (mesh == null)
                {
                    continue;
                }
                for (var i = 0; i < mesh.blendShapeCount; i++)
                {
                    var group = GetVisemeGroup(mesh.GetBlendShapeName(i));
                    if (group < 0)
                    {
                        continue;
                    }
                    visemes.Add(new Viseme
                    {
                        Renderer = renderers[r],
                        Index = i,
                        BaseWeight = renderers[r].GetBlendShapeWeight(i),
                        Group = group
                    });
                }
            }
            fallbackVisemeGroup = SelectFallbackVisemeGroup(visemes);
        }

        private void CacheExpressions()
        {
            if (avatar == null)
            {
                return;
            }

            var renderers = avatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                var mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }
                for (var shapeIndex = 0; shapeIndex < mesh.blendShapeCount; shapeIndex++)
                {
                    var name = Normalize(mesh.GetBlendShapeName(shapeIndex));
                    if (IsMouthShape(name) || !IsExpressionShape(name))
                    {
                        continue;
                    }
                    expressions.Add(new ExpressionMorph
                    {
                        Renderer = renderer,
                        Index = shapeIndex,
                        Name = name,
                        BaseWeight = renderer.GetBlendShapeWeight(shapeIndex),
                        CurrentWeight = 0f
                    });
                }
            }
        }

        private void ApplyExpressions()
        {
            if (expressions.Count == 0)
            {
                return;
            }

            var primaryExpression = SelectPrimaryExpression(targetEmotion);
            var blinkPulse = Mathf.Pow(Mathf.Clamp01(Mathf.Sin(Time.unscaledTime * .66f + .8f)), 28f);
            var step = Time.unscaledDeltaTime * expressionBlendSpeed;
            for (var index = 0; index < expressions.Count; index++)
            {
                var expression = expressions[index];
                var target = index == primaryExpression
                    ? GetExpressionWeight(expression.Name, targetEmotion, targetEmotionIntensity)
                    : 0f;
                if (IsBlinkShape(expression.Name))
                {
                    target = Mathf.Max(target, blinkPulse * 72f);
                }
                expression.CurrentWeight = Mathf.MoveTowards(expression.CurrentWeight, target, step * 100f);
                if (expression.Renderer != null)
                {
                    var current = expression.Renderer.GetBlendShapeWeight(expression.Index);
                    var authored = ResolveMorphLayerBaseWeight(
                        current,
                        expression.LastCompositeWeight,
                        expression.LastExpressionWeight,
                        expression.HasExpressionComposite,
                        expression.BaseWeight);
                    var composite = ComposeMorphLayerWeight(authored, expression.CurrentWeight);
                    if (expression.CurrentWeight > .001f || expression.HasExpressionComposite)
                    {
                        expression.Renderer.SetBlendShapeWeight(expression.Index, composite);
                    }
                    expression.LastExpressionWeight = Mathf.Max(0f, composite - authored);
                    expression.LastCompositeWeight = composite;
                    expression.HasExpressionComposite = expression.CurrentWeight > .001f;
                }
                expressions[index] = expression;
            }
        }

        private void RestoreExpressions()
        {
            for (var index = 0; index < expressions.Count; index++)
            {
                var expression = expressions[index];
                if (expression.Renderer != null)
                {
                    var current = expression.Renderer.GetBlendShapeWeight(expression.Index);
                    var authored = ResolveMorphLayerBaseWeight(
                        current,
                        expression.LastCompositeWeight,
                        expression.LastExpressionWeight,
                        expression.HasExpressionComposite,
                        expression.BaseWeight);
                    if (expression.HasExpressionComposite)
                    {
                        expression.Renderer.SetBlendShapeWeight(expression.Index, authored);
                    }
                }
                expression.CurrentWeight = 0f;
                expression.LastExpressionWeight = 0f;
                expression.LastCompositeWeight = 0f;
                expression.HasExpressionComposite = false;
                expressions[index] = expression;
            }
        }

        public static float GetExpressionWeight(string shapeName, string emotion, float intensity)
        {
            var name = Normalize(shapeName);
            var value = string.IsNullOrWhiteSpace(emotion) ? "neutral" : emotion.ToLowerInvariant();
            var amount = Mathf.Clamp01(intensity) * 38f;
            if (value == "happy" || value == "joy" || value == "fond")
            {
                return ContainsAny(name, "smile", "happy", "joy", "laugh", "笑", "微笑",
                    "笑い", "なごみ", "にっこり", "にやり", "口角上げ") ? amount : 0f;
            }
            if (value == "sad" || value == "sorrow")
            {
                return ContainsAny(name, "sad", "sorrow", "cry", "涙", "悲",
                    "困る", "えーん", "悲しい") ? amount : 0f;
            }
            if (value == "angry" || value == "anger")
            {
                return ContainsAny(name, "angry", "anger", "mad", "怒",
                    "怒り", "怒る", "キリッ") ? amount : 0f;
            }
            if (value == "surprised" || value == "surprise")
            {
                return ContainsAny(name, "surprise", "astonish", "驚",
                    "びっくり", "驚き", "はっ") ? amount : 0f;
            }
            if (value == "embarrassed" || value == "shy")
            {
                return ContainsAny(name, "blush", "shy", "embarrass", "照", "赤",
                    "照れ", "赤面") ? amount * .7f : 0f;
            }
            return 0f;
        }

        private static bool IsExpressionShape(string name)
        {
            return ContainsAny(name, "smile", "happy", "joy", "laugh", "sad", "sorrow", "cry",
                "angry", "anger", "mad", "surprise", "astonish", "blush", "shy", "embarrass",
                "blink", "eyeclose", "eyesclose", "まばたき", "またたき", "瞬き",
                "笑", "微笑", "笑い", "なごみ", "にっこり", "にやり", "口角上げ",
                "悲", "困る", "えーん", "悲しい", "怒", "怒り", "怒る", "キリッ",
                "驚", "びっくり", "驚き", "はっ", "照", "照れ", "赤", "赤面");
        }

        private static bool IsBlinkShape(string name)
        {
            return ContainsAny(name, "blink", "eyeclose", "eyesclose", "まばたき", "またたき", "瞬き");
        }

        private static bool ContainsAny(string value, params string[] candidates)
        {
            for (var index = 0; index < candidates.Length; index++)
            {
                if (value.Contains(candidates[index]))
                {
                    return true;
                }
            }
            return false;
        }
        private void RestoreMouth()
        {
            for (var i = 0; i < visemes.Count; i++)
            {
                var viseme = visemes[i];
                if (viseme.Renderer != null)
                {
                    var current = viseme.Renderer.GetBlendShapeWeight(viseme.Index);
                    var authored = ResolveMorphLayerBaseWeight(
                        current,
                        viseme.LastCompositeWeight,
                        viseme.LastSpeechWeight,
                        viseme.HasSpeechComposite,
                        viseme.BaseWeight);
                    if (viseme.HasSpeechComposite)
                    {
                        viseme.Renderer.SetBlendShapeWeight(viseme.Index, authored);
                    }
                }
                viseme.LastSpeechWeight = 0f;
                viseme.LastCompositeWeight = 0f;
                viseme.HasSpeechComposite = false;
                visemes[i] = viseme;
            }
        }

        private void RestoreHead()
        {
            if (head != null)
            {
                head.localRotation = headBaseRotation;
            }
            smoothedHeadRotation = Quaternion.identity;
            hasSmoothedHeadRotation = false;
            authoredHeadRotation = Quaternion.identity;
            presentedHeadRotation = Quaternion.identity;
            hasPresentedHeadRotation = false;
        }

        private int SelectPrimaryExpression(string emotion)
        {
            var selected = -1;
            var selectedPriority = 0;
            for (var index = 0; index < expressions.Count; index++)
            {
                if (IsBlinkShape(expressions[index].Name)) continue;
                var priority = GetExpressionPriority(expressions[index].Name, emotion);
                if (priority > selectedPriority)
                {
                    selected = index;
                    selectedPriority = priority;
                }
            }
            return selected;
        }

        public static int GetExpressionPriority(string shapeName, string emotion)
        {
            if (GetExpressionWeight(shapeName, emotion, 1f) <= 0f) return 0;
            var name = Normalize(shapeName);
            // Prefer a single authored whole-face or neutral smile morph over
            // stacking left/right corners, mouth-open laughs and eye shapes.
            if (ContainsAny(name, "smile", "happy", "微笑", "なごみ", "にっこり")) return 120;
            if (ContainsAny(name, "sad", "sorrow", "angry", "anger", "surprise",
                    "astonish", "blush", "shy", "embarrass", "悲しい", "怒り",
                    "びっくり", "照れ", "赤面")) return 110;
            if (ContainsAny(name, "口角上げ", "口角下げ")) return 100;
            if (ContainsAny(name, "laugh", "笑い", "cry", "えーん")) return 90;
            return 80;
        }

        private static int SelectFallbackVisemeGroup(List<Viseme> available)
        {
            // With no phoneme timeline, RMS only carries mouth openness. A
            // stable A-like shape is truthful; cycling unrelated vowels is not.
            var preference = new[] { 0, 4, 2, 3, 1 };
            for (var candidateIndex = 0; candidateIndex < preference.Length; candidateIndex++)
            {
                for (var visemeIndex = 0; visemeIndex < available.Count; visemeIndex++)
                {
                    if (available[visemeIndex].Group == preference[candidateIndex])
                        return preference[candidateIndex];
                }
            }
            return -1;
        }

        private void RestoreJaw()
        {
            if (jaw != null)
            {
                jaw.localRotation = jawBaseRotation;
            }
        }

        private static Transform FindJaw(AvatarController target)
        {
            var bones = target.GetComponentsInChildren<MMDBoneTransform>(true);
            for (var i = 0; i < bones.Length; i++)
            {
                var name = Normalize(string.IsNullOrWhiteSpace(bones[i].boneName) ? bones[i].name : bones[i].boneName);
                if (name == "jaw" || name == "lowerjaw" || name == "mouth" || name == "口" || name == "あご" || name == "下顎" || name.Contains("jaw") || name.Contains("lowerjaw"))
                {
                    return bones[i].transform;
                }
            }
            return null;
        }

        private static Transform FindHead(AvatarController target)
        {
            var bones = target.GetComponentsInChildren<MMDBoneTransform>(true);
            for (var i = 0; i < bones.Length; i++)
            {
                var name = Normalize(string.IsNullOrWhiteSpace(bones[i].boneName) ? bones[i].name : bones[i].boneName);
                if (name == "head" || name == "\u982d" || name == "\u5934")
                {
                    return bones[i].transform;
                }
            }
            return null;
        }

        private static bool IsMouthShape(string name)
        {
            return GetVisemeGroup(name) >= 0;
        }

        public static int GetVisemeGroup(string value)
        {
            var name = Normalize(value);
            if (name == "a" || name == "aa" || name == "vrcvaa" || name == "moutha" ||
                name == "\u3042" || name == "\u53e3\u3042") return 0;
            if (name == "i" || name == "ih" || name == "vrcvih" || name == "mouthi" ||
                name == "\u3044" || name == "\u53e3\u3044") return 1;
            if (name == "u" || name == "ou" || name == "vrcvou" || name == "mouthu" ||
                name == "\u3046" || name == "\u53e3\u3046") return 2;
            if (name == "e" || name == "ee" || name == "vrcvee" || name == "mouthe" ||
                name == "\u3048" || name == "\u53e3\u3048") return 3;
            if (name == "o" || name == "oh" || name == "vrcvoh" || name == "moutho" ||
                name == "\u304a" || name == "\u53e3\u304a") return 4;
            return -1;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(".", string.Empty);
        }

        private void OnDisable()
        {
            if (activeTrackedAction != null)
            {
                EmitActionUpdate(
                    activeTrackedAction,
                    AvatarActionReceiptPhase.Interrupted,
                    "runtime",
                    "system_interrupted");
            }
            RestoreMouth();
            RestoreExpressions();
            RestoreJaw();
            RestoreHead();
        }
    }
}

using System;
using UnityEngine;

namespace QuestMmdPlayer
{
    [DisallowMultipleComponent]
    public sealed class ConversationController : MonoBehaviour
    {
        private readonly ConversationStateMachine stateMachine = new ConversationStateMachine();
        private IConversationTransport transport;
        private Pcm16StreamAudioPlayer audioPlayer;
        private AvatarConversationPresenter presenter;
        private AvatarHumanInteraction boundHumanInteraction;
        private HumanInteractionKind lastInteraction;
        private string latestInteractionEventId = string.Empty;
        private float interactionStartedAt;
        private float nextInteractionUpdateAt;
        private float interruptedUntil;
        private float errorUntil;
        private bool localReactionModeInitialized;
        private bool localReactionsEnabled = true;
        private float turnStartedAt = -1f;
        private float firstInputChunkAt = -1f;
        private float inputEndedAt = -1f;
        private float asrFinalAt = -1f;
        private float firstEventAt = -1f;
        private float firstTextAt = -1f;
        private float firstAudioAt = -1f;
        private float playbackStartedAt = -1f;
        private float replyEndedAt = -1f;
        private float audioDoneAt = -1f;
        private float responseWaitStartedAt = -1f;
        private float lastBackendProgressAt = -1f;
        private bool awaitingBackendResponse;
        private int replyAudioChunkCount;
        private string pendingLocalAction = string.Empty;
        private bool localActionStarted;
        private bool backendActionReceived;
        private bool backendSttReported;
        private bool backendDecisionReported;
        private bool backendTtsReported;
        private bool backendTotalReported;
        private int activePlaybackGeneration = -1;
        private RuntimeDebugLog diagnostics;
        [SerializeField] private bool allowAutomaticMockTransport;
        [SerializeField] private bool sendInteractionEvents = true;
        [SerializeField, Range(8f, 90f)] private float firstResponseTimeoutSeconds = 35f;
        [SerializeField, Range(8f, 90f)] private float responseStallTimeoutSeconds = 30f;
        private const float InteractionUpdateInterval = 5f;

        public event Action<ConversationState> StateChanged;

        public ConversationState State => stateMachine.State;
        public string TurnId => stateMachine.TurnId;
        public string Transcript => stateMachine.Transcript;
        public string ReplyText => stateMachine.ReplyText;
        public string TransportStatus => transport == null ? "No conversation transport" : transport.Status;
        public string PresenterStatus => presenter == null ? "No avatar presenter" : presenter.Status;
        public string LastErrorCode { get; private set; } = string.Empty;
        public string LastErrorMessage => stateMachine.ErrorMessage;
        public float BufferedAudioSeconds => audioPlayer == null ? 0f : audioPlayer.BufferedSeconds;
        // Capture stays armed while a reply is playing. A new VAD activation
        // is a deliberate barge-in: BeginVoiceInput cancels the current turn,
        // flushes playback, and starts a fresh generation.
        public bool CanStartVoiceInput => transport != null && transport.IsConnected &&
            stateMachine.State != ConversationState.Error &&
            (stateMachine.State != ConversationState.Listening || awaitingBackendResponse);
        public bool IsRealBackendConnected => transport != null && !(transport is MockConversationTransport) && transport.IsConnected;
        public bool IsUsingMockTransport => transport is MockConversationTransport;
        public string Status => $"{State} | {TransportStatus}";
        public string TurnTimingStatus => BuildTimingStatus(Time.unscaledTime);
        public bool AwaitingBackendResponse => awaitingBackendResponse;
        public int TranscriptCharacters => string.IsNullOrEmpty(stateMachine.Transcript) ? 0 : stateMachine.Transcript.Length;
        public int ReplyTextCharacters => string.IsNullOrEmpty(stateMachine.ReplyText) ? 0 : stateMachine.ReplyText.Length;
        public string ManualExpression => presenter == null ? "neutral" : presenter.ManualExpression;

        private void Awake()
        {
            diagnostics = GetComponent<RuntimeDebugLog>();
            audioPlayer = GetComponent<Pcm16StreamAudioPlayer>() ?? gameObject.AddComponent<Pcm16StreamAudioPlayer>();
            presenter = GetComponent<AvatarConversationPresenter>() ?? gameObject.AddComponent<AvatarConversationPresenter>();
            var discovered = FindTransport();
            if (discovered != null)
            {
                SetTransport(discovered);
            }
            else if (allowAutomaticMockTransport)
            {
                SetTransport(gameObject.AddComponent<MockConversationTransport>());
                Debug.LogWarning("[Conversation] Explicitly configured automatic Mock transport; AstrBot is bypassed.", this);
            }
            else
            {
                RecordStage("eventbus", "failed", "bridge_disconnected");
                Debug.LogError("[Conversation] No AstrBot transport found; conversation is unavailable until pairing succeeds.", this);
            }
            RefreshLocalReactionMode();
        }

        public void SetManualExpression(string expression)
        {
            presenter?.SetManualExpression(expression);
        }

        private void OnEnable()
        {
            SubscribeTransport();
            SubscribeInteraction();
            if (audioPlayer != null)
            {
                audioPlayer.PlaybackTelemetryReady += HandlePlaybackTelemetry;
            }
        }

        private void OnDisable()
        {
            if (sendInteractionEvents && lastInteraction != HumanInteractionKind.None)
            {
                transport?.SendInteraction(InteractionName(lastInteraction), "cancel", 0f, InteractionDurationMs());
                lastInteraction = HumanInteractionKind.None;
            }
            UnsubscribeTransport();
            UnsubscribeInteraction();
            if (audioPlayer != null)
            {
                audioPlayer.PlaybackTelemetryReady -= HandlePlaybackTelemetry;
            }
            if (transport != null && !string.IsNullOrEmpty(stateMachine.TurnId))
            {
                transport.Interrupt(stateMachine.TurnId);
            }
            if (audioPlayer != null)
            {
                StopAudioStream();
            }
            stateMachine.ResetToIdle();
            awaitingBackendResponse = false;
            if (presenter != null)
            {
                presenter.SetConversationState(ConversationState.Idle);
            }
        }

        private void Update()
        {
            RefreshLocalReactionMode();
            if (stateMachine.TryFinishAudio(audioPlayer == null || audioPlayer.IsDrained))
            {
                audioDoneAt = Time.unscaledTime;
                RecordStage(
                    "audio_playback",
                    "completed",
                    elapsedMs: ElapsedMsValue(firstAudioAt, audioDoneAt),
                    chunks: replyAudioChunkCount);
                Debug.Log("[Conversation] Voice timing " + BuildTimingStatus(Time.unscaledTime), this);
                NotifyStateChanged();
            }
            if (stateMachine.State == ConversationState.Interrupted && Time.unscaledTime >= interruptedUntil)
            {
                stateMachine.ResetToIdle();
                NotifyStateChanged();
            }
            if (stateMachine.State == ConversationState.Error && Time.unscaledTime >= errorUntil)
            {
                stateMachine.ResetToIdle();
                NotifyStateChanged();
            }
            if (ShouldTimeoutResponse(
                awaitingBackendResponse,
                stateMachine.ReplyEnded,
                Time.unscaledTime,
                responseWaitStartedAt,
                lastBackendProgressAt,
                firstResponseTimeoutSeconds,
                responseStallTimeoutSeconds,
                out var timeoutCode))
            {
                FailActiveTurn(timeoutCode, timeoutCode == "response_first_event_timeout"
                    ? "Backend accepted the turn but sent no response event"
                    : "Backend response stopped before reply.end");
            }
            if (sendInteractionEvents && lastInteraction != HumanInteractionKind.None &&
                Time.unscaledTime >= nextInteractionUpdateAt)
            {
                SendInteractionFact(
                    InteractionName(lastInteraction),
                    "update",
                    1f,
                    InteractionDurationMs());
                nextInteractionUpdateAt = Time.unscaledTime + InteractionUpdateInterval;
            }
        }

        public void Bind(AvatarController avatar, AvatarHumanInteraction humanInteraction)
        {
            UnsubscribeInteraction();
            boundHumanInteraction = humanInteraction;
            lastInteraction = HumanInteractionKind.None;
            latestInteractionEventId = string.Empty;
            if (boundHumanInteraction != null)
            {
                boundHumanInteraction.PhysicalInteractionChanged += HandleInteractionChanged;
            }
            if (presenter == null)
            {
                presenter = GetComponent<AvatarConversationPresenter>() ?? gameObject.AddComponent<AvatarConversationPresenter>();
            }
            presenter.Bind(avatar, humanInteraction, audioPlayer);
            presenter.SetConversationState(stateMachine.State);
            RefreshLocalReactionMode();
        }

        public bool BeginVoiceInput()
        {
            if (!CanStartVoiceInput)
            {
                RecordStage("microphone", "blocked", "voice_turn_rejected");
                return false;
            }

            CancelCurrentTurn(false);
            BeginAudioStream();
            LastErrorCode = string.Empty;
            latestInteractionEventId = string.Empty;
            pendingLocalAction = string.Empty;
            localActionStarted = false;
            backendActionReceived = false;
            var turnId = stateMachine.Begin(string.Empty);
            ResetTurnTiming();
            NotifyStateChanged();
            if (transport.BeginAudioTurn(turnId))
            {
                Debug.Log("[Conversation] Voice input start accepted.", this);
                return true;
            }

            StopAudioStream();
            stateMachine.ResetToIdle();
            NotifyStateChanged();
            Debug.LogWarning("[Conversation] Voice input start rejected by transport.", this);
            RecordStage("audio_upload", "failed", "voice_turn_rejected");
            return false;
        }

        public bool PushVoiceAudio(byte[] pcm16)
        {
            var accepted = stateMachine.State == ConversationState.Listening &&
                pcm16 != null && pcm16.Length > 0 &&
                transport != null && transport.QueueAudioChunk(stateMachine.TurnId, pcm16);
            if (accepted && firstInputChunkAt < 0f)
            {
                firstInputChunkAt = Time.unscaledTime;
            }
            return accepted;
        }

        public bool EndVoiceInput()
        {
            var accepted = stateMachine.State == ConversationState.Listening &&
                transport != null && transport.EndAudioTurn(stateMachine.TurnId);
            if (accepted)
            {
                inputEndedAt = Time.unscaledTime;
                BeginResponseWait(inputEndedAt);
            }
            Debug.Log(accepted
                ? "[Conversation] Voice input end accepted."
                : "[Conversation] Voice input end rejected.", this);
            if (!accepted) RecordStage("audio_upload", "failed", "voice_end_rejected");
            return accepted;
        }

        public void CancelVoiceInput()
        {
            if (stateMachine.State != ConversationState.Idle)
            {
                Interrupt();
            }
        }

        public void StartMockConversation(string userText)
        {
            if (transport == null || !(transport is MockConversationTransport))
            {
                SetTransport(gameObject.GetComponent<MockConversationTransport>() ??
                    gameObject.AddComponent<MockConversationTransport>());
                Debug.LogWarning("[Conversation] Local demo transport selected explicitly; AstrBot is bypassed for this turn.", this);
            }
            StartConversation(userText);
        }

        public void StartConversation(string userText)
        {
            if (transport == null)
            {
                Debug.LogWarning("[Conversation] No transport is configured.");
                return;
            }

            var text = string.IsNullOrWhiteSpace(userText) ? "你好，能听见我吗？" : userText.Trim();
            CancelCurrentTurn(false);
            BeginAudioStream();
            LastErrorCode = string.Empty;
            latestInteractionEventId = string.Empty;
            pendingLocalAction = string.Empty;
            localActionStarted = false;
            backendActionReceived = false;
            TryQueueLocalAction(text);
            var turnId = stateMachine.Begin(text);
            ResetTurnTiming();
            NotifyStateChanged();
            transport.StartTurn(turnId, text);
            BeginResponseWait(Time.unscaledTime);
            RecordStage("eventbus", "processing");
            Debug.Log("[Conversation] Text turn started.", this);
        }

        public void Interrupt()
        {
            if (!stateMachine.Interrupt())
            {
                return;
            }

            awaitingBackendResponse = false;
            pendingLocalAction = string.Empty;
            transport?.Interrupt(stateMachine.TurnId);
            StopAudioStream();
            interruptedUntil = Time.unscaledTime + .3f;
            NotifyStateChanged();
            RecordStage("interrupt", "completed");
            Debug.Log("[Conversation] Turn interrupted.", this);
        }

        public void SetTransport(IConversationTransport next)
        {
            if (ReferenceEquals(transport, next))
            {
                return;
            }
            UnsubscribeTransport();
            transport = next;
            localReactionModeInitialized = false;
            SubscribeTransport();
            RefreshLocalReactionMode();
        }

        private void HandleTransportEvent(ConversationEvent message)
        {
            if (message == null)
            {
                return;
            }
            if (message.Type != ConversationEventType.AudioChunk)
            {
                Debug.Log("[Conversation] Event received: " + message.Type, this);
            }
            if (message.Type == ConversationEventType.AvatarIntent)
            {
                Debug.Log("[Conversation] Avatar intent received: gesture=" +
                    (message.Gesture ?? "idle") + " reason=" + (message.ReasonCode ?? ""), this);
            }
            if (message.Type == ConversationEventType.AvatarIntent && string.IsNullOrEmpty(message.TurnId))
            {
                backendActionReceived |= IsExecutableAvatarAction(message.Gesture);
                presenter?.ApplyIntent(
                    message.Emotion, message.Gesture, message.LookAt, message.Intensity, message.DurationMs);
                return;
            }
            if (IsInteractionTurn(message))
            {
                if (!IsLatestInteractionEvent(message))
                {
                    return;
                }
                if (!string.Equals(stateMachine.TurnId, message.TurnId, StringComparison.Ordinal))
                {
                    // Touch is a parallel fact channel. A backend gesture may augment
                    // an active voice turn, but interaction speech must never seize
                    // the single audio/state channel or interrupt the current reply.
                    if (stateMachine.State != ConversationState.Idle)
                    {
                        if (message.Type == ConversationEventType.AvatarIntent)
                        {
                            presenter?.ApplyIntent(
                                message.Emotion, message.Gesture, message.LookAt,
                                message.Intensity, message.DurationMs);
                        }
                        return;
                    }
                    BeginAudioStream();
                    stateMachine.BeginExternal(message.TurnId);
                    NotifyStateChanged();
                }
            }

            var before = stateMachine.State;
            if (!stateMachine.Apply(message))
            {
                return;
            }
            if (message.Type == ConversationEventType.AsrFinal)
            {
                TryQueueLocalAction(message.Text);
            }
            if (message.Type != ConversationEventType.AvatarIntent && !message.IsSyntheticTransportEvent)
            {
                lastBackendProgressAt = Time.unscaledTime;
            }
            RecordEventTiming(message);

            switch (message.Type)
            {
                case ConversationEventType.AudioChunk:
                    audioPlayer?.Enqueue(message.Pcm16, message.SampleRate);
                    if (replyAudioChunkCount == 1)
                    {
                        RecordStage(
                            "audio_buffer",
                            "queued",
                            "first_pcm_chunk",
                            chunks: replyAudioChunkCount,
                            bytes: message.Pcm16 == null ? 0 : message.Pcm16.Length * 2,
                            queueDepth: audioPlayer == null ? -1 : audioPlayer.QueuedChunkCount,
                            bufferedMs: audioPlayer == null ? -1 : Mathf.RoundToInt(audioPlayer.BufferedSeconds * 1000f));
                    }
                    break;
                case ConversationEventType.ReplyEnd:
                    var actionOnlyReply = AcceptActionOnlyReplyEnd(
                        backendActionReceived,
                        stateMachine.ReplyText,
                        replyAudioChunkCount);
                    if (!actionOnlyReply && string.IsNullOrWhiteSpace(stateMachine.ReplyText) && replyAudioChunkCount == 0)
                    {
                        Debug.LogWarning(
                            $"[Conversation] Empty reply.end received; server text_sent={message.TextSent}, audio_sent={message.AudioSent}.",
                            this);
                        FailActiveTurn(
                            "empty_backend_reply",
                            "Backend completed the turn without text or audio");
                        return;
                    }
                    if (actionOnlyReply)
                    {
                        Debug.Log("[Conversation] Action-only reply.end accepted; avatar intent was already applied.", this);
                        diagnostics?.Record("AvatarAction", "action-only reply.end accepted after avatar intent; audio_buffer closed");
                    }
                    audioPlayer?.MarkStreamCompleted();
                    RecordStage(
                        "audio_buffer",
                        "completed",
                        "reply_end",
                        queueDepth: audioPlayer == null ? -1 : audioPlayer.QueuedChunkCount,
                        bufferedMs: audioPlayer == null ? -1 : Mathf.RoundToInt(audioPlayer.BufferedSeconds * 1000f));
                    awaitingBackendResponse = false;
                    TryRunLocalActionFallback();
                    break;
                case ConversationEventType.AvatarIntent:
                    backendActionReceived |= IsExecutableAvatarAction(message.Gesture);
                    presenter?.ApplyIntent(
                        message.Emotion, message.Gesture, message.LookAt, message.Intensity, message.DurationMs);
                    break;
                case ConversationEventType.Error:
                    StopAudioStream();
                    LastErrorCode = string.IsNullOrWhiteSpace(message.ErrorCode)
                        ? "conversation_error"
                        : message.ErrorCode;
                    awaitingBackendResponse = false;
                    errorUntil = Time.unscaledTime + 1.25f;
                    RecordStage("reply", "failed", LastErrorCode);
                    Debug.LogWarning("[Conversation] Voice/transport error code=" + LastErrorCode +
                        "; bridge=" + TransportStatus + "; timing=" + BuildTimingStatus(Time.unscaledTime), this);
                    break;
            }

            if (before != stateMachine.State)
            {
                NotifyStateChanged();
            }
        }

        private void HandleInteractionChanged(HumanInteractionKind next)
        {
            var previous = lastInteraction;
            if (sendInteractionEvents && lastInteraction != HumanInteractionKind.None && lastInteraction != next)
            {
                SendInteractionFact(
                    InteractionName(lastInteraction),
                    "end",
                    0f,
                    InteractionDurationMs());
            }
            if (next != HumanInteractionKind.None)
            {
                interactionStartedAt = Time.unscaledTime;
                nextInteractionUpdateAt = interactionStartedAt + InteractionUpdateInterval;
                if (sendInteractionEvents)
                {
                    SendInteractionFact(InteractionName(next), "start", 1f, 0);
                }
            }
            else if (previous != HumanInteractionKind.None)
            {
                interactionStartedAt = 0f;
                nextInteractionUpdateAt = 0f;
            }
            lastInteraction = next;
        }

        private void SendInteractionFact(string name, string phase, float strength, int durationMs)
        {
            var eventId = transport?.SendInteraction(name, phase, strength, durationMs);
            if (!string.IsNullOrEmpty(eventId))
            {
                latestInteractionEventId = eventId;
            }
        }

        private int InteractionDurationMs()
        {
            return interactionStartedAt <= 0f
                ? 0
                : Mathf.Clamp(Mathf.RoundToInt((Time.unscaledTime - interactionStartedAt) * 1000f), 0, 600000);
        }

        private void RefreshLocalReactionMode()
        {
            if (boundHumanInteraction == null)
            {
                return;
            }

            // Physical contact must feel immediate regardless of network state.
            // AstrBot remains the semantic decision layer and can temporarily
            // override this pose when an avatar.intent arrives.
            const bool shouldEnableLocal = true;
            if (localReactionModeInitialized && localReactionsEnabled == shouldEnableLocal)
            {
                return;
            }

            localReactionModeInitialized = true;
            localReactionsEnabled = shouldEnableLocal;
            boundHumanInteraction.SetLocalReactionsEnabled(shouldEnableLocal);
            Debug.Log("[Conversation] Immediate local interaction reactions enabled.", this);
        }

        private void UnsubscribeInteraction()
        {
            if (boundHumanInteraction != null)
            {
                boundHumanInteraction.PhysicalInteractionChanged -= HandleInteractionChanged;
            }
        }

        private void SubscribeInteraction()
        {
            if (boundHumanInteraction == null)
            {
                return;
            }
            boundHumanInteraction.PhysicalInteractionChanged -= HandleInteractionChanged;
            boundHumanInteraction.PhysicalInteractionChanged += HandleInteractionChanged;
        }

        private static string InteractionName(HumanInteractionKind kind)
        {
            switch (kind)
            {
                case HumanInteractionKind.Handshake: return "handshake";
                case HumanInteractionKind.HeadPat: return "head_pat";
                case HumanInteractionKind.CheekPinch: return "cheek_pinch";
                case HumanInteractionKind.BodyTouch: return string.Empty;
                default: return "none";
            }
        }

        private void CancelCurrentTurn(bool showInterrupted)
        {
            awaitingBackendResponse = false;
            if (showInterrupted)
            {
                pendingLocalAction = string.Empty;
            }
            if (stateMachine.State == ConversationState.Idle)
            {
                StopAudioStream();
                return;
            }

            transport?.Interrupt(stateMachine.TurnId);
            StopAudioStream();
            if (showInterrupted)
            {
                stateMachine.Interrupt();
                interruptedUntil = Time.unscaledTime + .3f;
                NotifyStateChanged();
            }
            else
            {
                stateMachine.ResetToIdle();
            }
        }

        private static bool IsInteractionTurn(ConversationEvent message)
        {
            return !string.IsNullOrEmpty(message.TurnId) &&
                message.TurnId.StartsWith("i:", StringComparison.Ordinal);
        }

        private bool IsLatestInteractionEvent(ConversationEvent message)
        {
            if (string.IsNullOrEmpty(latestInteractionEventId))
            {
                return false;
            }
            var eventId = string.IsNullOrEmpty(message.InReplyToEventId)
                ? message.TurnId.Substring(2)
                : message.InReplyToEventId;
            return string.Equals(eventId, latestInteractionEventId, StringComparison.Ordinal);
        }

        private IConversationTransport FindTransport()
        {
            var behaviours = GetComponents<MonoBehaviour>();
            for (var i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is IConversationTransport found)
                {
#if UNITY_EDITOR
                    if (found is AstrBotBridge bridge && !bridge.IsConfigured)
                    {
                        continue;
                    }
#endif
                    return found;
                }
            }
            return null;
        }

        private void NotifyStateChanged()
        {
            presenter?.SetConversationState(stateMachine.State);
            StateChanged?.Invoke(stateMachine.State);
            Debug.Log("[Conversation] State changed: " + stateMachine.State, this);
        }

        private void ResetTurnTiming()
        {
            turnStartedAt = Time.unscaledTime;
            firstInputChunkAt = -1f;
            inputEndedAt = -1f;
            asrFinalAt = -1f;
            firstEventAt = -1f;
            firstTextAt = -1f;
            firstAudioAt = -1f;
            playbackStartedAt = -1f;
            replyEndedAt = -1f;
            audioDoneAt = -1f;
            responseWaitStartedAt = -1f;
            lastBackendProgressAt = -1f;
            awaitingBackendResponse = false;
            replyAudioChunkCount = 0;
            backendSttReported = false;
            backendDecisionReported = false;
            backendTtsReported = false;
            backendTotalReported = false;
        }

        private void TryQueueLocalAction(string text)
        {
            if (string.IsNullOrEmpty(pendingLocalAction) &&
                ConversationActionIntent.TryDetect(text, out var detectedAction))
            {
                pendingLocalAction = detectedAction;
                Debug.Log("[Conversation] Explicit action request detected: " + detectedAction, this);
                diagnostics?.Record(
                    "AvatarAction",
                    "显式动作请求已排队，等待后端 avatar.intent：" + detectedAction);
            }
        }

        private void TryRunLocalActionFallback()
        {
            if (localActionStarted || backendActionReceived)
            {
                if (backendActionReceived)
                {
                    diagnostics?.Record("AvatarAction", "后端已返回可执行动作，本地动作回退未运行");
                }
                pendingLocalAction = string.Empty;
                localActionStarted = false;
                return;
            }

            if (string.IsNullOrEmpty(pendingLocalAction) &&
                ConversationActionIntent.TryDetect(stateMachine.ReplyText, out var replyAction))
            {
                pendingLocalAction = replyAction;
                diagnostics?.Record(
                    "AvatarAction",
                    "后端回复描述了动作但未返回 avatar.intent，已准备回退：" + replyAction);
            }
            if (string.IsNullOrEmpty(pendingLocalAction))
            {
                diagnostics?.Record("AvatarAction", "本轮没有可执行动作");
                return;
            }

            var action = pendingLocalAction;
            pendingLocalAction = string.Empty;
            presenter?.PlayLocalAction(action);
            localActionStarted = true;
            Debug.Log("[Conversation] Local action fallback executed: " + action +
                " (AstrBot reply had no executable avatar.intent).", this);
            diagnostics?.Record(
                "AvatarAction",
                "后端未返回可执行 avatar.intent，回复结束后执行本地回退：" + action);
        }

        private static bool IsExecutableAvatarAction(string gesture)
        {
            return !string.IsNullOrEmpty(gesture) &&
                !string.Equals(gesture, "idle", StringComparison.Ordinal) &&
                !string.Equals(gesture, "talk", StringComparison.Ordinal);
        }

        public static bool AcceptActionOnlyReplyEnd(
            bool backendActionReceived,
            string replyText,
            int audioChunkCount)
        {
            return backendActionReceived && string.IsNullOrWhiteSpace(replyText) &&
                audioChunkCount == 0;
        }

        private void BeginResponseWait(float now)
        {
            responseWaitStartedAt = now;
            lastBackendProgressAt = -1f;
            awaitingBackendResponse = true;
        }

        private void FailActiveTurn(string code, string message)
        {
            if (!stateMachine.Fail(message))
            {
                awaitingBackendResponse = false;
                return;
            }

            var turnId = stateMachine.TurnId;
            pendingLocalAction = string.Empty;
            awaitingBackendResponse = false;
            LastErrorCode = code;
            transport?.Interrupt(turnId);
            StopAudioStream();
            errorUntil = Time.unscaledTime + 1.25f;
            NotifyStateChanged();
            RecordStage("reply", "failed", code);
            Debug.LogWarning("[Conversation] Voice/transport error code=" + code +
                "; bridge=" + TransportStatus + "; timing=" + BuildTimingStatus(Time.unscaledTime), this);
        }

        public static bool ShouldTimeoutResponse(
            bool awaiting,
            bool replyEnded,
            float now,
            float waitStartedAt,
            float lastProgressAt,
            float firstEventTimeout,
            float stallTimeout,
            out string errorCode)
        {
            errorCode = string.Empty;
            if (!awaiting || replyEnded || waitStartedAt < 0f || now < waitStartedAt)
            {
                return false;
            }

            if (lastProgressAt < waitStartedAt)
            {
                if (now - waitStartedAt < Mathf.Max(1f, firstEventTimeout))
                {
                    return false;
                }
                errorCode = "response_first_event_timeout";
                return true;
            }

            if (now - lastProgressAt < Mathf.Max(1f, stallTimeout))
            {
                return false;
            }
            errorCode = "response_event_stall_timeout";
            return true;
        }

        private void BeginAudioStream()
        {
            activePlaybackGeneration = audioPlayer == null ? -1 : audioPlayer.BeginStream();
        }

        private void StopAudioStream()
        {
            audioPlayer?.StopAndClear();
            activePlaybackGeneration = -1;
        }

        private void RecordEventTiming(ConversationEvent message)
        {
            var now = Time.unscaledTime;
            RecordBackendTiming(message);
            if (message.IsSyntheticTransportEvent)
            {
                return;
            }
            if (firstEventAt < 0f)
            {
                firstEventAt = now;
                RecordStage(
                    "eventbus",
                    "processing",
                    "first_event",
                    ElapsedMsValue(inputEndedAt >= 0f ? inputEndedAt : turnStartedAt, firstEventAt));
            }
            if (message.Type == ConversationEventType.AsrFinal && asrFinalAt < 0f)
            {
                asrFinalAt = now;
                RecordStage(
                    "stt",
                    "completed",
                    "asr_final",
                    ElapsedMsValue(inputEndedAt >= 0f ? inputEndedAt : turnStartedAt, asrFinalAt));
            }
            if (message.Type == ConversationEventType.ReplyTextDelta && firstTextAt < 0f)
            {
                firstTextAt = now;
                RecordStage(
                    "llm",
                    "processing",
                    "first_text",
                    ElapsedMsValue(inputEndedAt >= 0f ? inputEndedAt : turnStartedAt, firstTextAt));
                if (asrFinalAt >= 0f)
                {
                    RecordStage(
                        "llm",
                        "completed",
                        "asr_to_first_text",
                        ElapsedMsValue(asrFinalAt, firstTextAt));
                }
            }
            if (message.Type == ConversationEventType.AudioChunk)
            {
                if (firstAudioAt < 0f)
                {
                    firstAudioAt = now;
                    RecordStage(
                        "tts",
                        "processing",
                        "first_audio",
                        ElapsedMsValue(inputEndedAt >= 0f ? inputEndedAt : turnStartedAt, firstAudioAt));
                    if (firstTextAt >= 0f)
                    {
                        RecordStage(
                            "tts",
                            "processing",
                            "text_to_first_audio",
                            ElapsedMsValue(firstTextAt, firstAudioAt));
                    }
                }
                replyAudioChunkCount++;
            }
            else if (message.Type == ConversationEventType.ReplyEnd)
            {
                replyEndedAt = now;
                RecordStage(
                    "reply",
                    "completed",
                    "reply_end",
                    ElapsedMsValue(inputEndedAt >= 0f ? inputEndedAt : turnStartedAt, replyEndedAt),
                    replyAudioChunkCount);
            }

        }

        private string BuildTimingStatus(float now)
        {
            if (turnStartedAt < 0f)
            {
                return "no active timing";
            }
            var responseStart = inputEndedAt >= 0f ? inputEndedAt : turnStartedAt;
            return $"capture={ElapsedMs(turnStartedAt, inputEndedAt)}ms " +
                $"firstChunk={ElapsedMs(turnStartedAt, firstInputChunkAt)}ms " +
                $"inputEnd={ElapsedMs(turnStartedAt, inputEndedAt)}ms " +
                $"asr={ElapsedMs(inputEndedAt, asrFinalAt)}ms " +
                $"firstEvent={ElapsedMs(responseStart, firstEventAt)}ms " +
                $"firstText={ElapsedMs(responseStart, firstTextAt)}ms " +
                $"firstAudio={ElapsedMs(responseStart, firstAudioAt)}ms " +
                $"playback={ElapsedMs(firstAudioAt, playbackStartedAt)}ms " +
                $"replyEnd={ElapsedMs(responseStart, replyEndedAt)}ms " +
                $"audioDone={ElapsedMs(responseStart, audioDoneAt)}ms chunks={replyAudioChunkCount}";
        }

        private void RecordBackendTiming(ConversationEvent message)
        {
            var timing = message == null ? null : message.BackendTiming;
            if (timing == null || !timing.IsValid)
            {
                return;
            }

            if (!backendSttReported && timing.SttMs > 0)
            {
                backendSttReported = true;
                RecordStage("backend_stt", "completed", "server_timing", elapsedMs: timing.SttMs);
            }
            if (!backendDecisionReported && timing.DecisionMs > 0)
            {
                backendDecisionReported = true;
                RecordStage(
                    "backend_decision",
                    "completed",
                    timing.SafeDecisionPath(),
                    elapsedMs: timing.DecisionMs);
            }
            if (!backendTtsReported && (timing.TtsFirstChunkMs > 0 || timing.TtsTotalMs > 0))
            {
                backendTtsReported = true;
                RecordStage(
                    "backend_tts",
                    "completed",
                    "server_timing",
                    elapsedMs: timing.TtsFirstChunkMs > 0 ? timing.TtsFirstChunkMs : timing.TtsTotalMs);
            }
            if (!backendTotalReported && timing.TurnTotalMs > 0)
            {
                backendTotalReported = true;
                RecordStage("backend_total", "completed", "server_timing", elapsedMs: timing.TurnTotalMs);
            }
        }

        private void HandlePlaybackTelemetry(PlaybackTelemetry telemetry)
        {
            if (telemetry.Generation != activePlaybackGeneration)
            {
                return;
            }
            playbackStartedAt = Time.unscaledTime;
            RecordStage(
                "audio_playback",
                "processing",
                "playback_callback",
                elapsedMs: ElapsedMsValue(firstAudioAt, playbackStartedAt),
                bufferedMs: telemetry.BufferedMs);
            RecordStage(
                "audio_playback",
                "ready",
                "playback_start",
                elapsedMs: telemetry.CallbackDelayMs,
                bufferedMs: telemetry.BufferedMs);
            if (telemetry.UnderflowCount > 0)
            {
                RecordStage(
                    "audio_playback",
                    "limited",
                    "audio_underflow",
                    eventCount: telemetry.UnderflowCount);
            }
        }

        private static string ElapsedMs(float start, float end)
        {
            return start < 0f || end < 0f ? "-" : Mathf.Max(0, Mathf.RoundToInt((end - start) * 1000f)).ToString();
        }

        private static int ElapsedMsValue(float start, float end)
        {
            return start < 0f || end < 0f
                ? -1
                : Mathf.Max(0, Mathf.RoundToInt((end - start) * 1000f));
        }

        private void RecordStage(
            string stage,
            string status,
            string code = "",
            int elapsedMs = -1,
            int chunks = 0,
            int bytes = 0,
            int eventCount = 0,
            int queueDepth = -1,
            int bufferedMs = -1,
            string traceId = "")
        {
            diagnostics = diagnostics != null ? diagnostics : GetComponent<RuntimeDebugLog>();
            diagnostics?.RecordStage(
                stage,
                status,
                code,
                elapsedMs: elapsedMs,
                chunks: chunks,
                bytes: bytes,
                eventCount: eventCount,
                traceId: string.IsNullOrEmpty(traceId)
                    ? RuntimeDebugLog.TraceLabel(stateMachine.TurnId)
                    : traceId,
                queueDepth: queueDepth,
                bufferedMs: bufferedMs);
        }

        private void SubscribeTransport()
        {
            if (transport != null)
            {
                transport.EventReceived -= HandleTransportEvent;
                transport.EventReceived += HandleTransportEvent;
            }
        }

        private void UnsubscribeTransport()
        {
            if (transport != null)
            {
                transport.EventReceived -= HandleTransportEvent;
            }
        }
    }
}

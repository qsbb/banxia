using System;
using System.Collections.Generic;
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
        private float firstBackendEventAt = -1f;
        private float lastReplyProgressAt = -1f;
        private bool validAvatarIntentReceived;
        private bool terminalEventReceived;
        private bool awaitingBackendResponse;
        private int replyAudioChunkCount;
        private string pendingLocalAction = string.Empty;
        private bool localActionStarted;
        private bool backendActionReceived;
        private bool backendActionDecisionReceived;
        private bool backendSttReported;
        private bool backendDecisionReported;
        private bool backendTtsReported;
        private bool backendTotalReported;
        private int activePlaybackGeneration = -1;
        private RuntimeDebugLog diagnostics;
        private readonly Dictionary<string, AvatarActionReceiptTracker> actionReceiptTrackers =
            new Dictionary<string, AvatarActionReceiptTracker>(StringComparer.Ordinal);
        private readonly Queue<string> actionReceiptOrder = new Queue<string>();
        private readonly HashSet<string> wholeBodyActionTurns =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> wholeBodyActionTurnOrder = new Queue<string>();
        private const int MaxTrackedActionReceipts = 64;
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
            SubscribePresenter();
            if (audioPlayer != null)
            {
                audioPlayer.PlaybackTelemetryReady += HandlePlaybackTelemetry;
            }
        }

        private void OnDisable()
        {
            RecordVoiceInterruption("system_disable", "lifecycle");
            presenter?.InterruptTrackedAction(stateMachine.TurnId, "system_interrupted");
            if (sendInteractionEvents && lastInteraction != HumanInteractionKind.None)
            {
                transport?.SendInteraction(InteractionName(lastInteraction), "cancel", 0f, InteractionDurationMs());
                lastInteraction = HumanInteractionKind.None;
            }
            UnsubscribeTransport();
            UnsubscribeInteraction();
            UnsubscribePresenter();
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
                validAvatarIntentReceived,
                terminalEventReceived,
                Time.unscaledTime,
                responseWaitStartedAt,
                firstBackendEventAt,
                lastReplyProgressAt,
                firstResponseTimeoutSeconds,
                responseStallTimeoutSeconds,
                out var timeoutCode))
            {
                FailActiveTurn(timeoutCode, timeoutCode == "response_first_event_timeout"
                    ? "Backend accepted the turn but sent no response event"
                    : timeoutCode == "response_terminal_event_missing_timeout"
                        ? "Backend emitted an avatar intent but no terminal response event"
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
            SubscribePresenter();
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

            CancelCurrentTurn(false, "barge_in");
            BeginAudioStream();
            LastErrorCode = string.Empty;
            latestInteractionEventId = string.Empty;
            pendingLocalAction = string.Empty;
            localActionStarted = false;
            backendActionReceived = false;
            backendActionDecisionReceived = false;
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
            if (transport == null || !transport.IsConnected)
            {
                LastErrorCode = "bridge_disconnected";
                RecordStage("eventbus", "failed", LastErrorCode);
                Debug.LogWarning("[Conversation] Text turn rejected because the event stream is not connected.", this);
                return;
            }

            var text = string.IsNullOrWhiteSpace(userText) ? "你好，能听见我吗？" : userText.Trim();
            CancelCurrentTurn(false, "new_text_turn");
            BeginAudioStream();
            LastErrorCode = string.Empty;
            latestInteractionEventId = string.Empty;
            pendingLocalAction = string.Empty;
            localActionStarted = false;
            backendActionReceived = false;
            backendActionDecisionReceived = false;
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

            RecordVoiceInterruption("user_interrupt", "explicit");
            awaitingBackendResponse = false;
            pendingLocalAction = string.Empty;
            presenter?.InterruptTrackedAction(stateMachine.TurnId, "user_interrupted");
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
                var applied = ApplyAvatarIntent(message);
                backendActionReceived |= applied && IsExecutableAvatarAction(message.Gesture);
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
                            ApplyAvatarIntent(message);
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
                case ConversationEventType.SpeechTimeline:
                    presenter?.SetSpeechTimeline(message.VisemeTimeline);
                    break;
                case ConversationEventType.ReplyEnd:
                    var actionOnlyReply = AcceptActionOnlyReplyEnd(
                        backendActionReceived,
                        stateMachine.ReplyText,
                        replyAudioChunkCount);
                    var localFallbackOnlyReply = AcceptLocalActionFallbackReplyEnd(
                        pendingLocalAction,
                        stateMachine.ReplyText,
                        replyAudioChunkCount);
                    if (!actionOnlyReply && !localFallbackOnlyReply &&
                        string.IsNullOrWhiteSpace(stateMachine.ReplyText) && replyAudioChunkCount == 0)
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
                    else if (localFallbackOnlyReply)
                    {
                        Debug.Log(
                            "[Conversation] Empty reply.end accepted for an explicit action request; running local fallback.",
                            this);
                        diagnostics?.Record(
                            "AvatarAction",
                            "empty reply.end accepted for queued explicit action; local fallback will execute");
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
                    var actionApplied = ApplyAvatarIntent(message);
                    backendActionDecisionReceived |= IsAuthoritativeActionDecision(message);
                    backendActionReceived |= actionApplied && IsExecutableAvatarAction(message.Gesture);
                    break;
                case ConversationEventType.Error:
                    var pipelineStopped = IsPipelineStoppedError(message.ErrorCode);
                    if (pipelineStopped)
                    {
                        RecordVoiceInterruption("pipeline_stopped", "transport_error");
                    }
                    StopAudioStream();
                    pendingLocalAction = string.Empty;
                    LastErrorCode = string.IsNullOrWhiteSpace(message.ErrorCode)
                        ? "conversation_error"
                        : message.ErrorCode;
                    // A terminal SSE/HTTP error must stop the matching backend
                    // turn as well as local playback. AstrBotBridge handles
                    // this idempotently and cancels any in-flight audio upload.
                    transport?.Interrupt(stateMachine.TurnId);
                    if (!pipelineStopped)
                    {
                        // Every terminal transport error interrupts the active
                        // voice turn, not only the explicit pipeline-stopped
                        // variant. Keep one uniform, searchable interruption
                        // record for upload, SSE, and backend failures.
                        RecordVoiceInterruption(LastErrorCode, "transport_error");
                    }
                    awaitingBackendResponse = false;
                    errorUntil = Time.unscaledTime + 1.25f;
                    RecordStage("reply", "failed", LastErrorCode);
                    var errorTrace = RuntimeDebugLog.TraceLabel(stateMachine.TurnId);
                    var errorTiming = BuildTimingStatus(Time.unscaledTime);
                    if (IsNoResponseCode(LastErrorCode))
                    {
                        Debug.LogWarning(
                            "[Conversation] No response: code=" + LastErrorCode +
                            " trace=" + errorTrace +
                            "; message=" + (message.Text ?? string.Empty) +
                            "; timing=" + errorTiming,
                            this);
                        diagnostics?.Record(
                            "VoiceInput",
                            "No response: code=" + LastErrorCode +
                            " trace=" + errorTrace + " timing=" + errorTiming);
                    }
                    Debug.LogWarning("[Conversation] Voice/transport error code=" + LastErrorCode +
                        "; trace=" + errorTrace + "; bridge=" + TransportStatus +
                        "; timing=" + errorTiming, this);
                    break;
            }

            if (before != stateMachine.State)
            {
                NotifyStateChanged();
            }
        }

        private bool ApplyAvatarIntent(ConversationEvent message)
        {
            if (message == null) return false;

            AvatarActionExecutionContext executionContext = null;
            if (IsExecutableAvatarAction(message.Gesture) &&
                AvatarActionReceiptTracker.IsActionId(message.ActionId))
            {
                if (actionReceiptTrackers.ContainsKey(message.ActionId))
                {
                    // SSE reconnects may replay the final frame. A server action
                    // id is single-use, so do not execute the same motion twice.
                    return false;
                }

                var tracker = new AvatarActionReceiptTracker();
                tracker.Reset(message.TurnId);
                if (tracker.TryPlan(
                        message.TurnId,
                        message.ActionId,
                        message.Gesture,
                        message.ActionParameters,
                        message.ActionTransition,
                        message.ActionSource,
                        out executionContext))
                {
                    actionReceiptTrackers.Add(message.ActionId, tracker);
                    actionReceiptOrder.Enqueue(message.ActionId);
                    while (actionReceiptOrder.Count > MaxTrackedActionReceipts)
                    {
                        actionReceiptTrackers.Remove(actionReceiptOrder.Dequeue());
                    }
                }
            }

            if (IsWholeBodyAction(message.Gesture) &&
                !string.IsNullOrEmpty(message.TurnId) &&
                wholeBodyActionTurns.Contains(message.TurnId))
            {
                HandleActionExecutionChanged(new AvatarActionExecutionUpdate
                {
                    Context = executionContext,
                    Phase = AvatarActionReceiptPhase.Rejected,
                    Source = "runtime",
                    ReasonCode = "superseded"
                });
                diagnostics?.RecordStage(
                    "avatar_action",
                    "blocked",
                    "whole_body_action_already_selected",
                    traceId: RuntimeDebugLog.TraceLabel(message.TurnId));
                return false;
            }

            if (presenter != null)
            {
                var applied = presenter.ApplyIntent(
                    message.Emotion,
                    message.Gesture,
                    message.LookAt,
                    message.Intensity,
                    message.DurationMs,
                    executionContext,
                    message.ActionParameters,
                    message.ActionTransition,
                    message.ActionSource);
                if (applied && IsWholeBodyAction(message.Gesture) &&
                    !string.IsNullOrEmpty(message.TurnId))
                {
                    wholeBodyActionTurns.Add(message.TurnId);
                    wholeBodyActionTurnOrder.Enqueue(message.TurnId);
                    while (wholeBodyActionTurnOrder.Count > MaxTrackedActionReceipts)
                    {
                        wholeBodyActionTurns.Remove(wholeBodyActionTurnOrder.Dequeue());
                    }
                }
                return applied;
            }

            if (executionContext != null)
            {
                HandleActionExecutionChanged(new AvatarActionExecutionUpdate
                {
                    Context = executionContext,
                    Phase = AvatarActionReceiptPhase.Rejected,
                    Source = "runtime",
                    ReasonCode = "invalid_state"
                });
            }
            return false;
        }

        private static bool IsWholeBodyAction(string action)
        {
            var normalized = AstrBotProtocol.SanitizeGesture(action);
            return normalized != "idle" && normalized != "talk";
        }

        private void HandleActionExecutionChanged(AvatarActionExecutionUpdate update)
        {
            if (update == null || update.Context == null ||
                !actionReceiptTrackers.TryGetValue(update.Context.ActionId, out var tracker) ||
                !tracker.TryAdvance(update, out var receipt))
            {
                return;
            }

            if (transport == null || !transport.SendActionResult(receipt))
            {
                diagnostics?.RecordStage(
                    "avatar_action",
                    "failed",
                    "action_receipt_not_queued",
                    traceId: RuntimeDebugLog.TraceLabel(update.Context.TurnId));
                return;
            }
            diagnostics?.RecordStage(
                "avatar_action",
                "completed",
                "action_receipt_queued",
                elapsedMs: receipt.DurationMs,
                traceId: RuntimeDebugLog.TraceLabel(receipt.TurnId));
        }

        private void SubscribePresenter()
        {
            if (presenter == null) return;
            presenter.ActionExecutionChanged -= HandleActionExecutionChanged;
            presenter.ActionExecutionChanged += HandleActionExecutionChanged;
        }

        private void UnsubscribePresenter()
        {
            if (presenter != null)
            {
                presenter.ActionExecutionChanged -= HandleActionExecutionChanged;
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

        private void CancelCurrentTurn(bool showInterrupted, string interruptionReason = "turn_cancelled")
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

            RecordVoiceInterruption(interruptionReason, "turn_replace");
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
            firstBackendEventAt = -1f;
            lastReplyProgressAt = -1f;
            validAvatarIntentReceived = false;
            terminalEventReceived = false;
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
            if (localActionStarted || backendActionReceived || backendActionDecisionReceived)
            {
                if (backendActionReceived)
                {
                    diagnostics?.Record("AvatarAction", "后端已返回可执行动作，本地动作回退未运行");
                }
                else if (backendActionDecisionReceived)
                {
                    diagnostics?.Record("AvatarAction", "后端已明确本轮动作决策，本地关键词回退未运行");
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

        private static bool IsAuthoritativeActionDecision(ConversationEvent message)
        {
            if (message == null)
            {
                return false;
            }
            return !string.IsNullOrWhiteSpace(message.ActionId) ||
                string.Equals(
                    message.ReasonCode,
                    "fast_action_no_action",
                    StringComparison.Ordinal);
        }

        public static bool AcceptActionOnlyReplyEnd(
            bool backendActionReceived,
            string replyText,
            int audioChunkCount)
        {
            return backendActionReceived && string.IsNullOrWhiteSpace(replyText) &&
                audioChunkCount == 0;
        }

        private static bool AcceptLocalActionFallbackReplyEnd(
            string pendingLocalAction,
            string replyText,
            int audioChunkCount)
        {
            return !string.IsNullOrWhiteSpace(pendingLocalAction) &&
                string.IsNullOrWhiteSpace(replyText) && audioChunkCount == 0;
        }

        private void BeginResponseWait(float now)
        {
            responseWaitStartedAt = now;
            firstBackendEventAt = -1f;
            lastReplyProgressAt = -1f;
            validAvatarIntentReceived = false;
            terminalEventReceived = false;
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
            RecordVoiceInterruption(code, "watchdog");
            pendingLocalAction = string.Empty;
            awaitingBackendResponse = false;
            LastErrorCode = code;
            transport?.Interrupt(turnId);
            StopAudioStream();
            errorUntil = Time.unscaledTime + 1.25f;
            NotifyStateChanged();
            var trace = RuntimeDebugLog.TraceLabel(turnId);
            var timing = BuildTimingStatus(Time.unscaledTime);
            RecordStage("reply", "failed", code, traceId: trace);
            if (IsNoResponseCode(code))
            {
                Debug.LogWarning(
                    "[Conversation] No response: code=" + code +
                    " trace=" + trace +
                    "; message=" + message +
                    "; timing=" + timing,
                    this);
                diagnostics?.Record(
                    "VoiceInput",
                    "No response: code=" + code + " trace=" + trace + " timing=" + timing);
            }
            Debug.LogWarning("[Conversation] Voice/transport error code=" + code +
                "; trace=" + trace + "; bridge=" + TransportStatus + "; timing=" + timing, this);
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

        public static bool ShouldTimeoutResponse(
            bool awaiting,
            bool replyEnded,
            bool hasAvatarIntent,
            bool terminalEventReceived,
            float now,
            float waitStartedAt,
            float firstBackendEventAt,
            float lastReplyProgressAt,
            float firstEventTimeout,
            float stallTimeout,
            out string errorCode)
        {
            errorCode = string.Empty;
            if (!awaiting || replyEnded || terminalEventReceived ||
                waitStartedAt < 0f || now < waitStartedAt)
            {
                return false;
            }

            if (firstBackendEventAt < waitStartedAt)
            {
                if (now - waitStartedAt < Mathf.Max(1f, firstEventTimeout))
                {
                    return false;
                }
                errorCode = "response_first_event_timeout";
                return true;
            }

            var hasReplyProgress = lastReplyProgressAt >= firstBackendEventAt;
            if (hasReplyProgress && now - lastReplyProgressAt >= Mathf.Max(1f, stallTimeout))
            {
                errorCode = "response_event_stall_timeout";
                return true;
            }

            if (hasAvatarIntent &&
                now - waitStartedAt >= Mathf.Max(1f, firstEventTimeout))
            {
                errorCode = "response_terminal_event_missing_timeout";
                return true;
            }

            if (!hasAvatarIntent &&
                now - firstBackendEventAt >= Mathf.Max(1f, stallTimeout))
            {
                errorCode = "response_event_stall_timeout";
                return true;
            }

            return false;
        }

        private void BeginAudioStream()
        {
            presenter?.ClearSpeechTimeline();
            activePlaybackGeneration = audioPlayer == null ? -1 : audioPlayer.BeginStream();
        }

        private void StopAudioStream()
        {
            audioPlayer?.StopAndClear();
            presenter?.ClearSpeechTimeline();
            activePlaybackGeneration = -1;
        }

        private static bool IsPipelineStoppedError(string errorCode)
        {
            return string.Equals(errorCode, "astrbot_pipeline_event_stopped", StringComparison.Ordinal) ||
                string.Equals(errorCode, "pipeline_stopped", StringComparison.Ordinal);
        }

        private static bool IsNoResponseCode(string code)
        {
            return string.Equals(code, "empty_backend_reply", StringComparison.Ordinal) ||
                string.Equals(code, "response_first_event_timeout", StringComparison.Ordinal) ||
                string.Equals(code, "response_event_stall_timeout", StringComparison.Ordinal) ||
                string.Equals(code, "response_terminal_event_missing_timeout", StringComparison.Ordinal) ||
                string.Equals(code, "stt_empty", StringComparison.Ordinal) ||
                string.Equals(code, "stt_unavailable", StringComparison.Ordinal) ||
                string.Equals(code, "stt_failed", StringComparison.Ordinal) ||
                string.Equals(code, "astrbot_pipeline_not_woken", StringComparison.Ordinal) ||
                string.Equals(code, "astrbot_pipeline_reply_capture_empty", StringComparison.Ordinal) ||
                string.Equals(code, "astrbot_pipeline_no_response", StringComparison.Ordinal) ||
                string.Equals(code, "astrbot_pipeline_empty_reply", StringComparison.Ordinal) ||
                string.Equals(code, "astrbot_pipeline_timeout", StringComparison.Ordinal);
        }

        private void RecordVoiceInterruption(string reason, string source)
        {
            var hasAudio = audioPlayer != null &&
                (audioPlayer.PlaybackStarted || audioPlayer.QueuedChunkCount > 0 || audioPlayer.BufferedSeconds > 0f);
            var hasTurn = stateMachine.State != ConversationState.Idle || awaitingBackendResponse || hasAudio;
            if (!hasTurn)
            {
                return;
            }

            var safeReason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
            var safeSource = string.IsNullOrWhiteSpace(source) ? "runtime" : source.Trim();
            var queueDepth = audioPlayer == null ? -1 : audioPlayer.QueuedChunkCount;
            var bufferedMs = audioPlayer == null ? -1 : Mathf.RoundToInt(audioPlayer.BufferedSeconds * 1000f);
            var trace = RuntimeDebugLog.TraceLabel(stateMachine.TurnId);
            var details = $"reason={safeReason} source={safeSource} turn={trace} state={stateMachine.State} " +
                $"awaiting={awaitingBackendResponse} transport={TransportStatus} queue_depth={queueDepth} buffered_ms={bufferedMs}";
            Debug.LogWarning("[Conversation] 刚刚又被打断了 / Voice interrupted: " + details, this);
            diagnostics?.Record("VoiceInput", "刚刚又被打断了 / Voice interrupted: " + details);
            RecordStage(
                "interrupt",
                "completed",
                "voice_interrupted_" + safeReason,
                eventCount: 1,
                queueDepth: queueDepth,
                bufferedMs: bufferedMs,
                traceId: trace);
        }

        private void RecordEventTiming(ConversationEvent message)
        {
            var now = Time.unscaledTime;
            RecordBackendTiming(message);
            if (message.IsSyntheticTransportEvent)
            {
                // PCM upload is complete when AstrBotBridge emits this marker.
                // Start the backend watchdog there, without counting the
                // synthetic event as the first real response event.
                if (message.Type == ConversationEventType.Thinking && awaitingBackendResponse)
                {
                    responseWaitStartedAt = now;
                    RecordStage("stt", "processing", "audio_upload_complete");
                }
                return;
            }
            if (firstBackendEventAt < 0f)
            {
                firstBackendEventAt = now;
            }
            if (message.Type == ConversationEventType.AvatarIntent)
            {
                validAvatarIntentReceived = true;
            }
            if (message.Type == ConversationEventType.ReplyTextDelta ||
                message.Type == ConversationEventType.AudioChunk ||
                message.Type == ConversationEventType.ReplyEnd ||
                message.Type == ConversationEventType.Error)
            {
                lastReplyProgressAt = now;
            }
            if (message.Type == ConversationEventType.ReplyEnd ||
                message.Type == ConversationEventType.Error)
            {
                terminalEventReceived = true;
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
                $"audioDone={ElapsedMs(responseStart, audioDoneAt)}ms " +
                $"backendFirst={ElapsedMs(responseStart, firstBackendEventAt)}ms " +
                $"replyProgress={ElapsedMs(responseStart, lastReplyProgressAt)}ms " +
                $"avatarIntent={(validAvatarIntentReceived ? "yes" : "no")} " +
                $"terminal={(terminalEventReceived ? "yes" : "no")} " +
                $"chunks={replyAudioChunkCount}";
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

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
        private float firstEventAt = -1f;
        private float firstTextAt = -1f;
        private float firstAudioAt = -1f;
        private float replyEndedAt = -1f;
        private int replyAudioChunkCount;
        [SerializeField] private bool sendInteractionEvents;
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
        public bool CanStartVoiceInput => transport != null && transport.IsConnected && stateMachine.State == ConversationState.Idle;
        public bool IsRealBackendConnected => transport != null && !(transport is MockConversationTransport) && transport.IsConnected;
        public bool IsUsingMockTransport => transport is MockConversationTransport;
        public string Status => $"{State} | {TransportStatus}";
        public string TurnTimingStatus => BuildTimingStatus(Time.unscaledTime);

        private void Awake()
        {
            audioPlayer = GetComponent<Pcm16StreamAudioPlayer>() ?? gameObject.AddComponent<Pcm16StreamAudioPlayer>();
            presenter = GetComponent<AvatarConversationPresenter>() ?? gameObject.AddComponent<AvatarConversationPresenter>();
            SetTransport(FindTransport() ?? gameObject.AddComponent<MockConversationTransport>());
            RefreshLocalReactionMode();
        }

        private void OnEnable()
        {
            SubscribeTransport();
            SubscribeInteraction();
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
            if (transport != null && !string.IsNullOrEmpty(stateMachine.TurnId))
            {
                transport.Interrupt(stateMachine.TurnId);
            }
            if (audioPlayer != null)
            {
                audioPlayer.StopAndClear();
            }
            stateMachine.ResetToIdle();
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
                boundHumanInteraction.InteractionChanged += HandleInteractionChanged;
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
                return false;
            }

            CancelCurrentTurn(false);
            audioPlayer?.BeginStream();
            LastErrorCode = string.Empty;
            latestInteractionEventId = string.Empty;
            var turnId = stateMachine.Begin(string.Empty);
            ResetTurnTiming();
            NotifyStateChanged();
            if (transport.BeginAudioTurn(turnId))
            {
                Debug.Log("[Conversation] Voice input start accepted.", this);
                return true;
            }

            audioPlayer?.StopAndClear();
            stateMachine.ResetToIdle();
            NotifyStateChanged();
            Debug.LogWarning("[Conversation] Voice input start rejected by transport.", this);
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
            }
            Debug.Log(accepted
                ? "[Conversation] Voice input end accepted."
                : "[Conversation] Voice input end rejected.", this);
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
            audioPlayer?.BeginStream();
            LastErrorCode = string.Empty;
            latestInteractionEventId = string.Empty;
            var turnId = stateMachine.Begin(text);
            ResetTurnTiming();
            NotifyStateChanged();
            transport.StartTurn(turnId, text);
            Debug.Log("[Conversation] Text turn started.", this);
        }

        public void Interrupt()
        {
            if (!stateMachine.Interrupt())
            {
                return;
            }

            transport?.Interrupt(stateMachine.TurnId);
            audioPlayer?.StopAndClear();
            interruptedUntil = Time.unscaledTime + .3f;
            NotifyStateChanged();
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
            if (message.Type == ConversationEventType.AvatarIntent && string.IsNullOrEmpty(message.TurnId))
            {
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
                    audioPlayer?.BeginStream();
                    stateMachine.BeginExternal(message.TurnId);
                    NotifyStateChanged();
                }
            }

            var before = stateMachine.State;
            if (!stateMachine.Apply(message))
            {
                return;
            }
            RecordEventTiming(message);

            switch (message.Type)
            {
                case ConversationEventType.AudioChunk:
                    audioPlayer?.Enqueue(message.Pcm16, message.SampleRate);
                    break;
                case ConversationEventType.ReplyEnd:
                    audioPlayer?.MarkStreamCompleted();
                    break;
                case ConversationEventType.AvatarIntent:
                    presenter?.ApplyIntent(
                        message.Emotion, message.Gesture, message.LookAt, message.Intensity, message.DurationMs);
                    break;
                case ConversationEventType.Error:
                    audioPlayer?.StopAndClear();
                    LastErrorCode = string.IsNullOrWhiteSpace(message.ErrorCode)
                        ? "conversation_error"
                        : message.ErrorCode;
                    errorUntil = Time.unscaledTime + 1.25f;
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
                boundHumanInteraction.InteractionChanged -= HandleInteractionChanged;
            }
        }

        private void SubscribeInteraction()
        {
            if (boundHumanInteraction == null)
            {
                return;
            }
            boundHumanInteraction.InteractionChanged -= HandleInteractionChanged;
            boundHumanInteraction.InteractionChanged += HandleInteractionChanged;
        }

        private static string InteractionName(HumanInteractionKind kind)
        {
            switch (kind)
            {
                case HumanInteractionKind.Handshake: return "handshake";
                case HumanInteractionKind.HeadPat: return "head_pat";
                case HumanInteractionKind.CheekPinch: return "cheek_pinch";
                default: return "none";
            }
        }

        private void CancelCurrentTurn(bool showInterrupted)
        {
            if (stateMachine.State == ConversationState.Idle)
            {
                audioPlayer?.StopAndClear();
                return;
            }

            transport?.Interrupt(stateMachine.TurnId);
            audioPlayer?.StopAndClear();
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
            firstEventAt = -1f;
            firstTextAt = -1f;
            firstAudioAt = -1f;
            replyEndedAt = -1f;
            replyAudioChunkCount = 0;
        }

        private void RecordEventTiming(ConversationEvent message)
        {
            var now = Time.unscaledTime;
            if (firstEventAt < 0f)
            {
                firstEventAt = now;
            }
            if (message.Type == ConversationEventType.ReplyTextDelta && firstTextAt < 0f)
            {
                firstTextAt = now;
            }
            if (message.Type == ConversationEventType.AudioChunk)
            {
                if (firstAudioAt < 0f)
                {
                    firstAudioAt = now;
                }
                replyAudioChunkCount++;
            }
            else if (message.Type == ConversationEventType.ReplyEnd)
            {
                replyEndedAt = now;
            }
        }

        private string BuildTimingStatus(float now)
        {
            if (turnStartedAt < 0f)
            {
                return "no active timing";
            }
            var responseStart = inputEndedAt >= 0f ? inputEndedAt : turnStartedAt;
            return $"firstChunk={ElapsedMs(turnStartedAt, firstInputChunkAt)}ms " +
                $"inputEnd={ElapsedMs(turnStartedAt, inputEndedAt)}ms " +
                $"firstEvent={ElapsedMs(responseStart, firstEventAt)}ms " +
                $"firstText={ElapsedMs(responseStart, firstTextAt)}ms " +
                $"firstAudio={ElapsedMs(responseStart, firstAudioAt)}ms " +
                $"replyEnd={ElapsedMs(responseStart, replyEndedAt)}ms " +
                $"audioDone={ElapsedMs(responseStart, now)}ms chunks={replyAudioChunkCount}";
        }

        private static string ElapsedMs(float start, float end)
        {
            return start < 0f || end < 0f ? "-" : Mathf.Max(0, Mathf.RoundToInt((end - start) * 1000f)).ToString();
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

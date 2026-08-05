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
        private bool localReactionModeInitialized;
        private bool localReactionsEnabled = true;
        [SerializeField] private bool sendInteractionEvents;
        private const float InteractionUpdateInterval = 5f;

        public event Action<ConversationState> StateChanged;

        public ConversationState State => stateMachine.State;
        public string TurnId => stateMachine.TurnId;
        public string Transcript => stateMachine.Transcript;
        public string ReplyText => stateMachine.ReplyText;
        public string TransportStatus => transport == null ? "No conversation transport" : transport.Status;
        public string PresenterStatus => presenter == null ? "No avatar presenter" : presenter.Status;
        public float BufferedAudioSeconds => audioPlayer == null ? 0f : audioPlayer.BufferedSeconds;
        public bool CanStartVoiceInput => transport != null && transport.IsConnected;
        public bool IsRealBackendConnected => transport != null && !(transport is MockConversationTransport) && transport.IsConnected;
        public string Status => $"{State} | {TransportStatus}";

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
                NotifyStateChanged();
            }
            if (stateMachine.State == ConversationState.Interrupted && Time.unscaledTime >= interruptedUntil)
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
            latestInteractionEventId = string.Empty;
            var turnId = stateMachine.Begin(string.Empty);
            NotifyStateChanged();
            if (transport.BeginAudioTurn(turnId))
            {
                Debug.Log($"[Conversation] Voice input started: {turnId}");
                return true;
            }

            stateMachine.ResetToIdle();
            NotifyStateChanged();
            return false;
        }

        public bool PushVoiceAudio(byte[] pcm16)
        {
            return stateMachine.State == ConversationState.Listening &&
                pcm16 != null && pcm16.Length > 0 &&
                transport != null && transport.QueueAudioChunk(stateMachine.TurnId, pcm16);
        }

        public bool EndVoiceInput()
        {
            return stateMachine.State == ConversationState.Listening &&
                transport != null && transport.EndAudioTurn(stateMachine.TurnId);
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
            latestInteractionEventId = string.Empty;
            var turnId = stateMachine.Begin(text);
            NotifyStateChanged();
            transport.StartTurn(turnId, text);
            Debug.Log($"[Conversation] Started {turnId}: {text}");
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
            Debug.Log($"[Conversation] Interrupted {stateMachine.TurnId}");
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
                    stateMachine.BeginExternal(message.TurnId);
                    NotifyStateChanged();
                }
            }

            var before = stateMachine.State;
            if (!stateMachine.Apply(message))
            {
                return;
            }

            switch (message.Type)
            {
                case ConversationEventType.AudioChunk:
                    audioPlayer?.Enqueue(message.Pcm16, message.SampleRate);
                    break;
                case ConversationEventType.AvatarIntent:
                    presenter?.ApplyIntent(
                        message.Emotion, message.Gesture, message.LookAt, message.Intensity, message.DurationMs);
                    break;
                case ConversationEventType.Error:
                    audioPlayer?.StopAndClear();
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

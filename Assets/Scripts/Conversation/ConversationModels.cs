using System;
using System.Buffers;
using System.Collections.Generic;

namespace QuestMmdPlayer
{
    public enum ConversationState
    {
        Idle,
        Listening,
        Thinking,
        Speaking,
        Interrupted,
        Error
    }

    public enum ConversationEventType
    {
        AsrPartial,
        AsrFinal,
        Thinking,
        ReplyTextDelta,
        AudioChunk,
        SpeechTimeline,
        AvatarIntent,
        ReplyEnd,
        Error
    }

    public sealed class ConversationEvent
    {
        public ConversationEventType Type;
        public string TurnId;
        public string Text;
        public string Emotion;
        public string Gesture;
        public string LookAt;
        public string InReplyToEventId;
        public string ReasonCode;
        public string ErrorCode;
        public float Intensity;
        public int DurationMs;
        public short[] Pcm16;
        public int Pcm16Length;
        internal bool Pcm16FromPool;
        public int SampleRate;
        public SpeechVisemeCue[] VisemeTimeline;
        public string ActionId;
        public string ActionMethod;
        public AvatarActionParameters ActionParameters;
        public AvatarActionTransition ActionTransition;
        public string ActionSource;
        public string SpeechId;
        public int AudioSequence = -1;
        public bool AudioFirst;
        public bool AudioEnd;
        public int AudioSequenceEnd = -1;
        public bool TextSent;
        public bool AudioSent;
        /// <summary>True for a local transport acknowledgement, not a backend SSE event.</summary>
        public bool IsSyntheticTransportEvent;
        /// <summary>Monotonic timestamp captured by the transport when an SSE frame arrived.</summary>
        public long TransportReceivedAtTicks;
        /// <summary>Delay from transport receipt to Unity main-thread dispatch, in milliseconds.</summary>
        public int TransportQueueDelayMs = -1;
        /// <summary>Monotonic timestamp captured immediately before EventReceived dispatch.</summary>
        public long TransportDispatchedAtTicks;
        /// <summary>Optional aggregate timings supplied by the bridge server.</summary>
        public BackendTimingSnapshot BackendTiming;

        public void ReleasePcm16()
        {
            if (Pcm16FromPool && Pcm16 != null)
            {
                ArrayPool<short>.Shared.Return(Pcm16);
            }
            Pcm16 = null;
            Pcm16Length = 0;
            Pcm16FromPool = false;
        }
    }

    public sealed class AvatarActionParameters
    {
        public float AngleDegrees;
        public float Depth;
        public int HoldMs;
        public string Style = "natural";
    }

    public sealed class AvatarActionTransition
    {
        public int EnterMs;
        public int ExitMs;
        public string Easing = "smoothstep";
    }

    public enum AvatarActionReceiptPhase
    {
        Planned,
        Accepted,
        Started,
        Completed,
        Rejected,
        Interrupted
    }

    [Serializable]
    public sealed class AvatarActionReceipt
    {
        public string TurnId;
        public string ActionId;
        public string ReceiptId;
        public string Action;
        public AvatarActionReceiptPhase Phase;
        public string ReasonCode;
        public int DurationMs;
    }

    public sealed class AvatarActionExecutionContext
    {
        public AvatarActionExecutionContext(
            string turnId,
            string actionId,
            string gesture,
            AvatarActionParameters parameters = null,
            AvatarActionTransition transition = null,
            string source = "backend")
        {
            TurnId = turnId ?? string.Empty;
            ActionId = actionId ?? string.Empty;
            Gesture = gesture ?? string.Empty;
            Parameters = parameters ?? new AvatarActionParameters();
            Transition = transition ?? new AvatarActionTransition();
            Source = string.IsNullOrWhiteSpace(source) ? "backend" : source;
        }

        public string TurnId { get; }
        public string ActionId { get; }
        public string Gesture { get; }
        public AvatarActionParameters Parameters { get; }
        public AvatarActionTransition Transition { get; }
        public string Source { get; }
    }

    public sealed class AvatarActionExecutionUpdate
    {
        public AvatarActionExecutionContext Context;
        public AvatarActionReceiptPhase Phase;
        public string Source;
        public string ReasonCode;
        public int ElapsedMs;
    }

    [Serializable]
    public sealed class SpeechVisemeCue
    {
        public string Symbol;
        public int StartMs;
        public int EndMs;
        public float Weight = 1f;
    }

    public sealed class AvatarActionReceiptTracker
    {
        private readonly HashSet<AvatarActionReceiptPhase> emitted =
            new HashSet<AvatarActionReceiptPhase>();
        private string turnId = string.Empty;
        private string actionId = string.Empty;
        private string gesture = string.Empty;
        private AvatarActionParameters parameters = new AvatarActionParameters();
        private AvatarActionTransition transition = new AvatarActionTransition();
        private string source = "backend";
        private AvatarActionReceiptPhase phase;
        private bool planned;
        private bool terminal;

        public string TurnId => turnId;
        public string ActionId => actionId;
        public bool HasPlannedAction => planned;
        public bool IsTerminal => terminal;

        public void Reset(string nextTurnId)
        {
            turnId = nextTurnId ?? string.Empty;
            actionId = string.Empty;
            gesture = string.Empty;
            parameters = new AvatarActionParameters();
            transition = new AvatarActionTransition();
            source = "backend";
            phase = AvatarActionReceiptPhase.Planned;
            planned = false;
            terminal = false;
            emitted.Clear();
        }

        public bool TryPlan(
            string eventTurnId,
            string suppliedActionId,
            string requestedGesture,
            out AvatarActionExecutionContext context)
        {
            return TryPlan(
                eventTurnId,
                suppliedActionId,
                requestedGesture,
                null,
                null,
                "backend",
                out context);
        }

        public bool TryPlan(
            string eventTurnId,
            string suppliedActionId,
            string requestedGesture,
            AvatarActionParameters requestedParameters,
            AvatarActionTransition requestedTransition,
            string requestedSource,
            out AvatarActionExecutionContext context)
        {
            context = null;
            var normalizedGesture = AstrBotProtocol.SanitizeGesture(requestedGesture);
            if (string.IsNullOrEmpty(eventTurnId) ||
                !string.Equals(eventTurnId, turnId, StringComparison.Ordinal) ||
                !IsActionId(suppliedActionId) ||
                normalizedGesture == "idle" || normalizedGesture == "talk")
            {
                return false;
            }

            if (planned)
            {
                return false;
            }

            actionId = suppliedActionId;
            gesture = normalizedGesture;
            parameters = requestedParameters ?? new AvatarActionParameters();
            transition = requestedTransition ?? new AvatarActionTransition();
            source = string.IsNullOrWhiteSpace(requestedSource) ? "backend" : requestedSource;
            planned = true;
            context = new AvatarActionExecutionContext(
                turnId,
                actionId,
                gesture,
                parameters,
                transition,
                source);
            return true;
        }

        public bool TryAdvance(AvatarActionExecutionUpdate update, out AvatarActionReceipt receipt)
        {
            receipt = null;
            if (!planned || terminal || update == null || update.Context == null ||
                !string.Equals(update.Context.TurnId, turnId, StringComparison.Ordinal) ||
                !string.Equals(update.Context.ActionId, actionId, StringComparison.Ordinal) ||
                emitted.Contains(update.Phase) || !CanAdvance(phase, update.Phase))
            {
                return false;
            }

            phase = update.Phase;
            emitted.Add(phase);
            terminal = phase == AvatarActionReceiptPhase.Completed ||
                phase == AvatarActionReceiptPhase.Rejected ||
                phase == AvatarActionReceiptPhase.Interrupted;
            receipt = BuildReceipt(phase, update.ReasonCode, update.ElapsedMs);
            return true;
        }

        public AvatarActionExecutionContext CurrentContext()
        {
            return planned
                ? new AvatarActionExecutionContext(turnId, actionId, gesture, parameters, transition, source)
                : null;
        }

        private AvatarActionReceipt BuildReceipt(
            AvatarActionReceiptPhase next,
            string reason,
            int elapsedMs)
        {
            return new AvatarActionReceipt
            {
                TurnId = turnId,
                ActionId = actionId,
                ReceiptId = "receipt-" + Guid.NewGuid().ToString("N"),
                Action = gesture,
                Phase = next,
                ReasonCode = NormalizeReason(next, reason),
                DurationMs = Math.Max(0, Math.Min(elapsedMs, 600000))
            };
        }

        private static string NormalizeReason(AvatarActionReceiptPhase next, string reason)
        {
            if (next == AvatarActionReceiptPhase.Accepted) return "accepted";
            if (next == AvatarActionReceiptPhase.Started) return "started";
            if (next == AvatarActionReceiptPhase.Completed) return "completed";

            var value = string.IsNullOrWhiteSpace(reason)
                ? string.Empty
                : reason.Trim().ToLowerInvariant();
            if (next == AvatarActionReceiptPhase.Rejected)
            {
                switch (value)
                {
                    case "unsupported":
                    case "busy":
                    case "blocked":
                    case "tracking_lost":
                    case "asset_missing":
                    case "invalid_state":
                    case "superseded":
                        return value;
                    default:
                        return "invalid_state";
                }
            }
            if (next == AvatarActionReceiptPhase.Interrupted)
            {
                switch (value)
                {
                    case "tracking_lost":
                    case "superseded":
                    case "user_interrupted":
                    case "system_interrupted":
                        return value;
                    default:
                        return "system_interrupted";
                }
            }
            return "invalid_state";
        }

        private static bool CanAdvance(
            AvatarActionReceiptPhase current,
            AvatarActionReceiptPhase next)
        {
            if (current == AvatarActionReceiptPhase.Planned)
                return next == AvatarActionReceiptPhase.Accepted || next == AvatarActionReceiptPhase.Rejected ||
                    next == AvatarActionReceiptPhase.Interrupted;
            if (current == AvatarActionReceiptPhase.Accepted)
                return next == AvatarActionReceiptPhase.Started || next == AvatarActionReceiptPhase.Rejected ||
                    next == AvatarActionReceiptPhase.Interrupted;
            return current == AvatarActionReceiptPhase.Started &&
                (next == AvatarActionReceiptPhase.Completed || next == AvatarActionReceiptPhase.Interrupted);
        }

        public static bool IsActionId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64 || !char.IsLetterOrDigit(value[0]))
                return false;
            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (!char.IsLetterOrDigit(character) && character != '.' && character != '_' &&
                    character != ':' && character != '-') return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Optional server-side timing summary. It is additive to protocol 1.0 and
    /// intentionally contains only bounded durations and a fixed path enum.
    /// </summary>
    [Serializable]
    public sealed class BackendTimingSnapshot
    {
        public int SchemaVersion;
        public int SttMs;
        public int DecisionMs;
        public int TtsFirstChunkMs;
        public int TtsTotalMs;
        public int TurnTotalMs;
        public string DecisionPath;
        public int DecisionHooksMs;
        public int DecisionProviderMs;
        public int EventLoopLagMs;
        public string ServerTraceId;

        public bool IsValid => SchemaVersion == 1;

        public int ClampDuration(int value)
        {
            return value <= 0 ? -1 : Math.Min(value, 3600000);
        }

        public string SafeDecisionPath()
        {
            return DecisionPath == "astrbot_event_bus" || DecisionPath == "direct_provider"
                ? DecisionPath
                : "unknown";
        }

        public string SafeTraceId()
        {
            var candidate = ServerTraceId ?? string.Empty;
            if (candidate.Length == 0 || candidate.Length > 64)
            {
                return string.Empty;
            }
            for (var index = 0; index < candidate.Length; index++)
            {
                var character = candidate[index];
                if (!char.IsLetterOrDigit(character) && character != '.' &&
                    character != '_' && character != ':' && character != '-')
                {
                    return string.Empty;
                }
            }
            return candidate;
        }
    }

    public interface IConversationTransport
    {
        event Action<ConversationEvent> EventReceived;
        bool IsConnected { get; }
        string Status { get; }
        void StartTurn(string turnId, string userText);
        /// <summary>文本轮次 + 可选摄像头单帧附件；attachment 为 null 等价于两参版本。</summary>
        void StartTurn(string turnId, string userText, TurnImageAttachment attachment);
        void Interrupt(string turnId);
        bool BeginAudioTurn(string turnId);
        bool QueueAudioChunk(string turnId, byte[] pcm16);
        bool EndAudioTurn(string turnId);
        string SendInteraction(string interactionName, string phase, float strength, int durationMs = 0, string hand = "none");
        bool SendActionResult(AvatarActionReceipt receipt);
    }
}

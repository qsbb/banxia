using System;

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
        public int SampleRate;
        public bool TextSent;
        public bool AudioSent;
        /// <summary>Monotonic timestamp captured by the transport when an SSE frame arrived.</summary>
        public long TransportReceivedAtTicks;
        /// <summary>Delay from transport receipt to Unity main-thread dispatch, in milliseconds.</summary>
        public int TransportQueueDelayMs = -1;
        /// <summary>Monotonic timestamp captured immediately before EventReceived dispatch.</summary>
        public long TransportDispatchedAtTicks;
        /// <summary>Optional aggregate timings supplied by the bridge server.</summary>
        public BackendTimingSnapshot BackendTiming;
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
    }

    public interface IConversationTransport
    {
        event Action<ConversationEvent> EventReceived;
        bool IsConnected { get; }
        string Status { get; }
        void StartTurn(string turnId, string userText);
        void Interrupt(string turnId);
        bool BeginAudioTurn(string turnId);
        bool QueueAudioChunk(string turnId, byte[] pcm16);
        bool EndAudioTurn(string turnId);
        string SendInteraction(string interactionName, string phase, float strength, int durationMs = 0, string hand = "none");
    }
}

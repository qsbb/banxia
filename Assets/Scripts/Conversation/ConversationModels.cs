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

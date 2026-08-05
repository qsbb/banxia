using System;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Provider-neutral turn state. Keeping this outside MonoBehaviour makes
    /// cancellation and stale-event handling deterministic and testable.
    /// </summary>
    public sealed class ConversationStateMachine
    {
        private long turnSequence;
        private bool acceptingEvents;
        private bool replyEnded;

        public ConversationState State { get; private set; } = ConversationState.Idle;
        public string TurnId { get; private set; } = string.Empty;
        public string UserText { get; private set; } = string.Empty;
        public string Transcript { get; private set; } = string.Empty;
        public string ReplyText { get; private set; } = string.Empty;
        public string ErrorMessage { get; private set; } = string.Empty;
        public bool ReplyEnded => replyEnded;

        public string Begin(string userText)
        {
            turnSequence++;
            TurnId = $"turn-{turnSequence:D6}";
            UserText = userText ?? string.Empty;
            Transcript = string.Empty;
            ReplyText = string.Empty;
            ErrorMessage = string.Empty;
            replyEnded = false;
            acceptingEvents = true;
            State = ConversationState.Listening;
            return TurnId;
        }

        public bool BeginExternal(string turnId)
        {
            if (string.IsNullOrWhiteSpace(turnId))
            {
                return false;
            }

            TurnId = turnId;
            UserText = string.Empty;
            Transcript = string.Empty;
            ReplyText = string.Empty;
            ErrorMessage = string.Empty;
            replyEnded = false;
            acceptingEvents = true;
            State = ConversationState.Thinking;
            return true;
        }

        public bool Apply(ConversationEvent message)
        {
            if (!acceptingEvents || message == null || !string.Equals(message.TurnId, TurnId, StringComparison.Ordinal))
            {
                return false;
            }

            switch (message.Type)
            {
                case ConversationEventType.AsrPartial:
                case ConversationEventType.AsrFinal:
                    Transcript = message.Text ?? string.Empty;
                    State = ConversationState.Listening;
                    break;
                case ConversationEventType.Thinking:
                    State = ConversationState.Thinking;
                    break;
                case ConversationEventType.ReplyTextDelta:
                    ReplyText += message.Text ?? string.Empty;
                    break;
                case ConversationEventType.AudioChunk:
                    State = ConversationState.Speaking;
                    break;
                case ConversationEventType.ReplyEnd:
                    replyEnded = true;
                    break;
                case ConversationEventType.Error:
                    ErrorMessage = string.IsNullOrWhiteSpace(message.Text) ? "Conversation failed" : message.Text;
                    State = ConversationState.Error;
                    replyEnded = false;
                    acceptingEvents = false;
                    break;
                case ConversationEventType.AvatarIntent:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return true;
        }

        public bool Interrupt()
        {
            if (!acceptingEvents || State == ConversationState.Idle)
            {
                return false;
            }

            acceptingEvents = false;
            replyEnded = false;
            State = ConversationState.Interrupted;
            return true;
        }

        public bool TryFinishAudio(bool audioDrained)
        {
            if (!acceptingEvents || !replyEnded || !audioDrained)
            {
                return false;
            }

            acceptingEvents = false;
            replyEnded = false;
            State = ConversationState.Idle;
            return true;
        }

        public void ResetToIdle()
        {
            acceptingEvents = false;
            replyEnded = false;
            State = ConversationState.Idle;
        }
    }
}

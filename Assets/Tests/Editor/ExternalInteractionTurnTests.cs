#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class ExternalInteractionTurnTests
    {
        [Test]
        public void CompleteReplyIsAcceptedAndOlderInteractionTurnIsRejected()
        {
            var machine = new ConversationStateMachine();

            Assert.IsTrue(machine.BeginExternal("i:event-2"));
            Assert.AreEqual(ConversationState.Thinking, machine.State);
            Assert.IsTrue(machine.Apply(Event("i:event-2", ConversationEventType.AvatarIntent)));
            Assert.IsTrue(machine.Apply(Event("i:event-2", ConversationEventType.ReplyTextDelta, "hello")));
            Assert.IsTrue(machine.Apply(Event("i:event-2", ConversationEventType.AudioChunk)));
            Assert.AreEqual(ConversationState.Speaking, machine.State);

            Assert.IsFalse(machine.Apply(Event("i:event-1", ConversationEventType.ReplyTextDelta, "stale")));
            Assert.AreEqual("hello", machine.ReplyText);
            Assert.IsTrue(machine.Apply(Event("i:event-2", ConversationEventType.ReplyEnd)));
            Assert.IsTrue(machine.TryFinishAudio(true));
            Assert.AreEqual(ConversationState.Idle, machine.State);
        }

        private static ConversationEvent Event(
            string turnId,
            ConversationEventType type,
            string text = null)
        {
            return new ConversationEvent { TurnId = turnId, Type = type, Text = text };
        }
    }
}
#endif

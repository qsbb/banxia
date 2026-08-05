#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class ConversationStateMachineTests
    {
        [Test]
        public void TurnMovesFromListeningThroughSpeakingToIdle()
        {
            var machine = new ConversationStateMachine();
            var turnId = machine.Begin("hello");

            Assert.AreEqual(ConversationState.Listening, machine.State);
            Assert.IsTrue(machine.Apply(Event(turnId, ConversationEventType.AsrFinal, "hello")));
            Assert.IsTrue(machine.Apply(Event(turnId, ConversationEventType.Thinking)));
            Assert.AreEqual(ConversationState.Thinking, machine.State);
            Assert.IsTrue(machine.Apply(Event(turnId, ConversationEventType.ReplyTextDelta, "hi")));
            Assert.IsTrue(machine.Apply(Event(turnId, ConversationEventType.AudioChunk)));
            Assert.AreEqual(ConversationState.Speaking, machine.State);
            Assert.IsTrue(machine.Apply(Event(turnId, ConversationEventType.ReplyEnd)));
            Assert.IsFalse(machine.TryFinishAudio(false));
            Assert.IsTrue(machine.TryFinishAudio(true));
            Assert.AreEqual(ConversationState.Idle, machine.State);
            Assert.AreEqual("hello", machine.Transcript);
            Assert.AreEqual("hi", machine.ReplyText);
        }

        [Test]
        public void StaleTurnAndInterruptedTurnCannotChangeState()
        {
            var machine = new ConversationStateMachine();
            var first = machine.Begin("first");
            var second = machine.Begin("second");

            Assert.IsFalse(machine.Apply(Event(first, ConversationEventType.AudioChunk)));
            Assert.AreEqual(ConversationState.Listening, machine.State);
            Assert.IsTrue(machine.Interrupt());
            Assert.AreEqual(ConversationState.Interrupted, machine.State);
            Assert.IsFalse(machine.Apply(Event(second, ConversationEventType.AudioChunk)));
        }

        private static ConversationEvent Event(string turnId, ConversationEventType type, string text = null)
        {
            return new ConversationEvent { TurnId = turnId, Type = type, Text = text };
        }
    }
}
#endif

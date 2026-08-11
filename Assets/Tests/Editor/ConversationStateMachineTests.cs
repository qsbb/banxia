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

        [Test]
        public void ReplyEndSealsTurnAgainstLateContentAndDuplicateEndIsHarmless()
        {
            var machine = new ConversationStateMachine();
            var turnId = machine.Begin("hello");

            Assert.IsTrue(machine.Apply(Event(turnId, ConversationEventType.ReplyEnd)));
            Assert.IsTrue(machine.Apply(Event(turnId, ConversationEventType.ReplyEnd)));
            Assert.IsFalse(machine.Apply(Event(turnId, ConversationEventType.ReplyTextDelta, "late")));
            Assert.IsFalse(machine.Apply(Event(turnId, ConversationEventType.AudioChunk)));
            Assert.IsEmpty(machine.ReplyText);
        }

        [Test]
        public void LocalFailureSealsTurnUntilReset()
        {
            var machine = new ConversationStateMachine();
            var turnId = machine.Begin(string.Empty);

            Assert.IsTrue(machine.Fail("Backend response timed out"));
            Assert.AreEqual(ConversationState.Error, machine.State);
            Assert.AreEqual("Backend response timed out", machine.ErrorMessage);
            Assert.IsFalse(machine.Apply(Event(turnId, ConversationEventType.AudioChunk)));
            Assert.IsFalse(machine.Fail("again"));
        }

        [Test]
        public void FirstTurnIdIsUniqueAcrossProcessStateMachines()
        {
            var first = new ConversationStateMachine().Begin("first");
            var second = new ConversationStateMachine().Begin("second");

            Assert.AreNotEqual(first, second);
            StringAssert.IsMatch(@"^turn-[0-9a-f]{8}-000001$", first);
            StringAssert.IsMatch(@"^turn-[0-9a-f]{8}-000001$", second);
        }

        private static ConversationEvent Event(string turnId, ConversationEventType type, string text = null)
        {
            return new ConversationEvent { TurnId = turnId, Type = type, Text = text };
        }
    }
}
#endif

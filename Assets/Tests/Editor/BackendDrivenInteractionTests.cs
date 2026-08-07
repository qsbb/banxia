#if UNITY_EDITOR
using NUnit.Framework;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class BackendDrivenInteractionTests
    {
        private GameObject avatarObject;
        private GameObject serviceObject;

        [TearDown]
        public void TearDown()
        {
            if (serviceObject != null) Object.DestroyImmediate(serviceObject);
            if (avatarObject != null) Object.DestroyImmediate(avatarObject);
        }

        [TestCase(ConversationState.Idle, false, true, true)]
        [TestCase(ConversationState.Listening, false, true, false)]
        [TestCase(ConversationState.Speaking, false, true, false)]
        [TestCase(ConversationState.Idle, true, true, false)]
        [TestCase(ConversationState.Idle, false, false, false)]
        public void IdleUserGazeOnlyRunsAtLowPriority(
            ConversationState conversationState,
            bool semanticContact,
            bool enabled,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                AvatarConversationPresenter.ShouldUseIdleUserGaze(conversationState, semanticContact, enabled));
        }

        [Test]
        public void SensorEventAndBackendReactionAreSeparate()
        {
            avatarObject = new GameObject("TestAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var head = new GameObject("Head");
            head.transform.SetParent(avatarObject.transform);
            head.AddComponent<MMDBoneTransform>().boneName = "\u982D";

            serviceObject = new GameObject("InteractionService");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            interaction.SetLocalReactionsEnabled(false);

            interaction.SimulateInteraction(HumanInteractionKind.Handshake);
            Assert.AreEqual(HumanInteractionKind.Handshake, interaction.CurrentInteraction);
            Assert.AreEqual(HumanInteractionKind.None, interaction.PendingBackendReaction);

            interaction.PlayReaction(HumanInteractionKind.HeadPat);
            Assert.AreEqual(HumanInteractionKind.HeadPat, interaction.PendingBackendReaction);
            Assert.IsFalse(interaction.LocalReactionsEnabled);
        }

        [Test]
        public void BehaviorCoordinatorRejectsRepeatedOrConflictingWholeBodyGestures()
        {
            var behavior = new AvatarBehaviorCoordinator();
            behavior.Reset(0f, 0f);

            Assert.IsTrue(behavior.TryAcceptIntent("wave", false, false, 1f, out var wave));
            Assert.AreEqual("wave", wave);
            Assert.IsFalse(behavior.TryAcceptIntent("wave", false, false, 2.5f, out _));
            Assert.IsFalse(behavior.TryAcceptIntent("bow", true, false, 8f, out _));
            Assert.IsFalse(behavior.TryAcceptIntent("bow", false, true, 8f, out _));
            Assert.IsFalse(behavior.TryAcceptIntent("step_back", false, false, 8f, out _));
            Assert.IsTrue(behavior.TryAcceptIntent("talk", true, true, 8f, out var talk));
            Assert.AreEqual("talk", talk);
        }

        [TestCase(ConversationState.Idle, false, false, "idle", true)]
        [TestCase(ConversationState.Listening, false, false, "idle", false)]
        [TestCase(ConversationState.Speaking, false, false, "idle", false)]
        [TestCase(ConversationState.Idle, true, false, "idle", false)]
        [TestCase(ConversationState.Idle, false, true, "idle", false)]
        [TestCase(ConversationState.Idle, false, false, "wave", false)]
        public void IdleBehaviorOnlyRunsWhenEveryHigherPriorityLayerIsIdle(
            ConversationState state,
            bool semanticContact,
            bool importedMotionBusy,
            string currentAction,
            bool expected)
        {
            Assert.AreEqual(
                expected,
                AvatarBehaviorCoordinator.CanRunIdleBehavior(
                    state,
                    semanticContact,
                    importedMotionBusy,
                    currentAction));
        }

        [Test]
        public void ExplicitGestureDefersTheNextAutomaticIdleMotion()
        {
            var behavior = new AvatarBehaviorCoordinator();
            behavior.Reset(0f, 0f);

            Assert.IsTrue(behavior.TryAcceptIntent("wave", false, false, 20f, out _));
            Assert.That(behavior.NextIdleBehaviorAt, Is.GreaterThanOrEqualTo(38f));
            Assert.IsFalse(behavior.TryTakeIdleBehavior(
                ConversationState.Idle,
                false,
                false,
                "idle",
                30f,
                0f,
                out _));
        }
    }
}
#endif

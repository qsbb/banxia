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
    }
}
#endif

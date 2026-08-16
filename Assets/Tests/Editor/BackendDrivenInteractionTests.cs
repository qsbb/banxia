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
        [TestCase(ConversationState.Thinking, false, true, true)]
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
        public void ThinkingOverridesAStaleAwayGaze()
        {
            Assert.AreEqual(
                "user",
                AvatarConversationPresenter.ResolveGazeMode(
                    ConversationState.Thinking,
                    true,
                    "away"));
            Assert.AreEqual(
                "away",
                AvatarConversationPresenter.ResolveGazeMode(
                    ConversationState.Speaking,
                    false,
                    "away"));
        }

        [Test]
        public void ConversationStatesKeepGazeOnUserWithoutAnExplicitIntent()
        {
            Assert.That(AvatarConversationPresenter.ShouldUseConversationUserGaze(
                ConversationState.Listening, false, true), Is.True);
            Assert.That(AvatarConversationPresenter.ShouldUseConversationUserGaze(
                ConversationState.Thinking, false, true), Is.True);
            Assert.That(AvatarConversationPresenter.ShouldUseConversationUserGaze(
                ConversationState.Speaking, false, true), Is.True);
            Assert.That(AvatarConversationPresenter.ShouldUseConversationUserGaze(
                ConversationState.Speaking, true, true), Is.False);
            Assert.That(AvatarConversationPresenter.ResolveGazeMode(
                ConversationState.Speaking, false, true, "none"), Is.EqualTo("user"));
        }

        [Test]
        public void ActionBoneWritersRetainHeadOwnershipDuringGazePass()
        {
            Assert.That(AvatarConversationPresenter.ShouldSuspendGazeForAction("wave"), Is.True);
            Assert.That(AvatarConversationPresenter.ShouldSuspendGazeForAction("dance"), Is.True);
            Assert.That(AvatarConversationPresenter.ShouldSuspendGazeForAction("crouch"), Is.True);
            Assert.That(AvatarConversationPresenter.ShouldSuspendGazeForAction("talk"), Is.False);
            Assert.That(AvatarConversationPresenter.ShouldSuspendGazeForAction("idle"), Is.False);
        }

        [Test]
        public void GazeRotationApproachesMovingTargetWithoutSnapping()
        {
            var target = Quaternion.Euler(0f, 30f, 0f);
            var first = AvatarConversationPresenter.SmoothGazeRotation(
                Quaternion.identity,
                target,
                .1f,
                8f);
            var second = AvatarConversationPresenter.SmoothGazeRotation(
                first,
                target,
                .1f,
                8f);

            var firstAngle = Quaternion.Angle(Quaternion.identity, first);
            var secondAngle = Quaternion.Angle(Quaternion.identity, second);
            Assert.That(firstAngle, Is.GreaterThan(0f).And.LessThan(30f));
            Assert.That(secondAngle, Is.GreaterThan(firstAngle).And.LessThan(30f));
        }

        [Test]
        public void PresenceDoesNotOverwritePresenterSpeechGaze()
        {
            avatarObject = new GameObject("Conversational gaze avatar");
            var headObject = new GameObject("Head");
            headObject.transform.SetParent(avatarObject.transform, false);
            headObject.transform.localPosition = new Vector3(0f, 1.4f, 0f);
            headObject.AddComponent<MMDBoneTransform>().boneName = "頭";
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var presenter = avatarObject.AddComponent<AvatarConversationPresenter>();
            var presence = avatarObject.AddComponent<AvatarPresence>();
            presenter.Bind(controller, null, null);
            presence.Bind(controller);
            presenter.SetConversationState(ConversationState.Speaking);

            serviceObject = new GameObject("Conversational gaze camera");
            serviceObject.tag = "MainCamera";
            serviceObject.transform.position = new Vector3(.35f, 1.55f, 1.5f);
            serviceObject.AddComponent<Camera>();
            var flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            typeof(AvatarConversationPresenter).GetField("gazeBlend", flags)
                ?.SetValue(presenter, 1f);
            typeof(AvatarConversationPresenter).GetField("smoothedHeadRotation", flags)
                ?.SetValue(presenter, Quaternion.Euler(0f, 8f, 0f));
            typeof(AvatarConversationPresenter).GetField("hasSmoothedHeadRotation", flags)
                ?.SetValue(presenter, true);

            typeof(AvatarConversationPresenter).GetMethod("LateUpdate", flags)
                ?.Invoke(presenter, null);
            var presenterRotation = headObject.transform.localRotation;
            typeof(AvatarPresence).GetMethod("LateUpdate", flags)
                ?.Invoke(presence, null);

            Assert.That(Quaternion.Angle(Quaternion.identity, presenterRotation), Is.GreaterThan(1f));
            Assert.That(
                Quaternion.Angle(presenterRotation, headObject.transform.localRotation),
                Is.LessThan(.01f));
        }

        [Test]
        public void PresenterReportsWhetherBackendActionWasActuallyAccepted()
        {
            avatarObject = new GameObject("TestAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);

            serviceObject = new GameObject("PresenterService");
            var presenter = serviceObject.AddComponent<AvatarConversationPresenter>();
            presenter.Bind(controller, null, null);

            Assert.IsTrue(presenter.ApplyIntent("happy", "wave", "user"));
            Assert.IsFalse(presenter.ApplyIntent("happy", "wave", "user"));
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
            Assert.IsTrue(behavior.TryAcceptIntent("step_back", false, false, 8f, out var stepBack));
            Assert.AreEqual("step_back", stepBack);
            Assert.IsTrue(behavior.TryAcceptIntent("talk", true, true, 8f, out var talk));
            Assert.AreEqual("talk", talk);
        }

        [TestCase("dance")]
        [TestCase("dance_next")]
        public void DanceRequestsCanSwitchAnImportedMotionAlreadyInProgress(string action)
        {
            var behavior = new AvatarBehaviorCoordinator();
            behavior.Reset(0f, 0f);

            Assert.That(
                behavior.TryAcceptIntent(action, false, true, 1f, out var accepted),
                Is.True);
            Assert.That(accepted, Is.EqualTo(action));
        }

        [Test]
        public void NonDanceGesturesCannotInterruptAnImportedMotion()
        {
            var behavior = new AvatarBehaviorCoordinator();
            behavior.Reset(0f, 0f);

            Assert.That(
                behavior.TryAcceptIntent("turn_half", false, true, 1f, out _),
                Is.False);
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
        [TestCase("sit")]
        [TestCase("lie_down")]
        public void ExplicitRestingGesturesAreStrictlyAllowlisted(string action)
        {
            var behavior = new AvatarBehaviorCoordinator();
            behavior.Reset(0f, 0f);

            Assert.That(behavior.TryAcceptIntent(action, false, false, 2f, out var accepted), Is.True);
            Assert.That(accepted, Is.EqualTo(action));
        }
    }
}
#endif

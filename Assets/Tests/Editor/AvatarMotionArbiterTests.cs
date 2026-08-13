#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarMotionArbiterTests
    {
        [Test]
        public void ImportedMotionBlocksIdleAndBackendUntilSystemRestores()
        {
            var idle = AvatarMotionArbiter.Decide(
                AvatarActionSource.Imported,
                AvatarActionSource.Idle,
                "vmd",
                "sway",
                true);
            Assert.IsFalse(idle.Accepted);
            Assert.AreEqual("imported_motion_busy", idle.Reason);

            var backend = AvatarMotionArbiter.Decide(
                AvatarActionSource.Imported,
                AvatarActionSource.Backend,
                "vmd",
                "wave",
                true);
            Assert.IsFalse(backend.Accepted);

            var restore = AvatarMotionArbiter.Decide(
                AvatarActionSource.Imported,
                AvatarActionSource.System,
                "vmd",
                "idle",
                true);
            Assert.IsTrue(restore.Accepted);
        }

        [Test]
        public void PhysicalTouchHasPriorityOverBackendGesture()
        {
            var decision = AvatarMotionArbiter.Decide(
                AvatarActionSource.Backend,
                AvatarActionSource.Touch,
                "wave",
                "head_pat",
                false);
            Assert.IsTrue(decision.Accepted);
        }

        [Test]
        public void IdleBehaviorCannotReplaceActiveBackendAction()
        {
            var decision = AvatarMotionArbiter.Decide(
                AvatarActionSource.Backend,
                AvatarActionSource.Idle,
                "wave",
                "sway",
                false);
            Assert.IsFalse(decision.Accepted);
            Assert.AreEqual("lower_priority_than_current", decision.Reason);
        }
    }
}
#endif

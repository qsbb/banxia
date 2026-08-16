#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarNaturalTurnTests
    {
        [Test]
        public void TurnUsesTwoMonotonicRotationStagesAndSettlesExactly()
        {
            var previous = 0f;
            for (var index = 0; index <= 100; index++)
            {
                var sample = AvatarController.SampleNaturalTurn(index / 100f, 1f);
                Assert.That(sample.YawProgress, Is.GreaterThanOrEqualTo(previous - .0001f));
                previous = sample.YawProgress;
            }

            Assert.That(AvatarController.SampleNaturalTurn(0f, 1f).YawProgress, Is.Zero);
            Assert.That(AvatarController.SampleNaturalTurn(.48f, 1f).YawProgress,
                Is.InRange(.47f, .53f));
            Assert.That(AvatarController.SampleNaturalTurn(1f, 1f).YawProgress, Is.EqualTo(1f));
        }

        [Test]
        public void RootUsesAWeightTransferPathButReturnsToPlacementAnchor()
        {
            var start = AvatarController.SampleNaturalTurn(0f, 1f).LocalRootOffset;
            var firstStep = AvatarController.SampleNaturalTurn(.3f, 1f).LocalRootOffset;
            var secondStep = AvatarController.SampleNaturalTurn(.7f, 1f).LocalRootOffset;
            var end = AvatarController.SampleNaturalTurn(1f, 1f).LocalRootOffset;

            Assert.That(start, Is.EqualTo(Vector3.zero));
            Assert.That(firstStep.sqrMagnitude, Is.GreaterThan(.0001f));
            Assert.That(secondStep.sqrMagnitude, Is.GreaterThan(.0001f));
            Assert.That(firstStep.magnitude, Is.GreaterThan(.07f));
            Assert.That(secondStep.magnitude, Is.GreaterThan(.07f));
            Assert.That(firstStep.x, Is.GreaterThan(secondStep.x));
            Assert.That(firstStep.y, Is.LessThan(0f));
            Assert.That(secondStep.z, Is.GreaterThan(.04f));
            Assert.That(end, Is.EqualTo(Vector3.zero));
        }

        [Test]
        public void HalfTurnRotationHasAnUnambiguousDirectionAndExactEndpoint()
        {
            var start = Quaternion.Euler(0f, 25f, 0f);
            var rightMid = AvatarController.NaturalTurnRotation(start, 1f, .25f);
            var leftMid = AvatarController.NaturalTurnRotation(start, -1f, .25f);
            var end = AvatarController.NaturalTurnRotation(start, 1f, 1f);

            Assert.That(Vector3.SignedAngle(
                start * Vector3.forward,
                rightMid * Vector3.forward,
                Vector3.up), Is.EqualTo(45f).Within(.001f));
            Assert.That(Vector3.SignedAngle(
                start * Vector3.forward,
                leftMid * Vector3.forward,
                Vector3.up), Is.EqualTo(-45f).Within(.001f));
            Assert.That(Quaternion.Angle(start, end), Is.EqualTo(180f).Within(.001f));
        }

        [Test]
        public void MirroredTurnsSwapTheLeadingFootAndMirrorLateralMotion()
        {
            var rightTurn = AvatarController.SampleNaturalTurn(.3f, 1f);
            var leftTurn = AvatarController.SampleNaturalTurn(.3f, -1f);

            Assert.That(rightTurn.LeftStep, Is.GreaterThan(rightTurn.RightStep));
            Assert.That(leftTurn.RightStep, Is.GreaterThan(leftTurn.LeftStep));
            Assert.That(leftTurn.LocalRootOffset.x,
                Is.EqualTo(-rightTurn.LocalRootOffset.x).Within(.0001f));
            Assert.That(leftTurn.PelvisYaw,
                Is.EqualTo(-rightTurn.PelvisYaw).Within(.0001f));
        }

        [Test]
        public void AuthoredAndImportedTurnsOwnRootMotionWhileIdlePresenceDoesNot()
        {
            Assert.That(AvatarPresence.IsActionTurnBlocked("turn_half"), Is.True);
            Assert.That(AvatarPresence.IsActionTurnBlocked("vmd"), Is.True);
            Assert.That(AvatarPresence.IsActionTurnBlocked("idle"), Is.False);
        }
    }
}
#endif

#if UNITY_EDITOR
using NUnit.Framework;
using UMT;
using UnityEngine;
using UnityEngine.XR.Hands;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarTouchInteractionTests
    {
        private GameObject avatarObject;
        private GameObject serviceObject;

        [TearDown]
        public void TearDown()
        {
            if (serviceObject != null) Object.DestroyImmediate(serviceObject);
            if (avatarObject != null) Object.DestroyImmediate(avatarObject);
        }

        [Test]
        public void PinchUsesSeparateEnterAndReleaseThresholds()
        {
            Assert.IsFalse(AvatarTouchInteraction.UpdatePinchState(false, .038f));
            Assert.IsTrue(AvatarTouchInteraction.UpdatePinchState(false, .031f));
            Assert.IsTrue(AvatarTouchInteraction.UpdatePinchState(true, .041f));
            Assert.IsFalse(AvatarTouchInteraction.UpdatePinchState(true, .046f));
        }

        [Test]
        public void TrackingGraceHasBoundedLifetime()
        {
            Assert.IsTrue(AvatarTouchInteraction.IsTrackingGraceActive(10.08f, 10f, .1f));
            Assert.IsFalse(AvatarTouchInteraction.IsTrackingGraceActive(10.11f, 10f, .1f));
            Assert.IsFalse(AvatarTouchInteraction.IsTrackingGraceActive(9.9f, 10f, .1f));
        }

        [Test]
        public void ExistingContactUsesWiderReleaseThreshold()
        {
            Assert.AreEqual(.07f, AvatarTouchInteraction.ContactThreshold(false, .07f), .0001f);
            Assert.AreEqual(.0945f, AvatarTouchInteraction.ContactThreshold(true, .07f), .0001f);
        }

        [Test]
        public void OnlyPalmAndPinchTipsCreateSemanticContactProbes()
        {
            Assert.AreEqual(
                TrackedHandContactProbe.Palm,
                QuestTrackedHandVisualizer.ContactProbeForJoint(XRHandJointID.Palm));
            Assert.AreEqual(
                TrackedHandContactProbe.PinchTip,
                QuestTrackedHandVisualizer.ContactProbeForJoint(XRHandJointID.IndexTip));
            Assert.AreEqual(
                TrackedHandContactProbe.PinchTip,
                QuestTrackedHandVisualizer.ContactProbeForJoint(XRHandJointID.ThumbTip));
            Assert.AreEqual(
                TrackedHandContactProbe.None,
                QuestTrackedHandVisualizer.ContactProbeForJoint(XRHandJointID.Wrist));
            Assert.AreEqual(
                TrackedHandContactProbe.None,
                QuestTrackedHandVisualizer.ContactProbeForJoint(XRHandJointID.MiddleTip));
        }

        [Test]
        public void ProbeRolesRejectAccidentalWholeHandGestures()
        {
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.Palm,
                AvatarContactRegion.Head,
                false));
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.Palm,
                AvatarContactRegion.Hand,
                false));
            Assert.IsFalse(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.Palm,
                AvatarContactRegion.Face,
                true));
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face,
                true));
            Assert.IsFalse(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face,
                false));
            Assert.IsFalse(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Head,
                true));
        }

        [Test]
        public void BindCreatesContactRegionAtAnimatedHandBone()
        {
            avatarObject = new GameObject("TouchAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var rightHand = new GameObject("RightHand");
            rightHand.transform.SetParent(avatarObject.transform, false);
            rightHand.transform.localPosition = new Vector3(1f, 1f, 0f);
            var bone = rightHand.AddComponent<MMDBoneTransform>();
            bone.boneName = "\u53F3\u624B\u9996";

            serviceObject = new GameObject("TouchService");
            var touch = serviceObject.AddComponent<AvatarTouchInteraction>();
            touch.Bind(controller);
            Physics.SyncTransforms();

            Assert.IsTrue(touch.TryGetContactRegion(rightHand.transform.position, .03f, out var region));
            Assert.AreEqual(AvatarContactRegion.Hand, region);
        }
    }
}
#endif

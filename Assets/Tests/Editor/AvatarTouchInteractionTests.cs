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
        public void TrackedHandPinchNeverDragsTheWholeAvatar()
        {
            Assert.IsFalse(AvatarTouchInteraction.CanDragAvatar(true, true, true, true));
            Assert.IsTrue(AvatarTouchInteraction.CanDragAvatar(false, true, true, true));
            Assert.IsFalse(AvatarTouchInteraction.CanDragAvatar(false, true, false, true));
            Assert.IsFalse(AvatarTouchInteraction.CanDragAvatar(false, true, true, false));
        }

        [Test]
        public void ContactProxyCancelsAvatarRootScale()
        {
            var compensation = AvatarTouchInteraction.CalculateWorldScaleCompensation(
                new Vector3(.1f, 2f, .25f));

            Assert.That(compensation.x, Is.EqualTo(10f).Within(.001f));
            Assert.That(compensation.y, Is.EqualTo(.5f).Within(.001f));
            Assert.That(compensation.z, Is.EqualTo(4f).Within(.001f));
            Assert.That(
                QuestTrackedHandVisualizer.MaximumScale(new Vector3(-2f, .5f, 1.25f)),
                Is.EqualTo(2f).Within(.001f));
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
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.Palm,
                AvatarContactRegion.Body,
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

        [Test]
        public void BindUsesPmxRigidBodyVolumeInsteadOfWholeAvatarBounds()
        {
            avatarObject = new GameObject("PmxTouchAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);

            var torso = new GameObject("TorsoBone");
            torso.transform.SetParent(avatarObject.transform, false);
            torso.transform.localPosition = new Vector3(0f, 1f, 0f);
            var bone = torso.AddComponent<MMDBoneTransform>();
            bone.boneName = "upperbody";

            var volume = new GameObject("TorsoRigidBody");
            volume.transform.SetParent(torso.transform, false);
            var body = volume.AddComponent<MMDRigidBody>();
            body.relatedBone = bone;
            body.shape = PMXRigidBody.Shape.Box;
            body.size = new Unity.Mathematics.float3(.18f, .30f, .12f);

            serviceObject = new GameObject("TouchService");
            var touch = serviceObject.AddComponent<AvatarTouchInteraction>();
            touch.Bind(controller);
            Physics.SyncTransforms();

            Assert.AreEqual(1, touch.ModelCollisionVolumeCount);
            Assert.That(
                avatarObject.GetComponentsInChildren<BoxCollider>(true),
                Has.Length.EqualTo(1));
            Assert.IsTrue(touch.TryGetContactRegion(volume.transform.position, .01f, out var region));
            Assert.AreEqual(AvatarContactRegion.Body, region);
        }

        [Test]
        public void PmxVolumeClassificationKeepsAnatomyAndRejectsHairPhysics()
        {
            avatarObject = new GameObject("PmxClassificationAvatar");
            var boneObject = new GameObject("Bone");
            boneObject.transform.SetParent(avatarObject.transform, false);
            var bone = boneObject.AddComponent<MMDBoneTransform>();
            var body = boneObject.AddComponent<MMDRigidBody>();
            body.relatedBone = bone;

            bone.boneName = "Hair_03";
            Assert.AreEqual(AvatarContactRegion.None, AvatarTouchInteraction.ClassifyPmxContactRegion(body));

            bone.boneName = "\u53f3\u624b\u9996";
            Assert.AreEqual(AvatarContactRegion.Hand, AvatarTouchInteraction.ClassifyPmxContactRegion(body));

            bone.boneName = "Head";
            Assert.AreEqual(AvatarContactRegion.Head, AvatarTouchInteraction.ClassifyPmxContactRegion(body));
        }

        [Test]
        public void SweptContactFindsThinProxyBetweenFrames()
        {
            avatarObject = new GameObject("SweepAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var volume = new GameObject("Head");
            volume.transform.SetParent(avatarObject.transform, false);
            volume.transform.localPosition = new Vector3(0f, .30f, 0f);
            var bone = volume.AddComponent<MMDBoneTransform>();
            bone.boneName = "head";
            var body = volume.AddComponent<MMDRigidBody>();
            body.relatedBone = bone;
            body.shape = PMXRigidBody.Shape.Sphere;
            body.size = new Unity.Mathematics.float3(.10f, 0f, 0f);
            serviceObject = new GameObject("TouchService");
            var touch = serviceObject.AddComponent<AvatarTouchInteraction>();
            touch.Bind(controller);
            Physics.SyncTransforms();

            Assert.That(touch.TryGetContactRegionSwept(
                new Vector3(-.15f, .40f, -.02f),
                new Vector3(.15f, .40f, -.02f),
                .02f,
                out var region,
                out _), Is.True);
            Assert.That(region, Is.EqualTo(AvatarContactRegion.Head));
        }

        [Test]
        public void PenetrationCorrectionPushesTrackedHandOutOfAvatarVolume()
        {
            avatarObject = new GameObject("PenetrationAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);

            var head = new GameObject("Head");
            head.transform.SetParent(avatarObject.transform, false);
            head.transform.localPosition = new Vector3(0f, .30f, 0f);
            var bone = head.AddComponent<MMDBoneTransform>();
            bone.boneName = "head";
            var body = head.AddComponent<MMDRigidBody>();
            body.relatedBone = bone;
            body.shape = PMXRigidBody.Shape.Sphere;
            body.size = new Unity.Mathematics.float3(.10f, 0f, 0f);

            serviceObject = new GameObject("TouchService");
            var touch = serviceObject.AddComponent<AvatarTouchInteraction>();
            touch.Bind(controller);
            var hand = new GameObject("TrackedPalmProbe");
            hand.transform.SetParent(serviceObject.transform, false);
            hand.transform.localPosition = new Vector3(0f, .40f, -.06f);
            var probe = hand.AddComponent<SphereCollider>();
            probe.radius = .06f;
            Physics.SyncTransforms();

            Assert.That(
                touch.TryGetPenetrationCorrection(probe, out var correction, out var region),
                Is.True);
            Assert.That(region, Is.EqualTo(AvatarContactRegion.Head));
            Assert.That(correction.magnitude, Is.GreaterThan(.0001f));
        }
    }
}
#endif

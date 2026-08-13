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
        private GameObject cameraObject;

        [TearDown]
        public void TearDown()
        {
            if (serviceObject != null) Object.DestroyImmediate(serviceObject);
            if (avatarObject != null) Object.DestroyImmediate(avatarObject);
            if (cameraObject != null) Object.DestroyImmediate(cameraObject);
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
        public void PhysicalHandPoseSurvivesOnlyBriefTrackingDropout()
        {
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldRetainTrackedHandPose(
                "hand_tracking", 10.08f, 10f, .1f));
            Assert.IsFalse(QuestTrackedHandVisualizer.ShouldRetainTrackedHandPose(
                "hand_tracking", 10.11f, 10f, .1f));
            Assert.IsFalse(QuestTrackedHandVisualizer.ShouldRetainTrackedHandPose(
                "controller", 10.01f, 10f, .1f));
            Assert.IsFalse(QuestTrackedHandVisualizer.ShouldRetainTrackedHandPose(
                "none", 10.01f, 10f, .1f));
        }

        [Test]
        public void HandVisibilityToggleCanRecoverAfterBeingDisabled()
        {
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldShowTrackedHand("hand_tracking", true));
            Assert.IsFalse(QuestTrackedHandVisualizer.ShouldShowTrackedHand("hand_tracking", false));
            Assert.IsFalse(QuestTrackedHandVisualizer.ShouldShowTrackedHand("none", true));
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldShowTrackedHand("controller", true));
        }

        [Test]
        public void PokeLifecycleMapsContactFactsToEnterHoverExitWithoutMovingPose()
        {
            var lifecycle = new PokeInteractionLifecycle();
            var point = new Vector3(.2f, .3f, .4f);
            var began = lifecycle.Observe(new TrackedHandContactFact(
                1,
                TrackedHandContactPhase.Began,
                UnityEngine.XR.XRNode.RightHand,
                UnityEngine.XR.Hands.XRHandJointID.IndexTip,
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face,
                point,
                Vector3.forward,
                .01f,
                0f,
                true,
                true));
            Assert.AreEqual(PokeInteractionPhase.Enter, began.Phase);
            Assert.IsTrue(lifecycle.IsActive);
            Assert.AreEqual(point, began.Point);

            var hover = lifecycle.Observe(new TrackedHandContactFact(
                1,
                TrackedHandContactPhase.Updated,
                UnityEngine.XR.XRNode.RightHand,
                UnityEngine.XR.Hands.XRHandJointID.IndexTip,
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face,
                point + Vector3.up * .001f,
                Vector3.forward,
                .02f,
                .1f,
                true,
                true));
            Assert.AreEqual(PokeInteractionPhase.Hover, hover.Phase);

            var ended = lifecycle.Observe(new TrackedHandContactFact(
                1,
                TrackedHandContactPhase.Ended,
                UnityEngine.XR.XRNode.RightHand,
                UnityEngine.XR.Hands.XRHandJointID.IndexTip,
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face,
                point,
                Vector3.forward,
                0f,
                .2f,
                true,
                true));
            Assert.AreEqual(PokeInteractionPhase.Exit, ended.Phase);
            Assert.IsFalse(lifecycle.IsActive);
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
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.Palm,
                AvatarContactRegion.Hair,
                false));
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.Palm,
                AvatarContactRegion.Limb,
                false));
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.Palm,
                AvatarContactRegion.Face,
                false));
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face,
                true));
            Assert.IsFalse(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face,
                false));
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Head,
                false));
            Assert.IsTrue(QuestTrackedHandVisualizer.ShouldReportContact(
                TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Hair,
                false));
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
        public void PmxVolumeClassificationKeepsAnatomyAndIncludesHairPhysics()
        {
            avatarObject = new GameObject("PmxClassificationAvatar");
            var boneObject = new GameObject("Bone");
            boneObject.transform.SetParent(avatarObject.transform, false);
            var bone = boneObject.AddComponent<MMDBoneTransform>();
            var body = boneObject.AddComponent<MMDRigidBody>();
            body.relatedBone = bone;

            bone.boneName = "Hair_03";
            Assert.AreEqual(AvatarContactRegion.Hair, AvatarTouchInteraction.ClassifyPmxContactRegion(body));

            bone.boneName = "\u53f3\u624b\u9996";
            Assert.AreEqual(AvatarContactRegion.Hand, AvatarTouchInteraction.ClassifyPmxContactRegion(body));

            bone.boneName = "Head";
            Assert.AreEqual(AvatarContactRegion.Head, AvatarTouchInteraction.ClassifyPmxContactRegion(body));
        }

        [Test]
        public void HairPmxVolumeIsReturnedAsTouchableRegion()
        {
            avatarObject = new GameObject("HairTouchAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);

            var hair = new GameObject("HairBone");
            hair.transform.SetParent(avatarObject.transform, false);
            hair.transform.localPosition = new Vector3(0f, .35f, 0f);
            var bone = hair.AddComponent<MMDBoneTransform>();
            bone.boneName = "Hair_03";
            var body = hair.AddComponent<MMDRigidBody>();
            body.relatedBone = bone;
            body.shape = PMXRigidBody.Shape.Sphere;
            body.size = new Unity.Mathematics.float3(.08f, 0f, 0f);

            serviceObject = new GameObject("HairTouchService");
            var touch = serviceObject.AddComponent<AvatarTouchInteraction>();
            touch.Bind(controller);
            Physics.SyncTransforms();

            Assert.IsTrue(touch.TryGetContactRegion(hair.transform.position, .01f, out var region));
            Assert.AreEqual(AvatarContactRegion.Hair, region);
        }

        [Test]
        public void FaceProxyIsPlacedOnTheViewerSideOfNegativeZModel()
        {
            cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.position = new Vector3(0f, .35f, -2f);
            cameraObject.AddComponent<Camera>();

            avatarObject = new GameObject("NegativeZAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var head = new GameObject("Head");
            head.transform.SetParent(avatarObject.transform, false);
            head.transform.localPosition = new Vector3(0f, .35f, 0f);
            var headBone = head.AddComponent<MMDBoneTransform>();
            headBone.boneName = "head";

            serviceObject = new GameObject("TouchService");
            var touch = serviceObject.AddComponent<AvatarTouchInteraction>();
            touch.Bind(controller);
            Physics.SyncTransforms();

            Assert.That(touch.TryGetContactRegion(
                new Vector3(0f, .37f, -.06f),
                .01f,
                out var region), Is.True);
            Assert.That(region, Is.EqualTo(AvatarContactRegion.Face));
        }

        [Test]
        public void MissingPmxLimbMetadataGetsBoneFollowingContactProxy()
        {
            avatarObject = new GameObject("FallbackLimbAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var arm = new GameObject("LeftUpperArm");
            arm.transform.SetParent(avatarObject.transform, false);
            arm.transform.localPosition = new Vector3(.6f, .45f, 0f);
            var armBone = arm.AddComponent<MMDBoneTransform>();
            armBone.boneName = "leftupperarm";

            serviceObject = new GameObject("TouchService");
            var touch = serviceObject.AddComponent<AvatarTouchInteraction>();
            touch.Bind(controller);
            Physics.SyncTransforms();

            Assert.That(touch.TryGetContactRegion(arm.transform.position, .01f, out var region), Is.True);
            Assert.That(region, Is.EqualTo(AvatarContactRegion.Limb));
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

        [Test]
        public void ContactSurfaceReportsOutwardNormalWithoutMovingProbe()
        {
            avatarObject = new GameObject("SurfaceAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var head = new GameObject("Head");
            head.transform.SetParent(avatarObject.transform, false);
            var bone = head.AddComponent<MMDBoneTransform>();
            bone.boneName = "head";
            var body = head.AddComponent<MMDRigidBody>();
            body.relatedBone = bone;
            body.shape = PMXRigidBody.Shape.Sphere;
            body.size = new Unity.Mathematics.float3(.1f, 0f, 0f);

            serviceObject = new GameObject("TouchService");
            var touch = serviceObject.AddComponent<AvatarTouchInteraction>();
            touch.Bind(controller);
            Physics.SyncTransforms();
            var probe = new Vector3(.12f, 0f, 0f);

            Assert.That(touch.TryGetContactSurface(
                probe,
                .03f,
                AvatarContactRegion.Head,
                out var surface,
                out var normal), Is.True);
            Assert.That(surface.x, Is.EqualTo(.1f).Within(.005f));
            Assert.That(Vector3.Dot(normal, Vector3.right), Is.GreaterThan(.99f));
            Assert.That(probe, Is.EqualTo(new Vector3(.12f, 0f, 0f)));
        }

        [Test]
        public void ContactTrackerPublishesStableLifecycleDurationAndNormal()
        {
            var tracker = new TrackedHandContactTracker(
                UnityEngine.XR.XRNode.LeftHand,
                XRHandJointID.Palm,
                TrackedHandContactProbe.Palm);
            var facts = new System.Collections.Generic.List<TrackedHandContactFact>();
            tracker.FactChanged += facts.Add;

            tracker.Observe(
                AvatarContactRegion.Head,
                Vector3.one,
                Vector3.up * 3f,
                .02f,
                false,
                true,
                10f,
                .1f);
            tracker.Observe(
                AvatarContactRegion.Head,
                Vector3.one,
                Vector3.up,
                .01f,
                false,
                true,
                10.05f,
                .1f);
            tracker.Observe(
                AvatarContactRegion.Head,
                Vector3.one,
                Vector3.up,
                .01f,
                false,
                true,
                10.12f,
                .1f);
            tracker.Clear(10.25f);

            Assert.That(facts, Has.Count.EqualTo(3));
            Assert.That(facts[0].Phase, Is.EqualTo(TrackedHandContactPhase.Began));
            Assert.That(facts[1].Phase, Is.EqualTo(TrackedHandContactPhase.Updated));
            Assert.That(facts[2].Phase, Is.EqualTo(TrackedHandContactPhase.Ended));
            Assert.That(facts[2].DurationSeconds, Is.EqualTo(.25f).Within(.0001f));
            Assert.That(facts[0].SurfaceNormal, Is.EqualTo(Vector3.up));
            Assert.That(facts[0].UsesAuthoritativeTrackedPose, Is.True);
            Assert.That(facts[0].SequenceId, Is.EqualTo(facts[2].SequenceId));
        }

        [Test]
        public void ContactTrackerRestartsWhenSemanticIdentityChanges()
        {
            var tracker = new TrackedHandContactTracker(
                UnityEngine.XR.XRNode.RightHand,
                XRHandJointID.IndexTip,
                TrackedHandContactProbe.PinchTip);
            var facts = new System.Collections.Generic.List<TrackedHandContactFact>();
            tracker.FactChanged += facts.Add;

            tracker.Observe(
                AvatarContactRegion.Face,
                Vector3.zero,
                Vector3.forward,
                0f,
                false,
                true,
                1f);
            tracker.Observe(
                AvatarContactRegion.Face,
                Vector3.zero,
                Vector3.forward,
                .01f,
                true,
                true,
                1.2f);

            Assert.That(facts, Has.Count.EqualTo(3));
            Assert.That(facts[0].Phase, Is.EqualTo(TrackedHandContactPhase.Began));
            Assert.That(facts[1].Phase, Is.EqualTo(TrackedHandContactPhase.Ended));
            Assert.That(facts[2].Phase, Is.EqualTo(TrackedHandContactPhase.Began));
            Assert.That(facts[2].Pinching, Is.True);
            Assert.That(facts[2].SequenceId, Is.GreaterThan(facts[0].SequenceId));
        }

        [Test]
        public void ContactAggregatorKeepsSecondProbeWhenFirstEnds()
        {
            var aggregator = new TrackedHandContactAggregator();
            var selected = new System.Collections.Generic.List<TrackedHandContactFact>();
            aggregator.FactChanged += selected.Add;
            var palm = new TrackedHandContactFact(
                1, TrackedHandContactPhase.Began, UnityEngine.XR.XRNode.LeftHand,
                XRHandJointID.Palm, TrackedHandContactProbe.Palm,
                AvatarContactRegion.Body, Vector3.zero, Vector3.forward, .02f, 0f, false, true);
            var finger = new TrackedHandContactFact(
                1, TrackedHandContactPhase.Began, UnityEngine.XR.XRNode.LeftHand,
                XRHandJointID.IndexTip, TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Head, Vector3.one, Vector3.up, .01f, 0f, false, true);
            aggregator.Accept(palm);
            aggregator.Accept(finger);
            aggregator.Accept(new TrackedHandContactFact(
                1, TrackedHandContactPhase.Ended, UnityEngine.XR.XRNode.LeftHand,
                XRHandJointID.Palm, TrackedHandContactProbe.Palm,
                AvatarContactRegion.Body, Vector3.zero, Vector3.forward, .02f, .2f, false, true));

            Assert.That(aggregator.ActiveCount, Is.EqualTo(1));
            Assert.That(selected[selected.Count - 1].Region, Is.EqualTo(AvatarContactRegion.Head));
            Assert.That(selected[selected.Count - 1].Phase, Is.EqualTo(TrackedHandContactPhase.Began));
        }

        [Test]
        public void ContactAggregatorPrefersHigherRegionButKeepsStableTie()
        {
            var aggregator = new TrackedHandContactAggregator();
            var selected = new System.Collections.Generic.List<TrackedHandContactFact>();
            aggregator.FactChanged += selected.Add;
            var body = new TrackedHandContactFact(
                2, TrackedHandContactPhase.Began, UnityEngine.XR.XRNode.RightHand,
                XRHandJointID.Palm, TrackedHandContactProbe.Palm,
                AvatarContactRegion.Body, Vector3.zero, Vector3.up, 0f, 0f, false, true);
            var face = new TrackedHandContactFact(
                3, TrackedHandContactPhase.Began, UnityEngine.XR.XRNode.RightHand,
                XRHandJointID.IndexTip, TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face, Vector3.one, Vector3.forward, 0f, 0f, true, true);
            aggregator.Accept(body);
            aggregator.Accept(face);
            Assert.That(selected[selected.Count - 1].Region, Is.EqualTo(AvatarContactRegion.Face));
            aggregator.Accept(new TrackedHandContactFact(
                3, TrackedHandContactPhase.Updated, UnityEngine.XR.XRNode.RightHand,
                XRHandJointID.IndexTip, TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face, Vector3.one * 2f, Vector3.forward, 0f, .1f, true, true));
            Assert.That(selected[selected.Count - 1].Region, Is.EqualTo(AvatarContactRegion.Face));
            Assert.That(selected[selected.Count - 1].Point, Is.EqualTo(Vector3.one * 2f));
        }

        [Test]
        public void ContactAggregatorReplacesOlderSequenceForSameProbe()
        {
            var aggregator = new TrackedHandContactAggregator();
            aggregator.Accept(new TrackedHandContactFact(
                1, TrackedHandContactPhase.Began, UnityEngine.XR.XRNode.LeftHand,
                XRHandJointID.IndexTip, TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Head, Vector3.zero, Vector3.up, 0f, 0f, false, true));
            aggregator.Accept(new TrackedHandContactFact(
                2, TrackedHandContactPhase.Began, UnityEngine.XR.XRNode.LeftHand,
                XRHandJointID.IndexTip, TrackedHandContactProbe.PinchTip,
                AvatarContactRegion.Face, Vector3.one, Vector3.forward, 0f, 0f, true, true));

            Assert.That(aggregator.ActiveCount, Is.EqualTo(1));
        }

        [Test]
        public void ContactDiagnosticsAlwaysRecordLifecycleAndThrottleHover()
        {
            Assert.That(QuestTrackedHandVisualizer.ShouldRecordContactDiagnostic(
                TrackedHandContactPhase.Began, 10f, 10f, 1f), Is.True);
            Assert.That(QuestTrackedHandVisualizer.ShouldRecordContactDiagnostic(
                TrackedHandContactPhase.Updated, 10.1f, 10f, 1f), Is.False);
            Assert.That(QuestTrackedHandVisualizer.ShouldRecordContactDiagnostic(
                TrackedHandContactPhase.Updated, 11.01f, 10f, 1f), Is.True);
            Assert.That(QuestTrackedHandVisualizer.ShouldRecordContactDiagnostic(
                TrackedHandContactPhase.Ended, 10.2f, 10f, 1f), Is.True);
        }
    }
}
#endif

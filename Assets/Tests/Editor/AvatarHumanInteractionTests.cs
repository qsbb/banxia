#if UNITY_EDITOR
using NUnit.Framework;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarHumanInteractionTests
    {
        private GameObject avatarObject;
        private GameObject serviceObject;
        private Mesh generatedMesh;

        [TearDown]
        public void TearDown()
        {
            if (serviceObject != null) Object.DestroyImmediate(serviceObject);
            if (avatarObject != null) Object.DestroyImmediate(avatarObject);
            if (generatedMesh != null) Object.DestroyImmediate(generatedMesh);
        }

        [Test]
        public void BindFindsMmdBonesAndSimulationChangesState()
        {
            avatarObject = new GameObject("TestAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            CreateBone("Head", "\u982D");
            CreateBone("RightHand", "\u53F3\u624B\u9996");
            CreateBone("RightUpperArm", "\u53F3\u8155");
            CreateBone("RightLowerArm", "\u53F3\u8098");

            serviceObject = new GameObject("InteractionService");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);

            Assert.IsTrue(interaction.HasHeadBone);
            Assert.IsTrue(interaction.HasHandBones);

            interaction.SimulateInteraction(HumanInteractionKind.Handshake);
            Assert.AreEqual(HumanInteractionKind.Handshake, interaction.CurrentInteraction);

            interaction.SimulateInteraction(HumanInteractionKind.HeadPat);
            Assert.AreEqual(HumanInteractionKind.HeadPat, interaction.CurrentInteraction);

            interaction.SimulateInteraction(HumanInteractionKind.CheekPinch);
            Assert.AreEqual(HumanInteractionKind.CheekPinch, interaction.CurrentInteraction);
        }

        [Test]
        public void SimulatedTouchRunsDirectContactReaction()
        {
            avatarObject = new GameObject();
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);

            serviceObject = new GameObject();
            var touch = serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            touch.Bind(controller);
            interaction.Bind(controller);

            touch.SimulateContactForQa(nameof(SimulatedTouchRunsDirectContactReaction));

            Assert.AreEqual(HumanInteractionKind.Handshake, interaction.CurrentInteraction);
        }

        [Test]
        public void PreviewSimulationDoesNotPretendToBePhysicalContact()
        {
            avatarObject = new GameObject("PreviewAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);

            serviceObject = new GameObject("PreviewInteraction");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            var physicalChanges = 0;
            interaction.PhysicalInteractionChanged += _ => physicalChanges++;

            interaction.SimulateInteraction(HumanInteractionKind.HeadPat);

            Assert.AreEqual(HumanInteractionKind.HeadPat, interaction.CurrentInteraction);
            Assert.AreEqual(0, physicalChanges);
        }

        [Test]
        public void DisableSkipsMorphRendererDestroyedBeforeInteractionService()
        {
            avatarObject = new GameObject("MorphTeardownAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var rendererObject = new GameObject("FaceRenderer");
            rendererObject.transform.SetParent(avatarObject.transform, false);
            var renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();
            var mesh = new Mesh();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.AddBlendShapeFrame(
                "smile",
                100f,
                new[] { Vector3.zero, Vector3.zero, Vector3.zero },
                new[] { Vector3.zero, Vector3.zero, Vector3.zero },
                new[] { Vector3.zero, Vector3.zero, Vector3.zero });
            renderer.sharedMesh = mesh;

            serviceObject = new GameObject("MorphTeardownInteraction");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            Assert.That(interaction.MatchedMorphCount, Is.EqualTo(1));

            Object.DestroyImmediate(renderer);
            Assert.DoesNotThrow(() => interaction.enabled = false);
            Object.DestroyImmediate(mesh);
        }

        [Test]
        public void TouchMorphLayerPreservesAndRestoresFreshUpstreamExpression()
        {
            avatarObject = new GameObject("LayeredMorphAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var rendererObject = new GameObject("LayeredFaceRenderer");
            rendererObject.transform.SetParent(avatarObject.transform, false);
            var renderer = rendererObject.AddComponent<SkinnedMeshRenderer>();
            generatedMesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                triangles = new[] { 0, 1, 2 }
            };
            var delta = new Vector3[3];
            generatedMesh.AddBlendShapeFrame(
                "smile",
                100f,
                delta,
                delta,
                delta);
            renderer.sharedMesh = generatedMesh;

            serviceObject = new GameObject("LayeredMorphInteraction");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            interaction.PlayReaction(HumanInteractionKind.HeadPat, 2f);
            var flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            typeof(AvatarHumanInteraction).GetField("fade", flags)
                ?.SetValue(interaction, 1f);
            var lateUpdate = typeof(AvatarHumanInteraction).GetMethod("LateUpdate", flags);
            var restoreMorphs = typeof(AvatarHumanInteraction).GetMethod("RestoreMorphs", flags);
            Assert.That(lateUpdate, Is.Not.Null);
            Assert.That(restoreMorphs, Is.Not.Null);

            renderer.SetBlendShapeWeight(0, 40f);
            lateUpdate.Invoke(interaction, null);
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(76f).Within(.001f));

            renderer.SetBlendShapeWeight(0, 45f);
            lateUpdate.Invoke(interaction, null);
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(81f).Within(.001f));

            renderer.SetBlendShapeWeight(0, 52f);
            restoreMorphs.Invoke(interaction, null);
            Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(52f).Within(.001f));
        }

        [Test]
        public void TrackedColliderContactRaisesPhysicalSemanticEvent()
        {
            avatarObject = new GameObject("PhysicalAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            CreateBone("Head", "\u982D");

            serviceObject = new GameObject("PhysicalInteraction");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            var physical = HumanInteractionKind.None;
            interaction.PhysicalInteractionChanged += next => physical = next;

            interaction.ReportTrackedHandContact(AvatarContactRegion.Head, false, Vector3.zero);
            typeof(AvatarHumanInteraction)
                .GetField("stateTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(interaction, 1f);
            typeof(AvatarHumanInteraction)
                .GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(interaction, null);

            Assert.AreEqual(HumanInteractionKind.HeadPat, interaction.CurrentInteraction);
            Assert.AreEqual(HumanInteractionKind.HeadPat, physical);
        }

        [Test]
        public void PhysicalContactStoresAvatarYieldInsteadOfMovingTrackedHand()
        {
            avatarObject = new GameObject("YieldAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            CreateBone("Head", "\u982D");

            serviceObject = new GameObject("YieldInteraction");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            interaction.ReportTrackedHandContact(
                AvatarContactRegion.Head,
                false,
                new Vector3(0f, 1f, 0f),
                new Vector3(.2f, 0f, 0f));

            var stored = (Vector3)(typeof(AvatarHumanInteraction)
                .GetField("trackedContactPush", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(interaction) ?? Vector3.zero);
            Assert.That(stored.magnitude, Is.EqualTo(.055f).Within(.0001f));
            Assert.That(Vector3.Dot(stored.normalized, Vector3.right), Is.GreaterThan(.99f));
        }

        [Test]
        public void PenetratingContactStartsCompliantResponseWithoutSemanticHoldDelay()
        {
            avatarObject = new GameObject("PromptPhysicalAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            CreateBone("Head", "\u982D");

            serviceObject = new GameObject("PromptPhysicalInteraction");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            interaction.ReportTrackedHandContact(
                AvatarContactRegion.Head,
                false,
                new Vector3(0f, 1f, 0f),
                Vector3.right * .03f);
            typeof(AvatarHumanInteraction)
                .GetField("stateTime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(interaction, .03f);

            typeof(AvatarHumanInteraction)
                .GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(interaction, null);

            Assert.AreEqual(HumanInteractionKind.HeadPat, interaction.CurrentInteraction);
            var expiresAt = (float)(typeof(AvatarHumanInteraction)
                .GetField("trackedContactUntil", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.GetValue(interaction) ?? 0f);
            Assert.That(expiresAt - Time.unscaledTime, Is.GreaterThan(.18f));
        }

        [Test]
        public void SweptPhysicalContactWithoutPenetrationStillStartsPromptResponse()
        {
            avatarObject = new GameObject("SweptPhysicalAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            CreateBone("Head", "\u982D");

            serviceObject = new GameObject("SweptPhysicalInteraction");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            interaction.ReportTrackedHandContact(
                AvatarContactRegion.Head,
                false,
                new Vector3(0f, 1f, 0f),
                Vector3.zero);
            typeof(AvatarHumanInteraction)
                .GetField("stateTime", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(interaction, .03f);

            typeof(AvatarHumanInteraction)
                .GetMethod("Update", System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(interaction, null);

            Assert.AreEqual(HumanInteractionKind.HeadPat, interaction.CurrentInteraction);
        }

        [Test]
        public void PhysicalFaceComplianceFollowsContactDirection()
        {
            avatarObject = new GameObject("DirectedFaceAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            CreateBone("Head", "\u982D");

            serviceObject = new GameObject("DirectedFaceInteraction");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            var flags = System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic;
            typeof(AvatarHumanInteraction).GetField("trackedContactRegion", flags)
                ?.SetValue(interaction, AvatarContactRegion.Face);
            var pushField = typeof(AvatarHumanInteraction).GetField("smoothedContactPush", flags);
            var offsetMethod = typeof(AvatarHumanInteraction)
                .GetMethod("PhysicalBodyTouchHeadOffset", flags);
            Assert.That(pushField, Is.Not.Null);
            Assert.That(offsetMethod, Is.Not.Null);

            pushField.SetValue(interaction, Vector3.right * .055f);
            var rightPush = (Quaternion)offsetMethod.Invoke(interaction, new object[] { 0f });
            pushField.SetValue(interaction, Vector3.left * .055f);
            var leftPush = (Quaternion)offsetMethod.Invoke(interaction, new object[] { 0f });

            Assert.That(Quaternion.Angle(Quaternion.identity, rightPush), Is.GreaterThan(4f));
            Assert.That(Quaternion.Angle(Quaternion.identity, leftPush), Is.GreaterThan(4f));
            Assert.That(Quaternion.Angle(rightPush, leftPush), Is.GreaterThan(8f));
        }

        [Test]
        public void ReactionTransitionUsesSmoothAsymmetricBlend()
        {
            var velocity = 0f;
            var entered = NaturalMotionTransition.UpdateWeight(
                0f, 1f, ref velocity, .34f, .52f, .08f);
            Assert.That(entered, Is.GreaterThan(0f).And.LessThan(1f));

            velocity = 0f;
            var exited = NaturalMotionTransition.UpdateWeight(
                1f, 0f, ref velocity, .34f, .52f, .08f);
            Assert.That(exited, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.That(entered, Is.GreaterThan(1f - exited));
        }

        [TestCase(0f, 0f)]
        [TestCase(.5f, .5f)]
        [TestCase(1f, 1f)]
        public void NaturalTransitionIsBoundedAndSymmetric(float input, float expected)
        {
            Assert.That(NaturalMotionTransition.Smooth01(input), Is.EqualTo(expected).Within(.0001f));
        }

        [Test]
        public void BodyContactProducesLocalBodyTouchWithoutBecomingHandshake()
        {
            Assert.AreEqual(
                HumanInteractionKind.BodyTouch,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Body, false));
            Assert.AreEqual(
                HumanInteractionKind.BodyTouch,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Face, false));
            Assert.AreEqual(
                HumanInteractionKind.HeadPat,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Head, false));
            Assert.AreEqual(
                HumanInteractionKind.HeadPat,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Hair, true));
            Assert.AreEqual(
                HumanInteractionKind.BodyTouch,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Limb, false));
            Assert.AreEqual(
                HumanInteractionKind.CheekPinch,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Face, true));
        }

        [Test]
        public void HeadPatReactionUsesSubtleHeadAndUpperBodyMotion()
        {
            avatarObject = new GameObject("HeadPatAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            CreateBone("UpperBody", "\u4E0A\u534A\u8EAB");
            CreateBone("Head", "\u982D");
            var upperBody = avatarObject.transform.Find("UpperBody");
            var head = avatarObject.transform.Find("Head");

            serviceObject = new GameObject("HeadPatInteraction");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var interaction = serviceObject.AddComponent<AvatarHumanInteraction>();
            interaction.Bind(controller);
            interaction.PlayReaction(HumanInteractionKind.HeadPat, 2f);
            typeof(AvatarHumanInteraction)
                .GetField("fade", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(interaction, 1f);
            typeof(AvatarHumanInteraction)
                .GetMethod("LateUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(interaction, null);

            Assert.That(Quaternion.Angle(Quaternion.identity, head.localRotation), Is.InRange(3f, 7f));
            Assert.That(Quaternion.Angle(Quaternion.identity, upperBody.localRotation), Is.InRange(.5f, 3f));
        }
        [Test]
        public void TwoBoneIkReachesTargetWithoutChangingSegmentLengths()
        {
            var upperObject = new GameObject("Upper");
            var lowerObject = new GameObject("Lower");
            var handObject = new GameObject("Hand");
            avatarObject = upperObject;
            lowerObject.transform.SetParent(upperObject.transform, false);
            handObject.transform.SetParent(lowerObject.transform, false);
            lowerObject.transform.localPosition = Vector3.right;
            handObject.transform.localPosition = Vector3.right;
            var upperLength = Vector3.Distance(upperObject.transform.position, lowerObject.transform.position);
            var lowerLength = Vector3.Distance(lowerObject.transform.position, handObject.transform.position);
            var target = new Vector3(1.25f, .8f, .2f);

            var solved = AvatarHumanInteraction.SolveTwoBoneIk(
                upperObject.transform,
                lowerObject.transform,
                handObject.transform,
                target,
                new Vector3(0f, -1f, 1f));

            Assert.IsTrue(solved);
            Assert.That(Vector3.Distance(handObject.transform.position, target), Is.LessThan(.01f));
            Assert.That(Vector3.Distance(upperObject.transform.position, lowerObject.transform.position), Is.EqualTo(upperLength).Within(.0001f));
            Assert.That(Vector3.Distance(lowerObject.transform.position, handObject.transform.position), Is.EqualTo(lowerLength).Within(.0001f));
        }

        [Test]
        public void TwoBoneIkClampsUnreachableTargetToConfiguredStretch()
        {
            var upperObject = new GameObject("Upper");
            var lowerObject = new GameObject("Lower");
            var handObject = new GameObject("Hand");
            avatarObject = upperObject;
            lowerObject.transform.SetParent(upperObject.transform, false);
            handObject.transform.SetParent(lowerObject.transform, false);
            lowerObject.transform.localPosition = Vector3.right;
            handObject.transform.localPosition = Vector3.right;

            var solved = AvatarHumanInteraction.SolveTwoBoneIk(
                upperObject.transform,
                lowerObject.transform,
                handObject.transform,
                Vector3.right * 10f,
                Vector3.down,
                .9f);

            Assert.IsTrue(solved);
            Assert.That(Vector3.Distance(upperObject.transform.position, handObject.transform.position), Is.LessThanOrEqualTo(1.801f));
        }
        [Test]
        public void TrackedHandContactMapsToHandshake()
        {
            Assert.AreEqual(
                HumanInteractionKind.Handshake,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Hand, false));
        }

        [Test]
        public void PhysicalContactWinsOverBackendReaction()
        {
            Assert.AreEqual(
                HumanInteractionKind.HeadPat,
                AvatarHumanInteraction.SelectDesiredInteraction(
                    HumanInteractionKind.HeadPat,
                    true,
                    HumanInteractionKind.Handshake,
                    true,
                    true));
        }

        [Test]
        public void BackendReactionIsUsedWhenNoPhysicalContactIsActive()
        {
            Assert.AreEqual(
                HumanInteractionKind.Handshake,
                AvatarHumanInteraction.SelectDesiredInteraction(
                    HumanInteractionKind.None,
                    false,
                    HumanInteractionKind.Handshake,
                    true,
                    true));
        }
        private void CreateBone(string objectName, string mmdName)
        {
            var boneObject = new GameObject(objectName);
            boneObject.transform.SetParent(avatarObject.transform);
            var bone = boneObject.AddComponent<MMDBoneTransform>();
            bone.boneName = mmdName;
        }
    }
}
#endif

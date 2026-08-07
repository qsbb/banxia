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

        [TearDown]
        public void TearDown()
        {
            if (serviceObject != null) Object.DestroyImmediate(serviceObject);
            if (avatarObject != null) Object.DestroyImmediate(avatarObject);
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
        public void BodyContactDoesNotBecomeHandshakeWithoutHandGrip()
        {
            Assert.AreEqual(
                HumanInteractionKind.None,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Body, false));
            Assert.AreEqual(
                HumanInteractionKind.None,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Face, false));
            Assert.AreEqual(
                HumanInteractionKind.HeadPat,
                AvatarHumanInteraction.ClassifyPhysicalContact(AvatarContactRegion.Head, false));
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

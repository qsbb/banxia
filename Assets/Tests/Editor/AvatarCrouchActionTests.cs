#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarCrouchActionTests
    {
        private GameObject owner;

        [TearDown]
        public void TearDown()
        {
            if (owner != null) Object.DestroyImmediate(owner);
        }

        [Test]
        public void CrouchTimelineDescendsHoldsAndReturnsSmoothly()
        {
            var start = AvatarController.SampleCrouch(0f);
            var descending = AvatarController.SampleCrouch(.275f);
            var held = AvatarController.SampleCrouch(.8f);
            var returning = AvatarController.SampleCrouch(1.775f);
            var finished = AvatarController.SampleCrouch(2.1f);

            Assert.That(start.PoseAmount, Is.Zero);
            Assert.That(descending.PoseAmount, Is.InRange(.45f, .55f));
            Assert.That(held.PoseAmount, Is.EqualTo(1f).Within(.001f));
            Assert.That(returning.PoseAmount, Is.InRange(.45f, .55f));
            Assert.That(finished.PoseAmount, Is.Zero.Within(.001f));
            Assert.That(held.KneePitch, Is.GreaterThan(held.HipPitch));
            Assert.That(held.PelvisDrop, Is.GreaterThan(.05f));
        }

        [Test]
        public void CompleteLegRigExecutesCrouchAndPinsFeet()
        {
            owner = new GameObject("Crouch rig");
            CreateBone("LowerBody", "下半身", owner.transform, new Vector3(0f, .9f, 0f));
            CreateBone("UpperBody", "上半身", owner.transform, new Vector3(0f, 1.05f, 0f));
            CreateBone("Head", "頭", owner.transform, new Vector3(0f, 1.55f, 0f));
            var leftUpper = CreateBone("LeftUpperLeg", "左足", owner.transform, new Vector3(-.1f, .9f, 0f));
            var leftLower = CreateBone("LeftLowerLeg", "左ひざ", leftUpper, new Vector3(0f, -.42f, 0f));
            var leftFoot = CreateBone("LeftFoot", "左足首", leftLower, new Vector3(0f, -.42f, .02f));
            var rightUpper = CreateBone("RightUpperLeg", "右足", owner.transform, new Vector3(.1f, .9f, 0f));
            var rightLower = CreateBone("RightLowerLeg", "右ひざ", rightUpper, new Vector3(0f, -.42f, 0f));
            var rightFoot = CreateBone("RightFoot", "右足首", rightLower, new Vector3(0f, -.42f, .02f));
            var controller = owner.AddComponent<AvatarController>();
            controller.Initialize(owner.transform);
            var leftAnchor = leftFoot.position;
            var rightAnchor = rightFoot.position;

            var played = controller.PlayActionFromSource(
                "crouch",
                AvatarActionSource.Backend,
                new AvatarActionParameters { Depth = .65f, HoldMs = 900 },
                new AvatarActionTransition { EnterMs = 550, ExitMs = 650, Easing = "ease_in_out" },
                "explicit_request");
            Evaluate(controller, .8f);

            Assert.That(played, Is.True);
            Assert.That(controller.SupportsCrouch, Is.True);
            Assert.That(controller.CurrentAction, Is.EqualTo("crouch"));
            Assert.That(Vector3.Distance(leftFoot.position, leftAnchor), Is.LessThan(.035f));
            Assert.That(Vector3.Distance(rightFoot.position, rightAnchor), Is.LessThan(.035f));
            Assert.That(Quaternion.Angle(Quaternion.identity, leftLower.localRotation), Is.GreaterThan(10f));
            Assert.That(Quaternion.Angle(Quaternion.identity, rightLower.localRotation), Is.GreaterThan(10f));

            Evaluate(controller, 2.2f);
            Assert.That(controller.CurrentAction, Is.EqualTo("idle"));
        }

        [Test]
        public void IncompleteLegRigRejectsCrouchWithoutChangingAction()
        {
            owner = new GameObject("Incomplete crouch rig");
            CreateBone("LowerBody", "下半身", owner.transform, new Vector3(0f, .9f, 0f));
            var controller = owner.AddComponent<AvatarController>();
            controller.Initialize(owner.transform);

            Assert.That(controller.SupportsCrouch, Is.False);
            Assert.That(controller.PlayActionFromSource("crouch", AvatarActionSource.Backend), Is.False);
            Assert.That(controller.CurrentAction, Is.EqualTo("idle"));
        }

        private static void Evaluate(AvatarController controller, float actionClock)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(AvatarController).GetField("actionClock", flags)?.SetValue(controller, actionClock);
            typeof(AvatarController).GetField("actionTransitionClock", flags)?.SetValue(controller, actionClock);
            typeof(AvatarController).GetMethod("LateUpdate", flags)?.Invoke(controller, null);
        }

        private static Transform CreateBone(
            string objectName,
            string boneName,
            Transform parent,
            Vector3 localPosition)
        {
            var value = new GameObject(objectName);
            value.transform.SetParent(parent, false);
            value.transform.localPosition = localPosition;
            value.AddComponent<MMDBoneTransform>().boneName = boneName;
            return value.transform;
        }
    }
}
#endif

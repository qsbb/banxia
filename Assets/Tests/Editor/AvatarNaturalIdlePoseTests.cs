#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarNaturalIdlePoseTests
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
        public void BindAppliesRelaxedArmOffsetsBeforeInteractionBaseline()
        {
            avatarObject = new GameObject("NaturalPoseAvatar");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var left = CreateBone("LeftUpper", "左腕");
            var right = CreateBone("RightUpper", "右腕");
            var leftBefore = left.localRotation;
            var rightBefore = right.localRotation;

            serviceObject = new GameObject("NaturalPoseService");
            var idlePose = serviceObject.AddComponent<AvatarNaturalIdlePose>();
            idlePose.Bind(controller);

            Assert.That(idlePose.IsBound, Is.True);
            Assert.That(controller.VisualRoot, Is.SameAs(avatarObject.transform));
            Assert.That(Quaternion.Angle(leftBefore, left.localRotation), Is.GreaterThan(20f));
            Assert.That(Quaternion.Angle(rightBefore, right.localRotation), Is.GreaterThan(20f));
        }

        [Test]
        public void TouchDistanceUsesNearestAvatarSurface()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one * 2f);

            Assert.That(AvatarTouchInteraction.IsWithinDistance(bounds, new Vector3(1.09f, 0f, 0f), .1f), Is.True);
            Assert.That(AvatarTouchInteraction.IsWithinDistance(bounds, new Vector3(1.2f, 0f, 0f), .1f), Is.False);
            Assert.That(AvatarTouchInteraction.IsWithinDistance(bounds, Vector3.zero, 0f), Is.True);
        }

        [Test]
        public void BuiltInWaveAnimatesArmWithoutRotatingWholeModel()
        {
            avatarObject = new GameObject("ProceduralActionAvatar");
            avatarObject.transform.localPosition = new Vector3(.2f, .3f, .4f);
            avatarObject.transform.localRotation = Quaternion.Euler(0f, 17f, 0f);
            var upper = CreateBone("RightUpper", "\u53F3\u8155");
            CreateBone("RightLower", "\u53F3\u8098");
            CreateBone("RightHand", "\u53F3\u624B\u9996");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var rootPosition = avatarObject.transform.localPosition;
            var rootRotation = avatarObject.transform.localRotation;
            var armRotation = upper.localRotation;

            controller.PlayAction("wave");
            EvaluateAction(controller, .5f);

            Assert.That(Vector3.Distance(rootPosition, avatarObject.transform.localPosition), Is.LessThan(.0001f));
            Assert.That(Quaternion.Angle(rootRotation, avatarObject.transform.localRotation), Is.LessThan(.01f));
            Assert.That(Quaternion.Angle(armRotation, upper.localRotation), Is.GreaterThan(20f));

            controller.PlayAction("idle");
            Assert.That(Quaternion.Angle(armRotation, upper.localRotation), Is.LessThan(.01f));
        }

        [Test]
        public void BuiltInWaveIsNotResetByIdleHumanInteractionArbiter()
        {
            avatarObject = new GameObject("WaveInteractionAvatar");
            var upper = CreateBone("RightUpper", "右腕");
            CreateBone("RightLower", "右肘");
            CreateBone("RightHand", "右手首");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);

            serviceObject = new GameObject("IdleInteractionService");
            serviceObject.AddComponent<AvatarTouchInteraction>();
            var human = serviceObject.AddComponent<AvatarHumanInteraction>();
            human.Bind(controller);

            var baseRotation = upper.localRotation;
            controller.PlayAction("wave");
            EvaluateAction(controller, .5f);
            typeof(AvatarHumanInteraction)
                .GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(human, null);

            Assert.That(Quaternion.Angle(baseRotation, upper.localRotation), Is.GreaterThan(20f));
        }

        [Test]
        public void BuiltInBowAnimatesUpperBodyWithoutTiltingWholeModel()
        {
            avatarObject = new GameObject("ProceduralBowAvatar");
            avatarObject.transform.localRotation = Quaternion.Euler(0f, -23f, 0f);
            var upperBody = CreateBone("UpperBody", "\u4E0A\u534A\u8EAB");
            CreateBone("Head", "\u982D");
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);
            var rootRotation = avatarObject.transform.localRotation;
            var upperBodyRotation = upperBody.localRotation;

            controller.PlayAction("bow");
            EvaluateAction(controller, .8f);

            Assert.That(Quaternion.Angle(rootRotation, avatarObject.transform.localRotation), Is.LessThan(.01f));
            Assert.That(Quaternion.Angle(upperBodyRotation, upperBody.localRotation), Is.GreaterThan(10f));
        }

        private static void EvaluateAction(AvatarController controller, float clock)
        {
            var type = typeof(AvatarController);
            type.GetField("actionClock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(controller, clock);
            type.GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(controller, null);
        }

        private Transform CreateBone(string objectName, string mmdName)
        {
            var boneObject = new GameObject(objectName);
            boneObject.transform.SetParent(avatarObject.transform);
            var bone = boneObject.AddComponent<MMDBoneTransform>();
            bone.boneName = mmdName;
            return boneObject.transform;
        }
    }
}
#endif

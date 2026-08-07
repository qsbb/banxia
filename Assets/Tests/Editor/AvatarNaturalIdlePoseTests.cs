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
        public void FormalIdleKeepsBothForearmsOutsideTheTorso()
        {
            avatarObject = new GameObject("DirectionalIdleAvatar");
            var leftUpper = CreateBone("LeftUpper", "\u5de6\u8155", avatarObject.transform, new Vector3(-.16f, 1.4f, 0f));
            var leftLower = CreateBone("LeftLower", "\u5de6\u3072\u3058", leftUpper, new Vector3(-.28f, 0f, 0f));
            var leftHand = CreateBone("LeftHand", "\u5de6\u624b\u9996", leftLower, new Vector3(-.25f, 0f, 0f));
            var rightUpper = CreateBone("RightUpper", "\u53f3\u8155", avatarObject.transform, new Vector3(.16f, 1.4f, 0f));
            var rightLower = CreateBone("RightLower", "\u53f3\u3072\u3058", rightUpper, new Vector3(.28f, 0f, 0f));
            var rightHand = CreateBone("RightHand", "\u53f3\u624b\u9996", rightLower, new Vector3(.25f, 0f, 0f));
            var controller = avatarObject.AddComponent<AvatarController>();
            controller.Initialize(avatarObject.transform);

            serviceObject = new GameObject("DirectionalIdleService");
            var idlePose = serviceObject.AddComponent<AvatarNaturalIdlePose>();
            idlePose.SetPreset(AvatarIdlePreset.Formal);
            idlePose.Bind(controller);

            var leftForearm = (leftHand.position - leftLower.position).normalized;
            var rightForearm = (rightHand.position - rightLower.position).normalized;
            Assert.That(leftForearm.x, Is.LessThan(0f));
            Assert.That(rightForearm.x, Is.GreaterThan(0f));
            Assert.That(leftForearm.y, Is.LessThan(-.95f));
            Assert.That(rightForearm.y, Is.LessThan(-.95f));
            Assert.That(leftHand.position.x, Is.LessThan(leftLower.position.x));
            Assert.That(rightHand.position.x, Is.GreaterThan(rightLower.position.x));
        }
        [Test]
        public void DirectionalIdleCapsLargeBoneAxisCorrection()
        {
            var root = new GameObject("ArmCorrectionRoot");
            var bone = new GameObject("ArmCorrectionBone");
            var child = new GameObject("ArmCorrectionChild");
            try
            {
                bone.transform.SetParent(root.transform, false);
                child.transform.SetParent(bone.transform, false);
                child.transform.localPosition = Vector3.right;
                var method = typeof(AvatarNaturalIdlePose).GetMethod(
                    "CalculateAlignedLocalRotation",
                    BindingFlags.Static | BindingFlags.NonPublic);

                Assert.That(method, Is.Not.Null);
                var result = (Quaternion)method.Invoke(null, new object[]
                {
                    bone.transform,
                    child.transform,
                    Vector3.down,
                    Quaternion.identity,
                    35f
                });

                Assert.That(Quaternion.Angle(Quaternion.identity, result), Is.EqualTo(35f).Within(.01f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
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
            EvaluateAction(controller, 1f);
            Assert.That(Quaternion.Angle(armRotation, upper.localRotation), Is.LessThan(.01f));
        }

        [Test]
        public void NaturalNodUsesAStrongThenSoftAcknowledgement()
        {
            Assert.That(AvatarController.NaturalNodPitch(0f), Is.EqualTo(0f).Within(.001f));
            Assert.That(AvatarController.NaturalNodPitch(1f), Is.EqualTo(0f).Within(.001f));
            var primary = AvatarController.NaturalNodPitch(.33f);
            var secondary = AvatarController.NaturalNodPitch(.65f);
            Assert.That(primary, Is.GreaterThan(7f));
            Assert.That(secondary, Is.GreaterThan(3f));
            Assert.That(primary, Is.GreaterThan(secondary));
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
            type.GetField("actionTransitionClock", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(controller, clock);
            type.GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(controller, null);
        }

        private Transform CreateBone(
            string objectName,
            string mmdName,
            Transform parent,
            Vector3 localPosition)
        {
            var boneObject = new GameObject(objectName);
            boneObject.transform.SetParent(parent, false);
            boneObject.transform.localPosition = localPosition;
            var bone = boneObject.AddComponent<MMDBoneTransform>();
            bone.boneName = mmdName;
            return boneObject.transform;
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

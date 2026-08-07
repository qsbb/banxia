using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestInteractionUxTests
    {
        [TestCase(0f, 0f)]
        [TestCase(.2f, 0f)]
        [TestCase(.6f, .5f)]
        [TestCase(1f, 1f)]
        [TestCase(-.6f, -.5f)]
        public void StickDeadZoneIsRemappedContinuously(float input, float expected)
        {
            Assert.That(QuestXrInputUtility.RemapAxisOutsideDeadZone(input, .2f), Is.EqualTo(expected).Within(.0001f));
        }

        [Test]
        public void SmoothTurnDeltaIsLinearInInputAndTime()
        {
            var halfInputOneSecond = QuestVrLocomotion.CalculateTurnDelta(.6f, .2f, 90f, 1f);
            var fullInputHalfSecond = QuestVrLocomotion.CalculateTurnDelta(1f, .2f, 90f, .5f);

            Assert.That(halfInputOneSecond, Is.EqualTo(45f).Within(.0001f));
            Assert.That(fullInputHalfSecond, Is.EqualTo(45f).Within(.0001f));
        }

        [Test]
        public void TurnInputSmoothingConvergesWithoutSnapping()
        {
            var first = QuestVrLocomotion.SmoothTurnInput(0f, 1f, 1f / 90f, .055f);
            var second = QuestVrLocomotion.SmoothTurnInput(first, 1f, 1f / 90f, .055f);

            Assert.That(first, Is.GreaterThan(0f).And.LessThan(1f));
            Assert.That(second, Is.GreaterThan(first).And.LessThan(1f));
            Assert.That(QuestVrLocomotion.SmoothTurnInput(.5f, 0f, 1f / 90f, 0f), Is.Zero);
        }

        [TestCase(40f, 58f, false)]
        [TestCase(58f, 58f, false)]
        [TestCase(-72f, 58f, true)]
        public void BodyTurnUsesAStableYawThreshold(float yaw, float threshold, bool expected)
        {
            Assert.That(AvatarPresence.ShouldTurnBody(yaw, threshold), Is.EqualTo(expected));
        }

        [Test]
        public void BodyTurnUsesBoundedStepsAndLeavesHeadResidual()
        {
            Assert.That(AvatarPresence.CalculateTurnStep(40f, 58f, 22f, 35f, 58f), Is.Zero);
            Assert.That(AvatarPresence.CalculateTurnStep(90f, 58f, 22f, 35f, 58f), Is.EqualTo(58f).Within(.0001f));
            Assert.That(AvatarPresence.CalculateTurnStep(-180f, 58f, 22f, 35f, 58f), Is.EqualTo(-58f).Within(.0001f));
            Assert.That(AvatarPresence.CalculateTurnStep(60f, 58f, 22f, 35f, 58f), Is.EqualTo(38f).Within(.0001f));
        }

        [Test]
        public void BodyTurnProgressIsSmoothAndBounded()
        {
            Assert.That(AvatarPresence.SmoothTurnProgress(0f), Is.Zero);
            Assert.That(AvatarPresence.SmoothTurnProgress(.5f), Is.EqualTo(.5f).Within(.0001f));
            Assert.That(AvatarPresence.SmoothTurnProgress(1f), Is.EqualTo(1f).Within(.0001f));
            Assert.That(AvatarPresence.SmoothTurnProgress(2f), Is.EqualTo(1f).Within(.0001f));
        }

        [Test]
        public void MenuPoseAppearsInFrontOfHeadAtStableHeight()
        {
            var head = new Pose(new Vector3(1f, 1.6f, 2f), Quaternion.Euler(0f, 90f, 0f));
            var menu = CompanionWorldMenu.CalculateMenuPose(head, .9f, -.12f);

            Assert.That(Vector3.Distance(menu.position, new Vector3(1.9f, 1.48f, 2f)), Is.LessThan(.0001f));
            Assert.That(Vector3.Angle(menu.rotation * Vector3.forward, Vector3.right), Is.LessThan(.001f));
        }

        [Test]
        public void AvatarPointerSelectsSemanticInteractionByRegionAndGrip()
        {
            var bounds = new Bounds(new Vector3(0f, 1f, 0f), new Vector3(1f, 2f, 1f));

            Assert.That(
                QuestAvatarRayInteraction.ClassifyInteraction(bounds, new Vector3(0f, 1.7f, 0f), false),
                Is.EqualTo(HumanInteractionKind.HeadPat));
            Assert.That(
                QuestAvatarRayInteraction.ClassifyInteraction(bounds, new Vector3(0f, 1.7f, 0f), true),
                Is.EqualTo(HumanInteractionKind.CheekPinch));
            Assert.That(
                QuestAvatarRayInteraction.ClassifyInteraction(bounds, new Vector3(0f, .7f, 0f), false),
                Is.EqualTo(HumanInteractionKind.Handshake));
        }
        [Test]
        public void WorldMenuModalLayersBlockUnderlyingButtonColliders()
        {
            var cameraObject = new GameObject("Modal Test Camera");
            var menuObject = new GameObject("Modal Test Menu");
            try
            {
                cameraObject.tag = "MainCamera";
                cameraObject.AddComponent<Camera>();
                var menu = menuObject.AddComponent<CompanionWorldMenu>();
                menu.Initialize(null);
                menu.ShowInFront();

                InvokeMenuMethod(menu, "ShowActionPanel");
                InvokeMenuMethod(menu, "ShowActionList");
                var root = (GameObject)typeof(CompanionWorldMenu).GetField("menuRoot", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(menu);
                var actionLayer = root.transform.Find("Action Presets Layer");
                var actionList = actionLayer.Find("Added Actions List");
                var underlying = actionLayer
                    .GetComponentsInChildren<BoxCollider>(true)
                    .Where(value => value.transform.parent == actionLayer)
                    .ToArray();

                Assert.That(actionList.gameObject.activeSelf, Is.True);
                Assert.That(underlying.Length, Is.GreaterThan(0));
                Assert.That(underlying.All(value => !value.enabled), Is.True);
                Assert.That(actionList.GetComponentsInChildren<BoxCollider>(true).Any(value => value.enabled), Is.True);

                InvokeMenuMethod(menu, "HideActionList");
                Assert.That(underlying.All(value => value.enabled), Is.True);

                InvokeMenuMethod(menu, "ShowPairingPanel");
                InvokeMenuMethod(menu, "OpenPairingKeyboard");
                var pairingLayer = root.transform.Find("Backend Pairing Layer");
                var keyboardLayer = pairingLayer.Find("Pairing Server Keyboard");
                var pairingButtons = pairingLayer
                    .GetComponentsInChildren<BoxCollider>(true)
                    .Where(value => value.transform.parent == pairingLayer)
                    .ToArray();

                Assert.That(keyboardLayer.gameObject.activeSelf, Is.True);
                Assert.That(pairingButtons.All(value => !value.enabled), Is.True);
                Assert.That(keyboardLayer.GetComponentsInChildren<BoxCollider>(true).All(value => value.enabled), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(menuObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        private static void InvokeMenuMethod(CompanionWorldMenu menu, string name)
        {
            typeof(CompanionWorldMenu)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(menu, null);
        }
        [Test]
        public void WorldMenuBuildsMainAndActionPresetButtons()
        {
            var cameraObject = new GameObject("Main Camera");
            var ownerObject = new GameObject("Owner");
            try
            {
                cameraObject.tag = "MainCamera";
                cameraObject.transform.position = new Vector3(0f, 1.6f, 0f);
                cameraObject.AddComponent<Camera>();
                var owner = ownerObject.AddComponent<QuestMmdPlayerBootstrap>();
                var menu = ownerObject.AddComponent<CompanionWorldMenu>();

                menu.Initialize(owner);
                menu.ShowInFront();

                var root = GameObject.Find("Companion World Menu");
                Assert.That(root, Is.Not.Null);
                Assert.That(root.GetComponentsInChildren<BoxCollider>(true).Length, Is.EqualTo(46));
                Assert.That(root.transform.Find("Appearance Layer/\u626b\u63cf\u623f\u95f4"), Is.Not.Null);
                Assert.That(root.transform.Find("Main Menu Layer/绑定后端"), Is.Not.Null);
                Assert.That(root.transform.Find("Main Menu Layer/调试"), Is.Not.Null);
                Assert.That(root.transform.Find("Action Presets Layer/刷新外部动作"), Is.Not.Null);
                Assert.That(root.transform.Find("Action Presets Layer/导入文件"), Is.Not.Null);
                Assert.That(root.transform.Find("Action Presets Layer/播放选中"), Is.Not.Null);
                Assert.That(root.transform.Find("Action Presets Layer/点头"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/描边开关"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/站立校准"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/画质"), Is.Not.Null);
                Assert.That(root.transform.Find("Quality Layer/清晰"), Is.Not.Null);
            }
            finally
            {
                Object.DestroyImmediate(ownerObject);
                Object.DestroyImmediate(cameraObject);
            }
        }
    }
}

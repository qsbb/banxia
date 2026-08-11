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
        public void AvatarPointerCannotSynthesizeSemanticTouch()
        {
            Assert.That(QuestAvatarRayInteraction.CanSynthesizeSemanticTouch, Is.False);
        }

        [Test]
        public void MouthEnvelopeUsesSeparateSmoothAttackAndRelease()
        {
            var opened = AvatarConversationPresenter.SmoothMouthAmount(0f, 1f, .02f, 10f, 4f);
            var released = AvatarConversationPresenter.SmoothMouthAmount(opened, 0f, .02f, 10f, 4f);

            Assert.That(opened, Is.EqualTo(.2f).Within(.0001f));
            Assert.That(released, Is.EqualTo(.12f).Within(.0001f));
            Assert.That(released, Is.GreaterThan(0f));
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
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.ActionList));
                Assert.That(underlying.Length, Is.GreaterThan(0));
                Assert.That(underlying.All(value => !value.enabled), Is.True);
                Assert.That(actionList.Find("List Background").GetComponent<BoxCollider>(), Is.Not.Null);
                Assert.That(actionList.GetComponentsInChildren<BoxCollider>(true).Any(value => value.enabled), Is.True);

                InvokeMenuMethod(menu, "HideActionList");
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.Actions));
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
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.PairingKeyboard));
                Assert.That(pairingButtons.All(value => !value.enabled), Is.True);
                Assert.That(keyboardLayer.Find("Keyboard Background").GetComponent<BoxCollider>(), Is.Not.Null);
                Assert.That(keyboardLayer.GetComponentsInChildren<BoxCollider>(true).All(value => value.enabled), Is.True);

                var pairingTarget = FindButtonTarget(pairingButtons.First());
                var keyboardTarget = FindButtonTarget(
                    FindFirstButtonCollider(keyboardLayer));
                InvokeTargetMethod(pairingTarget, "SetInteractive", true);
                Assert.That(
                    InvokeMenuMethod(menu, "IsTargetInFocusedLayer", pairingTarget),
                    Is.False);
                Assert.That(
                    InvokeMenuMethod(menu, "IsTargetInFocusedLayer", keyboardTarget),
                    Is.True);
            }
            finally
            {
                Object.DestroyImmediate(menuObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void WorldMenuModalFocusRejectsUnderlyingTargetsEvenIfTheirColliderIsReenabled()
        {
            var cameraObject = new GameObject("Modal Focus Test Camera");
            var menuObject = new GameObject("Modal Focus Test Menu");
            try
            {
                cameraObject.tag = "MainCamera";
                cameraObject.AddComponent<Camera>();
                var menu = menuObject.AddComponent<CompanionWorldMenu>();
                menu.Initialize(null);
                menu.ShowInFront();

                InvokeMenuMethod(menu, "ShowActionPanel");
                InvokeMenuMethod(menu, "ShowActionList");
                var root = GetMenuField<GameObject>(menu, "menuRoot");
                var actionLayer = root.transform.Find("Action Presets Layer");
                var actionList = actionLayer.Find("Added Actions List");
                var underlyingTarget = FindButtonTarget(
                    actionLayer
                        .GetComponentsInChildren<BoxCollider>(true)
                        .First(value => value.transform.parent == actionLayer));
                var modalTarget = FindButtonTarget(
                    FindFirstButtonCollider(actionList));

                InvokeTargetMethod(underlyingTarget, "SetInteractive", true);

                Assert.That(
                    InvokeMenuMethod(menu, "IsTargetInFocusedLayer", underlyingTarget),
                    Is.False);
                Assert.That(
                    InvokeMenuMethod(menu, "IsTargetInFocusedLayer", modalTarget),
                    Is.True);
            }
            finally
            {
                Object.DestroyImmediate(menuObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void WorldMenuLayerTransitionRequiresReleaseAndAllowsOnlyOnePressPerFrame()
        {
            var cameraObject = new GameObject("Input Gate Test Camera");
            var menuObject = new GameObject("Input Gate Test Menu");
            try
            {
                cameraObject.tag = "MainCamera";
                cameraObject.AddComponent<Camera>();
                var menu = menuObject.AddComponent<CompanionWorldMenu>();
                menu.Initialize(null);
                menu.ShowInFront();
                InvokeMenuMethod(menu, "ShowActionPanel");
                InvokeMenuMethod(menu, "ShowActionList");

                var root = GetMenuField<GameObject>(menu, "menuRoot");
                var actionList = root.transform.Find("Action Presets Layer/Added Actions List");
                var modalTarget = FindButtonTarget(
                    FindFirstButtonCollider(actionList));

                Assert.That(GetMenuField<bool>(menu, "pointerReleaseRequired"), Is.True);
                Assert.That(
                    InvokeMenuMethod(menu, "CanDispatchPointerPress", modalTarget, true),
                    Is.False,
                    "A press held across a modal transition must not activate the new layer.");

                InvokeMenuMethod(menu, "ReleasePointerInputGateIfReady");

                Assert.That(GetMenuField<bool>(menu, "pointerReleaseRequired"), Is.False);
                Assert.That(
                    InvokeMenuMethod(menu, "CanDispatchPointerPress", modalTarget, true),
                    Is.True);
                Assert.That(
                    InvokeMenuMethod(menu, "CanDispatchPointerPress", modalTarget, true),
                    Is.False,
                    "The other pointer must not dispatch a second button in the same frame.");
            }
            finally
            {
                Object.DestroyImmediate(menuObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        private static object InvokeMenuMethod(CompanionWorldMenu menu, string name, params object[] arguments)
        {
            return typeof(CompanionWorldMenu)
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(menu, arguments);
        }

        private static T GetMenuField<T>(CompanionWorldMenu menu, string name)
        {
            return (T)typeof(CompanionWorldMenu)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(menu);
        }

        private static Component FindButtonTarget(BoxCollider collider)
        {
            return collider
                .GetComponents<Component>()
                .Single(value => value.GetType().Name == "CompanionMenuButtonTarget");
        }

        private static BoxCollider FindFirstButtonCollider(Transform root)
        {
            return root
                .GetComponentsInChildren<BoxCollider>(true)
                .First(value => value.GetComponents<Component>()
                    .Any(component => component.GetType().Name == "CompanionMenuButtonTarget"));
        }

        private static object InvokeTargetMethod(Component target, string name, params object[] arguments)
        {
            return target
                .GetType()
                .GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.Invoke(target, arguments);
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
                Assert.That(root.GetComponentsInChildren<BoxCollider>(true).Length, Is.GreaterThanOrEqualTo(55));
                Assert.That(root.transform.Find("Appearance Layer/\u626b\u63cf\u623f\u95f4"), Is.Not.Null);
                Assert.That(root.transform.Find("Main Menu Layer/绑定后端"), Is.Not.Null);
                Assert.That(root.transform.Find("Main Menu Layer/诊断"), Is.Not.Null);
                Assert.That(root.transform.Find("Main Menu Layer/语音"), Is.Not.Null);
                Assert.That(root.transform.Find("Voice Layer/常开监听"), Is.Not.Null);
                Assert.That(root.transform.Find("Voice Layer/开始说话"), Is.Not.Null);
                Assert.That(root.transform.Find("Voice Layer/文字对话"), Is.Not.Null);
                Assert.That(root.transform.Find("Text Conversation Layer/打开键盘"), Is.Not.Null);
                Assert.That(root.transform.Find("Text Conversation Layer/发送"), Is.Not.Null);
                Assert.That(root.transform.Find("Text Conversation Layer/链路测试"), Is.Not.Null);
                Assert.That(root.transform.Find("Debug Layer/清空记录"), Is.Not.Null);
                Assert.That(root.transform.Find("Action Presets Layer/刷新外部动作"), Is.Not.Null);
                Assert.That(root.transform.Find("Action Presets Layer/导入文件"), Is.Not.Null);
                Assert.That(root.transform.Find("Action Presets Layer/播放选中"), Is.Not.Null);
                Assert.That(root.transform.Find("Action Presets Layer/点头"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/描边开关"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/站立校准"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/画质"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/表情：模型默认"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/角色模型"), Is.Not.Null);
                Assert.That(root.transform.Find("Model Library Layer/导入模型"), Is.Not.Null);
                Assert.That(root.transform.Find("Model Library Layer/加载选中"), Is.Not.Null);
                Assert.That(root.transform.Find("Quality Layer/清晰"), Is.Not.Null);

                InvokeTargetMethod(menu, "ShowDebugPanel");
                var mainLayer = root.transform.Find("Main Menu Layer");
                var debugLayer = root.transform.Find("Debug Layer");
                Assert.That(mainLayer.gameObject.activeSelf, Is.True);
                Assert.That(debugLayer.gameObject.activeSelf, Is.True);
                Assert.That(debugLayer.localPosition.x, Is.LessThan(-360f));

                InvokeTargetMethod(menu, "ShowActionPanel");
                Assert.That(debugLayer.gameObject.activeSelf, Is.True);
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.Actions));
                var debugTarget = FindButtonTarget(debugLayer.Find("清空记录").GetComponent<BoxCollider>());
                Assert.That(InvokeMenuMethod(menu, "IsTargetInFocusedLayer", debugTarget), Is.True);

                InvokeTargetMethod(menu, "ShowModelPanel");
                Assert.That(debugLayer.gameObject.activeSelf, Is.True);
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.Models));

                InvokeTargetMethod(menu, "ToggleDebugMode");
                Assert.That(debugLayer.gameObject.activeSelf, Is.False);
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.Models));

                InvokeTargetMethod(menu, "ShowDebugPanel");
                Assert.That(debugLayer.gameObject.activeSelf, Is.True);
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.Models),
                    "Opening diagnostics must not replace the current primary page.");

                InvokeTargetMethod(menu, "ShowTextInputPanel");
                var textInputLayer = root.transform.Find("Text Conversation Layer");
                Assert.That(textInputLayer.gameObject.activeSelf, Is.True);
                Assert.That(debugLayer.gameObject.activeSelf, Is.True);
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.TextInput));
                Assert.That(mainLayer.gameObject.activeSelf, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(ownerObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void ConversationInputIsBoundedAndSingleLine()
        {
            Assert.That(
                CompanionWorldMenu.NormalizeConversationInput("  你好\r\n世界  "),
                Is.EqualTo("你好  世界"));
            Assert.That(
                CompanionWorldMenu.NormalizeConversationInput(new string('a', 700)).Length,
                Is.EqualTo(512));
            Assert.That(CompanionWorldMenu.NormalizeConversationInput(null), Is.Empty);
        }

        [Test]
        public void QaWaitIgnoresLargeResumeFrameGap()
        {
            Assert.That(CompanionWorldMenu.ActiveWaitDelta(-1f), Is.Zero);
            Assert.That(CompanionWorldMenu.ActiveWaitDelta(.016f), Is.EqualTo(.016f));
            Assert.That(CompanionWorldMenu.ActiveWaitDelta(120f), Is.EqualTo(.1f));
        }
    }
}

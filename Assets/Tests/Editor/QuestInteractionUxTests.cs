using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestInteractionUxTests
    {
        [TestCase(-10, 5, 0)]
        [TestCase(3, 5, 3)]
        [TestCase(99, 5, 4)]
        [TestCase(0, 0, -1)]
        public void QaScenarioIndicesAreBounded(int requested, int count, int expected)
        {
            Assert.That(CompanionWorldMenu.ClampQaIndex(requested, count), Is.EqualTo(expected));
        }

        [Test]
        public void DebugAutoScrollPersistsAndManualPagingDisablesIt()
        {
            const string preferenceKey = "Banxia.Debug.AutoScroll";
            var hadPreference = PlayerPrefs.HasKey(preferenceKey);
            var previousPreference = PlayerPrefs.GetInt(preferenceKey, 1);
            var menuObject = new GameObject("Debug auto-scroll persistence test");
            try
            {
                var menu = menuObject.AddComponent<CompanionWorldMenu>();
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var autoScroll = typeof(CompanionWorldMenu).GetField(
                    "debugAutoScroll",
                    flags);
                var timelineOffset = typeof(CompanionWorldMenu).GetField(
                    "debugTimelineOffset",
                    flags);
                var toggle = typeof(CompanionWorldMenu).GetMethod(
                    "ToggleDebugAutoScroll",
                    flags);
                var scroll = typeof(CompanionWorldMenu).GetMethod(
                    "ScrollDebugTimeline",
                    flags);
                Assert.That(autoScroll, Is.Not.Null);
                Assert.That(timelineOffset, Is.Not.Null);
                Assert.That(toggle, Is.Not.Null);
                Assert.That(scroll, Is.Not.Null);

                autoScroll.SetValue(menu, false);
                timelineOffset.SetValue(menu, 4);
                toggle.Invoke(menu, null);
                Assert.That((bool)autoScroll.GetValue(menu), Is.True);
                Assert.That((int)timelineOffset.GetValue(menu), Is.Zero);
                Assert.That(PlayerPrefs.GetInt(preferenceKey), Is.EqualTo(1));

                scroll.Invoke(menu, new object[] { 1 });
                Assert.That((bool)autoScroll.GetValue(menu), Is.False);
                Assert.That(PlayerPrefs.GetInt(preferenceKey), Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(menuObject);
                if (hadPreference)
                {
                    PlayerPrefs.SetInt(preferenceKey, previousPreference);
                }
                else
                {
                    PlayerPrefs.DeleteKey(preferenceKey);
                }
                PlayerPrefs.Save();
            }
        }

        [TestCase(-1, 10, 30, 10)]
        [TestCase(0, 10, 30, 10)]
        [TestCase(12, 10, 30, 12)]
        [TestCase(999, 10, 30, 30)]
        [TestCase(999, 30, 999, 120)]
        public void PerformanceQaDurationsAreStrictlyBounded(
            int requested,
            int fallback,
            int maximum,
            int expected)
        {
            Assert.That(
                CompanionWorldMenu.NormalizePerformanceQaDuration(
                    requested,
                    fallback,
                    maximum),
                Is.EqualTo(expected));
        }

        [Test]
        public void PerformanceQaOutputContainsEveryRequiredMetric()
        {
            var line = CompanionWorldMenu.FormatPerformanceQaResult(
                2,
                10,
                30,
                1800,
                13f,
                14f,
                20f,
                "balanced",
                "on",
                "off",
                null);

            Assert.That(line, Does.StartWith("[BanxiaQA] performance_result status=completed"));
            foreach (var field in new[]
            {
                "model_index=", "warmup_s=", "sample_s=", "sampled_frames=",
                "physics_profile=balanced", "hand_contact=on", "outline=off",
                "fps_5s=", "fps_30s=", "frame_p50_ms=", "frame_p95_ms=",
                "frame_max_ms=", "xr_cpu_ms=", "xr_gpu_ms=", "xr_cpu_util=",
                "xr_gpu_util=", "compositor_dropped=", "physics_drop_s=",
                "bullet_ms=", "bone_ik_ms=", "flush_ms=", "sdef_ms=",
                "hand_contact_ms=", "outline_submit_ms=", "outline_submeshes=",
                "xr_cpu_p50_ms=", "xr_cpu_p95_ms=", "xr_gpu_p50_ms=",
                "xr_gpu_p95_ms=", "mmd_sampling_p50_ms=", "mmd_sampling_p95_ms=",
                "mmd_bone_ik_p50_ms=", "mmd_bone_ik_p95_ms=",
                "mmd_physics_p50_ms=", "mmd_physics_p95_ms=",
                "mmd_flush_p50_ms=", "mmd_flush_p95_ms=",
                "mmd_sdef_p50_ms=", "mmd_sdef_p95_ms=",
                "hand_contact_p50_ms=", "hand_contact_p95_ms=",
                "outline_submit_p50_ms=", "outline_submit_p95_ms="
            })
            {
                Assert.That(line, Does.Contain(field), field);
            }
        }

        [Test]
        public void PerformanceQaRunnerIsNotAPublicUiCommand()
        {
            var privateRunner = typeof(CompanionWorldMenu).GetMethod(
                "RunQaPerformanceScenarioWhenReady",
                BindingFlags.Instance | BindingFlags.NonPublic);
            var publicRunner = typeof(CompanionWorldMenu).GetMethod(
                "RunQaPerformanceScenarioWhenReady",
                BindingFlags.Instance | BindingFlags.Public);

            Assert.That(privateRunner, Is.Not.Null);
            Assert.That(publicRunner, Is.Null);
        }

        [Test]
        public void PerformanceQaCanRestoreModelSelectionPreferences()
        {
            const string absoluteKey = "Banxia.RuntimeMmdModel.SelectedPath";
            const string relativeKey = "Banxia.RuntimeMmdModel.SelectedRelativePath";
            var hadAbsolute = PlayerPrefs.HasKey(absoluteKey);
            var hadRelative = PlayerPrefs.HasKey(relativeKey);
            var previousAbsolute = PlayerPrefs.GetString(absoluteKey, string.Empty);
            var previousRelative = PlayerPrefs.GetString(relativeKey, string.Empty);
            var owner = new GameObject("Performance QA Model Preferences");
            try
            {
                PlayerPrefs.SetString(absoluteKey, "temporary-model.pmx");
                PlayerPrefs.SetString(relativeKey, "Imported/temporary/model.pmx");
                var loader = owner.AddComponent<RuntimeMmdModelLoader>();
                var restorePreferences = typeof(RuntimeMmdModelLoader).GetMethod(
                    "RestoreSelectedModelPreferencesForQa",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(restorePreferences, Is.Not.Null);

                restorePreferences.Invoke(
                    loader,
                    new object[]
                    {
                        "original-model.pmx",
                        "Imported/original/model.pmx"
                    });

                Assert.That(
                    PlayerPrefs.GetString(absoluteKey),
                    Is.EqualTo("original-model.pmx"));
                Assert.That(
                    PlayerPrefs.GetString(relativeKey),
                    Is.EqualTo("Imported/original/model.pmx"));
                restorePreferences.Invoke(
                    loader,
                    new object[] { string.Empty, string.Empty });
                Assert.That(PlayerPrefs.HasKey(absoluteKey), Is.False);
                Assert.That(PlayerPrefs.HasKey(relativeKey), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(owner);
                if (hadAbsolute)
                {
                    PlayerPrefs.SetString(absoluteKey, previousAbsolute);
                }
                else
                {
                    PlayerPrefs.DeleteKey(absoluteKey);
                }
                if (hadRelative)
                {
                    PlayerPrefs.SetString(relativeKey, previousRelative);
                }
                else
                {
                    PlayerPrefs.DeleteKey(relativeKey);
                }
                PlayerPrefs.Save();
            }
        }

        [TestCase("performance", MmdPhysicsPreset.Performance, true)]
        [TestCase("balanced", MmdPhysicsPreset.Balanced, true)]
        [TestCase("precise", MmdPhysicsPreset.Fine, true)]
        [TestCase("fine", MmdPhysicsPreset.Balanced, false)]
        [TestCase("Performance", MmdPhysicsPreset.Balanced, false)]
        [TestCase(" balanced", MmdPhysicsPreset.Balanced, false)]
        [TestCase("", MmdPhysicsPreset.Balanced, false)]
        public void PerformanceQaPhysicsProfileUsesAStrictEnumeration(
            string value,
            MmdPhysicsPreset expected,
            bool valid)
        {
            Assert.That(
                CompanionWorldMenu.TryParsePerformanceQaPhysicsProfile(
                    value,
                    out var parsed),
                Is.EqualTo(valid));
            Assert.That(parsed, Is.EqualTo(expected));
        }

        [TestCase("on", true, true)]
        [TestCase("off", false, true)]
        [TestCase("ON", false, false)]
        [TestCase("true", false, false)]
        [TestCase("", false, false)]
        public void PerformanceQaToggleUsesAStrictEnumeration(
            string value,
            bool expected,
            bool valid)
        {
            Assert.That(
                CompanionWorldMenu.TryParsePerformanceQaToggle(value, out var parsed),
                Is.EqualTo(valid));
            Assert.That(parsed, Is.EqualTo(expected));
        }

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
        public void AudioOutputLatencyUsesHardwareSampleRate()
        {
            Assert.That(
                Pcm16StreamAudioPlayer.CalculateOutputLatencySeconds(256, 4, 48000),
                Is.EqualTo(0.021333d).Within(.000001d));
            Assert.That(
                Pcm16StreamAudioPlayer.CalculateOutputLatencySeconds(0, 4, 48000),
                Is.Zero);
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

        private static void SetMenuField<T>(CompanionWorldMenu menu, string name, T value)
        {
            typeof(CompanionWorldMenu)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(menu, value);
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

                var root = GetMenuField<GameObject>(menu, "menuRoot");
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
                Assert.That(root.transform.Find("Main Menu Layer/挥手"), Is.Null);
                Assert.That(root.transform.Find("Main Menu Layer/摸摸头"), Is.Null);
                Assert.That(root.transform.Find("Main Menu Layer/握手"), Is.Null);
                Assert.That(root.transform.Find("Main Menu Layer/捏脸"), Is.Null);
                Assert.That(root.transform.Find("Action Presets Layer/挥手"), Is.Null);
                Assert.That(root.transform.Find("Action Presets Layer/鞠躬"), Is.Null);
                Assert.That(root.transform.Find("Action Presets Layer/点头"), Is.Null);
                Assert.That(root.transform.Find("Action Presets Layer/轻摆"), Is.Null);
                Assert.That(root.transform.Find("Appearance Layer/描边开关"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/站立校准"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/画质"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/表情：模型默认"), Is.Not.Null);
                Assert.That(root.transform.Find("Appearance Layer/角色模型"), Is.Not.Null);
                Assert.That(root.transform.Find("Model Library Layer/加载选中"), Is.Not.Null);
                Assert.That(root.transform.Find("Model Library Layer/选择模型"), Is.Not.Null);
                Assert.That(root.transform.Find("Model Library Layer/导入模型包"), Is.Not.Null);
                Assert.That(root.transform.Find("Model Library Layer/Installed Model List/删除模型包"), Is.Not.Null);
                Assert.That(root.transform.Find("Model Library Layer/Installed Model List/上一页"), Is.Not.Null);
                Assert.That(root.transform.Find("Model Library Layer/Installed Model List/下一页"), Is.Not.Null);
                Assert.That(root.transform.Find("Quality Layer/画质：清晰"), Is.Not.Null);
                Assert.That(root.transform.Find("Quality Layer/物理：平衡"), Is.Not.Null);
                Assert.That(root.transform.Find("Quality Layer/物理：精细"), Is.Not.Null);

                InvokeTargetMethod(menu, "ShowDebugPanel");
                var mainLayer = root.transform.Find("Main Menu Layer");
                var debugLayer = root.transform.Find("Debug Layer");
                Assert.That(mainLayer.gameObject.activeSelf, Is.True);
                Assert.That(debugLayer.gameObject.activeSelf, Is.True);
                Assert.That(debugLayer.localPosition.x, Is.LessThan(-360f));
                Assert.That(debugLayer.Find("Sidebar Background").GetComponent("CompanionMenuInputBlocker"), Is.Not.Null);

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
        public void ModelListModalBlocksUnderlyingModelButtons()
        {
            var cameraObject = new GameObject("Model List Camera");
            var ownerObject = new GameObject("Model List Owner");
            try
            {
                cameraObject.tag = "MainCamera";
                cameraObject.AddComponent<Camera>();
                var owner = ownerObject.AddComponent<QuestMmdPlayerBootstrap>();
                var menu = ownerObject.AddComponent<CompanionWorldMenu>();
                menu.Initialize(owner);
                menu.ShowInFront();

                InvokeMenuMethod(menu, "ShowModelPanel");
                InvokeMenuMethod(menu, "ShowModelList");
                var root = GetMenuField<GameObject>(menu, "menuRoot");
                var modelLayer = root.transform.Find("Model Library Layer");
                var list = modelLayer.Find("Installed Model List");
                var underlying = modelLayer
                    .GetComponentsInChildren<BoxCollider>(true)
                    .Where(value => value.transform.parent == modelLayer)
                    .ToArray();

                Assert.That(list.gameObject.activeSelf, Is.True);
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.ModelList));
                Assert.That(underlying.Length, Is.GreaterThan(0));
                Assert.That(underlying.All(value => !value.enabled), Is.True);
                Assert.That(list.Find("List Background").GetComponent<BoxCollider>(), Is.Not.Null);
                Assert.That(list.GetComponentsInChildren<BoxCollider>(true).Any(value => value.enabled), Is.True);

                InvokeMenuMethod(menu, "HideModelList");
                Assert.That(menu.ActiveLayer, Is.EqualTo(RuntimeMenuLayer.Models));
                Assert.That(underlying.All(value => value.enabled), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(ownerObject);
                Object.DestroyImmediate(cameraObject);
            }
        }

        [Test]
        public void ModelListUsesFixedPagesAndSelectionStaysInTheList()
        {
            var cameraObject = new GameObject("Model List Selection Camera");
            var ownerObject = new GameObject("Model List Selection Owner");
            try
            {
                cameraObject.tag = "MainCamera";
                cameraObject.AddComponent<Camera>();
                var owner = ownerObject.AddComponent<QuestMmdPlayerBootstrap>();
                var menu = ownerObject.AddComponent<CompanionWorldMenu>();
                menu.Initialize(owner);
                menu.ShowInFront();

                InvokeMenuMethod(menu, "ShowModelPanel");
                var options = GetMenuField<System.Collections.Generic.List<RuntimeMmdModelInfo>>(menu, "modelOptions");
                options.Clear();
                for (var index = 0; index < 18; index++)
                {
                    options.Add(new RuntimeMmdModelInfo($"模型 {index + 1:00}", $"D:/models/model-{index + 1:00}.pmx"));
                }
                SetMenuField(menu, "modelIndex", 1);
                SetMenuField(menu, "modelListPage", 0);
                InvokeMenuMethod(menu, "RefreshModelList");

                var listLayer = GetMenuField<GameObject>(menu, "modelListLayer");
                listLayer.SetActive(true);
                var entries = GetMenuField<System.Collections.Generic.List<GameObject>>(menu, "modelListEntries");
                Assert.That(entries.Count, Is.EqualTo(8));
                Assert.That(entries[0].name, Is.EqualTo("模型 01"));
                Assert.That(entries[1].name, Is.EqualTo("[已选] 模型 02"));

                var thirdTarget = entries[2].GetComponent("CompanionMenuButtonTarget");
                InvokeTargetMethod(thirdTarget, "Press");
                Assert.That(listLayer.activeSelf, Is.True, "Selecting an item must not close the model list.");
                Assert.That(GetMenuField<int>(menu, "modelIndex"), Is.EqualTo(2));
                entries = GetMenuField<System.Collections.Generic.List<GameObject>>(menu, "modelListEntries");
                Assert.That(entries[2].name, Is.EqualTo("[已选] 模型 03"));

                InvokeMenuMethod(menu, "PageModelList", 1);
                Assert.That(GetMenuField<int>(menu, "modelIndex"), Is.EqualTo(2), "Paging must not silently change the selected model.");
                Assert.That(GetMenuField<int>(menu, "modelListPage"), Is.EqualTo(1));
                entries = GetMenuField<System.Collections.Generic.List<GameObject>>(menu, "modelListEntries");
                Assert.That(entries.Count, Is.EqualTo(8));
                Assert.That(entries[0].name, Is.EqualTo("模型 09"));
                Assert.That(entries[7].name, Is.EqualTo("模型 16"));
                Assert.That(GetMenuField<Text>(menu, "modelListPageText").text, Is.EqualTo("第 2 / 3 页"));
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

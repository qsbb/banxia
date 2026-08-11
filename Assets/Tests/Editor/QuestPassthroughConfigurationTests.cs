#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestPassthroughConfigurationTests
    {
        [Test]
        public void AndroidTaskLabelMatchesProductName()
        {
            Assert.That(QuestMmdPlayerBootstrap.AndroidTaskLabel, Is.EqualTo("\u4F34\u590F"));
            Assert.That(PlayerSettings.productName, Is.EqualTo(QuestMmdPlayerBootstrap.AndroidTaskLabel));
            Assert.That(PlayerSettings.GetApplicationIdentifier(BuildTargetGroup.Android), Is.EqualTo("com.lingxi.banxia"));
            Assert.That(PlayerSettings.bundleVersion, Is.EqualTo("0.2.0"));
            Assert.That(PlayerSettings.Android.bundleVersionCode, Is.EqualTo(11));
        }

        [Test]
        public void UrpSettingsPreservePassthroughAlpha()
        {
            var pipeline = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>("Assets/Settings/QuestMmdPlayerURP.asset");
            var renderer = AssetDatabase.LoadAssetAtPath<UniversalRendererData>("Assets/Settings/QuestMmdPlayerURP_Renderer.asset");
            Assert.That(pipeline, Is.Not.Null);
            Assert.That(renderer, Is.Not.Null);

            var pipelineSettings = new SerializedObject(pipeline);
            Assert.That(pipelineSettings.FindProperty("m_SupportsHDR").boolValue, Is.False);
            Assert.That(pipelineSettings.FindProperty("m_SupportsTerrainHoles").boolValue, Is.False);

            var rendererSettings = new SerializedObject(renderer);
            Assert.That(rendererSettings.FindProperty("m_IntermediateTextureMode").intValue, Is.Zero);
        }

        [TestCase(true, true, true, false, PassthroughLifecycleAction.Suspend)]
        [TestCase(true, false, false, false, PassthroughLifecycleAction.Suspend)]
        [TestCase(true, true, false, false, PassthroughLifecycleAction.Suspend)]
        [TestCase(true, false, true, true, PassthroughLifecycleAction.Restore)]
        [TestCase(true, true, true, true, PassthroughLifecycleAction.None)]
        [TestCase(true, false, false, true, PassthroughLifecycleAction.None)]
        [TestCase(false, true, true, false, PassthroughLifecycleAction.None)]
        [TestCase(false, false, true, true, PassthroughLifecycleAction.None)]
        public void LifecycleDecisionPreservesUserIntentAndIsIdempotent(
            bool requestedEnabled,
            bool applicationPaused,
            bool applicationFocused,
            bool suspendedForLifecycle,
            PassthroughLifecycleAction expected)
        {
            var actual = PassthroughFacade.DecideLifecycleAction(
                requestedEnabled,
                applicationPaused,
                applicationFocused,
                suspendedForLifecycle);

            Assert.That(actual, Is.EqualTo(expected));
        }
    }
}
#endif

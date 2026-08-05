#if UNITY_EDITOR
using NUnit.Framework;
using UnityEditor;
using UnityEngine.Rendering.Universal;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestPassthroughConfigurationTests
    {
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
    }
}
#endif
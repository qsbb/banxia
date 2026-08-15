using System.Reflection;
using NUnit.Framework;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class PmxMaterialCompatibilityTests
    {
        [TestCase("eye")]
        [TestCase("眼白")]
        [TestCase("目影")]
        [TestCase("虹彩")]
        [TestCase("瞳孔")]
        public void EyeMaterialRecognitionSupportsSourceMmdNames(string name)
        {
            var material = new PMXMaterial { originalName = name };
            Assert.That(IsEyeMaterial(material), Is.True);
        }

        [TestCase("face")]
        [TestCase("顔")]
        [TestCase("脸")]
        [TestCase("肌")]
        public void FaceMaterialRecognitionSupportsSourceMmdNames(string name)
        {
            var material = new PMXMaterial { originalName = name };
            Assert.That(IsFaceMaterial(material), Is.True);
        }

        [Test]
        public void NonFaceMaterialIsNotMisclassified()
        {
            var material = new PMXMaterial { originalName = "衣服" };
            Assert.That(IsEyeMaterial(material), Is.False);
            Assert.That(IsFaceMaterial(material), Is.False);
        }

        [Test]
        public void RuntimeNamePreservationRestoresMaterialNamesForShadingHeuristics()
        {
            var model = ScriptableObject.CreateInstance<PMXModel>();
            try
            {
                model.bones = new PMXBone[0];
                model.morphs = new PMXMorph[0];
                model.materials = new[]
                {
                    new PMXMaterial { originalName = "眼白" },
                    new PMXMaterial { originalName = "face" }
                };

                var method = typeof(RuntimeMmdModelLoader).GetMethod(
                    "PreserveOriginalNames",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(method, Is.Not.Null);
                method.Invoke(null, new object[] { model });

                Assert.That(model.materials[0].renamedName.ToString(), Is.EqualTo("眼白"));
                Assert.That(model.materials[1].renamedName.ToString(), Is.EqualTo("face"));
                Assert.That(IsEyeMaterial(model.materials[0]), Is.True);
                Assert.That(IsFaceMaterial(model.materials[1]), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(model);
            }
        }

        [TestCase(.5f, 0f)]
        [TestCase(.95f, 1f)]
        public void UrpFallbackPreservesUntexturedTransparentPmxColorAndDepthSemantics(
            float alpha,
            float expectedZWrite)
        {
            var model = ScriptableObject.CreateInstance<PMXModel>();
            Material material = null;
            try
            {
                model.materials = new[]
                {
                    new PMXMaterial
                    {
                        originalName = "目影",
                        renamedName = "目影",
                        diffuse = new Color(0f, 0f, 0f, alpha),
                        textureIndex = -1,
                        sphereTextureIndex = -1,
                        faceIndexCount = 0
                    }
                };

                var built = PMXMaterialBuilder.Build(
                    model,
                    new PMXImportOptions(),
                    "eye-shadow-test",
                    System.Array.Empty<Texture2D>());
                Assert.That(built, Has.Count.EqualTo(1));
                material = built[0];
                Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Unlit"));
                Assert.That(material.HasProperty("_BaseColor"), Is.True);
                Assert.That(material.GetColor("_BaseColor"), Is.EqualTo(new Color(0f, 0f, 0f, alpha)));
                Assert.That(material.GetFloat("_Surface"), Is.EqualTo(1f));
                Assert.That(material.GetFloat("_SrcBlend"), Is.EqualTo((float)UnityEngine.Rendering.BlendMode.SrcAlpha));
                Assert.That(material.GetFloat("_DstBlend"), Is.EqualTo((float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
                Assert.That(material.GetFloat("_ZWrite"), Is.EqualTo(expectedZWrite));
                Assert.That(material.renderQueue, Is.EqualTo(3000));
                Assert.That(material.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), Is.True);
            }
            finally
            {
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(model);
            }
        }

        private static bool IsEyeMaterial(PMXMaterial material)
        {
            return Invoke("IsEyeMaterial", material);
        }

        private static bool IsFaceMaterial(PMXMaterial material)
        {
            return Invoke("IsFaceMaterial", material);
        }

        private static bool Invoke(string methodName, PMXMaterial material)
        {
            var method = typeof(PMXMaterialBuilder).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, new object[] { material });
        }
    }
}

using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UMT;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

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

        [TestCase("eye shadow")]
        [TestCase("eyeshadow")]
        [TestCase("目影")]
        [TestCase("眼影")]
        [TestCase("目陰")]
        public void EyeShadowOverlayRecognitionSupportsCommonMmdNames(string name)
        {
            var material = new PMXMaterial
            {
                originalName = name,
                diffuse = new Color(0f, 0f, 0f, .5f),
                textureIndex = -1
            };

            Assert.That(IsEyeShadowOverlay(material), Is.True);
        }

        [Test]
        public void EyeShadowOverlayRecognitionDoesNotHideTexturedMakeupOrOpaqueEyeParts()
        {
            Assert.That(
                IsEyeShadowOverlay(new PMXMaterial
                {
                    originalName = "eye shadow",
                    diffuse = new Color(0f, 0f, 0f, .5f),
                    textureIndex = 0
                }),
                Is.False);
            Assert.That(
                IsEyeShadowOverlay(new PMXMaterial
                {
                    originalName = "目影",
                    diffuse = Color.black,
                    textureIndex = -1
                }),
                Is.False);
            Assert.That(
                IsEyeShadowOverlay(new PMXMaterial
                {
                    originalName = "眼白",
                    diffuse = new Color(1f, 1f, 1f, .5f),
                    textureIndex = -1
                }),
                Is.False);
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

        [Test]
        public void AmbiguousRecursiveTextureBasenameFailsClosed()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "banxia-texture-ambiguity-" + System.Guid.NewGuid().ToString("N"));
            var first = Path.Combine(root, "variant-a");
            var second = Path.Combine(root, "variant-b");
            Directory.CreateDirectory(first);
            Directory.CreateDirectory(second);
            File.WriteAllBytes(Path.Combine(first, "face.png"), new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(second, "face.png"), new byte[] { 2 });
            try
            {
                var options = new PMXImportOptions
                {
                    sourcePath = Path.Combine(root, "model.pmx"),
                    textureBaseDirectory = root
                };
                var method = typeof(PMXTextureLoader).GetMethod(
                    "ResolveTexturePath",
                    BindingFlags.Static | BindingFlags.NonPublic);
                Assert.That(method, Is.Not.Null);

                Assert.That(
                    method.Invoke(null, new object[] { "face.png", options }),
                    Is.Null,
                    "A sibling variant's texture must never be selected arbitrarily.");
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [UnityTest]
        public IEnumerator CancelledAsyncBuildDestroysPartialRootAndTextures()
        {
            var model = ScriptableObject.CreateInstance<PMXModel>();
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            var cancellation = new CancellationTokenSource();
            var modelName = "banxia-cancel-cleanup-" + System.Guid.NewGuid().ToString("N");
            var options = new PMXImportOptions
            {
                sourceName = modelName,
                applyRenames = false,
                createAvatar = false,
                loadTextures = (_, __) => new[] { texture },
                timingCallback = (stage, _) =>
                {
                    if (stage == "Load Textures") cancellation.Cancel();
                }
            };
            try
            {
                Task<PMXImportResult> build = PMXImporter.BuildUnityObjectsAsync(
                    new UMTFrameBudget(1000d),
                    model,
                    options,
                    cancellation.Token);
                while (!build.IsCompleted)
                {
                    yield return null;
                }

                Assert.That(build.IsCanceled, Is.True,
                    build.Exception?.GetBaseException().ToString());
                Assert.Throws<System.OperationCanceledException>(
                    () => build.GetAwaiter().GetResult());

                Assert.That(GameObject.Find(modelName), Is.Null);
                Assert.That(texture == null, Is.True);
            }
            finally
            {
                cancellation.Dispose();
                if (texture != null) Object.DestroyImmediate(texture);
                Object.DestroyImmediate(model);
            }
        }

        [UnityTest]
        public IEnumerator CancellationDuringAsyncReadDestroysPartialModel()
        {
            var before = Resources.FindObjectsOfTypeAll<PMXModel>().Length;
            using var stream = new MemoryStream(BuildLargePartialPmx(4096), false);
            using var cancellation = new CancellationTokenSource();
            Task<PMXModel> read = PMXReader.ReadAsync(
                new UMTFrameBudget(0d),
                stream,
                true,
                cancellation.Token);

            yield return null;
            Assert.That(read.IsCompleted, Is.False,
                "The synthetic PMX must still be in its budgeted vertex loop.");
            cancellation.Cancel();
            while (!read.IsCompleted)
            {
                yield return null;
            }

            Assert.That(read.IsCanceled, Is.True,
                read.Exception?.GetBaseException().ToString());
            Assert.Throws<System.OperationCanceledException>(
                () => read.GetAwaiter().GetResult());
            Assert.That(
                Resources.FindObjectsOfTypeAll<PMXModel>().Length,
                Is.EqualTo(before),
                "A cancelled PMX read must not retain its partial ScriptableObject.");
        }

        [UnityTest]
        public IEnumerator AsyncSingleGroupMeshBuildUsesFineGrainedFrameBudgetCheckpoints()
        {
            var model = ScriptableObject.CreateInstance<PMXModel>();
            var group = new PMXMorphLinkedMaterialGroup();
            group.materialIndices.Add(0);
            var budget = new UMTFrameBudget(0d);
            List<PMXImportedMesh> meshes = null;
            try
            {
                model.vertices = new[]
                {
                    new PMXVertex { position = new float3(0f, 0f, 0f), normal = new float3(0f, 1f, 0f), uv = new float2(0f, 0f) },
                    new PMXVertex { position = new float3(1f, 0f, 0f), normal = new float3(0f, 1f, 0f), uv = new float2(1f, 0f) },
                    new PMXVertex { position = new float3(0f, 1f, 0f), normal = new float3(0f, 1f, 0f), uv = new float2(0f, 1f) }
                };
                model.indices = new uint[] { 0, 1, 2 };
                model.materials = new[]
                {
                    new PMXMaterial
                    {
                        originalName = "single-group",
                        renamedName = "single-group",
                        faceIndexCount = 3
                    }
                };
                model.bones = System.Array.Empty<PMXBone>();
                model.morphs = System.Array.Empty<PMXMorph>();

                Task<List<PMXImportedMesh>> build = PMXMeshBuilder.BuildAsync(
                    budget,
                    model,
                    "budgeted-single-group",
                    new[] { group },
                    System.Array.Empty<Matrix4x4>());
                while (!build.IsCompleted)
                {
                    yield return null;
                }
                if (build.IsFaulted)
                {
                    throw build.Exception?.GetBaseException();
                }

                meshes = build.Result;
                Assert.That(meshes, Has.Count.EqualTo(1));
                Assert.That(
                    budget.YieldCount,
                    Is.GreaterThanOrEqualTo(6),
                    "One morph-linked material group still needs checkpoints inside triangle remapping, vertex upload, indices, morphs, and bounds.");
            }
            finally
            {
                if (meshes != null)
                {
                    foreach (var mesh in meshes)
                    {
                        if (mesh?.mesh != null) Object.DestroyImmediate(mesh.mesh);
                    }
                }
                Object.DestroyImmediate(model);
            }
        }

        [UnityTest]
        public IEnumerator AsyncMaterialBuildUsesPredecodedPixelsForUnreadableSharedTexture()
        {
            var model = ScriptableObject.CreateInstance<PMXModel>();
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            List<Material> materials = null;
            try
            {
                texture.SetPixels32(new[]
                {
                    new Color32(255, 255, 255, 0),
                    new Color32(255, 255, 255, 0),
                    new Color32(255, 255, 255, 0),
                    new Color32(255, 255, 255, 0)
                });
                texture.Apply(false, true);
                Assert.That(texture.isReadable, Is.False);

                model.vertices = new[]
                {
                    new PMXVertex { uv = new float2(0f, 0f) },
                    new PMXVertex { uv = new float2(1f, 0f) },
                    new PMXVertex { uv = new float2(0f, 1f) }
                };
                model.indices = new uint[] { 0, 1, 2, 0, 1, 2 };
                model.texturePaths = new FixedString128Bytes[] { "shared.tga" };
                model.materials = new[]
                {
                    TransparentTestMaterial("shared-a"),
                    TransparentTestMaterial("shared-b")
                };
                var decoded = new Dictionary<int, PMXMaterialBuilder.SourcePixels>
                {
                    [0] = new PMXMaterialBuilder.SourcePixels(
                        new[]
                        {
                            new Color32(255, 255, 255, 0),
                            new Color32(255, 255, 255, 0),
                            new Color32(255, 255, 255, 0),
                            new Color32(255, 255, 255, 0)
                        },
                        2,
                        2)
                };

                Task<List<Material>> build = PMXMaterialBuilder.BuildAsync(
                    new UMTFrameBudget(0d),
                    model,
                    new PMXImportOptions { textureBaseDirectory = "missing" },
                    "predecoded-source-pixels",
                    new[] { texture },
                    default,
                    decoded);
                while (!build.IsCompleted)
                {
                    yield return null;
                }
                if (build.IsFaulted)
                {
                    throw build.Exception?.GetBaseException();
                }

                materials = build.Result;
                Assert.That(materials, Has.Count.EqualTo(2));
                Assert.That(materials[0].renderQueue, Is.EqualTo(3000));
                Assert.That(materials[1].renderQueue, Is.EqualTo(3001));
            }
            finally
            {
                if (materials != null)
                {
                    foreach (var material in materials)
                    {
                        if (material != null) Object.DestroyImmediate(material);
                    }
                }
                if (texture != null) Object.DestroyImmediate(texture);
                Object.DestroyImmediate(model);
            }
        }

        [Test]
        public void InvalidSynchronousReadDestroysPartialModel()
        {
            var before = Resources.FindObjectsOfTypeAll<PMXModel>().Length;
            using var stream = new MemoryStream(new byte[] { 0x00 }, false);
            Assert.Catch(() => PMXReader.Read(stream, true));
            Assert.That(
                Resources.FindObjectsOfTypeAll<PMXModel>().Length,
                Is.EqualTo(before),
                "A failed PMX read must not retain its partial ScriptableObject.");
        }

        private static byte[] BuildLargePartialPmx(int vertexCount)
        {
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("PMX "));
                writer.Write(2.0f);
                writer.Write((byte)8);
                writer.Write((byte)PMXHeader.TextEncoding.UTF8);
                writer.Write((byte)0);
                for (var i = 0; i < 6; i++)
                {
                    writer.Write((byte)1);
                }
                for (var i = 0; i < 4; i++)
                {
                    writer.Write(0);
                }

                writer.Write(vertexCount);
                for (var i = 0; i < vertexCount; i++)
                {
                    for (var value = 0; value < 8; value++)
                    {
                        writer.Write(0f);
                    }
                    writer.Write((byte)PMXWeight.Type.BDEF1);
                    writer.Write((sbyte)0);
                    writer.Write(1f);
                }
            }
            return stream.ToArray();
        }

        private static PMXMaterial TransparentTestMaterial(string name)
        {
            return new PMXMaterial
            {
                originalName = name,
                renamedName = name,
                diffuse = Color.white,
                textureIndex = 0,
                sphereTextureIndex = -1,
                faceIndexCount = 3
            };
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
                        originalName = "translucent-overlay",
                        renamedName = "translucent-overlay",
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

        [Test]
        public void UntexturedEyeShadowOverlayNeverWritesDepthOrDrawsAnOutline()
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
                        diffuse = new Color(0f, 0f, 0f, .95f),
                        drawingFlags = PMXMaterial.DrawingFlags.DrawEdge,
                        textureIndex = -1,
                        sphereTextureIndex = -1,
                        faceIndexCount = 0
                    }
                };

                var built = PMXMaterialBuilder.Build(
                    model,
                    new PMXImportOptions(),
                    "eye-shadow-overlay-test",
                    System.Array.Empty<Texture2D>());
                material = built[0];

                Assert.That(material.shader.name, Is.EqualTo("Universal Render Pipeline/Unlit"));
                Assert.That(material.GetFloat("_Surface"), Is.EqualTo(1f));
                Assert.That(material.GetFloat("_ZWrite"), Is.EqualTo(0f));
                Assert.That(material.renderQueue, Is.EqualTo(3000));
                Assert.That(material.GetColor("_BaseColor").a, Is.EqualTo(.95f).Within(.001f));
                Assert.That(material.GetTag("BanxiaOutline", false, "missing"), Is.EqualTo("0"));
            }
            finally
            {
                if (material != null) Object.DestroyImmediate(material);
                Object.DestroyImmediate(model);
            }
        }

        [Test]
        public void OpaqueDrawEdgeMaterialDeclaresUrpOutlinePassEligibility()
        {
            var model = ScriptableObject.CreateInstance<PMXModel>();
            Material material = null;
            try
            {
                model.materials = new[]
                {
                    new PMXMaterial
                    {
                        originalName = "body",
                        renamedName = "body",
                        diffuse = Color.white,
                        drawingFlags = PMXMaterial.DrawingFlags.DrawEdge,
                        textureIndex = -1,
                        sphereTextureIndex = -1,
                        faceIndexCount = 0
                    }
                };

                material = PMXMaterialBuilder.Build(
                    model,
                    new PMXImportOptions(),
                    "outline-tag-test",
                    System.Array.Empty<Texture2D>())[0];

                Assert.That(material.GetTag("BanxiaOutline", false, "0"), Is.EqualTo("1"));
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

        private static bool IsEyeShadowOverlay(PMXMaterial material)
        {
            return Invoke("IsEyeShadowOverlay", material);
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

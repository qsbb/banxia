#if UNITY_EDITOR
using System;
using System.IO;
using UMT;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace QuestMmdPlayer.Editor
{
    public static class QuestMmdPlayerPreview
    {
        private const string ScenePath = "Assets/Scenes/Prototype.unity";
        private const string DefaultOutputPath = "Builds/ModelPreview.png";
        private const string InputEnvironmentVariable = "BANXIA_PREVIEW_PMX";
        private const string OutputEnvironmentVariable = "BANXIA_PREVIEW_OUTPUT";

        [MenuItem("伴夏/Render Model Preview")]
        public static void RenderModelPreview()
        {
            QuestMmdPlayerMenu.EnsureRenderPipeline();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            string pmxPath = Environment.GetEnvironmentVariable(InputEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(pmxPath))
            {
                pmxPath = EditorUtility.OpenFilePanel("Select a local PMX model", string.Empty, "pmx");
            }
            if (string.IsNullOrWhiteSpace(pmxPath)) return;
            if (!File.Exists(pmxPath))
            {
                throw new FileNotFoundException("Selected PMX model was not found.", pmxPath);
            }

            string outputPath = Environment.GetEnvironmentVariable(OutputEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                outputPath = DefaultOutputPath;
            }
            string textureDirectory = Path.GetDirectoryName(pmxPath);
            PMXImportResult result = null;

            try
            {
                var options = new PMXImportOptions
                {
                    sourcePath = pmxPath,
                    textureBaseDirectory = textureDirectory,
                    applyRenames = false,
                    createAvatar = false,
                    strictVersion = true
                };
                PMXModel model;
                using (var stream = File.OpenRead(pmxPath))
                {
                    model = PMXReader.Read(stream, true);
                }
                PreserveOriginalNames(model);
                result = PMXImporter.BuildUnityObjects(model, options);
                result.root.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

                var renderers = result.root.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    throw new MissingComponentException("The imported PMX model has no renderers.");
                }

                var bounds = AvatarQaCapture.CalculateBounds(result.root);

                var cameraObject = new GameObject("Model Preview Camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.08f, 0.09f, 0.11f, 1f);
                camera.fieldOfView = 24f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 100f;
                cameraObject.transform.position = bounds.center + result.root.transform.forward * 3f;
                var pose = AvatarQaCapture.CalculateCameraPose(result.root, bounds, camera);
                cameraObject.transform.SetPositionAndRotation(pose.position, pose.rotation);

                var lightObject = new GameObject("Model Preview Light");
                lightObject.transform.rotation = cameraObject.transform.rotation * Quaternion.Euler(35f, -28f, 0f);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.4f;

                const int width = 1024;
                const int height = 1024;
                var target = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
                var previous = RenderTexture.active;
                try
                {
                    camera.targetTexture = target;
                    camera.Render();
                    RenderTexture.active = target;
                    var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
                    image.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                    image.Apply();

                    var outputDirectory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrWhiteSpace(outputDirectory))
                    {
                        Directory.CreateDirectory(outputDirectory);
                    }
                    File.WriteAllBytes(outputPath, image.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(image);
                    Debug.Log($"Runtime PMX preview rendered: {outputPath}; renderers={renderers.Length}; bounds={bounds.size}");
                }
                finally
                {
                    camera.targetTexture = null;
                    RenderTexture.active = previous;
                    RenderTexture.ReleaseTemporary(target);
                    UnityEngine.Object.DestroyImmediate(cameraObject);
                    UnityEngine.Object.DestroyImmediate(lightObject);
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                throw;
            }
            finally
            {
                if (result != null)
                {
                    if (result.root != null)
                    {
                        UnityEngine.Object.DestroyImmediate(result.root);
                    }

                    foreach (var texture in result.textures)
                    {
                        if (texture != null)
                        {
                            UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }

                    if (result.model != null)
                    {
                        UnityEngine.Object.DestroyImmediate(result.model);
                    }
                }
            }
        }

        private static void PreserveOriginalNames(PMXModel model)
        {
            for (var index = 0; index < model.bones.Length; index++)
            {
                var bone = model.bones[index];
                bone.renamedName = bone.originalName;
                model.bones[index] = bone;
            }

            for (var index = 0; index < model.morphs.Length; index++)
            {
                var morph = model.morphs[index];
                morph.renamedName = morph.originalName;
                model.morphs[index] = morph;
            }

            for (var index = 0; index < model.materials.Length; index++)
            {
                var material = model.materials[index];
                material.renamedName = material.originalName;
                model.materials[index] = material;
            }
        }
    }
}
#endif

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
        private const string OutputPath = "Builds/ModelPreview.png";

        [MenuItem("Quest MMD Player/Render Model Preview")]
        public static void RenderModelPreview()
        {
            QuestMmdPlayerMenu.EnsureRenderPipeline();
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            string pmxPath = EditorUtility.OpenFilePanel("Select a local PMX model", string.Empty, "pmx");
            if (string.IsNullOrWhiteSpace(pmxPath)) return;
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
                result = PMXImporter.Import(pmxPath, options);
                result.root.transform.position = new Vector3(0f, 0f, 2.2f);

                var renderers = result.root.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    throw new MissingComponentException("The imported PMX model has no renderers.");
                }

                var bounds = renderers[0].bounds;
                for (var i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }

                var cameraObject = new GameObject("Model Preview Camera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.08f, 0.09f, 0.11f, 1f);
                camera.fieldOfView = 35f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 100f;

                const float aspect = 0.75f;
                var verticalDistance = bounds.size.y * 0.5f / Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad);
                var horizontalFov = 2f * Mathf.Atan(Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * aspect);
                var horizontalDistance = bounds.size.x * 0.5f / Mathf.Tan(horizontalFov * 0.5f);
                var distance = Mathf.Max(verticalDistance, horizontalDistance) * 1.18f;
                cameraObject.transform.position = bounds.center + Vector3.back * distance;
                cameraObject.transform.rotation = Quaternion.LookRotation(bounds.center - cameraObject.transform.position, Vector3.up);

                var lightObject = new GameObject("Model Preview Light");
                lightObject.transform.rotation = Quaternion.Euler(35f, -25f, 0f);
                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.4f;

                const int width = 768;
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

                    Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
                    File.WriteAllBytes(OutputPath, image.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(image);
                    Debug.Log($"Runtime PMX preview rendered: {OutputPath}; renderers={renderers.Length}; bounds={bounds.size}");
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
    }
}
#endif

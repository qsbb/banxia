#if UNITY_EDITOR
using System;
using System.IO;
using System.Reflection;
using UMT;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestMmdPlayer.Editor
{
    public static class QuestMmdHumanInteractionPreview
    {
        private const string OutputDirectory = "Builds/HumanInteractionPreviews";
        private static string selectedPmxPath;

        private struct View
        {
            public string name;
            public Vector3 direction;
            public View(string viewName, Vector3 viewDirection) { name = viewName; direction = viewDirection.normalized; }
        }

        [MenuItem("Quest MMD Player/Render Human Interaction Previews")]
        public static void RenderAll()
        {
            QuestMmdPlayerMenu.EnsureRenderPipeline();
            selectedPmxPath = EditorUtility.OpenFilePanel("Select a local PMX model", string.Empty, "pmx");
            if (string.IsNullOrWhiteSpace(selectedPmxPath)) return;
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory(OutputDirectory);
            RenderInteraction(HumanInteractionKind.None, "idle", 1f);
            RenderInteraction(HumanInteractionKind.Handshake, "handshake", 1f);
            RenderInteraction(HumanInteractionKind.HeadPat, "head_pat", .58f);
            RenderInteraction(HumanInteractionKind.CheekPinch, "cheek_pinch", .52f);
            AssetDatabase.Refresh();
            Debug.Log("Human interaction previews rendered to " + OutputDirectory);
        }

        private static void RenderInteraction(HumanInteractionKind kind, string fileName, float frameHeight)
        {
            string pmxPath = selectedPmxPath;
            PMXImportResult result = null;
            GameObject host = null;
            GameObject cameraObject = null;
            GameObject keyObject = null;
            GameObject fillObject = null;
            GameObject floor = null;
            try
            {
                var options = new PMXImportOptions
                {
                    sourcePath = pmxPath,
                    textureBaseDirectory = Path.GetDirectoryName(pmxPath),
                    applyRenames = false,
                    createAvatar = false,
                    strictVersion = true
                };
                result = PMXImporter.Import(pmxPath, options);
                if (result == null || result.root == null) throw new InvalidOperationException("PMX import returned no root.");

                host = new GameObject(result.root.name + "_Avatar");
                host.transform.position = Vector3.zero;
                result.root.transform.SetParent(host.transform, false);
                var avatar = host.AddComponent<AvatarController>();
                avatar.Initialize(result.root.transform);
                var idlePose = host.AddComponent<AvatarNaturalIdlePose>();
                idlePose.SetPreset(AvatarIdlePreset.Relaxed);
                idlePose.Bind(avatar);
                avatar.CaptureCurrentActionPose();

                cameraObject = new GameObject("Preview Camera") { tag = "MainCamera" };
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.055f, .07f, .09f, 1f);
                camera.fieldOfView = 36f;
                camera.aspect = 1f;
                camera.nearClipPlane = .01f;
                camera.farClipPlane = 100f;

                keyObject = CreateLight("Key Light", new Color(1f, .91f, .82f), 1.65f, new Vector3(38f, -28f, 0f));
                fillObject = CreateLight("Fill Light", new Color(.72f, .84f, 1f), .8f, new Vector3(22f, 145f, 0f));
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(.2f, .24f, .3f);
                RenderSettings.ambientEquatorColor = new Color(.1f, .12f, .16f);
                RenderSettings.ambientGroundColor = new Color(.04f, .045f, .055f);

                var touch = host.AddComponent<AvatarTouchInteraction>();
                touch.Bind(avatar);
                var interaction = host.AddComponent<AvatarHumanInteraction>();
                interaction.Bind(avatar);
                if (!interaction.HasHeadBone || !interaction.HasHandBones)
                    Debug.LogWarning("Preview bone matching is incomplete: head=" + interaction.HasHeadBone + ", hands=" + interaction.HasHandBones);
                if (kind != HumanInteractionKind.None)
                {
                    interaction.SimulateInteraction(kind, 30f);
                    SetPrivateFloat(interaction, "fade", 1f);
                    InvokePoseMethod(interaction, "ApplyMorphs", kind, 1f);
                    InvokePoseMethod(interaction, "ApplyBones", kind, 1f);
                }
                Physics.SyncTransforms();

                var bounds = GetBounds(host);
                var views = new[]
                {
                    new View("front", Vector3.back),
                    new View("three_quarter", new Vector3(-.7f, .08f, -1f)),
                    new View("side", Vector3.left)
                };
                for (var i = 0; i < views.Length; i++)
                {
                    var focus = kind == HumanInteractionKind.Handshake
                        ? bounds.center + Vector3.up * .02f
                        : new Vector3(bounds.center.x, bounds.max.y - bounds.size.y * (1f - frameHeight) * .48f, bounds.center.z);
                    var distance = GetCameraDistance(
                        camera,
                        bounds,
                        frameHeight,
                        kind == HumanInteractionKind.Handshake || kind == HumanInteractionKind.None);
                    cameraObject.transform.position = focus - views[i].direction * distance + Vector3.up * .01f;
                    cameraObject.transform.rotation = Quaternion.LookRotation(focus - cameraObject.transform.position, Vector3.up);
                    Render(camera, Path.Combine(OutputDirectory, fileName + "_" + views[i].name + ".png"));
                }
                Debug.Log(
                    "Human interaction preview PASS: " + kind +
                    ", renderers=" + host.GetComponentsInChildren<Renderer>(true).Length +
                    ", morphs=" + interaction.MatchedMorphCount +
                    ", modelCollisionVolumes=" + touch.ModelCollisionVolumeCount);
            }
            finally
            {
                if (floor != null) UnityEngine.Object.DestroyImmediate(floor);
                if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
                if (keyObject != null) UnityEngine.Object.DestroyImmediate(keyObject);
                if (fillObject != null) UnityEngine.Object.DestroyImmediate(fillObject);
                if (host != null) UnityEngine.Object.DestroyImmediate(host);
                else if (result != null && result.root != null) UnityEngine.Object.DestroyImmediate(result.root);
                if (result != null)
                {
                    foreach (var texture in result.textures) if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
                    if (result.model != null) UnityEngine.Object.DestroyImmediate(result.model);
                }
            }
        }

        private static GameObject CreateLight(string name, Color color, float intensity, Vector3 rotation)
        {
            var gameObject = new GameObject(name);
            gameObject.transform.rotation = Quaternion.Euler(rotation);
            var light = gameObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = color;
            light.intensity = intensity;
            return gameObject;
        }

        private static Bounds GetBounds(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) throw new MissingComponentException("Imported model has no renderers.");
            var bounds = renderers[0].bounds;
            for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }

        private static float GetCameraDistance(Camera camera, Bounds bounds, float frameHeight, bool fullBody)
        {
            var height = fullBody ? bounds.size.y * 1.08f : bounds.size.y * frameHeight;
            var vertical = height * .5f / Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad);
            var width = bounds.size.x * (fullBody ? 1.15f : .92f);
            var horizontalFov = 2f * Mathf.Atan(Mathf.Tan(camera.fieldOfView * .5f * Mathf.Deg2Rad) * camera.aspect);
            var horizontal = width * .5f / Mathf.Tan(horizontalFov * .5f);
            return Mathf.Max(vertical, horizontal) * 1.12f;
        }

        private static void Render(Camera camera, string outputPath)
        {
            const int size = 1024;
            var target = RenderTexture.GetTemporary(size, size, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            Texture2D image = null;
            try
            {
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                image = new Texture2D(size, size, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0f, 0f, size, size), 0, 0);
                image.Apply();
                File.WriteAllBytes(outputPath, image.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (image != null) UnityEngine.Object.DestroyImmediate(image);
            }
        }

        private static void SetPrivateFloat(object target, string name, float value)
        {
            typeof(AvatarHumanInteraction).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        }

        private static void InvokePoseMethod(AvatarHumanInteraction target, string name, HumanInteractionKind kind, float amount)
        {
            typeof(AvatarHumanInteraction).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, new object[] { kind, amount });
        }
    }
}
#endif

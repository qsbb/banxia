#if UNITY_EDITOR
using System.IO;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuestMmdPlayer.Editor
{
    public static class CompanionMenuCapture
    {
        public static void Capture()
        {
            QuestMmdPlayerMenu.EnsureRenderPipeline();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var cameraObject = new GameObject("Preview Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetPositionAndRotation(new Vector3(0f, 1.6f, 0f), Quaternion.identity);
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(.025f, .035f, .04f, 1f);
            camera.fieldOfView = 64f;
            camera.nearClipPlane = .03f;
            camera.farClipPlane = 10f;

            var ownerObject = new GameObject("Preview Bootstrap");
            var owner = ownerObject.AddComponent<QuestMmdPlayerBootstrap>();
            var menu = ownerObject.AddComponent<CompanionWorldMenu>();
            menu.Initialize(owner);
            menu.ShowInFront();
            Canvas.ForceUpdateCanvases();

            var buttons = Object.FindObjectsOfType<BoxCollider>(true);
            if (buttons.Length != 19)
            {
                throw new InvalidDataException($"Expected 19 world-menu button colliders, found {buttons.Length}.");
            }

            const int width = 1600;
            const int height = 1200;
            var target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            camera.targetTexture = target;
            camera.Render();
            RenderTexture.active = target;
            image.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
            image.Apply();

            const string output = "C:/tmp/quest_companion_world_menu_preview.png";
            File.WriteAllBytes(output, image.EncodeToPNG());
            Debug.Log($"[CompanionMenuCapture] Saved {output}; buttons={buttons.Length}.");

            RenderTexture.active = null;
            camera.targetTexture = null;
            Object.DestroyImmediate(image);
            Object.DestroyImmediate(target);
            Object.DestroyImmediate(ownerObject);
            Object.DestroyImmediate(cameraObject);
        }
    }
}
#endif
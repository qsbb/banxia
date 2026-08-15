#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.OpenXR.Features;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Hands.OpenXR;
using UnityEngine.XR.OpenXR;
using UnityEngine.SceneManagement;

namespace QuestMmdPlayer.Editor
{
    public static class QuestMmdPlayerMenu
    {
        private const string UrpAssetPath = "Assets/Settings/QuestMmdPlayerURP.asset";
        private const string UrpRendererPath = "Assets/Settings/QuestMmdPlayerURP_Renderer.asset";
        [MenuItem("伴夏/Create Prototype Scene")]
        public static void CreatePrototypeScene()
        {
            EnsureRenderPipeline();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = new GameObject("BanxiaBootstrap");
            root.AddComponent<QuestMmdPlayerBootstrap>();
            root.AddComponent<RuntimeMmdModelLoader>();
            root.AddComponent<ARSession>();

            CreateMrRig();

            var directory = "Assets/Scenes";
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                AssetDatabase.Refresh();
            }

            EditorSceneManager.SaveScene(scene, Path.Combine(directory, "Prototype.unity"));
            Selection.activeGameObject = root;
            Debug.Log("Created Assets/Scenes/Prototype.unity. Import and select an on-device PMX model at runtime.");
        }

        internal static XROrigin CreateMrRig()
        {
            var originObject = new GameObject("XR Origin");
            var origin = originObject.AddComponent<XROrigin>();

            var cameraOffset = new GameObject("Camera Offset");
            cameraOffset.transform.SetParent(originObject.transform, false);

            var cameraObject = new GameObject("Main Camera");
            cameraObject.tag = "MainCamera";
            cameraObject.transform.SetParent(cameraOffset.transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.045f, 0.065f, 0f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 50f;
            cameraObject.AddComponent<AudioListener>();
            cameraObject.AddComponent<ARCameraManager>();

            var trackedPoseDriver = cameraObject.AddComponent<TrackedPoseDriver>();
            var positionAction = new InputAction(
                "Head Position",
                binding: "<XRHMD>/centerEyePosition",
                expectedControlType: "Vector3");
            var rotationAction = new InputAction(
                "Head Rotation",
                binding: "<XRHMD>/centerEyeRotation",
                expectedControlType: "Quaternion");
            trackedPoseDriver.positionInput = new InputActionProperty(positionAction);
            trackedPoseDriver.rotationInput = new InputActionProperty(rotationAction);

            origin.CameraFloorOffsetObject = cameraOffset;
            origin.Camera = camera;
            origin.RequestedTrackingOriginMode = XROrigin.TrackingOriginMode.Floor;
            origin.CameraYOffset = 0f;

            var planeManager = originObject.AddComponent<ARPlaneManager>();
            planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal;
            planeManager.enabled = false;
            var raycastManager = originObject.AddComponent<ARRaycastManager>();
            raycastManager.enabled = false;
            return origin;
        }

        [MenuItem("伴夏/Enable Quest Hand Tracking")]
        public static void EnableQuestHandTracking()
        {
            FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings == null)
            {
                Debug.LogWarning("Android OpenXR settings are not available yet.");
                return;
            }

            var feature = settings.GetFeature<HandTracking>();
            if (feature == null)
            {
                Debug.LogWarning("XR Hands Hand Tracking Subsystem feature is missing. Reimport com.unity.xr.hands.");
                return;
            }

            feature.enabled = true;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            Debug.Log("Quest Hand Tracking Subsystem enabled for Android.");
        }
        [MenuItem("伴夏/Ensure URP Rendering")]
        public static void EnsureRenderPipeline()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UrpAssetPath));
            AssetDatabase.Refresh();

            var rendererData = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(UrpRendererPath);
            if (rendererData == null)
            {
                rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
                rendererData.name = "QuestMmdPlayerURP_Renderer";
                AssetDatabase.CreateAsset(rendererData, UrpRendererPath);
            }

            var pipelineAsset = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (pipelineAsset == null)
            {
                pipelineAsset = UniversalRenderPipelineAsset.Create(rendererData);
                pipelineAsset.name = "QuestMmdPlayerURP";
                AssetDatabase.CreateAsset(pipelineAsset, UrpAssetPath);
            }

            var pipelineSettings = new SerializedObject(pipelineAsset);
            SetRequiredBool(pipelineSettings, "m_SupportsTerrainHoles", false);
            SetRequiredBool(pipelineSettings, "m_SupportsHDR", false);
            pipelineSettings.ApplyModifiedPropertiesWithoutUndo();

            var rendererSettings = new SerializedObject(rendererData);
            var intermediateTextureMode = rendererSettings.FindProperty("m_IntermediateTextureMode");
            if (intermediateTextureMode == null)
            {
                throw new InvalidDataException("URP renderer is missing m_IntermediateTextureMode.");
            }
            intermediateTextureMode.intValue = 0;
            rendererSettings.ApplyModifiedPropertiesWithoutUndo();

            GraphicsSettings.defaultRenderPipeline = pipelineAsset;
            QualitySettings.renderPipeline = pipelineAsset;
            EditorUtility.SetDirty(rendererData);
            EditorUtility.SetDirty(pipelineAsset);
            AssetDatabase.SaveAssets();
            Debug.Log($"URP Passthrough rendering configured: {AssetDatabase.GetAssetPath(pipelineAsset)}");
        }

        private static void SetRequiredBool(SerializedObject settings, string propertyName, bool value)
        {
            var property = settings.FindProperty(propertyName);
            if (property == null)
            {
                throw new InvalidDataException($"URP asset is missing {propertyName}.");
            }
            property.boolValue = value;
        }
    }
}
#endif

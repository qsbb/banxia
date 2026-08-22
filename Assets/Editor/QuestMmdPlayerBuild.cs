#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;
using UnityEngine.XR.OpenXR.Features.Meta;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.Hands.OpenXR;

namespace QuestMmdPlayer.Editor
{
    internal sealed class QuestMmdPlayerBundledAvatarBuildGuard : IPreprocessBuildWithReport
    {
        public int callbackOrder => -10000;

        public void OnPreprocessBuild(BuildReport report)
        {
            QuestMmdPlayerBuild.ValidateNoBundledAvatarModels();
        }
    }

    public static class QuestMmdPlayerBuild
    {
        private const string ScenePath = "Assets/Scenes/Prototype.unity";
        private const string OutputPath = "Builds/Banxia.apk";
        private const string AndroidApplicationIdentifier = "com.lingxi.banxia";
        private const string AndroidVersionName = "0.2.21";
        private const int AndroidVersionCode = 31;
        private const string OpenXrLoader = "UnityEngine.XR.OpenXR.OpenXRLoader";
        private const string XrSettingsPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
        private const string EditorSimulationSettingsPath = "Assets/XR/Settings/XRSimulationSettings.asset";
        internal const string EditorSimulationSettingsConfigKey =
            "com.unity.xr.arfoundation.simulation_settings";
        private static readonly string[] RuntimeShaderNames =
        {
            "Universal Render Pipeline/Unlit",
            "QuestMmdPlayer/Avatar Outline"
        };
        private static readonly string[] BundledAvatarExtensions =
        {
            ".pmx", ".pmd", ".vrm", ".glb", ".gltf"
        };

        [MenuItem("伴夏/Build Android APK")]
        public static void BuildAndroidApk()
        {
            if (!File.Exists(ScenePath))
            {
                QuestMmdPlayerMenu.CreatePrototypeScene();
            }
            ValidateNoBundledAvatarModels();

            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.Android, BuildTarget.Android))
            {
                throw new BuildFailedException("Could not switch the active build target to Android.");
            }

            QuestMmdPlayerMenu.EnsureRenderPipeline();

            RestoreSimulationSettingsFromTemp();
            using (new PreloadedAssetsScope())
            {
                ConfigureOpenXr();
                RemoveEditorOnlyPreloadedAssets();
                ConfigureRuntimeShaders();
                EditorUserBuildSettings.buildAppBundle = false;
                PlayerSettings.companyName = "Banxia";
                PlayerSettings.productName = QuestMmdPlayerBootstrap.AndroidTaskLabel;
                PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.Android, AndroidApplicationIdentifier);
                PlayerSettings.bundleVersion = AndroidVersionName;
                PlayerSettings.Android.bundleVersionCode = AndroidVersionCode;
                PlayerSettings.colorSpace = ColorSpace.Linear;
                PlayerSettings.insecureHttpOption = InsecureHttpOption.AlwaysAllowed;
                PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
                PlayerSettings.SetScriptingBackend(BuildTargetGroup.Android, ScriptingImplementation.IL2CPP);
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
                PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;

                Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
                BuildReport report;
                using (new EditorBuildSettingsConfigScope(EditorSimulationSettingsConfigKey))
                {
                    report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                    {
                        scenes = new[] { ScenePath },
                        locationPathName = OutputPath,
                        target = BuildTarget.Android,
                        options = BuildOptions.None
                    });
                }

                if (report.summary.result != BuildResult.Succeeded)
                {
                    throw new BuildFailedException($"Android build failed: {report.summary.result}");
                }

                Debug.Log($"Android APK created: {report.summary.outputPath} ({report.summary.totalSize} bytes)");
            }
        }

        internal static void ValidateNoBundledAvatarModels(string assetsRootOverride = null)
        {
            var assetsRoot = string.IsNullOrWhiteSpace(assetsRootOverride)
                ? Application.dataPath
                : assetsRootOverride;
            if (!Directory.Exists(assetsRoot)) return;

            var bundledModels = Directory.GetFiles(assetsRoot, "*", SearchOption.AllDirectories)
                .Where(path => BundledAvatarExtensions.Contains(
                    Path.GetExtension(path),
                    StringComparer.OrdinalIgnoreCase))
                .Select(path => path.Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (bundledModels.Length == 0) return;

            throw new BuildFailedException(
                "Production Assets must not contain avatar model sources. Import them on-device instead: " +
                string.Join(", ", bundledModels));
        }

        private static void ConfigureRuntimeShaders()
        {
            var graphicsSettingsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset");
            if (graphicsSettingsAssets.Length == 0)
            {
                throw new BuildFailedException("Could not load GraphicsSettings.asset.");
            }

            var graphicsSettings = new SerializedObject(graphicsSettingsAssets[0]);
            var alwaysIncludedShaders = graphicsSettings.FindProperty("m_AlwaysIncludedShaders");
            if (alwaysIncludedShaders == null || !alwaysIncludedShaders.isArray)
            {
                throw new BuildFailedException("Could not read the always-included shader list.");
            }

            foreach (var shaderName in RuntimeShaderNames)
            {
                var shader = Shader.Find(shaderName);
                if (shader == null)
                {
                    throw new BuildFailedException($"Required runtime shader was not found: {shaderName}");
                }

                var alreadyIncluded = false;
                for (var index = 0; index < alwaysIncludedShaders.arraySize; index++)
                {
                    if (alwaysIncludedShaders.GetArrayElementAtIndex(index).objectReferenceValue == shader)
                    {
                        alreadyIncluded = true;
                        break;
                    }
                }

                if (!alreadyIncluded)
                {
                    var index = alwaysIncludedShaders.arraySize;
                    alwaysIncludedShaders.InsertArrayElementAtIndex(index);
                    alwaysIncludedShaders.GetArrayElementAtIndex(index).objectReferenceValue = shader;
                }
            }

            graphicsSettings.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        private static void RemoveEditorOnlyPreloadedAssets()
        {
            var preloadedAssets = PlayerSettings.GetPreloadedAssets();
            var filtered = preloadedAssets
                .Where(asset => asset == null || !string.Equals(
                    AssetDatabase.GetAssetPath(asset),
                    EditorSimulationSettingsPath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (filtered.Length == preloadedAssets.Length)
            {
                return;
            }

            PlayerSettings.SetPreloadedAssets(filtered);
            Debug.Log("Removed editor-only XR simulation settings from Android preloaded assets.");
        }

        internal sealed class EditorBuildSettingsConfigScope : IDisposable
        {
            private readonly string configKey;
            private readonly UnityEngine.Object originalValue;
            private readonly string originalAssetPath;
            private readonly bool restoreOriginal;
            private bool disposed;

            internal EditorBuildSettingsConfigScope(string configKey)
            {
                if (string.IsNullOrWhiteSpace(configKey))
                {
                    throw new ArgumentException("A config key is required.", nameof(configKey));
                }

                this.configKey = configKey;
                restoreOriginal = EditorBuildSettings.TryGetConfigObject(configKey, out originalValue);
                originalAssetPath = originalValue == null
                    ? string.Empty
                    : AssetDatabase.GetAssetPath(originalValue);
                if (restoreOriginal)
                {
                    EditorBuildSettings.RemoveConfigObject(configKey);
                    Debug.Log($"Temporarily removed editor-only build config: {configKey}");
                }
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                if (!restoreOriginal)
                {
                    return;
                }

                var valueToRestore = ResolveOriginalConfigObject(originalValue, originalAssetPath);
                if (valueToRestore == null)
                {
                    throw new InvalidOperationException(
                        $"Could not restore editor-only build config: {configKey}");
                }

                EditorBuildSettings.AddConfigObject(configKey, valueToRestore, true);
                AssetDatabase.SaveAssets();
                Debug.Log($"Restored editor-only build config: {configKey}");
            }

            internal static UnityEngine.Object ResolveOriginalConfigObject(
                UnityEngine.Object originalValue,
                string originalAssetPath)
            {
                if (originalValue != null)
                {
                    return originalValue;
                }

                return string.IsNullOrWhiteSpace(originalAssetPath)
                    ? null
                    : AssetDatabase.LoadMainAssetAtPath(originalAssetPath);
            }
        }

        internal sealed class PreloadedAssetsScope : IDisposable
        {
            private readonly UnityEngine.Object[] originalAssets = PlayerSettings.GetPreloadedAssets();
            private bool disposed;

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                PlayerSettings.SetPreloadedAssets(originalAssets);
                AssetDatabase.SaveAssets();
                Debug.Log("Restored preloaded assets after Android build.");
            }
        }

        private static void ConfigureOpenXr()
        {
            var guids = AssetDatabase.FindAssets("t:XRGeneralSettingsPerBuildTarget");
            XRGeneralSettingsPerBuildTarget buildTargetSettings;
            if (guids.Length == 0)
            {
                Directory.CreateDirectory("Assets/XR");
                buildTargetSettings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
                AssetDatabase.CreateAsset(buildTargetSettings, XrSettingsPath);
                AssetDatabase.SaveAssets();
            }
            else
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                buildTargetSettings = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(path);
            }

            if (buildTargetSettings == null)
            {
                throw new BuildFailedException("Could not create XR Management settings.");
            }

            if (!buildTargetSettings.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
            {
                buildTargetSettings.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            }

            var generalSettings = buildTargetSettings.SettingsForBuildTarget(BuildTargetGroup.Android);
            if (generalSettings == null || generalSettings.Manager == null)
            {
                throw new BuildFailedException("XR Management settings are not available for Android.");
            }

            if (!XRPackageMetadataStore.AssignLoader(generalSettings.Manager, OpenXrLoader, BuildTargetGroup.Android))
            {
                throw new BuildFailedException("Could not enable the Android OpenXR loader.");
            }

            var openXrSettings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            var metaQuestFeature = openXrSettings == null ? null : openXrSettings.GetFeature<MetaQuestFeature>();
            if (metaQuestFeature == null)
            {
                throw new BuildFailedException("Meta Quest Support is not available in Android OpenXR settings.");
            }

            ConfigureQuestInteractionProfiles(openXrSettings);
            DisableDeprecatedOculusQuestFeatures(openXrSettings);

            foreach (var feature in openXrSettings.GetFeatures<MetaQuestFeature>())
            {
                feature.enabled = true;
                var serializedFeature = new SerializedObject(feature);
                var forceRemoveInternetPermission = serializedFeature.FindProperty("forceRemoveInternetPermission");
                if (forceRemoveInternetPermission != null)
                {
                    forceRemoveInternetPermission.boolValue = false;
                    serializedFeature.ApplyModifiedPropertiesWithoutUndo();
                }

                EditorUtility.SetDirty(feature);
            }

            var arCameraFeatures = openXrSettings.GetFeatures<ARCameraFeature>();
            if (arCameraFeatures.Length == 0)
            {
                throw new BuildFailedException("Meta AR Camera (Passthrough) is not available in Android OpenXR settings.");
            }

            foreach (var arCameraFeature in arCameraFeatures)
            {
                arCameraFeature.enabled = true;
                EditorUtility.SetDirty(arCameraFeature);
            }

            var arSessionFeatures = openXrSettings.GetFeatures<ARSessionFeature>();
            if (arSessionFeatures.Length == 0)
            {
                throw new BuildFailedException("Meta AR Session is not available in Android OpenXR settings.");
            }

            foreach (var arSessionFeature in arSessionFeatures)
            {
                arSessionFeature.enabled = true;
                EditorUtility.SetDirty(arSessionFeature);
            }

            EnableRequiredOpenXrFeature<ARPlaneFeature>(openXrSettings, "Meta AR Plane Detection");
            EnableRequiredOpenXrFeature<ARRaycastFeature>(openXrSettings, "Meta AR Raycasts");
            EnableRequiredOpenXrFeature<DisplayUtilitiesFeature>(openXrSettings, "Meta Display Utilities");


            foreach (var handTrackingFeature in openXrSettings.GetFeatures<HandTracking>())
            {
                handTrackingFeature.enabled = true;
                EditorUtility.SetDirty(handTrackingFeature);
            }

            EditorUtility.SetDirty(buildTargetSettings);
            EditorUtility.SetDirty(generalSettings);
            EditorUtility.SetDirty(generalSettings.Manager);
            AssetDatabase.SaveAssets();
        }

        private static void EnableRequiredOpenXrFeature<T>(OpenXRSettings settings, string displayName)
            where T : OpenXRFeature
        {
            var features = settings.GetFeatures<T>();
            if (features.Length == 0)
            {
                throw new BuildFailedException($"{displayName} is not available in Android OpenXR settings.");
            }

            foreach (var feature in features)
            {
                feature.enabled = true;
                EditorUtility.SetDirty(feature);
            }
        }

        private static void ConfigureQuestInteractionProfiles(OpenXRSettings openXrSettings)
        {
            foreach (var interactionFeature in openXrSettings.GetFeatures<OpenXRInteractionFeature>())
            {
                var supported = interactionFeature is OculusTouchControllerProfile ||
                    interactionFeature is MetaQuestTouchProControllerProfile;
                if (interactionFeature.enabled != supported)
                {
                    interactionFeature.enabled = supported;
                    EditorUtility.SetDirty(interactionFeature);
                }
            }
        }

        private static void DisableDeprecatedOculusQuestFeatures(OpenXRSettings openXrSettings)
        {
            foreach (var feature in openXrSettings.GetFeatures())
            {
                if (feature.GetType().Name != "OculusQuestFeature")
                {
                    continue;
                }

                feature.enabled = false;
                EditorUtility.SetDirty(feature);
            }
        }

        private static void RestoreSimulationSettingsFromTemp()
        {
            const string tempDirectory = "Assets/XR/Temp";
            const string userSettingsDirectory = "Assets/XR/UserSimulationSettings/Resources";
            var fileNames = new[]
            {
                "XRSimulationPreferences.asset",
                "XRSimulationRuntimeSettings.asset"
            };

            foreach (var fileName in fileNames)
            {
                var tempPath = $"{tempDirectory}/{fileName}";
                var userPath = $"{userSettingsDirectory}/{fileName}";
                var tempAsset = AssetDatabase.LoadMainAssetAtPath(tempPath);
                if (tempAsset == null)
                {
                    continue;
                }

                if (AssetDatabase.LoadMainAssetAtPath(userPath) != null)
                {
                    if (!AssetDatabase.DeleteAsset(tempPath))
                    {
                        throw new BuildFailedException($"Could not remove stale XR Simulation asset {fileName}.");
                    }

                    continue;
                }

                var moveError = AssetDatabase.MoveAsset(tempPath, userPath);
                if (!string.IsNullOrEmpty(moveError))
                {
                    throw new BuildFailedException($"Could not restore XR Simulation asset {fileName}: {moveError}");
                }
            }

            AssetDatabase.Refresh();
        }
    }
}
#endif

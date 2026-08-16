#if UNITY_EDITOR
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using QuestMmdPlayer.Editor;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestMmdPlayerBuildTests
    {
        private string temporaryDirectory;

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(temporaryDirectory) && Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, true);
        }

        [Test]
        public void ProductionBuildInputContainsNoBundledAvatarModels()
        {
            Assert.DoesNotThrow(() => QuestMmdPlayerBuild.ValidateNoBundledAvatarModels());
        }

        [Test]
        public void AndroidNativePhysicsLibrariesMatchAndExposeConfigApi()
        {
            var paths = new[]
            {
                Path.Combine("Assets", "Plugins", "Android", "arm64-v8a", "libUMTNativePlugin.so"),
                Path.Combine("Packages", "com.candidumgames.unitymmdtools", "Plugins", "Android", "arm64-v8a", "libUMTNativePlugin.so")
            };
            var first = File.ReadAllBytes(paths[0]);
            var second = File.ReadAllBytes(paths[1]);

            Assert.That(first.Length, Is.GreaterThan(1024 * 1024));
            Assert.That(Encoding.ASCII.GetString(first), Does.Contain("MMDBulletPhysicsSetConfig"));
            CollectionAssert.AreEqual(first, second);
            Assert.That(File.Exists(Path.Combine(
                "Assets", "Plugins", "Android", "arm64-v8a", "libc++_shared.so")), Is.True);
        }

        [Test]
        public void EditorProjectKeepsXrSimulationSettingsAvailable()
        {
            var settings = File.ReadAllText(Path.Combine(
                Directory.GetCurrentDirectory(),
                "ProjectSettings",
                "ProjectSettings.asset"));

            Assert.That(settings, Does.Contain("95dd2578b466e9c4e8351e2510c43c14"));
            Assert.That(settings, Does.Contain("03e1a762918a63944853fff14acce044"));
            Assert.That(settings, Does.Contain("4ad4ed07a8ef21b4cbd8d65faa9366bf"));
        }

        [Test]
        public void AndroidBuildTemporarilyExcludesEditorOnlyXrSimulationSettings()
        {
            const string simulationPath = "Assets/XR/Settings/XRSimulationSettings.asset";
            var method = typeof(QuestMmdPlayerBuild).GetMethod(
                "RemoveEditorOnlyPreloadedAssets",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            Assert.That(PlayerSettings.GetPreloadedAssets().Any(asset =>
                asset != null && AssetDatabase.GetAssetPath(asset) == simulationPath), Is.True);

            using (new QuestMmdPlayerBuild.PreloadedAssetsScope())
            {
                method.Invoke(null, null);
                Assert.That(PlayerSettings.GetPreloadedAssets().Any(asset =>
                    asset != null && AssetDatabase.GetAssetPath(asset) == simulationPath), Is.False);
            }

            Assert.That(PlayerSettings.GetPreloadedAssets().Any(asset =>
                asset != null && AssetDatabase.GetAssetPath(asset) == simulationPath), Is.True);
        }

        [Test]
        public void AndroidBuildScopeTemporarilyRemovesAndRestoresSimulationConfig()
        {
            Assert.That(EditorBuildSettings.TryGetConfigObject(
                QuestMmdPlayerBuild.EditorSimulationSettingsConfigKey,
                out Object original), Is.True);
            Assert.That(original, Is.Not.Null);

            using (new QuestMmdPlayerBuild.EditorBuildSettingsConfigScope(
                QuestMmdPlayerBuild.EditorSimulationSettingsConfigKey))
            {
                Assert.That(EditorBuildSettings.TryGetConfigObject(
                    QuestMmdPlayerBuild.EditorSimulationSettingsConfigKey,
                    out Object removed), Is.False);
                Assert.That(removed, Is.Null);
            }

            Assert.That(EditorBuildSettings.TryGetConfigObject(
                QuestMmdPlayerBuild.EditorSimulationSettingsConfigKey,
                out Object restored), Is.True);
            Assert.That(restored, Is.SameAs(original));
        }

        [Test]
        public void AndroidBuildScopeCanReloadSimulationConfigAfterUnityUnloadsReference()
        {
            const string path = "Assets/XR/Settings/XRSimulationSettings.asset";

            var restored = QuestMmdPlayerBuild.EditorBuildSettingsConfigScope
                .ResolveOriginalConfigObject(null, path);

            Assert.That(restored, Is.Not.Null);
            Assert.That(AssetDatabase.GetAssetPath(restored), Is.EqualTo(path));
        }

        [Test]
        public void AndroidBuildScopeRestoresPreloadedAssets()
        {
            var original = PlayerSettings.GetPreloadedAssets();
            using (new QuestMmdPlayerBuild.PreloadedAssetsScope())
            {
                PlayerSettings.SetPreloadedAssets(new Object[0]);
            }

            Assert.That(PlayerSettings.GetPreloadedAssets(), Is.EqualTo(original));
        }

        [Test]
        public void ProductionBuildRejectsAvatarModelsInStreamingAssets()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-build-gate-" + System.Guid.NewGuid());
            Directory.CreateDirectory(temporaryDirectory);
            File.WriteAllBytes(Path.Combine(temporaryDirectory, "avatar.pmx"), new byte[] { 1 });

            Assert.Throws<BuildFailedException>(
                () => QuestMmdPlayerBuild.ValidateNoBundledAvatarModels(temporaryDirectory));
        }

        [Test]
        public void RetiredBundledSampleCleanupPreservesUnknownAndChangedFiles()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-retired-sample-" + System.Guid.NewGuid());
            var sampleDirectory = Path.Combine(temporaryDirectory, "ForestBerry");
            Directory.CreateDirectory(sampleDirectory);
            var pmx = Path.Combine(sampleDirectory, "ForestBerry.pmx");
            File.WriteAllText(pmx, "user model with the old name");
            var userFile = Path.Combine(sampleDirectory, "user-notes.txt");
            File.WriteAllText(userFile, "keep me");

            Assert.That(RemoveRetiredBundledSample(temporaryDirectory), Is.EqualTo(0));
            Assert.IsTrue(File.Exists(pmx));
            Assert.IsTrue(File.Exists(userFile));

            var retiredBytes = new byte[] { 1, 2, 3, 4, 5 };
            File.WriteAllBytes(pmx, retiredBytes);
            var hash = System.BitConverter.ToString(SHA256.Create().ComputeHash(retiredBytes))
                .Replace("-", string.Empty);
            var expected = new Dictionary<string, string>
            {
                { "ForestBerry.pmx", hash }
            };
            Assert.That(RemoveRetiredBundledSample(temporaryDirectory, expected), Is.EqualTo(1));
            Assert.IsFalse(File.Exists(pmx));
            Assert.IsTrue(File.Exists(userFile));
            Assert.IsTrue(Directory.Exists(sampleDirectory));
        }

        [Test]
        public void StandardUnityBuildCallbackAlsoRejectsBundledAvatarModels()
        {
            Assert.DoesNotThrow(
                () => new QuestMmdPlayerBundledAvatarBuildGuard().OnPreprocessBuild(null));
        }

        [Test]
        public void ModelDiscoveryIncludesNestedUppercasePmxAndKeepsPackageRoot()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-model-catalog-" + System.Guid.NewGuid());
            var package = Path.Combine(temporaryDirectory, "Imported", "完整模型包");
            var nested = Path.Combine(package, "角色", "变体");
            Directory.CreateDirectory(nested);
            var model = Path.Combine(nested, "角色.PMX");
            File.WriteAllBytes(model, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(nested, "忽略.txt"), new byte[] { 1 });

            var discovered = DiscoverInstalledModels(temporaryDirectory);

            Assert.That(discovered.Count, Is.EqualTo(1));
            Assert.That(discovered[0].DisplayName, Is.EqualTo("角色"));
            Assert.That(discovered[0].Path, Is.EqualTo(Path.GetFullPath(model)));
            Assert.That(discovered[0].PackageRoot, Is.EqualTo(Path.GetFullPath(package)));
        }

        [Test]
        public void ModelDiscoveryKeepsEveryPmxVariantInStableOrder()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-multi-model-" + System.Guid.NewGuid());
            var package = Path.Combine(temporaryDirectory, "Imported", "多模型包");
            Directory.CreateDirectory(Path.Combine(package, "版本B"));
            Directory.CreateDirectory(Path.Combine(package, "版本A"));
            var upper = Path.Combine(package, "版本B", "角色.PMX");
            var lower = Path.Combine(package, "版本A", "角色.pmx");
            File.WriteAllBytes(upper, new byte[] { 1 });
            File.WriteAllBytes(lower, new byte[] { 2 });

            var discovered = DiscoverInstalledModels(temporaryDirectory);

            Assert.That(discovered.Select(model => model.Path), Is.EqualTo(new[]
            {
                Path.GetFullPath(lower),
                Path.GetFullPath(upper)
            }));
            Assert.That(discovered.Select(model => model.DisplayName), Is.EqualTo(new[]
            {
                "角色",
                "角色 (2)"
            }));
            Assert.That(discovered.All(model => model.PackageRoot == Path.GetFullPath(package)), Is.True);
        }

        [Test]
        public void PackageRootResolutionCannotPromoteNestedFolderAboveImportedPackage()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-package-root-" + System.Guid.NewGuid());
            var modelsRoot = Path.Combine(temporaryDirectory, "MmdModels");
            var package = Path.Combine(modelsRoot, "Imported", "包A");
            var nestedModel = Path.Combine(package, "角色", "变体", "角色.pmx");

            Assert.That(ResolvePackageRoot(modelsRoot, nestedModel), Is.EqualTo(Path.GetFullPath(package)));
        }

        [Test]
        public void DeleteInstalledPackageOnlyAcceptsDirectImportedPackageAndRejectsCurrentModel()
        {
            var packageName = "banxia-delete-boundary-" + System.Guid.NewGuid().ToString("N");
            var modelsRoot = Path.Combine(Application.persistentDataPath, "MmdModels");
            var package = Path.Combine(modelsRoot, "Imported", packageName);
            var nested = Path.Combine(package, "角色");
            var modelPath = Path.Combine(nested, "角色.pmx");
            var outside = Path.Combine(Application.persistentDataPath, packageName + "-outside");
            Directory.CreateDirectory(nested);
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(modelPath, new byte[] { 1 });
            var host = new GameObject("Delete installed model package boundary test");
            var loader = host.AddComponent<RuntimeMmdModelLoader>();
            try
            {
                Assert.That(loader.DeleteInstalledPackage(
                    new RuntimeMmdModelInfo("nested", modelPath, nested)), Is.False);
                Assert.That(Directory.Exists(nested), Is.True);
                Assert.That(loader.DeleteInstalledPackage(
                    new RuntimeMmdModelInfo("outside", modelPath, outside)), Is.False);
                Assert.That(Directory.Exists(outside), Is.True);

                SetCurrentModelPath(loader, modelPath);
                Assert.That(loader.DeleteInstalledPackage(
                    new RuntimeMmdModelInfo("current", modelPath, package)), Is.False);
                Assert.That(Directory.Exists(package), Is.True);

                SetCurrentModelPath(loader, null);
                Assert.That(loader.DeleteInstalledPackage(
                    new RuntimeMmdModelInfo("installed", modelPath, package)), Is.True);
                Assert.That(Directory.Exists(package), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (Directory.Exists(package)) Directory.Delete(package, true);
                if (Directory.Exists(outside)) Directory.Delete(outside, true);
            }
        }

        [Test]
        public void ModelDiscoverySkipsEmptyPmxFiles()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-empty-model-" + System.Guid.NewGuid());
            Directory.CreateDirectory(temporaryDirectory);
            File.WriteAllBytes(Path.Combine(temporaryDirectory, "empty.pmx"), new byte[0]);

            Assert.That(DiscoverInstalledModels(temporaryDirectory), Is.Empty);
        }

        [Test]
        public void CorruptedFileNameFallsBackToEmbeddedPmxModelName()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-pmx-display-name-" + System.Guid.NewGuid());
            Directory.CreateDirectory(temporaryDirectory);
            var model = Path.Combine(temporaryDirectory, "����.pmx");
            WriteMinimalPmxHeader(model, "休日冒险");

            Assert.That(ResolveDisplayName(model), Is.EqualTo("休日冒险"));
        }

        [Test]
        public void UnreadableCorruptedFileNameUsesLocalizedFallback()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-pmx-display-fallback-" + System.Guid.NewGuid());
            Directory.CreateDirectory(temporaryDirectory);
            var model = Path.Combine(temporaryDirectory, "����.pmx");
            File.WriteAllBytes(model, new byte[] { 1, 2, 3 });

            Assert.That(ResolveDisplayName(model), Is.EqualTo("已导入角色"));
        }

        [Test]
        public void IdenticalPmxWithDifferentResourceSetsRemainSelectable()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-pmx-duplicate-" + System.Guid.NewGuid());
            var imported = Path.Combine(temporaryDirectory, "Imported");
            var complete = Path.Combine(imported, "完整模型包");
            var incomplete = Path.Combine(imported, "旧单文件");
            Directory.CreateDirectory(complete);
            Directory.CreateDirectory(incomplete);
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var completeModel = Path.Combine(complete, "角色.pmx");
            File.WriteAllBytes(completeModel, bytes);
            File.WriteAllBytes(Path.Combine(incomplete, "角色.pmx"), bytes);
            File.WriteAllBytes(Path.Combine(complete, "face.png"), new byte[] { 9 });

            var discovered = DiscoverInstalledModels(temporaryDirectory);

            Assert.That(discovered.Count, Is.EqualTo(2));
            CollectionAssert.AreEquivalent(
                new[]
                {
                    Path.GetFullPath(completeModel),
                    Path.GetFullPath(Path.Combine(incomplete, "角色.pmx"))
                },
                discovered.Select(model => model.Path));
        }

        [Test]
        public void IdenticalPmxInSeparatePackagesKeepIndependentNames()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-pmx-name-recovery-" + System.Guid.NewGuid());
            var complete = Path.Combine(temporaryDirectory, "Imported", "完整模型包");
            var legacy = Path.Combine(temporaryDirectory, "Imported", "旧导入");
            Directory.CreateDirectory(complete);
            Directory.CreateDirectory(legacy);
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            var completeModel = Path.Combine(complete, "����.pmx");
            File.WriteAllBytes(completeModel, bytes);
            File.WriteAllBytes(Path.Combine(legacy, "休日冒险.pmx"), bytes);
            File.WriteAllBytes(Path.Combine(complete, "face.png"), new byte[] { 9 });

            var discovered = DiscoverInstalledModels(temporaryDirectory);

            Assert.That(discovered.Count, Is.EqualTo(2));
            Assert.That(discovered.Any(model => model.DisplayName == "休日冒险"), Is.True);
            Assert.That(discovered.Any(model => model.Path == Path.GetFullPath(completeModel)), Is.True);
        }

        [Test]
        public void SameSizedDifferentPmxVariantsAreNotMerged()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-pmx-same-size-" + System.Guid.NewGuid());
            var firstPackage = Path.Combine(temporaryDirectory, "Imported", "A");
            var secondPackage = Path.Combine(temporaryDirectory, "Imported", "B");
            Directory.CreateDirectory(firstPackage);
            Directory.CreateDirectory(secondPackage);
            File.WriteAllBytes(Path.Combine(firstPackage, "角色.pmx"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(secondPackage, "角色.pmx"), new byte[] { 3, 2, 1 });

            Assert.That(DiscoverInstalledModels(temporaryDirectory).Count, Is.EqualTo(2));
        }

        [Test]
        public void ParsedModelCacheNeverEvictsAFreshEntryDuringCapacityTrim()
        {
            Assert.That(ShouldRetainParsedModelCacheEntry(false, 0f), Is.True);
            Assert.That(ShouldRetainParsedModelCacheEntry(false, 179.9f), Is.True);
            Assert.That(ShouldRetainParsedModelCacheEntry(false, 180f), Is.False);
            Assert.That(ShouldRetainParsedModelCacheEntry(true, 1000f), Is.True);
        }

        [Test]
        public void SelectedModelPathPersistsAcrossLoaderInstances()
        {
            const string preferenceKey = "Banxia.RuntimeMmdModel.SelectedPath";
            const string relativePreferenceKey = "Banxia.RuntimeMmdModel.SelectedRelativePath";
            temporaryDirectory = Path.Combine(
                Application.persistentDataPath,
                "MmdModels",
                "Imported",
                "banxia-persistence-test-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var modelPath = Path.Combine(temporaryDirectory, "角色.pmx");
            File.WriteAllBytes(modelPath, new byte[] { 1, 2, 3 });
            var firstObject = new GameObject("Model persistence writer");
            var secondObject = new GameObject("Model persistence reader");
            PlayerPrefs.DeleteKey(preferenceKey);
            PlayerPrefs.DeleteKey(relativePreferenceKey);
            try
            {
                var first = firstObject.AddComponent<RuntimeMmdModelLoader>();
                RememberSelectedModel(first, modelPath);
                Assert.That(first.SavedModelPath, Is.EqualTo(Path.GetFullPath(modelPath)));
                Assert.That(
                    first.SavedModelRelativePath.Replace('\\', '/'),
                    Does.StartWith("Imported/banxia-persistence-test-"));

                Object.DestroyImmediate(firstObject);
                var second = secondObject.AddComponent<RuntimeMmdModelLoader>();
                Assert.That(second.SavedModelPath, Is.EqualTo(Path.GetFullPath(modelPath)));
                Assert.That(
                    second.SavedModelRelativePath,
                    Is.EqualTo(first.SavedModelRelativePath));
            }
            finally
            {
                PlayerPrefs.DeleteKey(preferenceKey);
                PlayerPrefs.DeleteKey(relativePreferenceKey);
                PlayerPrefs.Save();
                if (firstObject != null) Object.DestroyImmediate(firstObject);
                if (secondObject != null) Object.DestroyImmediate(secondObject);
            }
        }

        [Test]
        public void SavedModelRelativePathSurvivesAndroidStorageAliasChanges()
        {
            var installed = new RuntimeMmdModelInfo(
                "Forest Berry",
                "/storage/emulated/0/Android/data/com.lingxi.banxia/files/" +
                    "MmdModels/Imported/ForestBerry/ForestBerry.pmx");
            var resolved = FindSavedModel(
                "/storage/emulated/0/Android/data/com.lingxi.banxia/files/MmdModels",
                new[] { installed },
                "/sdcard/Android/data/com.lingxi.banxia/files/" +
                    "MmdModels/Imported/ForestBerry/ForestBerry.pmx",
                string.Empty);

            Assert.That(resolved, Is.SameAs(installed));
        }

        [Test]
        public void SavedModelRelativePathRejectsTraversalAndMatchesExactModel()
        {
            var first = new RuntimeMmdModelInfo(
                "First",
                "/storage/emulated/0/app/MmdModels/Imported/First/model.pmx");
            var second = new RuntimeMmdModelInfo(
                "Second",
                "/storage/emulated/0/app/MmdModels/Imported/Second/model.pmx");
            var models = new[] { first, second };

            Assert.That(
                FindSavedModel(
                    "/storage/emulated/0/app/MmdModels",
                    models,
                    string.Empty,
                    "Imported/Second/model.pmx"),
                Is.SameAs(second));
            Assert.That(
                FindSavedModel(
                    "/storage/emulated/0/app/MmdModels",
                    models,
                    string.Empty,
                    "../Imported/First/model.pmx"),
                Is.Null);
        }

        [Test]
        public void AndroidStorageAliasesAreAcceptedOnlyInsideTheModelRoot()
        {
            Assert.That(
                IsPathWithin(
                    "/storage/emulated/0/Android/data/com.lingxi.banxia/files/MmdModels",
                    "/sdcard/Android/data/com.lingxi.banxia/files/MmdModels/Imported/Forest/model.pmx"),
                Is.True);
            Assert.That(
                IsPathWithin(
                    "/storage/emulated/0/Android/data/com.lingxi.banxia/files/MmdModels",
                    "/sdcard/Android/data/com.lingxi.banxia/files/Other/model.pmx"),
                Is.False);
        }

        [Test]
        public void MissingSavedModelReportsRestoreFailureWithoutClearingSelection()
        {
            const string preferenceKey = "Banxia.RuntimeMmdModel.SelectedPath";
            const string relativePreferenceKey = "Banxia.RuntimeMmdModel.SelectedRelativePath";
            var previousPath = PlayerPrefs.GetString(preferenceKey, string.Empty);
            var previousRelativePath = PlayerPrefs.GetString(relativePreferenceKey, string.Empty);
            var missingPath = Path.Combine(
                Application.persistentDataPath,
                "MmdModels",
                "Imported",
                "missing-model-package",
                "missing.pmx");
            var root = new GameObject("Missing model restore test");
            try
            {
                PlayerPrefs.SetString(preferenceKey, missingPath);
                PlayerPrefs.SetString(
                    relativePreferenceKey,
                    "Imported/missing-model-package/missing.pmx");
                PlayerPrefs.Save();
                var loader = root.AddComponent<RuntimeMmdModelLoader>();
                bool? reportedResult = null;
                loader.LastModelRestoreCompleted += restored => reportedResult = restored;

                var restored = loader.RestoreLastModelAsync().GetAwaiter().GetResult();

                Assert.That(restored, Is.False);
                Assert.That(reportedResult, Is.False);
                Assert.That(loader.CurrentModel, Is.Null);
                Assert.That(PlayerPrefs.GetString(preferenceKey), Is.EqualTo(missingPath));
                Assert.That(
                    PlayerPrefs.GetString(relativePreferenceKey),
                    Is.EqualTo("Imported/missing-model-package/missing.pmx"));
            }
            finally
            {
                if (string.IsNullOrEmpty(previousPath)) PlayerPrefs.DeleteKey(preferenceKey);
                else PlayerPrefs.SetString(preferenceKey, previousPath);
                if (string.IsNullOrEmpty(previousRelativePath))
                {
                    PlayerPrefs.DeleteKey(relativePreferenceKey);
                }
                else
                {
                    PlayerPrefs.SetString(relativePreferenceKey, previousRelativePath);
                }
                PlayerPrefs.Save();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LoadFrameMetricsBeginAfterThePrimingFrame()
        {
            Assert.That(
                RuntimeMmdModelLoader.ShouldCountLoadFrame(true, 40, int.MaxValue),
                Is.False);
            Assert.That(
                RuntimeMmdModelLoader.ShouldCountLoadFrame(true, 40, 40),
                Is.False);
            Assert.That(
                RuntimeMmdModelLoader.ShouldCountLoadFrame(true, 41, 40),
                Is.True);
            Assert.That(
                RuntimeMmdModelLoader.ShouldCountLoadFrame(false, 41, 40),
                Is.False);
        }

        private static IReadOnlyList<RuntimeMmdModelInfo> DiscoverInstalledModels(string root)
        {
            var method = typeof(RuntimeMmdModelLoader).GetMethod(
                "DiscoverInstalledModels",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (IReadOnlyList<RuntimeMmdModelInfo>)method.Invoke(null, new object[] { root });
        }

        private static RuntimeMmdModelInfo FindSavedModel(
            string root,
            IReadOnlyList<RuntimeMmdModelInfo> models,
            string absolutePath,
            string relativePath)
        {
            var method = typeof(RuntimeMmdModelLoader).GetMethod(
                "FindSavedModel",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (RuntimeMmdModelInfo)method.Invoke(
                null,
                new object[] { root, models, absolutePath, relativePath });
        }

        private static bool IsPathWithin(string root, string path)
        {
            var method = typeof(RuntimeMmdModelLoader).GetMethod(
                "IsPathWithin",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, new object[] { root, path });
        }

        private static bool ShouldRetainParsedModelCacheEntry(
            bool isCurrent,
            float ageSeconds)
        {
            var method = typeof(RuntimeMmdModelLoader).GetMethod(
                "ShouldRetainParsedModelCacheEntry",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(null, new object[] { isCurrent, ageSeconds });
        }

        private static void RememberSelectedModel(
            RuntimeMmdModelLoader loader,
            string modelPath)
        {
            var method = typeof(RuntimeMmdModelLoader).GetMethod(
                "RememberSelectedModel",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            method.Invoke(loader, new object[] { modelPath });
        }

        private static string ResolveDisplayName(string model)
        {
            var method = typeof(RuntimeMmdModelLoader).GetMethod(
                "ResolveDisplayName",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (string)method.Invoke(null, new object[] { model });
        }

        private static void WriteMinimalPmxHeader(string path, string name)
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, false))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("PMX "));
                writer.Write(2.0f);
                writer.Write((byte)8);
                writer.Write((byte)1);
                writer.Write(new byte[7]);
                var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
                writer.Write(nameBytes.Length);
                writer.Write(nameBytes);
            }
        }

        private static string ResolvePackageRoot(string root, string model)
        {
            var method = typeof(RuntimeMmdModelLoader).GetMethod(
                "ResolvePackageRoot",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (string)method.Invoke(null, new object[] { root, model });
        }

        private static void SetCurrentModelPath(RuntimeMmdModelLoader loader, string value)
        {
            var property = typeof(RuntimeMmdModelLoader).GetProperty(
                "CurrentModelPath",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null);
            property.SetValue(loader, value);
        }

        private static int RemoveRetiredBundledSample(
            string root,
            IReadOnlyDictionary<string, string> expected = null)
        {
            var method = typeof(RuntimeMmdModelLoader).GetMethod(
                "RemoveRetiredBundledSample",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (int)method.Invoke(null, new object[] { root, expected });
        }
    }
}
#endif

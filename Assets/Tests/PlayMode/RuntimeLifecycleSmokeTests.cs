using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace QuestMmdPlayer.PlayModeTests
{
    public sealed class RuntimeLifecycleSmokeTests
    {
        [UnityTest]
        public IEnumerator OptionalRealPmxUsesTheRuntimeBudgetedPipeline()
        {
            var pmxPath = Environment.GetEnvironmentVariable("BANXIA_TEST_PMX");
            if (string.IsNullOrWhiteSpace(pmxPath) || !File.Exists(pmxPath))
            {
                Assert.Ignore("BANXIA_TEST_PMX is not configured for this run.");
            }

            var root = new GameObject("Real PMX runtime pipeline test");
            try
            {
                var loader = root.AddComponent(RuntimeType(
                    "QuestMmdPlayer.RuntimeMmdModelLoader"));
                // The test owns model selection; suppress the component's
                // next-frame PlayerPrefs restore so both loads cannot race.
                SetField(loader, "restoreStarted", true);
                var task = (Task)Invoke(
                    loader,
                    "LoadFromFileAsync",
                    pmxPath,
                    Path.GetDirectoryName(pmxPath));
                while (!task.IsCompleted)
                {
                    yield return null;
                }
                if (task.IsFaulted)
                {
                    throw task.Exception?.GetBaseException() ??
                        new InvalidOperationException("Real PMX load failed.");
                }

                var loadedModel = Read<GameObject>(loader, "CurrentModel");
                Assert.That(loadedModel, Is.Not.Null);
                Assert.That(Read<int>(loader, "LastLoadFrameCount"), Is.GreaterThan(0));
                Assert.That(
                    Read<float>(loader, "LastLoadMaximumFrameMilliseconds"),
                    Is.LessThan(100f),
                    "A real PMX first load must not regress to a multi-hundred-millisecond main-thread stall.");
                var auditedMaterials = 0;
                foreach (var material in loadedModel
                    .GetComponentsInChildren<Renderer>(true)
                    .SelectMany(renderer => renderer.sharedMaterials)
                    .Where(material => material != null)
                    .Distinct())
                {
                    if (!material.HasProperty("_Color") || !material.HasProperty("_BaseColor"))
                    {
                        continue;
                    }
                    auditedMaterials++;
                    var legacyColor = material.GetColor("_Color");
                    var urpColor = material.GetColor("_BaseColor");
                    Assert.That(urpColor.r, Is.EqualTo(legacyColor.r).Within(.002f), material.name);
                    Assert.That(urpColor.g, Is.EqualTo(legacyColor.g).Within(.002f), material.name);
                    Assert.That(urpColor.b, Is.EqualTo(legacyColor.b).Within(.002f), material.name);
                    Assert.That(urpColor.a, Is.EqualTo(legacyColor.a).Within(.002f), material.name);
                }
                Assert.That(auditedMaterials, Is.GreaterThan(0));
                Debug.Log(
                    "[Real PMX Runtime Test] totalMs=" + Read<int>(loader, "LastLoadMilliseconds") +
                    " frames=" + Read<int>(loader, "LastLoadFrameCount") +
                    " longFrames=" + Read<int>(loader, "LastLoadLongFrameCount") +
                    " maxFrameMs=" + Read<float>(loader, "LastLoadMaximumFrameMilliseconds").ToString("F1") +
                    " parsedCache=" + Read<bool>(loader, "LastLoadUsedParsedCache") +
                    " urpMaterials=" + auditedMaterials);

                var cachedTask = (Task)Invoke(
                    loader,
                    "LoadFromFileAsync",
                    pmxPath,
                    Path.GetDirectoryName(pmxPath));
                while (!cachedTask.IsCompleted)
                {
                    yield return null;
                }
                if (cachedTask.IsFaulted)
                {
                    throw cachedTask.Exception?.GetBaseException() ??
                        new InvalidOperationException("Cached PMX load failed.");
                }
                Assert.That(Read<bool>(loader, "LastLoadUsedParsedCache"), Is.True);
                Assert.That(Read<int>(loader, "LastReadMilliseconds"), Is.Zero);
                Debug.Log(
                    "[Real PMX Runtime Cache Test] totalMs=" + Read<int>(loader, "LastLoadMilliseconds") +
                    " frames=" + Read<int>(loader, "LastLoadFrameCount") +
                    " longFrames=" + Read<int>(loader, "LastLoadLongFrameCount") +
                    " maxFrameMs=" + Read<float>(loader, "LastLoadMaximumFrameMilliseconds").ToString("F1"));
            }
            finally
            {
                UnityEngine.Object.Destroy(root);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator OptionalRealPmxSelectionRestoresAfterLoaderRecreation()
        {
            const string preferenceKey = "Banxia.RuntimeMmdModel.SelectedPath";
            var sourcePmxPath = Environment.GetEnvironmentVariable("BANXIA_TEST_PMX");
            if (string.IsNullOrWhiteSpace(sourcePmxPath) || !File.Exists(sourcePmxPath))
            {
                Assert.Ignore("BANXIA_TEST_PMX is not configured for this run.");
            }

            var previousSelection = PlayerPrefs.GetString(preferenceKey, string.Empty);
            var packageRoot = Path.Combine(
                Application.persistentDataPath,
                "MmdModels",
                "Imported",
                "banxia-real-restore-" + Guid.NewGuid().ToString("N"));
            var copiedPmxPath = Path.Combine(packageRoot, Path.GetFileName(sourcePmxPath));
            GameObject firstRoot = null;
            GameObject restoredRoot = null;
            try
            {
                Directory.CreateDirectory(packageRoot);
                foreach (var sourcePath in Directory.GetFiles(Path.GetDirectoryName(sourcePmxPath)))
                {
                    File.Copy(
                        sourcePath,
                        Path.Combine(packageRoot, Path.GetFileName(sourcePath)),
                        true);
                }

                firstRoot = new GameObject("Real PMX persistence writer");
                var firstLoader = firstRoot.AddComponent(RuntimeType(
                    "QuestMmdPlayer.RuntimeMmdModelLoader"));
                SetField(firstLoader, "restoreStarted", true);
                var firstLoad = (Task)Invoke(
                    firstLoader,
                    "LoadFromFileAsync",
                    copiedPmxPath,
                    packageRoot);
                while (!firstLoad.IsCompleted)
                {
                    yield return null;
                }
                if (firstLoad.IsFaulted)
                {
                    throw firstLoad.Exception?.GetBaseException() ??
                        new InvalidOperationException("Real PMX persistence setup failed.");
                }
                Assert.That(
                    PlayerPrefs.GetString(preferenceKey, string.Empty),
                    Is.EqualTo(Path.GetFullPath(copiedPmxPath)));

                UnityEngine.Object.Destroy(firstRoot);
                firstRoot = null;
                yield return null;

                restoredRoot = new GameObject("Real PMX persistence reader");
                var restoredLoader = restoredRoot.AddComponent(RuntimeType(
                    "QuestMmdPlayer.RuntimeMmdModelLoader"));
                SetField(restoredLoader, "restoreStarted", true);
                var restoreTask = (Task<bool>)Invoke(restoredLoader, "RestoreLastModelAsync");
                while (!restoreTask.IsCompleted)
                {
                    yield return null;
                }
                if (restoreTask.IsFaulted)
                {
                    throw restoreTask.Exception?.GetBaseException() ??
                        new InvalidOperationException("Real PMX startup restore failed.");
                }

                Assert.That(restoreTask.Result, Is.True);
                Assert.That(Read<object>(restoredLoader, "CurrentModel"), Is.Not.Null);
                Assert.That(
                    Read<string>(restoredLoader, "CurrentModelPath"),
                    Is.EqualTo(Path.GetFullPath(copiedPmxPath)).IgnoreCase);
                Debug.Log(
                    "[Real PMX Restore Test] restored=true totalMs=" +
                    Read<int>(restoredLoader, "LastLoadMilliseconds") +
                    " model=" + Path.GetFileNameWithoutExtension(copiedPmxPath));
            }
            finally
            {
                if (firstRoot != null) UnityEngine.Object.Destroy(firstRoot);
                if (restoredRoot != null) UnityEngine.Object.Destroy(restoredRoot);
                if (string.IsNullOrEmpty(previousSelection))
                {
                    PlayerPrefs.DeleteKey(preferenceKey);
                }
                else
                {
                    PlayerPrefs.SetString(preferenceKey, previousSelection);
                }
                PlayerPrefs.Save();
                if (Directory.Exists(packageRoot)) Directory.Delete(packageRoot, true);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator PcmStreamCanChangeFormatClearAndDestroyWithoutRetainingAudio()
        {
            var root = new GameObject("PCM runtime lifecycle test");
            try
            {
                var player = root.AddComponent(RuntimeType(
                    "QuestMmdPlayer.Pcm16StreamAudioPlayer"));
                yield return null;

                var firstGeneration = (int)Invoke(player, "BeginStream");
                Invoke(player, "Enqueue", new short[1024], 24000);
                Assert.That(Read<float>(player, "BufferedSeconds"),
                    Is.EqualTo(1024f / 24000f).Within(.002f));
                Assert.That(Read<int>(player, "QueuedChunkCount"), Is.EqualTo(1));
                Assert.That(Read<bool>(player, "StreamCompleted"), Is.False);

                // A source format change must discard the old queue instead of
                // playing samples at the wrong rate. This path only exists at runtime.
                Invoke(player, "Enqueue", new short[640], 16000);
                Assert.That(Read<float>(player, "BufferedSeconds"),
                    Is.EqualTo(640f / 16000f).Within(.002f));
                Assert.That(Read<int>(player, "QueuedChunkCount"), Is.EqualTo(1));

                Invoke(player, "StopAndClear");
                Assert.That(Read<float>(player, "BufferedSeconds"), Is.Zero);
                Assert.That(Read<int>(player, "QueuedChunkCount"), Is.Zero);
                Assert.That(Read<bool>(player, "StreamCompleted"), Is.True);
                Assert.That(Read<bool>(player, "PlaybackStarted"), Is.False);
                Assert.That((int)Invoke(player, "BeginStream"), Is.GreaterThan(firstGeneration));
            }
            finally
            {
                UnityEngine.Object.Destroy(root);
            }

            yield return null;
            Assert.That(root == null, Is.True);
        }

        [UnityTest]
        public IEnumerator PassthroughFacadeCanBeCreatedToggledAndDestroyedWithoutQuestRuntime()
        {
            var cameraRoot = new GameObject("Main Camera");
            var facadeRoot = new GameObject("Passthrough runtime lifecycle test");
            cameraRoot.tag = "MainCamera";
            cameraRoot.AddComponent<Camera>();
            try
            {
                var facade = facadeRoot.AddComponent(RuntimeType(
                    "QuestMmdPlayer.PassthroughFacade"));
                yield return null;

                Assert.That(Read<object>(facade, "State").ToString(), Is.EqualTo("Unavailable"));
                Invoke(facade, "SetEnabled", false);
                Invoke(facade, "SetEnabled", true);
                Invoke(facade, "Toggle");
                Assert.That(Read<string>(facade, "Status"), Is.Not.Null.And.Not.Empty);
            }
            finally
            {
                UnityEngine.Object.Destroy(facadeRoot);
                UnityEngine.Object.Destroy(cameraRoot);
            }

            yield return null;
            Assert.That(facadeRoot == null, Is.True);
            Assert.That(cameraRoot == null, Is.True);
        }

        [UnityTest]
        public IEnumerator PmxImportCleanupDestroysGeneratedMeshesMaterialsTexturesAndModel()
        {
            var resultType = Type.GetType("UMT.PMXImportResult, UMT.Runtime", true);
            var importedMeshType = Type.GetType("UMT.PMXImportedMesh, UMT.Runtime", true);
            var modelType = Type.GetType("UMT.PMXModel, UMT.Runtime", true);
            var result = Activator.CreateInstance(resultType);
            var root = new GameObject("Imported model root");
            var host = new GameObject("Imported avatar host");
            root.transform.SetParent(host.transform);
            var mesh = new Mesh();
            var shader = Shader.Find("Hidden/InternalErrorShader");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var texture = new Texture2D(2, 2);
            var model = ScriptableObject.CreateInstance(modelType);
            var importedMesh = Activator.CreateInstance(importedMeshType);

            resultType.GetField("root").SetValue(result, root);
            resultType.GetField("model").SetValue(result, model);
            importedMeshType.GetField("mesh").SetValue(importedMesh, mesh);
            ((IList)resultType.GetField("meshes").GetValue(result)).Add(importedMesh);
            ((IList)resultType.GetField("materials").GetValue(result)).Add(material);
            ((IList)resultType.GetField("textures").GetValue(result)).Add(texture);

            var loaderType = RuntimeType("QuestMmdPlayer.RuntimeMmdModelLoader");
            var cleanup = loaderType.GetMethod(
                "DestroyImportResult",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(cleanup, Is.Not.Null);
            cleanup.Invoke(null, new[] { result, host });

            yield return null;
            Assert.That(host == null, Is.True);
            Assert.That(root == null, Is.True);
            Assert.That(mesh == null, Is.True);
            Assert.That(material == null, Is.True);
            Assert.That(texture == null, Is.True);
            Assert.That(model == null, Is.True);
        }

        [UnityTest]
        public IEnumerator InvalidPmxImportDoesNotLeaveBrokenInstalledModelDirectory()
        {
            var sourceRoot = Path.Combine(Path.GetTempPath(), "banxia-invalid-pmx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceRoot);
            var invalidPmx = Path.Combine(sourceRoot, "BrokenModel.pmx");
            File.WriteAllBytes(invalidPmx, Encoding.ASCII.GetBytes("not a PMX file"));
            var installedRoot = Path.Combine(Application.persistentDataPath, "MmdModels", "Imported");
            var before = ExistingDirectories(installedRoot);
            var root = new GameObject("Invalid PMX import transaction test");
            try
            {
                var loader = root.AddComponent(RuntimeType("QuestMmdPlayer.RuntimeMmdModelLoader"));
                loader.GetType().GetEvent("ProgressChanged").AddEventHandler(
                    loader,
                    new Action<string>(_ => throw new InvalidOperationException("progress subscriber")));
                loader.GetType().GetEvent("LoadFailed").AddEventHandler(
                    loader,
                    new Action<string>(_ => throw new InvalidOperationException("failure subscriber")));
                var actions = root.AddComponent(RuntimeType("QuestMmdPlayer.VmdActionLibrary"));
                var importer = root.AddComponent(RuntimeType("QuestMmdPlayer.QuestFileImportService"));
                Invoke(importer, "Initialize", loader, actions);

                LogAssert.Expect(LogType.Exception, new Regex("EndOfStreamException"));
                var task = (Task)Invoke(importer, "ImportPathAsync", invalidPmx);
                while (!task.IsCompleted) yield return null;
                Assert.That(task.IsFaulted, Is.False);
                Assert.That(Read<bool>(loader, "IsLoading"), Is.False);
                Assert.That(ExistingDirectories(installedRoot), Is.EquivalentTo(before));
            }
            finally
            {
                UnityEngine.Object.Destroy(root);
                Directory.Delete(sourceRoot, true);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator InvalidMultiPmxPackageDoesNotEnterInstalledCatalog()
        {
            var sourceRoot = Path.Combine(
                Path.GetTempPath(),
                "banxia-invalid-multi-pmx-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceRoot);
            File.WriteAllBytes(
                Path.Combine(sourceRoot, "BrokenA.pmx"),
                Encoding.ASCII.GetBytes("not a PMX file"));
            File.WriteAllBytes(
                Path.Combine(sourceRoot, "BrokenB.PMX"),
                Encoding.ASCII.GetBytes("also not a PMX file"));
            var installedRoot = Path.Combine(
                Application.persistentDataPath,
                "MmdModels",
                "Imported");
            var before = ExistingDirectories(installedRoot);
            var root = new GameObject("Invalid multi PMX package transaction test");
            try
            {
                var loader = root.AddComponent(RuntimeType("QuestMmdPlayer.RuntimeMmdModelLoader"));
                var actions = root.AddComponent(RuntimeType("QuestMmdPlayer.VmdActionLibrary"));
                var importer = root.AddComponent(RuntimeType("QuestMmdPlayer.QuestFileImportService"));
                Invoke(importer, "Initialize", loader, actions);

                LogAssert.Expect(LogType.Exception, new Regex("EndOfStreamException"));
                LogAssert.Expect(LogType.Exception, new Regex("InvalidDataException: Invalid PMX signature"));
                var task = (Task)Invoke(importer, "ImportPathAsync", sourceRoot);
                while (!task.IsCompleted) yield return null;
                Assert.That(task.IsFaulted, Is.False);
                Assert.That(Read<bool>(loader, "IsLoading"), Is.False);
                Assert.That(ExistingDirectories(installedRoot), Is.EquivalentTo(before));
            }
            finally
            {
                UnityEngine.Object.Destroy(root);
                Directory.Delete(sourceRoot, true);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator RejectedVmdPackageDoesNotLeavePartialMotionDirectory()
        {
            var sourceRoot = Path.Combine(Path.GetTempPath(), "banxia-invalid-vmd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sourceRoot);
            WriteVmd(Path.Combine(sourceRoot, "motion.vmd"), boneFrame: 30, morphFrame: null);
            // ContainsModelTracks accepts this structurally valid file, while
            // the action policy rejects its >120 second duration.
            WriteVmd(Path.Combine(sourceRoot, "facial.vmd"), boneFrame: null, morphFrame: 4000);
            var motionsRoot = Path.Combine(Application.persistentDataPath, "Motions");
            var before = ExistingDirectories(motionsRoot);
            var root = new GameObject("Invalid VMD package transaction test");
            try
            {
                var loader = root.AddComponent(RuntimeType("QuestMmdPlayer.RuntimeMmdModelLoader"));
                var actions = root.AddComponent(RuntimeType("QuestMmdPlayer.VmdActionLibrary"));
                var importer = root.AddComponent(RuntimeType("QuestMmdPlayer.QuestFileImportService"));
                Invoke(importer, "Initialize", loader, actions);

                var task = (Task)Invoke(importer, "ImportPathAsync", sourceRoot);
                while (!task.IsCompleted) yield return null;
                Assert.That(task.IsFaulted, Is.False);
                Assert.That(ExistingDirectories(motionsRoot), Is.EquivalentTo(before));
            }
            finally
            {
                UnityEngine.Object.Destroy(root);
                Directory.Delete(sourceRoot, true);
            }
            yield return null;
        }

        private static Type RuntimeType(string fullName)
        {
            return Type.GetType(fullName + ", Assembly-CSharp", true);
        }

        private static object Invoke(Component target, string name, params object[] args)
        {
            var method = target.GetType().GetMethod(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "Missing runtime method: " + name);
            return method.Invoke(target, args);
        }

        private static T Read<T>(Component target, string name)
        {
            var property = target.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(property, Is.Not.Null, "Missing runtime property: " + name);
            return (T)property.GetValue(target);
        }

        private static void SetField(Component target, string name, object value)
        {
            var field = target.GetType().GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing runtime field: " + name);
            field.SetValue(target, value);
        }

        private static string[] ExistingDirectories(string root)
        {
            return Directory.Exists(root)
                ? Directory.GetDirectories(root).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
                : Array.Empty<string>();
        }

        private static void WriteVmd(string path, uint? boneFrame, uint? morphFrame)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, false);
            WriteFixedAscii(writer, "Vocaloid Motion Data 0002", 30);
            WriteFixedAscii(writer, "test", 20);
            writer.Write(boneFrame.HasValue ? 1u : 0u);
            if (boneFrame.HasValue)
            {
                WriteFixedAscii(writer, "center", 15);
                writer.Write(boneFrame.Value);
                for (var index = 0; index < 7; index++) writer.Write(index == 6 ? 1f : 0f);
                writer.Write(new byte[64]);
            }
            writer.Write(morphFrame.HasValue ? 1u : 0u);
            if (morphFrame.HasValue)
            {
                WriteFixedAscii(writer, "smile", 15);
                writer.Write(morphFrame.Value);
                writer.Write(1f);
            }
        }

        private static void WriteFixedAscii(BinaryWriter writer, string value, int length)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            writer.Write(bytes, 0, Math.Min(bytes.Length, length));
            if (bytes.Length < length) writer.Write(new byte[length - bytes.Length]);
        }
    }
}

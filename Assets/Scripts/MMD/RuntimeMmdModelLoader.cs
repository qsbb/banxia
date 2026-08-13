using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer
{
    public enum RuntimeModelLoadPhase
    {
        Idle,
        Reading,
        Building,
        Ready,
        Cancelled,
        Failed
    }

    [Serializable]
    public sealed class RuntimeMmdModelInfo
    {
        public string DisplayName { get; }
        public string Path { get; }

        public RuntimeMmdModelInfo(string displayName, string path)
        {
            DisplayName = displayName ?? string.Empty;
            Path = path ?? string.Empty;
        }
    }

    /// <summary>
    /// Loads user-imported PMX models directly at runtime. Production builds do
    /// not bundle a default avatar; installed models live in persistent data.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RuntimeMmdModelLoader : MonoBehaviour
    {
        private const string RetiredBundledSampleDirectory = "ForestBerry";
        private static readonly IReadOnlyDictionary<string, string> RetiredBundledSampleFiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "ForestBerry.pmx", "9B13CCA69078DDC76F1313DC8D864894EE74D3A341048EF2D2930D4F3A8D847A" },
                { "T_KokonaShiki_Body_104_D.png", "84048F28A8707A136D36979235B60E0EB38E5B823BC43228CD2288F02D2FED30" },
                { "T_KokonaShiki_Face.png", "CB9AEC7D575B499A0A7CF1D9BB3673DA52B634CB3CF3709F6787F07E8DCBC3E8" },
                { "T_KokonaShiki_Hair.TGA", "C9DC185DA9A20710EB3079705BB2AA5CAFE5B446B33BFFA1A7EF04D161BE71EF" },
                { "T_KokonaShiki_Hair_104_D.png", "2F178CB82DF769A2179B408DB03F70368AB02F5BA29D81FC9D0DCB719466D1E3" }
            };
        [SerializeField, Range(1f, 12f)] private float frameBudgetMilliseconds = 4f;
        [SerializeField] private Vector3 spawnPosition = new Vector3(0f, 0f, 2.2f);

        private CancellationTokenSource loadCancellation;
        private PMXImportResult currentResult;
        private AvatarController currentAvatar;

        public event Action<AvatarController> AvatarLoaded;
        public event Action ModelWillUnload;
        public event Action<string> LoadFailed;
        public event Action<string> ProgressChanged;

        public AvatarController CurrentAvatar => currentAvatar;
        public GameObject CurrentModel => currentResult == null ? null : currentResult.root;
        public PMXModel CurrentMmdModel => currentResult == null ? null : currentResult.model;
        public bool IsLoading { get; private set; }
        public string CurrentModelPath { get; private set; }
        public RuntimeModelLoadPhase LoadPhase { get; private set; } = RuntimeModelLoadPhase.Idle;
        public int LastLoadMilliseconds { get; private set; } = -1;
        public int LastReadMilliseconds { get; private set; } = -1;
        public int LastBuildMilliseconds { get; private set; } = -1;

        private void Awake()
        {
            RemoveRetiredBundledSample(
                Path.Combine(Application.persistentDataPath, "MmdModels"));
        }

        public IReadOnlyList<RuntimeMmdModelInfo> DiscoverInstalledModels()
        {
            var root = System.IO.Path.Combine(Application.persistentDataPath, "MmdModels");
            RemoveRetiredBundledSample(root);
            var results = new List<RuntimeMmdModelInfo>();
            if (!Directory.Exists(root))
            {
                return results;
            }

            var rootFull = System.IO.Path.GetFullPath(root)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) +
                System.IO.Path.DirectorySeparatorChar;
            string[] files;
            try
            {
                files = Directory.GetFiles(rootFull, "*.pmx", SearchOption.AllDirectories);
            }
            catch (IOException)
            {
                return results;
            }
            catch (UnauthorizedAccessException)
            {
                return results;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < files.Length; index++)
            {
                string fullPath;
                try
                {
                    fullPath = System.IO.Path.GetFullPath(files[index]);
                }
                catch (Exception exception) when (
                    exception is ArgumentException ||
                    exception is NotSupportedException ||
                    exception is PathTooLongException)
                {
                    continue;
                }
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath) || new FileInfo(fullPath).Length <= 0)
                {
                    continue;
                }

                var baseName = System.IO.Path.GetFileNameWithoutExtension(fullPath);
                names.TryGetValue(baseName, out var duplicateIndex);
                names[baseName] = duplicateIndex + 1;
                var displayName = duplicateIndex == 0
                    ? baseName
                    : baseName + " (" + (duplicateIndex + 1) + ")";
                results.Add(new RuntimeMmdModelInfo(displayName, fullPath));
            }
            return results;
        }

        internal static int RemoveRetiredBundledSample(
            string modelsRoot,
            IReadOnlyDictionary<string, string> expectedFiles = null)
        {
            if (string.IsNullOrWhiteSpace(modelsRoot)) return 0;
            var directory = Path.Combine(modelsRoot, RetiredBundledSampleDirectory);
            if (!Directory.Exists(directory)) return 0;
            var removed = 0;
            foreach (var sample in expectedFiles ?? RetiredBundledSampleFiles)
            {
                var path = Path.Combine(directory, sample.Key);
                if (!File.Exists(path) || !MatchesSha256(path, sample.Value)) continue;
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (removed == 0) return 0;
            TryDeleteEmptyDirectory(directory);
            Debug.Log($"[ModelLoader] Removed {removed} retired bundled sample files from persistent data.");
            return removed;
        }

        private static void TryDeleteEmptyDirectory(string directory)
        {
            try
            {
                if (Directory.GetFileSystemEntries(directory).Length == 0)
                    Directory.Delete(directory, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool MatchesSha256(string path, string expected)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var sha256 = SHA256.Create())
                {
                    return string.Equals(
                        BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty),
                        expected,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        public Task<AvatarController> LoadInstalledModelAsync(RuntimeMmdModelInfo model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.Path))
            {
                throw new ArgumentException("An installed model is required.", nameof(model));
            }
            return LoadFromFileAsync(model.Path, System.IO.Path.GetDirectoryName(model.Path));
        }

        /// <summary>
        /// Loads a PMX file and its adjacent texture directory. This is the API
        /// that a file picker, network transfer, or AstrBot bridge can call later.
        /// </summary>
        public async Task<AvatarController> LoadFromFileAsync(string pmxPath, string textureBaseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(pmxPath))
            {
                throw new ArgumentException("A PMX file path is required.", nameof(pmxPath));
            }

            if (!File.Exists(pmxPath))
            {
                throw new FileNotFoundException("The PMX file was not found.", pmxPath);
            }

            loadCancellation?.Cancel();
            loadCancellation?.Dispose();
            loadCancellation = new CancellationTokenSource();
            var token = loadCancellation.Token;
            IsLoading = true;
            LoadPhase = RuntimeModelLoadPhase.Reading;
            var loadStartedAt = Time.realtimeSinceStartup;
            NotifyProgress("Reading PMX");

            PMXImportResult importedResult = null;
            GameObject importedAvatarHost = null;
            try
            {
                importedResult = await ImportAsync(pmxPath, textureBaseDirectory, token);
                token.ThrowIfCancellationRequested();

                var modelRoot = importedResult.root;
                importedAvatarHost = new GameObject(modelRoot.name + "_Avatar");
                importedAvatarHost.transform.SetPositionAndRotation(spawnPosition, Quaternion.identity);
                modelRoot.transform.SetParent(importedAvatarHost.transform, false);
                var avatar = importedAvatarHost.AddComponent<AvatarController>();
                avatar.Initialize(modelRoot.transform);

                UnloadCurrentModel();
                currentResult = importedResult;
                currentAvatar = avatar;
                CurrentModelPath = pmxPath;
                importedResult = null;
                importedAvatarHost = null;
                NotifyProgress("Model ready");
                LoadPhase = RuntimeModelLoadPhase.Ready;
                LastLoadMilliseconds = ElapsedMilliseconds(loadStartedAt);
                NotifyAvatarLoaded(avatar);
                return avatar;
            }
            catch (OperationCanceledException)
            {
                LoadPhase = RuntimeModelLoadPhase.Cancelled;
                LastLoadMilliseconds = ElapsedMilliseconds(loadStartedAt);
                throw;
            }
            catch (Exception exception)
            {
                LoadPhase = RuntimeModelLoadPhase.Failed;
                LastLoadMilliseconds = ElapsedMilliseconds(loadStartedAt);
                Debug.LogException(exception, this);
                NotifyLoadFailed(exception.Message);
                throw;
            }
            finally
            {
                if (importedResult != null)
                {
                    DestroyImportResult(importedResult, importedAvatarHost);
                }
                IsLoading = false;
            }
        }

        public void CancelLoad()
        {
            loadCancellation?.Cancel();
        }

        private async Task<PMXImportResult> ImportAsync(string pmxPath, string textureBaseDirectory, CancellationToken token)
        {
            var budget = new UMTFrameBudget(frameBudgetMilliseconds);
            PMXModel model;
            var readStartedAt = Time.realtimeSinceStartup;
            using (var stream = File.OpenRead(pmxPath))
            {
                model = await PMXReader.ReadAsync(budget, stream, true);
            }
            LastReadMilliseconds = ElapsedMilliseconds(readStartedAt);

            token.ThrowIfCancellationRequested();
            PreserveOriginalNames(model);
            LoadPhase = RuntimeModelLoadPhase.Building;
            NotifyProgress("Building MMD materials and skeleton");
            var buildStartedAt = Time.realtimeSinceStartup;
            var options = new PMXImportOptions
            {
                sourcePath = pmxPath,
                textureBaseDirectory = string.IsNullOrWhiteSpace(textureBaseDirectory)
                    ? Path.GetDirectoryName(pmxPath)
                    : textureBaseDirectory,
                applyRenames = false,
                createAvatar = false
            };

            // Runtime loading deliberately keeps source names. UMT's editor-only
            // rename/romanization pass needs optional dictionary assets and is not
            // required to build meshes, textures, MMD physics, or morph data.
            token.ThrowIfCancellationRequested();
            var result = await PMXImporter.BuildUnityObjectsAsync(budget, model, options);
            LastBuildMilliseconds = ElapsedMilliseconds(buildStartedAt);
            if (result.warnings.Count > 0)
            {
                foreach (var warning in result.warnings)
                {
                    Debug.LogWarning($"[RuntimeMmdModelLoader] {warning}", result.root);
                }
            }

            return result;
        }

        public static int ElapsedMilliseconds(float startedAt, float now = -1f)
        {
            var current = now < 0f ? Time.realtimeSinceStartup : now;
            return Mathf.Max(0, Mathf.RoundToInt((current - startedAt) * 1000f));
        }

        private static void PreserveOriginalNames(PMXModel model)
        {
            if (model == null)
            {
                return;
            }

            // The runtime importer skips UMT's optional rename dictionary. Keep
            // source names so semantic interaction can find bones and morphs.
            for (var i = 0; i < model.bones.Length; i++)
            {
                var bone = model.bones[i];
                bone.renamedName = bone.originalName;
                model.bones[i] = bone;
            }

            for (var i = 0; i < model.morphs.Length; i++)
            {
                var morph = model.morphs[i];
                morph.renamedName = morph.originalName;
                model.morphs[i] = morph;
            }
        }
        private void UnloadCurrentModel()
        {
            if (currentResult == null)
            {
                currentAvatar = null;
                CurrentModelPath = null;
                return;
            }

            NotifyModelWillUnload();

            DestroyImportResult(currentResult, currentAvatar == null ? null : currentAvatar.gameObject);

            currentResult = null;
            currentAvatar = null;
            CurrentModelPath = null;
        }

        internal static void DestroyImportResult(PMXImportResult result, GameObject avatarHost = null)
        {
            if (result == null)
            {
                return;
            }

            var destroyed = new HashSet<UnityEngine.Object>();
            void DestroyOnce(UnityEngine.Object value)
            {
                if (value != null && destroyed.Add(value))
                {
                    Destroy(value);
                }
            }

            DestroyOnce(avatarHost != null ? avatarHost : result.root);
            foreach (var importedMesh in result.meshes)
            {
                DestroyOnce(importedMesh?.mesh);
            }
            foreach (var material in result.materials)
            {
                DestroyOnce(material);
            }
            foreach (var texture in result.textures)
            {
                DestroyOnce(texture);
            }
            DestroyOnce(result.model);
        }

        private void NotifyProgress(string message)
        {
            InvokeSafely(ProgressChanged, message);
        }

        private void NotifyAvatarLoaded(AvatarController avatar)
        {
            InvokeSafely(AvatarLoaded, avatar);
        }

        private void NotifyLoadFailed(string message)
        {
            InvokeSafely(LoadFailed, message);
        }

        private void NotifyModelWillUnload()
        {
            if (ModelWillUnload == null)
            {
                return;
            }

            foreach (Action subscriber in ModelWillUnload.GetInvocationList())
            {
                try
                {
                    subscriber();
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "[RuntimeMmdModelLoader] unload subscriber failed: " + exception.Message,
                        this);
                }
            }
        }

        private void InvokeSafely<T>(Action<T> callback, T value)
        {
            if (callback == null)
            {
                return;
            }

            foreach (Action<T> subscriber in callback.GetInvocationList())
            {
                try
                {
                    subscriber(value);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(
                        "[RuntimeMmdModelLoader] event subscriber failed: " + exception.Message,
                        this);
                }
            }
        }

        private void OnDestroy()
        {
            loadCancellation?.Cancel();
            loadCancellation?.Dispose();
            UnloadCurrentModel();
        }
    }
}

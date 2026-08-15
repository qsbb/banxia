using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        public string PackageRoot { get; }

        public RuntimeMmdModelInfo(string displayName, string path, string packageRoot = null)
        {
            DisplayName = displayName ?? string.Empty;
            Path = path ?? string.Empty;
            PackageRoot = packageRoot ?? System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
        }
    }

    /// <summary>
    /// Loads user-imported PMX models directly at runtime. Production builds do
    /// not bundle a default avatar; installed models live in persistent data.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RuntimeMmdModelLoader : MonoBehaviour
    {
        private const string SelectedModelPreference = "Banxia.RuntimeMmdModel.SelectedPath";
        private const string SelectedModelRelativePreference =
            "Banxia.RuntimeMmdModel.SelectedRelativePath";
        private const int ParsedModelCacheCapacity = 2;
        private const float ParsedModelCacheLifetimeSeconds = 180f;
        private const float ParsedModelCacheTrimIntervalSeconds = 30f;
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
        private long loadGeneration;
        private PMXImportResult currentResult;
        private AvatarController currentAvatar;
        private readonly List<ParsedModelCacheEntry> parsedModelCache = new List<ParsedModelCacheEntry>();
        private IReadOnlyList<RuntimeMmdModelInfo> installedModelCache;
        private float nextParsedCacheTrimAt;
        private bool restoreStarted;

        private sealed class ParsedModelCacheEntry
        {
            internal string path;
            internal long length;
            internal long lastWriteUtcTicks;
            internal PMXModel model;
            internal float lastUsedAt;
        }

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
        public RuntimeModelLoadPhase LastFailurePhase { get; private set; } = RuntimeModelLoadPhase.Idle;
        public int LastLoadMilliseconds { get; private set; } = -1;
        public int LastReadMilliseconds { get; private set; } = -1;
        public int LastBuildMilliseconds { get; private set; } = -1;
        public bool LastLoadUsedParsedCache { get; private set; }
        public int LastLoadFrameCount { get; private set; }
        public int LastLoadLongFrameCount { get; private set; }
        public float LastLoadMaximumFrameMilliseconds { get; private set; }

        private void Awake()
        {
            RemoveRetiredBundledSample(
                Path.Combine(Application.persistentDataPath, "MmdModels"));
            nextParsedCacheTrimAt = Time.realtimeSinceStartup + ParsedModelCacheTrimIntervalSeconds;
            Application.lowMemory += HandleLowMemory;
        }

        private async void Start()
        {
            // Start runs after the bootstrap has subscribed to AvatarLoaded, so
            // restoring a model here wires the same path as a manual selection.
            if (restoreStarted)
            {
                return;
            }
            restoreStarted = true;
            await RestoreLastModelAsync();
        }

        private void Update()
        {
            if (IsLoading)
            {
                LastLoadFrameCount++;
                var frameMilliseconds = Mathf.Max(0f, Time.unscaledDeltaTime * 1000f);
                LastLoadMaximumFrameMilliseconds = Mathf.Max(
                    LastLoadMaximumFrameMilliseconds,
                    frameMilliseconds);
                if (frameMilliseconds >= 1000f / 30f)
                {
                    LastLoadLongFrameCount++;
                }
            }
            if (Time.realtimeSinceStartup < nextParsedCacheTrimAt)
            {
                return;
            }
            nextParsedCacheTrimAt = Time.realtimeSinceStartup + ParsedModelCacheTrimIntervalSeconds;
            TrimParsedModelCache();
        }

        public IReadOnlyList<RuntimeMmdModelInfo> DiscoverInstalledModels()
        {
            if (installedModelCache != null)
            {
                return installedModelCache;
            }
            var root = System.IO.Path.Combine(Application.persistentDataPath, "MmdModels");
            RemoveRetiredBundledSample(root);
            var discovered = DiscoverInstalledModels(root);
            installedModelCache = discovered;
            Debug.Log("[ModelCatalog] ready models=" + discovered.Count +
                " names=" + string.Join(",", discovered.Select(model => model.DisplayName)));
            return installedModelCache;
        }

        public void InvalidateInstalledModelCache()
        {
            installedModelCache = null;
        }

        public string SavedModelPath => PlayerPrefs.GetString(SelectedModelPreference, string.Empty);

        public string SavedModelRelativePath =>
            PlayerPrefs.GetString(SelectedModelRelativePreference, string.Empty);

        public async Task<bool> RestoreLastModelAsync()
        {
            var savedPath = SavedModelPath;
            var savedRelativePath = SavedModelRelativePath;
            if (string.IsNullOrWhiteSpace(savedPath) &&
                string.IsNullOrWhiteSpace(savedRelativePath))
            {
                Debug.Log("[ModelLoader] startup restore skipped: no saved model.");
                return false;
            }

            var modelsRoot = Path.Combine(Application.persistentDataPath, "MmdModels");
            var selected = FindSavedModel(
                modelsRoot,
                DiscoverInstalledModels(),
                savedPath,
                savedRelativePath);
            if (selected == null)
            {
                // External storage can be temporarily unavailable while Android
                // finishes mounting it. Keep the selection so a later restart can
                // recover it instead of permanently forgetting the user's model.
                Debug.LogWarning("[ModelLoader] startup restore skipped: saved model is not currently available.");
                return false;
            }

            // Migrate legacy absolute-path selections to the mount-independent
            // relative identifier before loading the model.
            RememberSelectedModel(selected.Path);

            try
            {
                Debug.Log("[ModelLoader] startup restore begin name=" + selected.DisplayName);
                await LoadInstalledModelAsync(selected);
                Debug.Log("[ModelLoader] startup restore complete name=" + selected.DisplayName);
                return true;
            }
            catch (Exception exception)
            {
                // A bad imported package must not prevent the rest of the app
                // from starting. Keep the preference for a later retry after the
                // user repairs/replaces the package.
                Debug.LogWarning("[ModelLoader] startup restore failed: " + exception.GetType().Name);
                return false;
            }
        }

        internal static IReadOnlyList<RuntimeMmdModelInfo> DiscoverInstalledModels(string root)
        {
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
                files = Directory.GetFiles(rootFull, "*", SearchOption.AllDirectories);
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
            var candidates = new List<ModelCatalogCandidate>();
            for (var index = 0; index < files.Length; index++)
            {
                if (!string.Equals(
                    System.IO.Path.GetExtension(files[index]),
                    ".pmx",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

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

                var packageRoot = ResolvePackageRoot(rootFull, fullPath);
                candidates.Add(new ModelCatalogCandidate(
                    fullPath,
                    packageRoot,
                    new FileInfo(fullPath).Length,
                    -1));
            }

            // Identical PMX geometry can intentionally ship with different
            // adjacent textures. Keep every installed candidate selectable;
            // path-based display suffixes make duplicates explicit instead of
            // silently discarding a valid skin or clothing variant.
            var selectedCandidates = candidates;

            selectedCandidates.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in selectedCandidates)
            {
                var baseName = string.IsNullOrWhiteSpace(candidate.DisplayName)
                    ? ResolveDisplayName(candidate.Path)
                    : candidate.DisplayName;
                names.TryGetValue(baseName, out var duplicateIndex);
                names[baseName] = duplicateIndex + 1;
                var displayName = duplicateIndex == 0
                    ? baseName
                    : baseName + " (" + (duplicateIndex + 1) + ")";
                results.Add(new RuntimeMmdModelInfo(
                    displayName,
                    candidate.Path,
                    candidate.PackageRoot));
            }
            return results;
        }

        internal static RuntimeMmdModelInfo FindSavedModel(
            string modelsRoot,
            IReadOnlyList<RuntimeMmdModelInfo> installedModels,
            string savedAbsolutePath,
            string savedRelativePath)
        {
            if (installedModels == null || installedModels.Count == 0)
            {
                return null;
            }

            var normalizedRelative = NormalizeModelRelativePath(savedRelativePath);
            if (string.IsNullOrEmpty(normalizedRelative))
            {
                normalizedRelative = ExtractModelRelativePath(modelsRoot, savedAbsolutePath);
            }

            if (!string.IsNullOrEmpty(normalizedRelative))
            {
                var relativeMatch = installedModels.FirstOrDefault(model =>
                    string.Equals(
                        ExtractModelRelativePath(modelsRoot, model.Path),
                        normalizedRelative,
                        StringComparison.OrdinalIgnoreCase));
                if (relativeMatch != null)
                {
                    return relativeMatch;
                }
            }

            return installedModels.FirstOrDefault(model =>
                string.Equals(
                    NormalizePathSeparators(model.Path),
                    NormalizePathSeparators(savedAbsolutePath),
                    StringComparison.OrdinalIgnoreCase));
        }

        internal static string ExtractModelRelativePath(string modelsRoot, string modelPath)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                return string.Empty;
            }

            var normalizedPath = NormalizePathSeparators(modelPath).TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(modelsRoot))
            {
                var normalizedRoot = NormalizePathSeparators(modelsRoot).TrimEnd('/');
                if (normalizedPath.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeModelRelativePath(
                        normalizedPath.Substring(normalizedRoot.Length + 1));
                }
            }

            // Android may expose the same external storage through either
            // /sdcard or /storage/emulated/0. The suffix below MmdModels is the
            // stable identity shared by both aliases.
            const string marker = "/MmdModels/";
            var markerIndex = normalizedPath.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return markerIndex < 0
                ? string.Empty
                : NormalizeModelRelativePath(normalizedPath.Substring(markerIndex + marker.Length));
        }

        private static string NormalizeModelRelativePath(string value)
        {
            var normalized = NormalizePathSeparators(value).Trim('/');
            if (string.IsNullOrEmpty(normalized))
            {
                return string.Empty;
            }

            var segments = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment => segment == "." || segment == ".."))
            {
                return string.Empty;
            }
            return string.Join("/", segments);
        }

        private static string NormalizePathSeparators(string value)
        {
            return (value ?? string.Empty).Replace('\\', '/');
        }

        private sealed class ModelCatalogCandidate
        {
            internal readonly string Path;
            internal readonly string PackageRoot;
            internal readonly long Length;
            internal readonly int TextureCount;
            internal readonly string DisplayName;

            internal ModelCatalogCandidate(
                string path,
                string packageRoot,
                long length,
                int textureCount,
                string displayName = null)
            {
                Path = path;
                PackageRoot = packageRoot;
                Length = length;
                TextureCount = textureCount;
                DisplayName = displayName ?? string.Empty;
            }

            internal ModelCatalogCandidate WithDisplayName(string displayName)
            {
                return new ModelCatalogCandidate(Path, PackageRoot, Length, TextureCount, displayName);
            }

            internal ModelCatalogCandidate WithTextureCount(int textureCount)
            {
                return new ModelCatalogCandidate(Path, PackageRoot, Length, textureCount, DisplayName);
            }
        }

        internal static int CountTextureResources(string packageRoot)
        {
            if (string.IsNullOrWhiteSpace(packageRoot) || !Directory.Exists(packageRoot)) return 0;
            try
            {
                return Directory.GetFiles(packageRoot, "*", SearchOption.AllDirectories).Count(path =>
                {
                    var extension = System.IO.Path.GetExtension(path);
                    return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".dds", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".spa", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(extension, ".sph", StringComparison.OrdinalIgnoreCase);
                });
            }
            catch (IOException)
            {
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private static bool TryComputeSha256(string path, out string hash)
        {
            hash = string.Empty;
            try
            {
                using (var stream = File.OpenRead(path))
                using (var sha256 = SHA256.Create())
                {
                    hash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
                    return true;
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

        internal static string ResolveDisplayName(string modelPath)
        {
            var fileName = Path.GetFileNameWithoutExtension(modelPath) ?? string.Empty;
            if (!LooksCorruptedDisplayName(fileName))
            {
                return fileName;
            }

            if (TryReadPmxModelName(modelPath, out var embeddedName) &&
                !LooksCorruptedDisplayName(embeddedName))
            {
                return embeddedName;
            }
            return "已导入角色";
        }

        internal static bool LooksCorruptedDisplayName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var replacementCount = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == '\uFFFD') replacementCount++;
            }
            return replacementCount > 0 || value.Contains("????");
        }

        internal static bool TryReadPmxModelName(string modelPath, out string modelName)
        {
            modelName = string.Empty;
            try
            {
                using (var stream = File.OpenRead(modelPath))
                using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    if (stream.Length < 18 || Encoding.ASCII.GetString(reader.ReadBytes(4)) != "PMX ")
                    {
                        return false;
                    }
                    reader.ReadSingle();
                    var dataCount = reader.ReadByte();
                    if (dataCount < 8 || stream.Position + dataCount > stream.Length)
                    {
                        return false;
                    }
                    var header = reader.ReadBytes(dataCount);
                    var encoding = header[0];
                    if (encoding != 0 && encoding != 1)
                    {
                        return false;
                    }
                    var byteCount = reader.ReadInt32();
                    if (byteCount <= 0 || byteCount > 4096 || stream.Position + byteCount > stream.Length)
                    {
                        return false;
                    }
                    var bytes = reader.ReadBytes(byteCount);
                    modelName = (encoding == 0 ? Encoding.Unicode : Encoding.UTF8)
                        .GetString(bytes)
                        .Trim('\0', ' ', '\r', '\n', '\t');
                    return !string.IsNullOrWhiteSpace(modelName);
                }
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentException ||
                exception is EndOfStreamException)
            {
                return false;
            }
        }

        internal static string ResolvePackageRoot(string modelsRoot, string modelPath)
        {
            var rootFull = Path.GetFullPath(modelsRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var modelFull = Path.GetFullPath(modelPath);
            var importedRoot = Path.Combine(rootFull, "Imported")
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!modelFull.StartsWith(importedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(modelFull) ?? rootFull.TrimEnd(Path.DirectorySeparatorChar);
            }

            var relative = modelFull.Substring(importedRoot.Length);
            var separator = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            if (separator <= 0)
            {
                return Path.GetDirectoryName(modelFull) ?? importedRoot.TrimEnd(Path.DirectorySeparatorChar);
            }
            return Path.Combine(importedRoot, relative.Substring(0, separator));
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
            return LoadFromFileAsync(
                model.Path,
                System.IO.Path.GetDirectoryName(model.Path));
        }

        public bool DeleteInstalledPackage(RuntimeMmdModelInfo model)
        {
            if (model == null || string.IsNullOrWhiteSpace(model.PackageRoot))
            {
                return false;
            }

            var modelsRoot = Path.Combine(Application.persistentDataPath, "MmdModels");
            var importedRoot = Path.Combine(modelsRoot, "Imported");
            var packageRoot = Path.GetFullPath(model.PackageRoot);
            var importedFull = Path.GetFullPath(importedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var packageParent = Path.GetDirectoryName(packageRoot);
            if (!packageRoot.StartsWith(importedFull, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    packageParent?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    importedFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase) ||
                !Directory.Exists(packageRoot))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(CurrentModelPath) &&
                string.Equals(
                    ResolvePackageRoot(modelsRoot, CurrentModelPath),
                    packageRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var savedModel = FindSavedModel(
                modelsRoot,
                DiscoverInstalledModels(),
                SavedModelPath,
                SavedModelRelativePath);
            var deletingSavedSelection = savedModel != null &&
                string.Equals(
                    Path.GetFullPath(savedModel.PackageRoot),
                    packageRoot,
                    StringComparison.OrdinalIgnoreCase);

            try
            {
                Directory.Delete(packageRoot, true);
                InvalidateInstalledModelCache();
                if (deletingSavedSelection)
                {
                    PlayerPrefs.DeleteKey(SelectedModelPreference);
                    PlayerPrefs.DeleteKey(SelectedModelRelativePreference);
                    PlayerPrefs.Save();
                }
                return true;
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

            // A freshly imported package may have appeared since the menu last
            // queried the catalog. Refresh once before a manual or restored load.
            InvalidateInstalledModelCache();

            var previousCancellation = loadCancellation;
            var currentCancellation = new CancellationTokenSource();
            loadCancellation = currentCancellation;
            previousCancellation?.Cancel();
            var token = currentCancellation.Token;
            var generation = Interlocked.Increment(ref loadGeneration);
            IsLoading = true;
            LoadPhase = RuntimeModelLoadPhase.Reading;
            LastFailurePhase = RuntimeModelLoadPhase.Idle;
            LastReadMilliseconds = -1;
            LastBuildMilliseconds = -1;
            LastLoadUsedParsedCache = false;
            LastLoadFrameCount = 0;
            LastLoadLongFrameCount = 0;
            LastLoadMaximumFrameMilliseconds = 0f;
            var loadStartedAt = Time.realtimeSinceStartup;
            var displayName = ResolveDisplayName(pmxPath);
            var resolvedTextureRoot = string.IsNullOrWhiteSpace(textureBaseDirectory)
                ? Path.GetDirectoryName(pmxPath)
                : textureBaseDirectory;
            Debug.Log("[ModelLoad] start name=" + displayName +
                " bytes=" + new FileInfo(pmxPath).Length +
                " textureRoot=" + (Directory.Exists(resolvedTextureRoot) ? "ready" : "missing"));
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
                RememberSelectedModel(pmxPath);
                importedResult = null;
                importedAvatarHost = null;
                TrimParsedModelCache();
                NotifyProgress("Model ready");
                LoadPhase = RuntimeModelLoadPhase.Ready;
                LastLoadMilliseconds = ElapsedMilliseconds(loadStartedAt);
                Debug.Log("[ModelLoad] ready name=" + displayName +
                    " readMs=" + LastReadMilliseconds +
                    " buildMs=" + LastBuildMilliseconds +
                    " totalMs=" + LastLoadMilliseconds +
                    " loadFrames=" + LastLoadFrameCount +
                    " longFrames=" + LastLoadLongFrameCount +
                    " maxFrameMs=" + LastLoadMaximumFrameMilliseconds.ToString("F1") +
                    " parsedCache=" + (LastLoadUsedParsedCache ? "hit" : "miss") +
                    " materials=" + currentResult.materials.Count +
                    " loadedTextures=" + currentResult.textures.Count);
                NotifyAvatarLoaded(avatar);
                return avatar;
            }
            catch (OperationCanceledException)
            {
                if (generation == loadGeneration)
                {
                    LastFailurePhase = LoadPhase;
                    LoadPhase = RuntimeModelLoadPhase.Cancelled;
                    LastLoadMilliseconds = ElapsedMilliseconds(loadStartedAt);
                    Debug.LogWarning("[ModelLoad] cancelled name=" + displayName +
                        " phase=" + LastFailurePhase +
                        " totalMs=" + LastLoadMilliseconds);
                }
                throw;
            }
            catch (Exception exception)
            {
                if (generation == loadGeneration)
                {
                    LastFailurePhase = LoadPhase;
                    LoadPhase = RuntimeModelLoadPhase.Failed;
                    LastLoadMilliseconds = ElapsedMilliseconds(loadStartedAt);
                    Debug.LogWarning("[ModelLoad] failed name=" + displayName +
                        " phase=" + LastFailurePhase +
                        " totalMs=" + LastLoadMilliseconds +
                        " error=" + exception.GetType().Name);
                    Debug.LogException(exception, this);
                    NotifyLoadFailed(exception.Message);
                }
                throw;
            }
            finally
            {
                if (importedResult != null)
                {
                    DestroyImportResultInternal(
                        importedResult,
                        importedAvatarHost,
                        IsParsedModelCached(importedResult.model));
                }
                if (generation == loadGeneration)
                {
                    IsLoading = false;
                }
                if (ReferenceEquals(loadCancellation, currentCancellation))
                {
                    loadCancellation = null;
                }
                currentCancellation.Dispose();
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
            if (!TryGetParsedModel(pmxPath, out model))
            {
                using (var stream = File.OpenRead(pmxPath))
                {
                    model = await PMXReader.ReadAsync(budget, stream, true);
                }
                token.ThrowIfCancellationRequested();
                LastReadMilliseconds = ElapsedMilliseconds(readStartedAt);
                StoreParsedModel(pmxPath, model);
            }
            else
            {
                LastLoadUsedParsedCache = true;
                LastReadMilliseconds = 0;
            }
            var referencedTextures = model.texturePaths.Count(path => !string.IsNullOrWhiteSpace(path.ToString()));
            var eyeBoneCount = model.bones.Count(bone => LooksLikeEyeName(
                string.IsNullOrWhiteSpace(bone.originalName.ToString())
                    ? bone.originalNameEN.ToString()
                    : bone.originalName.ToString()));
            Debug.Log("[ModelLoad] parsed name=" + ResolveDisplayName(pmxPath) +
                " vertices=" + model.vertices.Length +
                " materials=" + model.materials.Length +
                " textureRefs=" + referencedTextures +
                " eyeBones=" + eyeBoneCount +
                " pipeline=umt_pmx_direct" +
                " readMs=" + LastReadMilliseconds);

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
                createAvatar = false,
                timingCallback = (stage, elapsed) => Debug.Log(
                    "[ModelLoad] build_stage name=" + ResolveDisplayName(pmxPath) +
                    " stage=" + stage.Replace(' ', '_').ToLowerInvariant() +
                    " elapsedMs=" + Mathf.Max(0, Mathf.RoundToInt((float)elapsed.TotalMilliseconds)))
            };

            // Runtime loading deliberately keeps source names. UMT's editor-only
            // rename/romanization pass needs optional dictionary assets and is not
            // required to build meshes, textures, MMD physics, or morph data.
            token.ThrowIfCancellationRequested();
            var result = await PMXImporter.BuildUnityObjectsAsync(
                budget,
                model,
                options,
                token);
            token.ThrowIfCancellationRequested();
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

        private void RememberSelectedModel(string pmxPath)
        {
            var modelsRoot = Path.Combine(Application.persistentDataPath, "MmdModels");
            if (!IsPathWithin(modelsRoot, pmxPath))
            {
                return;
            }
            PlayerPrefs.SetString(SelectedModelPreference, Path.GetFullPath(pmxPath));
            PlayerPrefs.SetString(
                SelectedModelRelativePreference,
                ExtractModelRelativePath(modelsRoot, pmxPath));
            PlayerPrefs.Save();
        }

        private static bool IsPathWithin(string root, string path)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
            {
                return false;
            }
            try
            {
                var rootFull = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                var pathFull = Path.GetFullPath(path);
                return pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (NotSupportedException)
            {
                return false;
            }
        }

        private bool TryGetParsedModel(string pmxPath, out PMXModel model)
        {
            model = null;
            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(pmxPath);
                if (!fileInfo.Exists)
                {
                    return false;
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }

            var fullPath = fileInfo.FullName;
            for (var index = parsedModelCache.Count - 1; index >= 0; index--)
            {
                var entry = parsedModelCache[index];
                if (!string.Equals(entry.path, fullPath, StringComparison.OrdinalIgnoreCase) ||
                    entry.length != fileInfo.Length ||
                    entry.lastWriteUtcTicks != fileInfo.LastWriteTimeUtc.Ticks ||
                    entry.model == null)
                {
                    continue;
                }

                entry.lastUsedAt = Time.realtimeSinceStartup;
                model = entry.model;
                Debug.Log("[ModelCache] hit name=" + ResolveDisplayName(fullPath));
                return true;
            }
            return false;
        }

        private void StoreParsedModel(string pmxPath, PMXModel model)
        {
            if (model == null)
            {
                return;
            }
            FileInfo fileInfo;
            try
            {
                fileInfo = new FileInfo(pmxPath);
            }
            catch (ArgumentException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            for (var index = parsedModelCache.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(parsedModelCache[index].path, fileInfo.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (parsedModelCache[index].model != null && parsedModelCache[index].model != model &&
                    !string.Equals(CurrentModelPath, fileInfo.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    Destroy(parsedModelCache[index].model);
                }
                parsedModelCache.RemoveAt(index);
            }

            parsedModelCache.Add(new ParsedModelCacheEntry
            {
                path = fileInfo.FullName,
                length = fileInfo.Length,
                lastWriteUtcTicks = fileInfo.LastWriteTimeUtc.Ticks,
                model = model,
                lastUsedAt = Time.realtimeSinceStartup
            });
            TrimParsedModelCache();
        }

        private bool IsParsedModelCached(PMXModel model)
        {
            if (model == null)
            {
                return false;
            }
            return parsedModelCache.Any(entry => entry.model == model);
        }

        private void TrimParsedModelCache()
        {
            var now = Time.realtimeSinceStartup;
            for (var index = parsedModelCache.Count - 1; index >= 0; index--)
            {
                var entry = parsedModelCache[index];
                var isCurrent = string.Equals(CurrentModelPath, entry.path, StringComparison.OrdinalIgnoreCase);
                if (ShouldRetainParsedModelCacheEntry(isCurrent, now - entry.lastUsedAt))
                {
                    continue;
                }
                Destroy(entry.model);
                parsedModelCache.RemoveAt(index);
            }

            while (parsedModelCache.Count > ParsedModelCacheCapacity)
            {
                var evictIndex = -1;
                var oldest = float.MaxValue;
                for (var index = 0; index < parsedModelCache.Count; index++)
                {
                    var entry = parsedModelCache[index];
                    if (string.Equals(CurrentModelPath, entry.path, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (entry.lastUsedAt < oldest)
                    {
                        oldest = entry.lastUsedAt;
                        evictIndex = index;
                    }
                }
                if (evictIndex < 0)
                {
                    break;
                }
                Destroy(parsedModelCache[evictIndex].model);
                parsedModelCache.RemoveAt(evictIndex);
            }
        }

        internal static bool ShouldRetainParsedModelCacheEntry(
            bool isCurrent,
            float ageSeconds)
        {
            return isCurrent || ageSeconds < ParsedModelCacheLifetimeSeconds;
        }

        private void HandleLowMemory()
        {
            var released = 0;
            for (var index = parsedModelCache.Count - 1; index >= 0; index--)
            {
                var entry = parsedModelCache[index];
                if (string.Equals(CurrentModelPath, entry.path, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                Destroy(entry.model);
                parsedModelCache.RemoveAt(index);
                released++;
            }
            if (released > 0)
            {
                Debug.Log("[ModelCache] low_memory released=" + released);
            }
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

            for (var i = 0; i < model.materials.Length; i++)
            {
                var material = model.materials[i];
                material.renamedName = material.originalName;
                model.materials[i] = material;
            }
        }

        internal static bool LooksLikeEyeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            var normalized = value.Trim().ToLowerInvariant();
            return normalized.Contains("eye") || normalized.Contains("iris") ||
                normalized.Contains("hitomi") || normalized.Contains("\u76ee") ||
                normalized.Contains("\u773c");
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

            DestroyImportResultInternal(
                currentResult,
                currentAvatar == null ? null : currentAvatar.gameObject,
                IsParsedModelCached(currentResult.model));

            currentResult = null;
            currentAvatar = null;
            CurrentModelPath = null;
        }

        internal static void DestroyImportResult(PMXImportResult result, GameObject avatarHost = null)
        {
            DestroyImportResultInternal(result, avatarHost, false);
        }

        private static void DestroyImportResultInternal(
            PMXImportResult result,
            GameObject avatarHost,
            bool preserveParsedModel)
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
            if (!preserveParsedModel)
            {
                DestroyOnce(result.model);
            }
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
            Application.lowMemory -= HandleLowMemory;
            loadCancellation?.Cancel();
            loadCancellation?.Dispose();
            UnloadCurrentModel();
            for (var index = 0; index < parsedModelCache.Count; index++)
            {
                if (parsedModelCache[index].model != null)
                {
                    Destroy(parsedModelCache[index].model);
                }
            }
            parsedModelCache.Clear();
        }
    }
}

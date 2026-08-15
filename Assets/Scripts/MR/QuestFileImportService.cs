using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace QuestMmdPlayer
{
    /// <summary>
    /// Imports PMX/VMD content through the Quest system picker without exposing
    /// arbitrary filesystem paths to AstrBot. PMX imports may include several
    /// selected texture files; ZIP packages are extracted with path validation.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class QuestFileImportService : MonoBehaviour
    {
        private const long MaximumExpandedArchiveBytes = 512L * 1024L * 1024L;
        private const int MaximumArchiveEntries = 2048;

        private RuntimeMmdModelLoader modelLoader;
        private VmdActionLibrary actionLibrary;
        private string pendingImportPath = string.Empty;

        public event Action<string> StatusChanged;
        public bool IsBusy { get; private set; }
        public string Status { get; private set; } = "文件导入就绪";

        public void Initialize(RuntimeMmdModelLoader nextModelLoader, VmdActionLibrary nextActionLibrary)
        {
            modelLoader = nextModelLoader;
            actionLibrary = nextActionLibrary;
            SetStatus("文件导入就绪");
        }

        public bool OpenPicker()
        {
            if (IsBusy)
            {
                SetStatus("正在处理上一个导入");
                return false;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var picker = new AndroidJavaClass("com.lingxi.banxia.filepicker.BanxiaFilePicker"))
                {
                    picker.CallStatic(
                        "open",
                        activity,
                        gameObject.name,
                        nameof(OnAndroidFileImported));
                }
                SetStatus("等待选择文件");
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[FileImport] Unable to open Android document picker: " + exception, this);
                SetStatus("无法打开文件选择器：" + exception.Message);
                return false;
            }
#elif UNITY_EDITOR
            var selected = EditorUtility.OpenFilePanelWithFilters(
                "导入伴夏文件",
                string.Empty,
                new[] { "PMX、VMD 或 ZIP", "pmx,vmd,zip" });
            if (string.IsNullOrWhiteSpace(selected))
            {
                SetStatus("已取消文件选择");
                return false;
            }

            _ = ImportPathAsync(selected);
            return true;
#else
            SetStatus("当前平台没有文件选择器");
            return false;
#endif
        }

        public void OnAndroidFileImported(string payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                SetStatus("文件选择器没有返回结果");
                return;
            }

            var separator = payload.IndexOf(':');
            if (separator <= 0)
            {
                SetStatus("文件选择结果无效");
                return;
            }

            var state = payload.Substring(0, separator);
            var encoded = payload.Substring(separator + 1);
            if (string.Equals(state, "cancel", StringComparison.Ordinal))
            {
                SetStatus("已取消文件选择");
                return;
            }

            if (!string.Equals(state, "ok", StringComparison.Ordinal))
            {
                SetStatus("文件选择失败：" + DecodePayload(encoded));
                return;
            }

            var path = DecodePayload(encoded);
            if (string.IsNullOrWhiteSpace(path))
            {
                SetStatus("未找到选中的文件");
                return;
            }

            _ = ImportPathAsync(path);
        }

        public static string SanitizeImportedName(string value, string fallback = "Imported")
        {
            var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            var builder = new StringBuilder(Math.Min(source.Length, 80));
            foreach (var character in source)
            {
                if (char.IsControl(character) || character == '/' || character == '\\' ||
                    invalid.Contains(character))
                {
                    builder.Append('_');
                }
                else
                {
                    builder.Append(character);
                }

                if (builder.Length >= 80)
                {
                    break;
                }
            }

            var result = builder.ToString().Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(result) || result == "." || result == ".."
                ? fallback
                : result;
        }

        public static bool IsSupportedImportExtension(string extension)
        {
            return string.Equals(extension, ".pmx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".vmd", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsArchiveMetadataPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return true;
            }

            var normalized = path.Replace('\\', '/');
            var fileName = Path.GetFileName(normalized);
            return normalized.Split('/').Any(part => string.Equals(part, "__MACOSX", StringComparison.OrdinalIgnoreCase)) ||
                fileName.StartsWith("._", StringComparison.Ordinal) ||
                string.Equals(fileName, ".DS_Store", StringComparison.OrdinalIgnoreCase);
        }

        private async Task ImportPathAsync(string path)
        {
            if (IsBusy)
            {
                return;
            }

            IsBusy = true;
            pendingImportPath = path;
            string stagingDirectory = string.Empty;
            var selectionDirectory = IsManagedSelectionDirectory(path) ? path : string.Empty;
            var originalArchivePath = string.Empty;
            try
            {
                SetStatus("检查导入文件");
                var sourceRoot = path;
                var sourceIsDirectory = Directory.Exists(path);
                if (!sourceIsDirectory && !File.Exists(path))
                {
                    throw new FileNotFoundException("导入文件不存在。", path);
                }

                if (!sourceIsDirectory &&
                    string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
                {
                    originalArchivePath = path;
                    stagingDirectory = Path.Combine(
                        Application.persistentDataPath,
                        "Imports",
                        "Zip_" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(stagingDirectory);
                    ExtractArchiveSafely(path, stagingDirectory);
                    sourceRoot = stagingDirectory;
                    sourceIsDirectory = true;
                }

                var files = sourceIsDirectory
                    ? Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
                    : new[] { path };
                var pmxFiles = FilesWithExtension(files, ".pmx");
                var vmdFiles = FilesWithExtension(files, ".vmd");

                if (pmxFiles.Length == 0 && vmdFiles.Length == 0 &&
                    string.IsNullOrEmpty(stagingDirectory))
                {
                    var archives = FilesWithExtension(files, ".zip");
                    if (archives.Length > 1)
                    {
                        throw new InvalidDataException("一次只能导入一个 ZIP 文件。");
                    }

                    if (archives.Length == 1)
                    {
                        originalArchivePath = archives[0];
                        stagingDirectory = Path.Combine(
                            Application.persistentDataPath,
                            "Imports",
                            "Zip_" + Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(stagingDirectory);
                        ExtractArchiveSafely(archives[0], stagingDirectory);
                        sourceRoot = stagingDirectory;
                        sourceIsDirectory = true;
                        files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
                        pmxFiles = FilesWithExtension(files, ".pmx");
                        vmdFiles = FilesWithExtension(files, ".vmd");
                    }
                }

                if (pmxFiles.Length > 0)
                {
                    await ImportPmxPackageAsync(
                        sourceRoot,
                        sourceIsDirectory,
                        pmxFiles,
                        originalArchivePath);
                }
                else if (vmdFiles.Length > 0)
                {
                    var playableVmdFiles = new List<string>();
                    foreach (var vmdFile in vmdFiles)
                    {
                        if (VmdActionFilePolicy.ContainsModelTracks(vmdFile))
                        {
                            playableVmdFiles.Add(vmdFile);
                        }
                    }

                    if (playableVmdFiles.Count == 0)
                    {
                        PreserveReferenceImport(originalArchivePath, sourceRoot, sourceIsDirectory, path);
                        return;
                    }

                    await ImportVmdAsync(sourceRoot, sourceIsDirectory, playableVmdFiles.ToArray());
                }
                else
                {
                    throw new InvalidDataException("没有找到 PMX 或 VMD 文件。");
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[FileImport] " + exception.Message, this);
                SetStatus("导入失败：" + exception.Message);
            }
            finally
            {
                if (!string.IsNullOrEmpty(stagingDirectory))
                {
                    TryDeleteDirectory(stagingDirectory);
                }
                if (!string.IsNullOrEmpty(selectionDirectory) &&
                    !string.Equals(selectionDirectory, stagingDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteDirectory(selectionDirectory);
                }

                pendingImportPath = string.Empty;
                IsBusy = false;
            }
        }

        private async Task ImportPmxPackageAsync(
            string sourceRoot,
            bool sourceIsDirectory,
            IReadOnlyList<string> sourcePmxFiles,
            string originalArchivePath)
        {
            if (modelLoader == null)
            {
                throw new InvalidOperationException("模型加载器尚未就绪。");
            }

            if (sourcePmxFiles == null || sourcePmxFiles.Count == 0)
            {
                throw new InvalidDataException("模型包中没有 PMX 模型。");
            }

            var preferredName = string.IsNullOrWhiteSpace(originalArchivePath)
                ? Path.GetFileNameWithoutExtension(sourcePmxFiles[0])
                : Path.GetFileNameWithoutExtension(originalArchivePath);
            var packageName = SanitizeImportedName(preferredName, "ImportedModel");
            var targetRoot = CreateUniqueDirectory(
                Path.Combine(Application.persistentDataPath, "MmdModels", "Imported"),
                packageName);
            var targetPmxFiles = new List<string>(sourcePmxFiles.Count);
            if (sourceIsDirectory)
            {
                CopyDirectory(sourceRoot, targetRoot);
                foreach (var sourcePmx in sourcePmxFiles)
                {
                    targetPmxFiles.Add(Path.Combine(targetRoot, GetRelativePath(sourceRoot, sourcePmx)));
                }
            }
            else
            {
                var sourcePmx = sourcePmxFiles[0];
                var targetPmx = Path.Combine(targetRoot, Path.GetFileName(sourcePmx));
                CopyFile(sourcePmx, targetPmx);
                targetPmxFiles.Add(targetPmx);
            }

            var selectedPmx = SelectPrimaryPmxCandidate(targetPmxFiles, preferredName);
            var orderedCandidates = new[] { selectedPmx }
                .Concat(targetPmxFiles.Where(path => !string.Equals(
                    path,
                    selectedPmx,
                    StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var selectedName = Path.GetFileNameWithoutExtension(selectedPmx);
            SetStatus(
                sourcePmxFiles.Count == 1
                    ? "正在加载角色：" + selectedName
                    : $"模型包已发现 {sourcePmxFiles.Count} 个角色，正在加载：{selectedName}");
            Exception lastException = null;
            var reachedBuildPhase = false;
            foreach (var candidate in orderedCandidates)
            {
                try
                {
                    await modelLoader.LoadFromFileAsync(candidate, targetRoot);
                    selectedPmx = candidate;
                    selectedName = Path.GetFileNameWithoutExtension(candidate);
                    lastException = null;
                    break;
                }
                catch (Exception exception)
                {
                    lastException = exception;
                    reachedBuildPhase |= modelLoader.LastFailurePhase == RuntimeModelLoadPhase.Building;
                    Debug.LogWarning(
                        "[FileImport] PMX variant preview failed: " +
                        Path.GetFileName(candidate) + ": " + exception.Message,
                        this);
                }
            }

            if (lastException != null)
            {
                // A package whose variants all fail parsing is not usable.
                // Once any PMX reaches the build phase, keep the complete
                // package because another device/runtime may build it and the
                // shared texture tree must remain intact.
                if (!reachedBuildPhase)
                {
                    TryDeleteDirectory(targetRoot);
                    throw lastException;
                }
                SetStatus($"模型包已导入（{sourcePmxFiles.Count} 个模型），预览加载失败，请从模型列表切换");
                return;
            }
            SetStatus(
                sourcePmxFiles.Count == 1
                    ? "角色导入完成：" + selectedName
                    : $"模型包导入完成：{sourcePmxFiles.Count} 个模型，当前为 {selectedName}");
        }

        internal static string SelectPrimaryPmxCandidate(
            IEnumerable<string> candidates,
            string preferredName)
        {
            var ordered = (candidates ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ordered.Length == 0)
            {
                throw new InvalidDataException("模型包中没有 PMX 模型。");
            }

            var preferredStem = Path.GetFileNameWithoutExtension(preferredName ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(preferredStem))
            {
                var exact = ordered.FirstOrDefault(path => string.Equals(
                    Path.GetFileNameWithoutExtension(path),
                    preferredStem,
                    StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(exact))
                {
                    return exact;
                }

                var archivePrefixMatch = ordered
                    .Select(path => new
                    {
                        Path = path,
                        Stem = Path.GetFileNameWithoutExtension(path)
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Stem) &&
                        preferredStem.StartsWith(item.Stem + "_", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.Stem.Length)
                    .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (archivePrefixMatch != null)
                {
                    return archivePrefixMatch.Path;
                }
            }
            return ordered[0];
        }

        private async Task ImportVmdAsync(string sourceRoot, bool sourceIsDirectory, string[] vmdFiles)
        {
            if (actionLibrary == null)
            {
                throw new InvalidOperationException("动作库尚未就绪。");
            }

            Directory.CreateDirectory(actionLibrary.MotionsDirectory);
            if (vmdFiles.Length == 1)
            {
                var actionName = SanitizeImportedName(
                    Path.GetFileNameWithoutExtension(vmdFiles[0]),
                    "ImportedMotion");
                actionName = CreateUniqueActionName(actionLibrary.MotionsDirectory, actionName);
                if (!VmdActionFilePolicy.TryResolveActionPath(
                    actionLibrary.MotionsDirectory,
                    actionName,
                    out var destination))
                {
                    throw new InvalidDataException("VMD 文件名不安全。");
                }

                VmdActionFilePolicy.Inspect(vmdFiles[0], actionName);
                try
                {
                    CopyFile(vmdFiles[0], destination);
                    await actionLibrary.RefreshAsync();
                }
                catch
                {
                    TryDeleteFile(destination);
                    throw;
                }
                SetStatus("动作导入完成：" + actionName);
                return;
            }

            var motion = vmdFiles.FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file), "motion.vmd", StringComparison.OrdinalIgnoreCase));
            motion ??= vmdFiles[0];
            var facial = vmdFiles.FirstOrDefault(file =>
                string.Equals(Path.GetFileName(file), "facial.vmd", StringComparison.OrdinalIgnoreCase));
            var packageName = "Imported_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff");
            VmdActionFilePolicy.Inspect(motion, packageName);
            if (!string.IsNullOrEmpty(facial))
            {
                VmdActionFilePolicy.Inspect(facial, packageName);
            }
            if (!VmdActionFilePolicy.TryResolvePackagePaths(
                actionLibrary.MotionsDirectory,
                packageName,
                out _,
                out _))
            {
                var packageDirectory = Path.Combine(actionLibrary.MotionsDirectory, packageName);
                Directory.CreateDirectory(packageDirectory);
            }

            var packageDirectoryPath = Path.Combine(actionLibrary.MotionsDirectory, packageName);
            try
            {
                Directory.CreateDirectory(packageDirectoryPath);
                var motionDestination = Path.Combine(packageDirectoryPath, "motion.vmd");
                CopyFile(motion, motionDestination);
                if (!string.IsNullOrEmpty(facial))
                {
                    CopyFile(facial, Path.Combine(packageDirectoryPath, "facial.vmd"));
                }

                await actionLibrary.RefreshAsync();
            }
            catch
            {
                TryDeleteDirectory(packageDirectoryPath);
                throw;
            }
            SetStatus("动作包导入完成：" + packageName);
        }

        private static string[] FilesWithExtension(IEnumerable<string> files, string extension)
        {
            return files
                .Where(file => !IsArchiveMetadataPath(file))
                .Where(file => string.Equals(
                    Path.GetExtension(file),
                    extension,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        private static string CreateUniqueActionName(string motionsDirectory, string baseName)
        {
            var candidate = SanitizeImportedName(baseName, "ImportedMotion");
            var suffix = 2;
            while (VmdActionFilePolicy.TryResolveActionPath(motionsDirectory, candidate, out var path) &&
                (File.Exists(path) || Directory.Exists(Path.Combine(motionsDirectory, candidate))))
            {
                candidate = SanitizeImportedName(baseName, "ImportedMotion") + "_" + suffix++;
            }

            return candidate;
        }

        private void PreserveReferenceImport(
            string originalArchivePath,
            string sourceRoot,
            bool sourceIsDirectory,
            string fallbackSourcePath)
        {
            var sourceName = !string.IsNullOrWhiteSpace(originalArchivePath)
                ? Path.GetFileNameWithoutExtension(originalArchivePath)
                : sourceIsDirectory
                    ? "ImportedReference"
                    : Path.GetFileNameWithoutExtension(fallbackSourcePath);
            var referenceRoot = Path.Combine(Application.persistentDataPath, "Imports", "References");
            var target = CreateUniqueDirectory(referenceRoot, SanitizeImportedName(sourceName, "ImportedReference"));

            if (!string.IsNullOrWhiteSpace(originalArchivePath) && File.Exists(originalArchivePath))
            {
                CopyFile(originalArchivePath, Path.Combine(target, Path.GetFileName(originalArchivePath)));
            }
            else if (sourceIsDirectory)
            {
                CopyDirectory(sourceRoot, target);
            }
            else if (File.Exists(fallbackSourcePath))
            {
                CopyFile(fallbackSourcePath, Path.Combine(target, Path.GetFileName(fallbackSourcePath)));
            }

            SetStatus("参考资源已保存，未加入角色动作列表：" + Path.GetFileName(target));
        }

        private static bool IsManagedSelectionDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return false;
            }

            var batchRoot = Path.Combine(Application.persistentDataPath, "Imports", "Batches");
            return IsWithinDirectory(batchRoot, path);
        }

        private static void ExtractArchiveSafely(string archivePath, string targetDirectory)
        {
            long expandedBytes = 0;
            var entryCount = 0;
            using (var stream = File.OpenRead(archivePath))
            using (var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Read,
                false,
                LegacyZipEntryEncoding.Instance))
            {
                foreach (var entry in archive.Entries)
                {
                    entryCount++;
                    if (entryCount > MaximumArchiveEntries)
                    {
                        throw new InvalidDataException("ZIP 文件条目过多。");
                    }

                    var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
                    if (!IsWithinDirectory(targetDirectory, destination))
                    {
                        throw new InvalidDataException("ZIP 文件包含越界路径。");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    expandedBytes = checked(expandedBytes + entry.Length);
                    if (expandedBytes > MaximumExpandedArchiveBytes)
                    {
                        throw new InvalidDataException("ZIP 解压内容超过 512 MiB 限制。");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var input = entry.Open())
                    using (var output = File.Create(destination))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        private static void CopyDirectory(string sourceDirectory, string targetDirectory)
        {
            foreach (var file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relative = GetRelativePath(sourceDirectory, file);
                var destination = Path.Combine(targetDirectory, relative);
                if (!IsWithinDirectory(sourceDirectory, file) || !IsWithinDirectory(targetDirectory, destination))
                {
                    throw new InvalidDataException("导入目录包含越界路径。");
                }

                CopyFile(file, destination);
            }
        }

        private static void CopyFile(string source, string destination)
        {
            var parent = Path.GetDirectoryName(destination);
            if (string.IsNullOrEmpty(parent))
            {
                throw new InvalidDataException("导入目标目录无效。");
            }

            Directory.CreateDirectory(parent);
            File.Copy(source, destination, true);
        }

        private static string CreateUniqueDirectory(string parent, string name)
        {
            Directory.CreateDirectory(parent);
            var candidate = Path.Combine(parent, name);
            var suffix = 2;
            while (Directory.Exists(candidate))
            {
                candidate = Path.Combine(parent, name + "_" + suffix++);
            }

            Directory.CreateDirectory(candidate);
            return candidate;
        }

        private static string GetRelativePath(string root, string path)
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("导入文件不在选择目录内。");
            }

            return fullPath.Substring(fullRoot.Length);
        }

        private static bool IsWithinDirectory(string root, string path)
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string DecodePayload(string value)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? string.Empty));
            }
            catch (FormatException)
            {
                return value ?? string.Empty;
            }
        }

        private void SetStatus(string value)
        {
            Status = value ?? string.Empty;
            StatusChanged?.Invoke(Status);
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[FileImport] Temporary cleanup failed: " + exception.Message);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[FileImport] Temporary file cleanup failed: " + exception.Message);
            }
        }

        private void OnDestroy()
        {
            pendingImportPath = string.Empty;
        }
    }
}

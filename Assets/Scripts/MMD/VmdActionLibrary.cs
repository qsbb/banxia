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
    public enum VmdPlaybackPhase
    {
        Idle,
        Loading,
        Playing,
        HoldingEndPose,
        BlendingOut,
        Failed
    }

    [Serializable]
    public sealed class VmdActionLimits
    {
        public long maxFileBytes = 16L * 1024L * 1024L;
        public int maxKeyframeCount = 100000;
        public float maxDurationSeconds = 120f;
        public float frameRate = 30f;
    }

    public sealed class VmdActionInfo
    {
        public VmdActionInfo(string id, long byteLength, int keyframeCount, uint lastFrame, float durationSeconds)
            : this(id, byteLength, keyframeCount, lastFrame, durationSeconds, false)
        {
        }

        public VmdActionInfo(string id, long byteLength, int keyframeCount, uint lastFrame, float durationSeconds, bool hasFacialTrack)
        {
            Id = id;
            DisplayName = id;
            ByteLength = byteLength;
            KeyframeCount = keyframeCount;
            LastFrame = lastFrame;
            DurationSeconds = durationSeconds;
            HasFacialTrack = hasFacialTrack;
        }

        public string Id { get; }
        public string DisplayName { get; }
        public long ByteLength { get; }
        public int KeyframeCount { get; }
        public uint LastFrame { get; }
        public float DurationSeconds { get; }
        public bool HasFacialTrack { get; }
    }

    public static class VmdActionFilePolicy
    {
        private const int SignatureLength = 30;
        private const int BoneRecordLength = 111;
        private const int MorphRecordLength = 23;
        private const int CameraRecordLength = 61;
        private const int LightRecordLength = 28;
        private const int ShadowRecordLength = 9;
        private const int ShowIkHeaderLength = 9;
        private const int IkToggleRecordLength = 21;

        public static bool TryResolveActionPath(string motionsDirectory, string actionId, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrEmpty(motionsDirectory) || !IsValidActionId(actionId))
            {
                return false;
            }

            try
            {
                var root = Path.GetFullPath(motionsDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var candidate = Path.GetFullPath(Path.Combine(root, actionId + ".vmd"));
                var parent = Path.GetDirectoryName(candidate)?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetExtension(candidate), ".vmd", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                path = candidate;
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException)
            {
                return false;
            }
        }

        public static bool TryResolvePackagePaths(
            string motionsDirectory,
            string actionId,
            out string motionPath,
            out string facialPath)
        {
            motionPath = string.Empty;
            facialPath = string.Empty;
            if (string.IsNullOrEmpty(motionsDirectory) || !IsValidActionId(actionId))
            {
                return false;
            }

            try
            {
                var root = Path.GetFullPath(motionsDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var package = Path.GetFullPath(Path.Combine(root, actionId));
                var parent = Path.GetDirectoryName(package)?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
                if (!string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var candidateMotionPath = Path.Combine(package, "motion.vmd");
                if (!File.Exists(candidateMotionPath))
                {
                    return false;
                }
                motionPath = candidateMotionPath;
                var candidateFacialPath = Path.Combine(package, "facial.vmd");
                facialPath = File.Exists(candidateFacialPath) ? candidateFacialPath : string.Empty;
                return true;
            }
            catch (Exception exception) when (
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PathTooLongException)
            {
                return false;
            }
        }

        public static VmdActionInfo Inspect(string path, string actionId, VmdActionLimits limits = null)
        {
            limits ??= new VmdActionLimits();
            ValidateLimits(limits);
            if (!IsValidActionId(actionId))
            {
                throw new InvalidDataException("VMD action id is invalid.");
            }

            var file = new FileInfo(path ?? string.Empty);
            if (!file.Exists)
            {
                throw new FileNotFoundException("VMD action was not found.", path);
            }
            if (file.Length <= SignatureLength || file.Length > limits.maxFileBytes)
            {
                throw new InvalidDataException("VMD action file size is outside the allowed range.");
            }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.ASCII, true);
            var signature = Encoding.ASCII.GetString(ReadExact(reader, SignatureLength, "signature")).TrimEnd('\0');
            int modelNameLength;
            if (signature == "Vocaloid Motion Data file")
            {
                modelNameLength = 10;
            }
            else if (signature == "Vocaloid Motion Data 0002")
            {
                modelNameLength = 20;
            }
            else
            {
                throw new InvalidDataException("VMD signature is invalid.");
            }

            SkipExact(stream, modelNameLength, "model name");
            long keyframeCount = 0;
            uint lastFrame = 0;
            var modelKeyframeCount = 0L;

            var boneCount = ReadCount(reader, "bone count");
            AddCount(ref keyframeCount, boneCount, limits);
            AddCount(ref modelKeyframeCount, boneCount, limits);
            InspectFixedRecords(reader, boneCount, BoneRecordLength, 15, ref lastFrame, "bone records");

            var morphCount = ReadCount(reader, "morph count");
            AddCount(ref keyframeCount, morphCount, limits);
            AddCount(ref modelKeyframeCount, morphCount, limits);
            InspectFixedRecords(reader, morphCount, MorphRecordLength, 15, ref lastFrame, "morph records");

            if (HasOptionalSection(stream))
            {
                var cameraCount = ReadCount(reader, "camera count");
                AddCount(ref keyframeCount, cameraCount, limits);
                InspectFixedRecords(reader, cameraCount, CameraRecordLength, 0, ref lastFrame, "camera records");
            }
            if (HasOptionalSection(stream))
            {
                var lightCount = ReadCount(reader, "light count");
                AddCount(ref keyframeCount, lightCount, limits);
                InspectFixedRecords(reader, lightCount, LightRecordLength, 0, ref lastFrame, "light records");
            }
            if (HasOptionalSection(stream))
            {
                var shadowCount = ReadCount(reader, "self-shadow count");
                AddCount(ref keyframeCount, shadowCount, limits);
                InspectFixedRecords(reader, shadowCount, ShadowRecordLength, 0, ref lastFrame, "self-shadow records");
            }
            if (HasOptionalSection(stream))
            {
                var showIkCount = ReadCount(reader, "show/IK count");
                AddCount(ref keyframeCount, showIkCount, limits);
                for (uint index = 0; index < showIkCount; index++)
                {
                    EnsureRemaining(stream, ShowIkHeaderLength, "show/IK record");
                    var frame = reader.ReadUInt32();
                    lastFrame = Math.Max(lastFrame, frame);
                    reader.ReadByte();
                    var toggleCount = reader.ReadUInt32();
                    AddCount(ref keyframeCount, toggleCount, limits);
                    EnsureRecordBlock(stream, toggleCount, IkToggleRecordLength, "IK toggle records");
                    stream.Seek((long)toggleCount * IkToggleRecordLength, SeekOrigin.Current);
                }
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("VMD action has an incomplete or unsupported trailing section.");
            }
            if (modelKeyframeCount == 0)
            {
                throw new InvalidDataException("VMD action does not contain model bone or morph keyframes.");
            }

            var duration = lastFrame / limits.frameRate;
            if (duration > limits.maxDurationSeconds)
            {
                throw new InvalidDataException("VMD action duration exceeds the allowed limit.");
            }

            return new VmdActionInfo(actionId, file.Length, checked((int)keyframeCount), lastFrame, duration);
        }

        /// <summary>
        /// Returns whether a VMD contains model tracks that can be applied to a
        /// PMX avatar. Camera-only VMD files are valid VMD files, but are not
        /// avatar actions and must stay out of the action library.
        /// </summary>
        public static bool ContainsModelTracks(string path)
        {
            var file = new FileInfo(path ?? string.Empty);
            if (!file.Exists || file.Length <= SignatureLength)
            {
                throw new InvalidDataException("VMD file is missing or too small.");
            }

            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.ASCII, true);
            var signature = Encoding.ASCII.GetString(ReadExact(reader, SignatureLength, "signature")).TrimEnd('\0');
            var modelNameLength = signature == "Vocaloid Motion Data file"
                ? 10
                : signature == "Vocaloid Motion Data 0002" ? 20 : 0;
            if (modelNameLength == 0)
            {
                throw new InvalidDataException("VMD signature is invalid.");
            }

            SkipExact(stream, modelNameLength, "model name");
            var boneCount = ReadCount(reader, "bone count");
            EnsureRecordBlock(stream, boneCount, BoneRecordLength, "bone records");
            if (boneCount > 0)
            {
                return true;
            }

            var morphCount = ReadCount(reader, "morph count");
            EnsureRecordBlock(stream, morphCount, MorphRecordLength, "morph records");
            return morphCount > 0;
        }

        private static bool IsValidActionId(string actionId)
        {
            if (string.IsNullOrWhiteSpace(actionId) || actionId.Length > 96 ||
                actionId != actionId.Trim() || actionId == "." || actionId == ".." ||
                actionId.EndsWith(".", StringComparison.Ordinal))
            {
                return false;
            }

            foreach (var value in actionId)
            {
                if (char.IsControl(value) || value == '/' || value == '\\' || value == ':' ||
                    Array.IndexOf(Path.GetInvalidFileNameChars(), value) >= 0)
                {
                    return false;
                }
            }
            return true;
        }

        private static void ValidateLimits(VmdActionLimits limits)
        {
            if (limits.maxFileBytes <= SignatureLength || limits.maxKeyframeCount <= 0 ||
                limits.maxDurationSeconds <= 0f || limits.frameRate <= 0f)
            {
                throw new ArgumentOutOfRangeException(nameof(limits), "VMD action limits must be positive.");
            }
        }

        private static byte[] ReadExact(BinaryReader reader, int count, string section)
        {
            EnsureRemaining(reader.BaseStream, count, section);
            var bytes = reader.ReadBytes(count);
            if (bytes.Length != count)
            {
                throw new InvalidDataException("VMD " + section + " is truncated.");
            }
            return bytes;
        }

        private static uint ReadCount(BinaryReader reader, string section)
        {
            EnsureRemaining(reader.BaseStream, sizeof(uint), section);
            return reader.ReadUInt32();
        }

        private static void InspectFixedRecords(
            BinaryReader reader,
            uint count,
            int recordLength,
            int frameOffset,
            ref uint lastFrame,
            string section)
        {
            var stream = reader.BaseStream;
            EnsureRecordBlock(stream, count, recordLength, section);
            for (uint index = 0; index < count; index++)
            {
                var start = stream.Position;
                stream.Seek(frameOffset, SeekOrigin.Current);
                var frame = reader.ReadUInt32();
                lastFrame = Math.Max(lastFrame, frame);
                stream.Position = start + recordLength;
            }
        }

        private static void AddCount(ref long total, uint count, VmdActionLimits limits)
        {
            total += count;
            if (total > limits.maxKeyframeCount)
            {
                throw new InvalidDataException("VMD action keyframe count exceeds the allowed limit.");
            }
        }

        private static bool HasOptionalSection(Stream stream)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining == 0)
            {
                return false;
            }
            if (remaining < sizeof(uint))
            {
                throw new InvalidDataException("VMD optional section count is truncated.");
            }
            return true;
        }

        private static void EnsureRecordBlock(Stream stream, uint count, int recordLength, string section)
        {
            var required = (long)count * recordLength;
            EnsureRemaining(stream, required, section);
        }

        private static void EnsureRemaining(Stream stream, long required, string section)
        {
            if (required < 0 || stream.Length - stream.Position < required)
            {
                throw new InvalidDataException("VMD " + section + " is truncated.");
            }
        }

        private static void SkipExact(Stream stream, int count, string section)
        {
            EnsureRemaining(stream, count, section);
            stream.Seek(count, SeekOrigin.Current);
        }
    }

    // UMT transform solver runs at 10000 in LateUpdate; VMD owns the final bone write.
    // Write imported motion before the post-process human interaction layer.
    [DefaultExecutionOrder(10900)]
    [DisallowMultipleComponent]
    public sealed class VmdActionLibrary : MonoBehaviour
    {
        private const string PersistentCacheConverterAbi = "banxia-umt-vmd-bake-1";
        [SerializeField] private VmdActionLimits limits = new VmdActionLimits();
        // Leave enough of Quest's 13.89 ms (72 Hz) frame for rendering and the
        // live avatar. The original player used a 3 ms slice; 10 ms caused the
        // VMD converter and a joint-heavy model's Bullet world to share a long
        // frame even though average CPU/GPU utilization stayed low.
        [SerializeField, Range(.5f, 8f)] private float frameBudgetMilliseconds = 2f;
        [SerializeField, Range(.25f, 2f)] private float endPoseHoldSeconds = 1f;
        [SerializeField, Range(.35f, 1.2f)] private float exitBlendSeconds = .65f;
        [SerializeField, Range(.05f, 1.2f)] private float physicsWarmUpDuration = .2f;
        [SerializeField, Range(30f, 900f)] private float cachedActionRetentionSeconds = 180f;
        [SerializeField, Range(1, 8)] private int maxPreparedActionCount = 3;

        private sealed class ActionSource
        {
            internal string motionPath;
            internal string facialPath;
            internal string cacheKey;
            internal VmdActionInfo info;
        }

        private sealed class CatalogScanResult
        {
            internal bool unchanged;
            internal bool stableSnapshot;
            internal string fingerprint = string.Empty;
            internal VmdActionInfo[] actions = Array.Empty<VmdActionInfo>();
            internal Dictionary<string, ActionSource> sources =
                new Dictionary<string, ActionSource>(StringComparer.OrdinalIgnoreCase);
            internal string[] warnings = Array.Empty<string>();
        }

        private sealed class PreparedAction
        {
            internal VmdActionInfo info;
            internal BoneBinding[] bones;
            internal MorphBinding[] morphs;
            internal string sourceCacheKey;
            internal float lastUsedAt;
        }

        private readonly Dictionary<string, ActionSource> actionPaths =
            new Dictionary<string, ActionSource>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, PreparedAction> preparedActions =
            new Dictionary<string, PreparedAction>(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim conversionGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim refreshGate = new SemaphoreSlim(1, 1);
        private readonly List<Task> pendingPersistentCacheWrites = new List<Task>();
        private VmdActionInfo[] actions = Array.Empty<VmdActionInfo>();
        private string lastPlayedActionId = string.Empty;
        private RuntimeDebugLog diagnostics;
        private string actionDirectoryFingerprint = string.Empty;
        private bool hasRefreshSnapshot;
        private PMXModel boundModel;
        private Transform boundRoot;
        private AvatarController boundAvatar;
        private MMDTransformManager transformManager;
        private PMXAnimationPaths animationPaths;
        private VmdPreparedClipCache preparedClipDiskCache;
        private string boundModelContentSha256 = string.Empty;
        private string boundAnimationBindingFingerprint = string.Empty;
        private BoneBinding[] boneBindings = Array.Empty<BoneBinding>();
        private MorphBinding[] morphBindings = Array.Empty<MorphBinding>();
        private int generation;
        private float playbackClock;
        private float playbackDuration;
        private bool physicsArbitrationActive;
        private bool previousTransformEnabled;
        private bool previousLivePhysics;
        private float physicsArbitrationStartedAt = -1f;
        private bool physicsResumePending;
        private float physicsResumedAt = -1f;
        private bool isDestroying;
        private bool isQuitting;
        private bool endPoseHoldActive;
        private float endPoseHoldClock;
        private bool blendOutActive;
        private float blendOutClock;
        private float nextCachePruneAt;

        public event Action ActionsChanged;
        public event Action PlaybackChanged;
        public event Action<string> OperationFailed;
        public event Action<string> ProgressChanged;

        public IReadOnlyList<VmdActionInfo> Actions => actions;
        public string MotionsDirectory => Path.Combine(Application.persistentDataPath, "Motions");
        public bool BoundModel => boundModel != null && boundRoot != null;
        public string CurrentActionId { get; private set; } = string.Empty;
        public bool IsLoading { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsHoldingEndPose => endPoseHoldActive;
        public bool IsBlendingOut => blendOutActive;
        public int PreparedActionCount => preparedActions.Count;
        public VmdPlaybackPhase PlaybackPhase { get; private set; } = VmdPlaybackPhase.Idle;
        public int CacheHitCount { get; private set; }
        public int CacheMissCount { get; private set; }
        public int CacheEvictionCount { get; private set; }
        public int DiskCacheHitCount { get; private set; }
        public int DiskCacheMissCount { get; private set; }
        public int DiskCacheInvalidCount { get; private set; }
        public int LastPrepareMilliseconds { get; private set; } = -1;
        public int LastPrepareReadMilliseconds { get; private set; } = -1;
        public int LastPrepareMotionConversionMilliseconds { get; private set; } = -1;
        public int LastPrepareFacialConversionMilliseconds { get; private set; } = -1;
        public int LastPrepareBindingMilliseconds { get; private set; } = -1;
        public int LastPrepareDiskReadMilliseconds { get; private set; } = -1;
        public int LastPrepareDiskRebuildMilliseconds { get; private set; } = -1;
        public int LastPrepareYieldCount { get; private set; }
        public float LastPrepareFrameBudgetMilliseconds { get; private set; } = -1f;
        public bool LastPrepareSuspendedLivePhysics { get; private set; }
        public bool LastPrepareUsedDiskCache { get; private set; }
        public int LastCatalogRefreshMilliseconds { get; private set; } = -1;
        public int LastCatalogActionCount { get; private set; }
        public bool IsPrepared(string actionId) => !string.IsNullOrEmpty(actionId) && preparedActions.ContainsKey(actionId);

        private sealed class BoneBinding
        {
            internal Transform target;
            internal AnimationCurve[] curves;
            internal Vector3 baselinePosition;
            internal Quaternion baselineRotation;
            internal Vector3 exitPosition;
            internal Quaternion exitRotation;
        }

        private sealed class MorphBinding
        {
            internal SkinnedMeshRenderer renderer;
            internal int blendShapeIndex;
            internal AnimationCurve curve;
            internal float baselineWeight;
            internal float exitWeight;
        }

        public async Task<IReadOnlyList<VmdActionInfo>> RefreshAsync()
        {
            var refreshStartedAt = Time.realtimeSinceStartup;
            diagnostics ??= GetComponent<RuntimeDebugLog>();
            diagnostics?.RecordStage("avatar_action", "processing", "vmd_catalog_scan");
            await refreshGate.WaitAsync();
            try
            {
                CatalogScanResult scan;
                try
                {
                    var directory = MotionsDirectory;
                    var scanLimits = CopyLimits(limits);
                    var previousFingerprint = actionDirectoryFingerprint;
                    var previousSnapshotAvailable = hasRefreshSnapshot;
                    scan = await Task.Run(() => ScanCatalog(
                        directory,
                        scanLimits,
                        previousFingerprint,
                        previousSnapshotAvailable));
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is ArgumentException ||
                    exception is InvalidDataException ||
                    exception is OverflowException)
                {
                    QuestDebugMode.Report(exception, "vmd.refresh");
                    QuestDebugMode.RethrowIfEnabled(exception, "vmd.refresh");
                    LastCatalogRefreshMilliseconds = ElapsedMilliseconds(refreshStartedAt);
                    ReportFailure("Unable to refresh VMD action directory: " + exception.Message);
                    diagnostics?.RecordStage(
                        "avatar_action",
                        "failed",
                        "vmd_catalog_failed",
                        elapsedMs: LastCatalogRefreshMilliseconds);
                    return actions;
                }

                if (scan.unchanged)
                {
                    LastCatalogRefreshMilliseconds = ElapsedMilliseconds(refreshStartedAt);
                    LastCatalogActionCount = actions.Length;
                    diagnostics?.RecordStage(
                        "avatar_action",
                        "completed",
                        "vmd_catalog_unchanged",
                        elapsedMs: LastCatalogRefreshMilliseconds,
                        eventCount: LastCatalogActionCount);
                    return actions;
                }
                foreach (var warning in scan.warnings)
                {
                    ReportFailure(warning);
                }
                actionPaths.Clear();
                var stalePrepared = preparedActions
                    .Where(pair => !scan.sources.TryGetValue(pair.Key, out var source) ||
                        !string.Equals(pair.Value.sourceCacheKey, source.cacheKey, StringComparison.Ordinal))
                    .Select(pair => pair.Key)
                    .ToArray();
                foreach (var actionId in stalePrepared)
                {
                    preparedActions.Remove(actionId);
                }
                foreach (var pair in scan.sources)
                {
                    actionPaths.Add(pair.Key, pair.Value);
                }
                actions = scan.actions;
                actionDirectoryFingerprint = scan.fingerprint;
                hasRefreshSnapshot = scan.stableSnapshot;
                LastCatalogRefreshMilliseconds = ElapsedMilliseconds(refreshStartedAt);
                LastCatalogActionCount = actions.Length;
                diagnostics?.RecordStage(
                    "avatar_action",
                    "completed",
                    "vmd_catalog_ready",
                    elapsedMs: LastCatalogRefreshMilliseconds,
                    eventCount: LastCatalogActionCount);
                ActionsChanged?.Invoke();
                return actions;
            }
            finally
            {
                refreshGate.Release();
            }
        }

        private static CatalogScanResult ScanCatalog(
            string directory,
            VmdActionLimits scanLimits,
            string previousFingerprint,
            bool previousSnapshotAvailable)
        {
            Directory.CreateDirectory(directory);
            var initialFingerprint = BuildActionDirectoryFingerprint(directory);
            if (previousSnapshotAvailable && string.Equals(
                    initialFingerprint,
                    previousFingerprint,
                    StringComparison.Ordinal))
            {
                return new CatalogScanResult
                {
                    unchanged = true,
                    stableSnapshot = true,
                    fingerprint = initialFingerprint
                };
            }

            var discovered = new List<VmdActionInfo>();
            var discoveredPaths = new Dictionary<string, ActionSource>(
                StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            var files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(
                    Path.GetExtension(path),
                    ".vmd",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var file in files)
            {
                var actionId = Path.GetFileNameWithoutExtension(file);
                if (!VmdActionFilePolicy.TryResolveActionPath(
                        directory,
                        actionId,
                        out var expectedPath) ||
                    !string.Equals(
                        Path.GetFullPath(file),
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase) ||
                    discoveredPaths.ContainsKey(actionId))
                {
                    warnings.Add("Ignored unsafe or duplicate VMD action: " + Path.GetFileName(file));
                    continue;
                }

                try
                {
                    var info = VmdActionFilePolicy.Inspect(file, actionId, scanLimits);
                    discovered.Add(info);
                    discoveredPaths.Add(info.Id, new ActionSource
                    {
                        motionPath = expectedPath,
                        cacheKey = BuildSourceCacheKey(expectedPath, string.Empty),
                        info = info
                    });
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is InvalidDataException ||
                    exception is ArgumentException)
                {
                    warnings.Add(
                        "VMD action rejected: " + Path.GetFileName(file) + " - " +
                        exception.Message);
                }
            }

            var packages = Directory.GetDirectories(
                    directory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var package in packages)
            {
                var actionId = Path.GetFileName(package);
                if (!VmdActionFilePolicy.TryResolvePackagePaths(
                        directory,
                        actionId,
                        out var motionPath,
                        out var facialPath) ||
                    discoveredPaths.ContainsKey(actionId))
                {
                    continue;
                }

                try
                {
                    var motionInfo = VmdActionFilePolicy.Inspect(
                        motionPath,
                        actionId,
                        scanLimits);
                    var info = motionInfo;
                    if (!string.IsNullOrEmpty(facialPath))
                    {
                        var facialInfo = VmdActionFilePolicy.Inspect(
                            facialPath,
                            actionId,
                            scanLimits);
                        var totalKeyframes = checked(
                            motionInfo.KeyframeCount + facialInfo.KeyframeCount);
                        if (totalKeyframes > scanLimits.maxKeyframeCount)
                        {
                            throw new InvalidDataException(
                                "VMD action package keyframe count exceeds the configured limit.");
                        }
                        info = new VmdActionInfo(
                            actionId,
                            checked(motionInfo.ByteLength + facialInfo.ByteLength),
                            totalKeyframes,
                            Math.Max(motionInfo.LastFrame, facialInfo.LastFrame),
                            Math.Max(
                                motionInfo.DurationSeconds,
                                facialInfo.DurationSeconds),
                            true);
                    }
                    discovered.Add(info);
                    discoveredPaths.Add(info.Id, new ActionSource
                    {
                        motionPath = motionPath,
                        facialPath = facialPath,
                        cacheKey = BuildSourceCacheKey(motionPath, facialPath),
                        info = info
                    });
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is InvalidDataException ||
                    exception is ArgumentException ||
                    exception is OverflowException)
                {
                    warnings.Add(
                        "VMD action package rejected: " + actionId + " - " +
                        exception.Message);
                }
            }

            var finalFingerprint = BuildActionDirectoryFingerprint(directory);
            return new CatalogScanResult
            {
                stableSnapshot = string.Equals(
                    initialFingerprint,
                    finalFingerprint,
                    StringComparison.Ordinal),
                fingerprint = finalFingerprint,
                actions = discovered.ToArray(),
                sources = discoveredPaths,
                warnings = warnings.ToArray()
            };
        }

        private static VmdActionLimits CopyLimits(VmdActionLimits source)
        {
            source ??= new VmdActionLimits();
            return new VmdActionLimits
            {
                maxFileBytes = source.maxFileBytes,
                maxKeyframeCount = source.maxKeyframeCount,
                maxDurationSeconds = source.maxDurationSeconds,
                frameRate = source.frameRate
            };
        }

        private static string BuildActionDirectoryFingerprint(string directory)
        {
            var root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var builder = new StringBuilder();
            var files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
            foreach (var path in files)
            {
                var info = new FileInfo(path);
                builder.Append(path.Substring(root.Length))
                    .Append(':')
                    .Append(info.Length)
                    .Append(':')
                    .Append(info.LastWriteTimeUtc.Ticks)
                    .Append('|');
            }
            return builder.ToString();
        }
        public void BindModel(
            PMXModel model,
            Transform modelRoot,
            AvatarController avatar,
            string modelContentSha256 = null)
        {
            ClearModel();
            diagnostics = GetComponent<RuntimeDebugLog>();
            if (model == null || modelRoot == null || avatar == null)
            {
                diagnostics?.RecordStage("avatar_action", "limited", "vmd_model_unbound");
                return;
            }

            generation++;
            preparedActions.Clear();
            boundModel = model;
            boundRoot = modelRoot;
            boundAvatar = avatar;
            transformManager = modelRoot.GetComponent<MMDTransformManager>();
            animationPaths = new PMXAnimationPaths();
            boundModelContentSha256 = NormalizeSha256(modelContentSha256);
            boundAnimationBindingFingerprint = BuildAnimationBindingFingerprint(modelRoot);
            diagnostics?.RecordStage("avatar_action", "ready", "vmd_model_bound");
        }

        public void ClearModel()
        {
            generation++;
            CompleteReturnToIdle();
            physicsArbitrationStartedAt = -1f;
            physicsResumePending = false;
            physicsResumedAt = -1f;
            preparedActions.Clear();
            boundModel = null;
            boundRoot = null;
            boundAvatar = null;
            transformManager = null;
            animationPaths = null;
            boundModelContentSha256 = string.Empty;
            boundAnimationBindingFingerprint = string.Empty;
            lastPlayedActionId = string.Empty;
            PlaybackPhase = VmdPlaybackPhase.Idle;
        }

        public async Task<bool> PlayAsync(string actionId)
        {
            var operationStartedAt = Time.realtimeSinceStartup;
            var usedPreparedCache = false;
            diagnostics ??= GetComponent<RuntimeDebugLog>();
            diagnostics?.RecordStage("avatar_action", "processing", "vmd_request");
            if (IsLoading)
            {
                ReportFailure("另一个 VMD 动作正在加载。");
                return false;
            }
            if (!BoundModel || string.IsNullOrEmpty(actionId) || !actionPaths.TryGetValue(actionId, out var source))
            {
                ReportFailure(BoundModel ? "请选择已刷新的 VMD 动作。" : "角色模型尚未准备好。");
                return false;
            }
            if (!IsSourcePathCurrent(actionId, source))
            {
                ReportFailure("VMD 动作路径验证失败。");
                return false;
            }

            IsLoading = true;
            PlaybackPhase = VmdPlaybackPhase.Loading;
            PlaybackChanged?.Invoke();
            var requestGeneration = generation;
            var requestModel = boundModel;
            var requestRoot = boundRoot;
            var requestTransformManager = transformManager;
            var requestAnimationPaths = animationPaths;
            var requestModelContentSha256 = boundModelContentSha256;
            var requestBindingFingerprint = boundAnimationBindingFingerprint;
            VMDAnimation motionAnimation = null;
            VMDAnimation facialAnimation = null;
            UMTFrameBudget preparationBudget = null;
            var preparationSuspendedLivePhysics = false;
            LastPrepareReadMilliseconds = -1;
            LastPrepareMotionConversionMilliseconds = -1;
            LastPrepareFacialConversionMilliseconds = -1;
            LastPrepareBindingMilliseconds = -1;
            LastPrepareDiskReadMilliseconds = -1;
            LastPrepareDiskRebuildMilliseconds = -1;
            LastPrepareYieldCount = 0;
            LastPrepareFrameBudgetMilliseconds = -1f;
            LastPrepareSuspendedLivePhysics = false;
            LastPrepareUsedDiskCache = false;
            await conversionGate.WaitAsync();
            try
            {
                VmdActionInfo info;
                BoneBinding[] nextBones;
                MorphBinding[] nextMorphs;
                var currentSourceCacheKey = BuildSourceCacheKey(
                    source.motionPath,
                    source.facialPath);
                if (!string.Equals(source.cacheKey, currentSourceCacheKey, StringComparison.Ordinal))
                {
                    source.cacheKey = currentSourceCacheKey;
                    source.info = null;
                    preparedActions.Remove(actionId);
                }
                if (preparedActions.TryGetValue(actionId, out var prepared) &&
                    string.Equals(prepared.sourceCacheKey, source.cacheKey, StringComparison.Ordinal))
                {
                    usedPreparedCache = true;
                    CacheHitCount++;
                    prepared.lastUsedAt = Time.unscaledTime;
                    info = prepared.info;
                    nextBones = prepared.bones;
                    nextMorphs = prepared.morphs;
                    ProgressChanged?.Invoke("正在使用已缓存动作 " + info.DisplayName);
                    diagnostics?.RecordStage("avatar_action", "processing", "vmd_cache_hit");
                }
                else
                {
                    CacheMissCount++;
                    preparedActions.Remove(actionId);
                    LastPrepareFrameBudgetMilliseconds = SelectPreparationFrameBudget(
                        frameBudgetMilliseconds,
                        requestModel == null || requestModel.rigidBodies == null
                            ? 0
                            : requestModel.rigidBodies.Length,
                        requestModel == null || requestModel.joints == null
                            ? 0
                            : requestModel.joints.Length);
                    preparationBudget = new UMTFrameBudget(
                        LastPrepareFrameBudgetMilliseconds);
                    preparationSuspendedLivePhysics = SuspendLivePhysicsForPreparation(
                        requestTransformManager);
                    LastPrepareSuspendedLivePhysics = preparationSuspendedLivePhysics;
                    info = source.info;
                    if (info == null)
                    {
                        info = VmdActionFilePolicy.Inspect(
                            source.motionPath,
                            actionId,
                            limits);
                        if (!string.IsNullOrEmpty(source.facialPath))
                        {
                            var facialInfo = VmdActionFilePolicy.Inspect(
                                source.facialPath,
                                actionId,
                                limits);
                            var totalKeyframes = checked(
                                info.KeyframeCount + facialInfo.KeyframeCount);
                            if (totalKeyframes > limits.maxKeyframeCount)
                            {
                                throw new InvalidDataException(
                                    "VMD action keyframe count exceeds the configured limit.");
                            }
                            info = new VmdActionInfo(
                                actionId,
                                checked(info.ByteLength + facialInfo.ByteLength),
                                totalKeyframes,
                                Math.Max(info.LastFrame, facialInfo.LastFrame),
                                Math.Max(
                                    info.DurationSeconds,
                                    facialInfo.DurationSeconds),
                                true);
                        }
                        source.info = info;
                    }

                    ProgressChanged?.Invoke("正在读取 " + info.DisplayName);
                    var options = CreateMotionConversionOptions(
                        limits.frameRate,
                        physicsWarmUpDuration);
                    var persistentCacheKey = await BuildPersistentCacheKeyAsync(
                        actionId,
                        source.motionPath,
                        source.facialPath,
                        requestModelContentSha256,
                        requestBindingFingerprint,
                        options,
                        Application.version,
                        Application.unityVersion);
                    var loadedFromDisk = false;
                    nextBones = Array.Empty<BoneBinding>();
                    nextMorphs = Array.Empty<MorphBinding>();
                    if (!string.IsNullOrEmpty(persistentCacheKey))
                    {
                        var diskReadStartedAt = Time.realtimeSinceStartup;
                        var diskResult = await GetPreparedClipDiskCache()
                            .TryReadAsync(persistentCacheKey);
                        LastPrepareDiskReadMilliseconds = ElapsedMilliseconds(diskReadStartedAt);
                        if (diskResult.IsHit && IsCachedClipCurrent(diskResult.Clip, info, actionId))
                        {
                            try
                            {
                                ProgressChanged?.Invoke("正在恢复已转换动作 " + info.DisplayName);
                                var rebuildStartedAt = Time.realtimeSinceStartup;
                                nextBones = await BuildBoneBindingsAsync(
                                    requestRoot,
                                    diskResult.Clip.Bones,
                                    preparationBudget,
                                    () => IsRequestCurrent(
                                        requestGeneration,
                                        requestModel,
                                        requestRoot));
                                if (!IsRequestCurrent(requestGeneration, requestModel, requestRoot))
                                {
                                    return false;
                                }
                                nextMorphs = await BuildMorphBindingsAsync(
                                    requestRoot,
                                    diskResult.Clip.Morphs,
                                    preparationBudget,
                                    () => IsRequestCurrent(
                                        requestGeneration,
                                        requestModel,
                                        requestRoot));
                                if (!IsRequestCurrent(requestGeneration, requestModel, requestRoot))
                                {
                                    return false;
                                }
                                LastPrepareDiskRebuildMilliseconds = ElapsedMilliseconds(
                                    rebuildStartedAt);
                                LastPrepareBindingMilliseconds =
                                    LastPrepareDiskRebuildMilliseconds;
                                if (nextBones.Length == 0 && nextMorphs.Length == 0)
                                {
                                    throw new InvalidDataException(
                                        "Cached VMD action has no tracks compatible with the current model.");
                                }
                                loadedFromDisk = true;
                                usedPreparedCache = true;
                                LastPrepareUsedDiskCache = true;
                                DiskCacheHitCount++;
                                diagnostics?.RecordStage(
                                    "avatar_action",
                                    "completed",
                                    "vmd_disk_cache_hit",
                                    elapsedMs: LastPrepareDiskReadMilliseconds +
                                        LastPrepareDiskRebuildMilliseconds);
                            }
                            catch (Exception exception) when (
                                exception is InvalidDataException ||
                                exception is ArgumentException ||
                                exception is OverflowException)
                            {
                                DiskCacheInvalidCount++;
                                GetPreparedClipDiskCache().Remove(persistentCacheKey);
                                diagnostics?.RecordStage(
                                    "avatar_action",
                                    "limited",
                                    "vmd_disk_cache_invalid");
                            }
                        }
                        else
                        {
                            DiskCacheMissCount++;
                            if (diskResult.IsHit)
                            {
                                DiskCacheInvalidCount++;
                                GetPreparedClipDiskCache().Remove(persistentCacheKey);
                            }
                            if (diskResult.CorruptEntryDeleted)
                            {
                                DiskCacheInvalidCount++;
                            }
                            diagnostics?.RecordStage(
                                "avatar_action",
                                "processing",
                                "vmd_disk_cache_miss",
                                elapsedMs: LastPrepareDiskReadMilliseconds);
                        }
                    }

                    if (!loadedFromDisk)
                    {
                        var stageStartedAt = Time.realtimeSinceStartup;
                        using (var stream = new FileStream(source.motionPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            motionAnimation = await VMDReader.ReadAsync(preparationBudget, stream);
                        }
                        if (!string.IsNullOrEmpty(source.facialPath))
                        {
                            using (var stream = new FileStream(source.facialPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                            {
                                facialAnimation = await VMDReader.ReadAsync(preparationBudget, stream);
                            }
                        }
                        LastPrepareReadMilliseconds = ElapsedMilliseconds(stageStartedAt);
                        diagnostics?.RecordStage(
                            "avatar_action",
                            "completed",
                            "vmd_read_completed",
                            elapsedMs: LastPrepareReadMilliseconds);
                        if (!IsRequestCurrent(requestGeneration, requestModel, requestRoot))
                        {
                            return false;
                        }

                        ProgressChanged?.Invoke("正在适配骨骼与表情");
                        stageStartedAt = Time.realtimeSinceStartup;
                        var clipData = await VMDAnimationClipConverter.ConvertAsync(
                            preparationBudget,
                            motionAnimation,
                            requestModel,
                            requestAnimationPaths,
                            options);
                        LastPrepareMotionConversionMilliseconds = ElapsedMilliseconds(stageStartedAt);
                        diagnostics?.RecordStage(
                            "avatar_action",
                            "completed",
                            "vmd_motion_converted",
                            elapsedMs: LastPrepareMotionConversionMilliseconds);
                        VMDModelClipData facialClipData = null;
                        if (facialAnimation != null)
                        {
                            stageStartedAt = Time.realtimeSinceStartup;
                            // Facial tracks do not require another full-body IK/physics bake.
                            facialClipData = await VMDAnimationClipConverter.ConvertAsync(
                                preparationBudget,
                                facialAnimation,
                                requestModel,
                                requestAnimationPaths,
                                CreateFacialConversionOptions(limits.frameRate));
                            LastPrepareFacialConversionMilliseconds = ElapsedMilliseconds(stageStartedAt);
                            diagnostics?.RecordStage(
                                "avatar_action",
                                "completed",
                                "vmd_facial_converted",
                                elapsedMs: LastPrepareFacialConversionMilliseconds);
                        }
                        if (!IsRequestCurrent(requestGeneration, requestModel, requestRoot))
                        {
                            return false;
                        }

                        stageStartedAt = Time.realtimeSinceStartup;
                        nextBones = BuildBoneBindings(requestRoot, clipData);
                        nextMorphs = MergeMorphBindings(
                            BuildMorphBindings(requestRoot, clipData),
                            BuildMorphBindings(requestRoot, facialClipData));
                        LastPrepareBindingMilliseconds = ElapsedMilliseconds(stageStartedAt);
                        if (!string.IsNullOrEmpty(persistentCacheKey))
                        {
                            TrackPersistentCacheWrite(CaptureAndPersistPreparedClipAsync(
                                persistentCacheKey,
                                info,
                                clipData,
                                facialClipData,
                                LastPrepareFrameBudgetMilliseconds));
                        }
                    }
                    LastPrepareYieldCount = preparationBudget.YieldCount;
                    diagnostics?.RecordStage(
                        "avatar_action",
                        "completed",
                        "vmd_bindings_ready",
                        elapsedMs: LastPrepareBindingMilliseconds,
                        eventCount: LastPrepareYieldCount);
                    if (nextBones.Length == 0 && nextMorphs.Length == 0)
                    {
                        throw new InvalidDataException("VMD action has no tracks compatible with the current model.");
                    }
                    preparedActions[actionId] = new PreparedAction
                    {
                        info = info,
                        bones = nextBones,
                        morphs = nextMorphs,
                        sourceCacheKey = source.cacheKey,
                        lastUsedAt = Time.unscaledTime
                    };
                    PrunePreparedActions(actionId);
                }

                if (!IsRequestCurrent(requestGeneration, requestModel, requestRoot))
                {
                    return false;
                }
                RestoreLivePhysicsAfterPreparation(
                    requestTransformManager,
                    ref preparationSuspendedLivePhysics);
                CompleteReturnToIdle();
                boneBindings = nextBones;
                morphBindings = nextMorphs;
                playbackClock = 0f;
                playbackDuration = Mathf.Max(.01f, info.DurationSeconds);
                CurrentActionId = info.Id;
                lastPlayedActionId = info.Id;
                BeginPhysicsArbitration();
                IsPlaying = true;
                PlaybackPhase = VmdPlaybackPhase.Playing;
                boundAvatar?.PlayActionFromSource("vmd", AvatarActionSource.Imported);
                ProgressChanged?.Invoke("正在播放 " + info.DisplayName);
                Debug.Log(
                    "[VmdActionLibrary] Playback ready: action=" + info.Id +
                    " cache=" + usedPreparedCache +
                    " disk_cache=" + LastPrepareUsedDiskCache +
                    " yields=" + LastPrepareYieldCount +
                    " disk_read_ms=" + LastPrepareDiskReadMilliseconds +
                    " disk_rebuild_ms=" + LastPrepareDiskRebuildMilliseconds +
                    " read_ms=" + LastPrepareReadMilliseconds +
                    " motion_convert_ms=" + LastPrepareMotionConversionMilliseconds +
                    " facial_convert_ms=" + LastPrepareFacialConversionMilliseconds +
                    " binding_ms=" + LastPrepareBindingMilliseconds +
                    " frame_budget_ms=" + LastPrepareFrameBudgetMilliseconds.ToString("F2") +
                    " live_physics_paused=" + LastPrepareSuspendedLivePhysics +
                    " elapsed_ms=" + Mathf.Max(0, Mathf.RoundToInt((Time.realtimeSinceStartup - operationStartedAt) * 1000f)),
                    this);
                LastPrepareMilliseconds = Mathf.Max(
                    0,
                    Mathf.RoundToInt((Time.realtimeSinceStartup - operationStartedAt) * 1000f));
                diagnostics?.RecordStage(
                    "avatar_action",
                    "completed",
                    usedPreparedCache ? "vmd_playback_cached" : "vmd_playback_prepared",
                    elapsedMs: LastPrepareMilliseconds);
                PlaybackChanged?.Invoke();
                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is InvalidDataException ||
                exception is ArgumentException ||
                exception is OverflowException)
            {
                QuestDebugMode.Report(exception, "vmd.play");
                QuestDebugMode.RethrowIfEnabled(exception, "vmd.play");
                ReportFailure("VMD 动作加载失败：" + exception.Message);
                PlaybackPhase = VmdPlaybackPhase.Failed;
                LastPrepareMilliseconds = Mathf.Max(
                    0,
                    Mathf.RoundToInt((Time.realtimeSinceStartup - operationStartedAt) * 1000f));
                Debug.LogWarning(
                    "[VmdActionLibrary] Playback failed: action=" + actionId +
                    " elapsed_ms=" + Mathf.Max(0, Mathf.RoundToInt((Time.realtimeSinceStartup - operationStartedAt) * 1000f)) +
                    " reason=" + exception.Message,
                    this);
                diagnostics?.RecordStage(
                    "avatar_action",
                    "failed",
                    "vmd_load_failed",
                    elapsedMs: LastPrepareMilliseconds);
                return false;
            }
            finally
            {
                RestoreLivePhysicsAfterPreparation(
                    requestTransformManager,
                    ref preparationSuspendedLivePhysics);
                if (motionAnimation != null)
                {
                    Destroy(motionAnimation);
                }
                if (facialAnimation != null)
                {
                    Destroy(facialAnimation);
                }
                conversionGate.Release();
                IsLoading = false;
                PlaybackChanged?.Invoke();
            }
        }

        private static VMDAnimationClipOptions CreateMotionConversionOptions(
            float frameRate,
            float warmUpDuration)
        {
            return new VMDAnimationClipOptions
            {
                frameRate = Mathf.Max(.01f, frameRate),
                bakeIKToFK = true,
                bakePhysicsToFK = true,
                physicsWarmUpDuration = Mathf.Clamp(warmUpDuration, .05f, 1.2f)
            };
        }

        public static float SelectPreparationFrameBudget(
            float configuredMilliseconds,
            int rigidBodyCount,
            int jointCount)
        {
            var configured = Mathf.Clamp(configuredMilliseconds, .5f, 8f);
            var bodies = Mathf.Max(0, rigidBodyCount);
            var joints = Mathf.Max(0, jointCount);

            // A joint-heavy avatar still runs its normal render/skin workload
            // while the converter bakes every motion frame on the main thread.
            // Keep those slices below one millisecond so the bake does not
            // consume the remaining headroom of a 72 Hz XR frame. Typical PMX
            // avatars retain the configured budget and therefore their current
            // first-play latency.
            if (bodies >= 90 || joints >= 100 || bodies + joints >= 200)
            {
                return Mathf.Min(configured, .8f);
            }
            return configured;
        }

        private static VMDAnimationClipOptions CreateFacialConversionOptions(float frameRate)
        {
            return new VMDAnimationClipOptions
            {
                frameRate = Mathf.Max(.01f, frameRate),
                bakeIKToFK = false,
                bakePhysicsToFK = false
            };
        }

        private static bool SuspendLivePhysicsForPreparation(MMDTransformManager manager)
        {
            if (manager == null || !manager.livePhysics)
            {
                return false;
            }

            manager.physicsManager?.DiscardAccumulatedSimulationTime();
            manager.livePhysics = false;
            return true;
        }

        private static void RestoreLivePhysicsAfterPreparation(
            MMDTransformManager manager,
            ref bool suspendedByPreparation)
        {
            if (!suspendedByPreparation)
            {
                return;
            }

            suspendedByPreparation = false;
            if (manager == null)
            {
                return;
            }
            manager.livePhysics = true;
            if (manager.physicsManager != null)
            {
                manager.physicsManager.ReseedFromCurrentPose();
                Debug.Log(
                    "[VmdActionLibrary] 实时物理已从当前骨架重新播种，头发和衣物速度已清除。",
                    manager.physicsManager);
            }
        }

        private static int ElapsedMilliseconds(float startedAt)
        {
            return Mathf.Max(
                0,
                Mathf.RoundToInt((Time.realtimeSinceStartup - startedAt) * 1000f));
        }

        public async Task<bool> PlayRecommendedDanceAsync()
        {
            // Refresh at request time so newly imported files are eligible;
            // unchanged directories return the cached catalog immediately.
            await RefreshAsync();

            var candidate = SelectRecommendedDance(actions);
            if (candidate == null)
            {
                Debug.LogWarning("[VmdActionLibrary] No imported action is available for the dance request.", this);
                return false;
            }

            Debug.Log("[VmdActionLibrary] Recommended dance selected imported action: " + candidate.Id, this);
            return await PlayAsync(candidate.Id);
        }

        public async Task<bool> PlayNextDanceAsync()
        {
            await RefreshAsync();
            var previous = string.IsNullOrEmpty(CurrentActionId)
                ? lastPlayedActionId
                : CurrentActionId;
            var candidate = SelectNextDance(actions, previous);
            if (candidate == null)
            {
                Debug.LogWarning("[VmdActionLibrary] No alternate imported action is available for the dance request.", this);
                return false;
            }

            Debug.Log("[VmdActionLibrary] Next dance selected imported action: " + candidate.Id +
                " previous=" + (string.IsNullOrEmpty(previous) ? "none" : previous), this);
            return await PlayAsync(candidate.Id);
        }

        public static VmdActionInfo SelectRecommendedDance(IEnumerable<VmdActionInfo> available)
        {
            var valid = (available ?? Enumerable.Empty<VmdActionInfo>())
                .Where(info => info != null)
                .ToArray();
            var candidate = valid
                .Where(info => info != null && IsDanceLikeName(info.Id))
                .OrderByDescending(info => DanceNameScore(info.Id))
                .ThenBy(info => info.Id, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            // Imported actions are user-authored and are not required to use a
            // dance-related filename. If no semantic filename is available,
            // choose the first validated custom action instead of silently
            // falling back to the rigid built-in pose.
            if (candidate == null)
            {
                candidate = valid
                    .OrderBy(info => info.Id, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            }
            return candidate;
        }

        public static VmdActionInfo SelectNextDance(
            IEnumerable<VmdActionInfo> available,
            string currentActionId)
        {
            var ordered = (available ?? Enumerable.Empty<VmdActionInfo>())
                .Where(info => info != null)
                .OrderByDescending(info => IsDanceLikeName(info.Id))
                .ThenByDescending(info => DanceNameScore(info.Id))
                .ThenBy(info => info.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (ordered.Length == 0)
            {
                return null;
            }

            var currentIndex = Array.FindIndex(
                ordered,
                info => string.Equals(info.Id, currentActionId, StringComparison.OrdinalIgnoreCase));
            return currentIndex < 0
                ? ordered[0]
                : ordered[(currentIndex + 1) % ordered.Length];
        }

        public static bool IsDanceLikeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
            var normalized = value.Trim().ToLowerInvariant();
            return normalized.Contains("dance") || normalized.Contains("舞") ||
                normalized.Contains("踊") || normalized.Contains("跳");
        }

        private static int DanceNameScore(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return 0;
            }
            var normalized = value.Trim().ToLowerInvariant();
            var score = 0;
            if (normalized.Contains("dance")) score += 4;
            if (normalized.Contains("舞")) score += 3;
            if (normalized.Contains("踊")) score += 2;
            if (normalized.Contains("跳")) score += 1;
            return score;
        }

        public async Task<bool> DeleteActionAsync(string actionId)
        {
            if (string.IsNullOrWhiteSpace(actionId) || !actionPaths.ContainsKey(actionId))
            {
                return false;
            }

            if (string.Equals(CurrentActionId, actionId, StringComparison.OrdinalIgnoreCase))
            {
                CompleteReturnToIdle();
            }

            var source = actionPaths[actionId];
            try
            {
                if (VmdActionFilePolicy.TryResolveActionPath(MotionsDirectory, actionId, out var directPath) &&
                    string.Equals(source.motionPath, directPath, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(directPath))
                    {
                        File.Delete(directPath);
                    }
                }
                else if (VmdActionFilePolicy.TryResolvePackagePaths(
                    MotionsDirectory, actionId, out var packageMotion, out _))
                {
                    var packageDirectory = Path.GetDirectoryName(packageMotion);
                    if (!string.IsNullOrEmpty(packageDirectory) && Directory.Exists(packageDirectory))
                    {
                        Directory.Delete(packageDirectory, true);
                    }
                }
                else
                {
                    return false;
                }

                await RefreshAsync();
                return true;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentException)
            {
                QuestDebugMode.Report(exception, "vmd.delete");
                QuestDebugMode.RethrowIfEnabled(exception, "vmd.delete");
                ReportFailure("VMD action delete failed: " + exception.Message);
                return false;
            }
        }
        public void StopAndReturnToIdle()
        {
            if (blendOutActive)
            {
                return;
            }
            if (!IsPlaying)
            {
                CompleteReturnToIdle();
                return;
            }
            BeginBlendOut();
        }

        private void LateUpdate()
        {
            if (physicsResumePending)
            {
                physicsResumePending = false;
                var elapsedMs = physicsResumedAt < 0f
                    ? -1
                    : Mathf.Max(0, Mathf.RoundToInt((Time.realtimeSinceStartup - physicsResumedAt) * 1000f));
                diagnostics?.RecordStage(
                    "mmd_physics",
                    "completed",
                    "physics_resume_first_frame",
                    elapsedMs: elapsedMs);
                physicsResumedAt = -1f;
            }
            if (blendOutActive)
            {
                UpdateBlendOut();
                return;
            }
            if (endPoseHoldActive)
            {
                // Keep writing the final frame so later animation systems cannot
                // expose the imported MMD default pose during the hold.
                ApplyPlaybackPose(playbackDuration);
                endPoseHoldClock += Time.deltaTime;
                if (IsEndPoseHoldComplete(endPoseHoldClock, endPoseHoldSeconds))
                {
                    BeginBlendOut();
                }
                return;
            }
            if (!IsPlaying)
            {
                return;
            }

            playbackClock += Time.deltaTime;
            ApplyPlaybackPose(Mathf.Min(playbackClock, playbackDuration));
            if (playbackClock >= playbackDuration)
            {
                BeginEndPoseHold();
            }
        }

        private void Update()
        {
            if (Time.unscaledTime < nextCachePruneAt)
            {
                return;
            }
            nextCachePruneAt = Time.unscaledTime + 5f;
            PrunePreparedActions(CurrentActionId);
        }

        private void PrunePreparedActions(string protectedActionId)
        {
            var now = Time.unscaledTime;
            var expired = preparedActions
                .Where(pair => !string.Equals(pair.Key, protectedActionId, StringComparison.OrdinalIgnoreCase) &&
                    IsPreparedActionExpired(pair.Value.lastUsedAt, now, cachedActionRetentionSeconds))
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var actionId in expired)
            {
                preparedActions.Remove(actionId);
                CacheEvictionCount++;
            }

            var maximum = Mathf.Clamp(maxPreparedActionCount, 1, 8);
            while (preparedActions.Count > maximum)
            {
                var oldest = preparedActions
                    .Where(pair => !string.Equals(pair.Key, protectedActionId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(pair => pair.Value.lastUsedAt)
                    .Select(pair => pair.Key)
                    .FirstOrDefault();
                if (string.IsNullOrEmpty(oldest))
                {
                    break;
                }
                preparedActions.Remove(oldest);
                CacheEvictionCount++;
            }
        }

        public static bool IsPreparedActionExpired(float lastUsedAt, float now, float retentionSeconds)
        {
            return now >= lastUsedAt && now - lastUsedAt >= Mathf.Max(0f, retentionSeconds);
        }

        private VmdPreparedClipCache GetPreparedClipDiskCache()
        {
            preparedClipDiskCache ??= new VmdPreparedClipCache();
            return preparedClipDiskCache;
        }

        private async Task PersistPreparedClipAsync(VmdPreparedClipDto clip)
        {
            try
            {
                var result = await GetPreparedClipDiskCache().TryWriteDetailedAsync(clip);
                diagnostics?.RecordStage(
                    "avatar_action",
                    result.Written ? "completed" : "limited",
                    result.Written
                        ? "vmd_disk_cache_written"
                        : "vmd_disk_cache_write_" + result.Reason,
                    bytes: (int)Math.Min(int.MaxValue, result.EntryBytes));
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is InvalidDataException ||
                exception is ArgumentException ||
                exception is OverflowException)
            {
                QuestDebugMode.Report(exception, "vmd.cache-write");
                diagnostics?.RecordStage(
                    "avatar_action",
                    "limited",
                    "vmd_disk_cache_write_failed");
            }
        }

        private void TrackPersistentCacheWrite(Task task)
        {
            if (task == null)
            {
                return;
            }
            pendingPersistentCacheWrites.Add(task);
            ObservePersistentCacheWriteAsync(task).Forget("vmd.cache-observe");
        }

        private async Task ObservePersistentCacheWriteAsync(Task task)
        {
            try
            {
                await task;
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "vmd.cache-write");
                diagnostics?.RecordStage(
                    "avatar_action",
                    "limited",
                    "vmd_disk_cache_write_failed");
            }
            finally
            {
                pendingPersistentCacheWrites.Remove(task);
            }
        }

        public async Task WaitForPendingDiskCacheWritesAsync()
        {
            while (pendingPersistentCacheWrites.Count > 0)
            {
                var snapshot = pendingPersistentCacheWrites.ToArray();
                try
                {
                    await Task.WhenAll(snapshot);
                }
                catch (Exception exception)
                {
                    QuestDebugMode.Report(exception, "vmd.cache-wait");
                    // Observation above records the bounded diagnostic. A cache
                    // write can never turn a successfully prepared action into
                    // a failed QA or conversation turn.
                }
                pendingPersistentCacheWrites.RemoveAll(item => item.IsCompleted);
            }
        }

        private async Task CaptureAndPersistPreparedClipAsync(
            string cacheKey,
            VmdActionInfo info,
            VMDModelClipData motion,
            VMDModelClipData facial,
            float frameBudget)
        {
            try
            {
                // Let the requested action start first. Curve projection then
                // continues in small main-thread slices while file I/O remains
                // entirely on the worker used by TryWriteAsync.
                await Task.Yield();
                var clip = await BuildPreparedClipDtoAsync(
                    cacheKey,
                    info,
                    motion,
                    facial,
                    new UMTFrameBudget(Math.Max(.5f, frameBudget)));
                var curveCount = clip.Bones.Sum(bone => bone.Curves.Count(curve => curve != null)) +
                    clip.Morphs.Length;
                var keyCount = clip.Bones.Sum(bone => bone.Curves
                        .Where(curve => curve != null)
                        .Sum(curve => curve.Keys.Length)) +
                    clip.Morphs.Sum(morph => morph.Curve.Keys.Length);
                Debug.Log(
                    "[VmdActionLibrary] Disk cache capture: bones=" + clip.Bones.Length +
                    " morphs=" + clip.Morphs.Length +
                    " curves=" + curveCount +
                    " keys=" + keyCount,
                    this);
                await PersistPreparedClipAsync(clip);
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is InvalidDataException ||
                exception is ArgumentException ||
                exception is OverflowException)
            {
                diagnostics?.RecordStage(
                    "avatar_action",
                    "limited",
                    "vmd_disk_cache_capture_failed");
            }
        }

        private static async Task<string> BuildPersistentCacheKeyAsync(
            string actionId,
            string motionPath,
            string facialPath,
            string modelContentSha256,
            string bindingFingerprint,
            VMDAnimationClipOptions options,
            string applicationVersion,
            string unityVersion)
        {
            var normalizedModelHash = NormalizeSha256(modelContentSha256);
            var normalizedBindingHash = NormalizeSha256(bindingFingerprint);
            if (string.IsNullOrEmpty(normalizedModelHash) ||
                string.IsNullOrEmpty(normalizedBindingHash) || options == null)
            {
                return string.Empty;
            }

            try
            {
                var sourceBefore = BuildSourceCacheKey(motionPath, facialPath);
                var hashes = await Task.Run(() => new[]
                {
                    ComputeFileSha256(motionPath),
                    string.IsNullOrEmpty(facialPath)
                        ? "absent"
                        : ComputeFileSha256(facialPath)
                });
                if (!string.Equals(
                    sourceBefore,
                    BuildSourceCacheKey(motionPath, facialPath),
                    StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                var descriptor = new StringBuilder(512);
                AppendCacheKeyPart(descriptor, "schema", VmdPreparedClipCache.CurrentFormatVersion.ToString());
                AppendCacheKeyPart(descriptor, "converter", PersistentCacheConverterAbi);
                AppendCacheKeyPart(descriptor, "app", applicationVersion ?? string.Empty);
                AppendCacheKeyPart(descriptor, "unity", unityVersion ?? string.Empty);
                AppendCacheKeyPart(descriptor, "umt", "0.5.0-bx");
                AppendCacheKeyPart(descriptor, "action", actionId ?? string.Empty);
                AppendCacheKeyPart(descriptor, "model", normalizedModelHash);
                AppendCacheKeyPart(descriptor, "bindings", normalizedBindingHash);
                AppendCacheKeyPart(descriptor, "motion", hashes[0]);
                AppendCacheKeyPart(descriptor, "facial", hashes[1]);
                AppendCacheKeyPart(descriptor, "frame_rate", FloatBits(options.frameRate).ToString());
                AppendCacheKeyPart(descriptor, "bake_ik", options.bakeIKToFK ? "1" : "0");
                AppendCacheKeyPart(descriptor, "bake_physics", options.bakePhysicsToFK ? "1" : "0");
                AppendCacheKeyPart(descriptor, "physics_seed", options.physicsSeed.ToString());
                AppendCacheKeyPart(
                    descriptor,
                    "physics_warmup",
                    FloatBits(options.physicsWarmUpDuration).ToString());
                return ComputeSha256Hex(Encoding.UTF8.GetBytes(descriptor.ToString()));
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is CryptographicException)
            {
                return string.Empty;
            }
        }

        private static async Task<VmdPreparedClipDto> BuildPreparedClipDtoAsync(
            string cacheKey,
            VmdActionInfo info,
            VMDModelClipData motion,
            VMDModelClipData facial,
            UMTFrameBudget budget)
        {
            if (info == null || motion == null || !motion.baked || motion.bones == null ||
                motion.bones.paths == null || motion.bones.curves == null)
            {
                throw new InvalidDataException("VMD converter did not return cacheable baked curves.");
            }

            const int channelCount = 7;
            var bones = new List<VmdPreparedBoneTrackDto>();
            for (var index = 0; index < motion.bones.paths.Length; index++)
            {
                var offset = checked(index * channelCount);
                if (offset + channelCount > motion.bones.curves.Length)
                {
                    throw new InvalidDataException("VMD bone curve layout is invalid.");
                }
                var curves = new VmdPreparedCurveDto[channelCount];
                var hasCurve = false;
                for (var channel = 0; channel < channelCount; channel++)
                {
                    var curve = motion.bones.curves[offset + channel];
                    curves[channel] = ToCurveDto(curve);
                    hasCurve |= curve != null;
                }
                if (hasCurve)
                {
                    bones.Add(new VmdPreparedBoneTrackDto
                    {
                        Path = motion.bones.paths[index],
                        Curves = curves
                    });
                }
                await budget.YieldIfNeeded();
            }

            var morphs = new Dictionary<string, VmdPreparedMorphTrackDto>(StringComparer.Ordinal);
            await AddMorphDtosAsync(morphs, motion, budget);
            await AddMorphDtosAsync(morphs, facial, budget);
            return new VmdPreparedClipDto
            {
                CacheKey = cacheKey,
                ActionId = info.Id,
                SourceByteLength = info.ByteLength,
                SourceKeyframeCount = info.KeyframeCount,
                LastFrame = info.LastFrame,
                DurationSeconds = info.DurationSeconds,
                HasFacialTrack = info.HasFacialTrack,
                Bones = bones.ToArray(),
                Morphs = morphs.Values.ToArray()
            };
        }

        private static async Task AddMorphDtosAsync(
            IDictionary<string, VmdPreparedMorphTrackDto> destination,
            VMDModelClipData clip,
            UMTFrameBudget budget)
        {
            if (clip?.morphs == null)
            {
                return;
            }
            var morphs = clip.morphs;
            if (morphs.paths == null || morphs.names == null || morphs.curves == null ||
                morphs.paths.Length != morphs.names.Length ||
                morphs.paths.Length != morphs.curves.Length)
            {
                throw new InvalidDataException("VMD morph curve layout is invalid.");
            }
            for (var index = 0; index < morphs.paths.Length; index++)
            {
                if (morphs.curves[index] != null)
                {
                    var track = new VmdPreparedMorphTrackDto
                    {
                        Path = morphs.paths[index],
                        Name = morphs.names[index],
                        Curve = ToCurveDto(morphs.curves[index])
                    };
                    destination[BuildMorphTrackKey(track.Path, track.Name)] = track;
                }
                await budget.YieldIfNeeded();
            }
        }

        private static async Task<BoneBinding[]> BuildBoneBindingsAsync(
            Transform root,
            IEnumerable<VmdPreparedBoneTrackDto> tracks,
            UMTFrameBudget budget,
            Func<bool> stillCurrent)
        {
            var result = new List<BoneBinding>();
            foreach (var track in tracks ?? Array.Empty<VmdPreparedBoneTrackDto>())
            {
                if (stillCurrent != null && !stillCurrent())
                {
                    return Array.Empty<BoneBinding>();
                }
                if (track?.Curves == null || track.Curves.Length != 7)
                {
                    throw new InvalidDataException("Cached VMD bone curve layout is invalid.");
                }
                var target = root.Find(track.Path);
                if (target != null)
                {
                    result.Add(new BoneBinding
                    {
                        target = target,
                        curves = track.Curves.Select(FromCurveDto).ToArray(),
                        baselinePosition = target.localPosition,
                        baselineRotation = target.localRotation
                    });
                }
                await budget.YieldIfNeeded();
            }
            return result.ToArray();
        }

        private static async Task<MorphBinding[]> BuildMorphBindingsAsync(
            Transform root,
            IEnumerable<VmdPreparedMorphTrackDto> tracks,
            UMTFrameBudget budget,
            Func<bool> stillCurrent)
        {
            var result = new List<MorphBinding>();
            foreach (var track in tracks ?? Array.Empty<VmdPreparedMorphTrackDto>())
            {
                if (stillCurrent != null && !stillCurrent())
                {
                    return Array.Empty<MorphBinding>();
                }
                if (track?.Curve == null)
                {
                    throw new InvalidDataException("Cached VMD morph curve is invalid.");
                }
                var target = root.Find(track.Path);
                var renderer = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                var blendShapeIndex = renderer == null || renderer.sharedMesh == null
                    ? -1
                    : renderer.sharedMesh.GetBlendShapeIndex(track.Name);
                if (blendShapeIndex >= 0)
                {
                    result.Add(new MorphBinding
                    {
                        renderer = renderer,
                        blendShapeIndex = blendShapeIndex,
                        curve = FromCurveDto(track.Curve),
                        baselineWeight = renderer.GetBlendShapeWeight(blendShapeIndex)
                    });
                }
                await budget.YieldIfNeeded();
            }
            return result.ToArray();
        }

        private static VmdPreparedCurveDto ToCurveDto(AnimationCurve curve)
        {
            if (curve == null)
            {
                return null;
            }
            var keys = curve.keys;
            var result = new VmdPreparedKeyframeDto[keys.Length];
            for (var index = 0; index < keys.Length; index++)
            {
                result[index] = new VmdPreparedKeyframeDto
                {
                    Time = keys[index].time,
                    Value = keys[index].value,
                    InTangent = keys[index].inTangent,
                    OutTangent = keys[index].outTangent,
                    InWeight = keys[index].inWeight,
                    OutWeight = keys[index].outWeight,
                    WeightedMode = (int)keys[index].weightedMode
                };
            }
            return new VmdPreparedCurveDto
            {
                PreWrapMode = (int)curve.preWrapMode,
                PostWrapMode = (int)curve.postWrapMode,
                Keys = result
            };
        }

        private static AnimationCurve FromCurveDto(VmdPreparedCurveDto curve)
        {
            if (curve == null)
            {
                return null;
            }
            var keys = new Keyframe[curve.Keys.Length];
            for (var index = 0; index < keys.Length; index++)
            {
                var source = curve.Keys[index];
                keys[index] = new Keyframe(
                    source.Time,
                    source.Value,
                    source.InTangent,
                    source.OutTangent,
                    source.InWeight,
                    source.OutWeight)
                {
                    weightedMode = (WeightedMode)source.WeightedMode
                };
            }
            return new AnimationCurve(keys)
            {
                preWrapMode = (WrapMode)curve.PreWrapMode,
                postWrapMode = (WrapMode)curve.PostWrapMode
            };
        }

        private static bool IsCachedClipCurrent(
            VmdPreparedClipDto clip,
            VmdActionInfo info,
            string actionId)
        {
            return clip != null && info != null &&
                string.Equals(clip.ActionId, actionId, StringComparison.Ordinal) &&
                clip.SourceByteLength == info.ByteLength &&
                clip.SourceKeyframeCount == info.KeyframeCount &&
                clip.LastFrame == info.LastFrame &&
                FloatBits(clip.DurationSeconds) == FloatBits(info.DurationSeconds) &&
                clip.HasFacialTrack == info.HasFacialTrack;
        }

        private static string BuildAnimationBindingFingerprint(Transform root)
        {
            if (root == null)
            {
                return string.Empty;
            }
            var entries = new List<string>();
            CollectBindingFingerprintEntries(root, string.Empty, entries);
            entries.Sort(StringComparer.Ordinal);
            return ComputeSha256Hex(Encoding.UTF8.GetBytes(string.Join("\n", entries)));
        }

        private static void CollectBindingFingerprintEntries(
            Transform current,
            string path,
            ICollection<string> entries)
        {
            entries.Add("T:" + path.Length + ":" + path);
            var renderer = current.GetComponent<SkinnedMeshRenderer>();
            if (renderer != null && renderer.sharedMesh != null)
            {
                var mesh = renderer.sharedMesh;
                entries.Add("R:" + path.Length + ":" + path + ":" + mesh.blendShapeCount);
                for (var index = 0; index < mesh.blendShapeCount; index++)
                {
                    var name = mesh.GetBlendShapeName(index) ?? string.Empty;
                    entries.Add("B:" + path.Length + ":" + path + ":" + name.Length + ":" + name);
                }
            }
            for (var index = 0; index < current.childCount; index++)
            {
                var child = current.GetChild(index);
                var childPath = string.IsNullOrEmpty(path)
                    ? child.name
                    : path + "/" + child.name;
                CollectBindingFingerprintEntries(child, childPath, entries);
            }
        }

        private static string NormalizeSha256(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64 ||
                value.Any(character => !Uri.IsHexDigit(character)))
            {
                return string.Empty;
            }
            return value.ToLowerInvariant();
        }

        private static string ComputeFileSha256(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            return ToLowerHex(sha256.ComputeHash(stream));
        }

        private static string ComputeSha256Hex(byte[] value)
        {
            using var sha256 = SHA256.Create();
            return ToLowerHex(sha256.ComputeHash(value));
        }

        private static string ToLowerHex(byte[] value)
        {
            var builder = new StringBuilder(value.Length * 2);
            foreach (var item in value)
            {
                builder.Append(item.ToString("x2"));
            }
            return builder.ToString();
        }

        private static void AppendCacheKeyPart(StringBuilder builder, string name, string value)
        {
            value ??= string.Empty;
            builder.Append(name).Append('=').Append(value.Length).Append(':').Append(value).Append('|');
        }

        private static int FloatBits(float value)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(value), 0);
        }

        private static string BuildMorphTrackKey(string path, string name)
        {
            return (path ?? string.Empty).Length + ":" + (path ?? string.Empty) +
                (name ?? string.Empty).Length + ":" + (name ?? string.Empty);
        }

        private static string BuildSourceCacheKey(string motionPath, string facialPath)
        {
            var motion = new FileInfo(motionPath ?? string.Empty);
            var facial = string.IsNullOrEmpty(facialPath) ? null : new FileInfo(facialPath);
            return motion.FullName + "|" + motion.Length + "|" + motion.LastWriteTimeUtc.Ticks + "|" +
                (facial == null ? string.Empty : facial.FullName + "|" + facial.Length + "|" + facial.LastWriteTimeUtc.Ticks);
        }

        private void BeginEndPoseHold()
        {
            if (endPoseHoldActive)
            {
                return;
            }
            if (!IsPlaying || (boneBindings.Length == 0 && morphBindings.Length == 0))
            {
                CompleteReturnToIdle();
                return;
            }

            endPoseHoldActive = true;
            endPoseHoldClock = 0f;
            PlaybackPhase = VmdPlaybackPhase.HoldingEndPose;
            diagnostics?.RecordStage("avatar_action", "processing", "vmd_end_pose_hold");
        }

        private void ApplyPlaybackPose(float time)
        {
            foreach (var binding in boneBindings)
            {
                var curves = binding.curves;
                var position = new Vector3(
                    Evaluate(curves[0], time, binding.baselinePosition.x),
                    Evaluate(curves[1], time, binding.baselinePosition.y),
                    Evaluate(curves[2], time, binding.baselinePosition.z));
                var rotation = new Quaternion(
                    Evaluate(curves[3], time, binding.baselineRotation.x),
                    Evaluate(curves[4], time, binding.baselineRotation.y),
                    Evaluate(curves[5], time, binding.baselineRotation.z),
                    Evaluate(curves[6], time, binding.baselineRotation.w));
                binding.target.localPosition = position;
                binding.target.localRotation = Normalize(rotation, binding.baselineRotation);
            }

            foreach (var binding in morphBindings)
            {
                binding.renderer.SetBlendShapeWeight(
                    binding.blendShapeIndex,
                    Mathf.Clamp(Evaluate(binding.curve, time, binding.baselineWeight), 0f, 100f));
            }
        }

        private void BeginBlendOut()
        {
            if (blendOutActive)
            {
                return;
            }
            endPoseHoldActive = false;
            endPoseHoldClock = 0f;
            if (!IsPlaying || (boneBindings.Length == 0 && morphBindings.Length == 0))
            {
                CompleteReturnToIdle();
                return;
            }

            foreach (var binding in boneBindings)
            {
                if (binding.target == null) continue;
                binding.exitPosition = binding.target.localPosition;
                binding.exitRotation = binding.target.localRotation;
            }
            foreach (var binding in morphBindings)
            {
                if (binding.renderer == null) continue;
                binding.exitWeight = binding.renderer.GetBlendShapeWeight(binding.blendShapeIndex);
            }

            IsPlaying = false;
            blendOutActive = true;
            blendOutClock = 0f;
            PlaybackPhase = VmdPlaybackPhase.BlendingOut;
            diagnostics?.RecordStage("avatar_action", "processing", "vmd_blend_out");
            PlaybackChanged?.Invoke();
        }

        private void UpdateBlendOut()
        {
            blendOutClock += Time.deltaTime;
            var progress = SmoothReturnProgress(blendOutClock / Mathf.Max(.01f, exitBlendSeconds));
            foreach (var binding in boneBindings)
            {
                if (binding.target == null) continue;
                binding.target.localPosition = Vector3.Lerp(binding.exitPosition, binding.baselinePosition, progress);
                binding.target.localRotation = Quaternion.Slerp(binding.exitRotation, binding.baselineRotation, progress);
            }
            foreach (var binding in morphBindings)
            {
                if (binding.renderer == null) continue;
                binding.renderer.SetBlendShapeWeight(
                    binding.blendShapeIndex,
                    Mathf.Lerp(binding.exitWeight, binding.baselineWeight, progress));
            }
            if (blendOutClock >= exitBlendSeconds)
            {
                CompleteReturnToIdle();
            }
        }

        public static float SmoothReturnProgress(float normalizedTime)
        {
            var value = Mathf.Clamp01(normalizedTime);
            return value * value * (3f - 2f * value);
        }

        public static bool IsEndPoseHoldComplete(float elapsedSeconds, float holdSeconds)
        {
            return elapsedSeconds >= Mathf.Max(0f, holdSeconds);
        }
        private void CompleteReturnToIdle()
        {
            var hadPlayback = IsPlaying || blendOutActive || boneBindings.Length > 0 || morphBindings.Length > 0;
            RestoreBindings();
            boneBindings = Array.Empty<BoneBinding>();
            morphBindings = Array.Empty<MorphBinding>();
            playbackClock = 0f;
            playbackDuration = 0f;
            endPoseHoldClock = 0f;
            endPoseHoldActive = false;
            blendOutClock = 0f;
            blendOutActive = false;
            CurrentActionId = string.Empty;
            IsPlaying = false;
            PlaybackPhase = VmdPlaybackPhase.Idle;
            EndPhysicsArbitration();
            if (hadPlayback && !isDestroying && !isQuitting &&
                boundAvatar != null && boundAvatar.isActiveAndEnabled)
            {
                boundAvatar.PlayActionFromSource("idle", AvatarActionSource.System);
            }
            if (hadPlayback)
            {
                diagnostics?.RecordStage("avatar_action", "completed", "vmd_idle_restored");
                PlaybackChanged?.Invoke();
            }
        }
        private void BeginPhysicsArbitration()
        {
            if (physicsArbitrationActive || transformManager == null)
            {
                return;
            }

            previousTransformEnabled = transformManager.transformEnabled;
            previousLivePhysics = transformManager.livePhysics;
            physicsArbitrationActive = true;
            physicsArbitrationStartedAt = Time.realtimeSinceStartup;
            transformManager.transformEnabled = false;
            transformManager.livePhysics = false;
            diagnostics?.RecordStage(
                "mmd_physics",
                "processing",
                "vmd_physics_arbitration_started",
                queueDepth: transformManager.physicsManager == null
                    ? -1
                    : transformManager.physicsManager.rigidBodies == null
                        ? 0
                        : transformManager.physicsManager.rigidBodies.Length,
                eventCount: transformManager.physicsManager == null || transformManager.physicsManager.joints == null
                    ? 0
                    : transformManager.physicsManager.joints.Length);
        }

        private void EndPhysicsArbitration()
        {
            if (!physicsArbitrationActive || transformManager == null)
            {
                return;
            }

            transformManager.transformEnabled = previousTransformEnabled;
            transformManager.livePhysics = previousLivePhysics;
            physicsArbitrationActive = false;
            var elapsedMs = physicsArbitrationStartedAt < 0f
                ? -1
                : Mathf.Max(0, Mathf.RoundToInt((Time.realtimeSinceStartup - physicsArbitrationStartedAt) * 1000f));
            physicsArbitrationStartedAt = -1f;
            physicsResumePending = true;
            physicsResumedAt = Time.realtimeSinceStartup;
            diagnostics?.RecordStage(
                "mmd_physics",
                "completed",
                "vmd_physics_arbitration_restored",
                elapsedMs: elapsedMs);
        }

        private bool IsRequestCurrent(int requestGeneration, PMXModel model, Transform root)
        {
            return requestGeneration == generation && model != null && root != null &&
                model == boundModel && root == boundRoot;
        }

        private bool IsSourcePathCurrent(string actionId, ActionSource source)
        {
            if (source == null || string.IsNullOrEmpty(source.motionPath))
            {
                return false;
            }
            if (VmdActionFilePolicy.TryResolveActionPath(MotionsDirectory, actionId, out var directPath) &&
                string.Equals(source.motionPath, directPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return VmdActionFilePolicy.TryResolvePackagePaths(MotionsDirectory, actionId, out var packageMotion, out var packageFacial) &&
                string.Equals(source.motionPath, packageMotion, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(source.facialPath ?? string.Empty, packageFacial, StringComparison.OrdinalIgnoreCase);
        }

        private static BoneBinding[] BuildBoneBindings(Transform root, VMDModelClipData clipData)
        {
            if (clipData == null || !clipData.baked || clipData.bones == null)
            {
                throw new InvalidDataException("VMD converter did not return baked bone curves.");
            }

            const int channelCount = 7;
            var result = new List<BoneBinding>();
            for (var index = 0; index < clipData.bones.paths.Length; index++)
            {
                var offset = index * channelCount;
                if (offset + channelCount > clipData.bones.curves.Length)
                {
                    throw new InvalidDataException("VMD bone curve layout is invalid.");
                }
                var curves = new AnimationCurve[channelCount];
                var hasCurve = false;
                for (var channel = 0; channel < channelCount; channel++)
                {
                    curves[channel] = clipData.bones.curves[offset + channel];
                    hasCurve |= curves[channel] != null;
                }
                if (!hasCurve)
                {
                    continue;
                }

                var target = root.Find(clipData.bones.paths[index]);
                if (target == null)
                {
                    continue;
                }
                var mmdBone = target.GetComponent<MMDBoneTransform>();
                result.Add(new BoneBinding
                {
                    target = target,
                    curves = curves,
                    // Capture the visible pre-motion pose, including the relaxed idle stance.
                    baselinePosition = target.localPosition,
                    baselineRotation = target.localRotation
                });
            }
            return result.ToArray();
        }

        private static MorphBinding[] BuildMorphBindings(Transform root, VMDModelClipData clipData)
        {
            if (clipData?.morphs == null)
            {
                return Array.Empty<MorphBinding>();
            }

            var morphs = clipData.morphs;
            if (morphs.paths.Length != morphs.names.Length || morphs.paths.Length != morphs.curves.Length)
            {
                throw new InvalidDataException("VMD morph curve layout is invalid.");
            }

            var result = new List<MorphBinding>();
            for (var index = 0; index < morphs.paths.Length; index++)
            {
                if (morphs.curves[index] == null)
                {
                    continue;
                }
                var target = root.Find(morphs.paths[index]);
                var renderer = target == null ? null : target.GetComponent<SkinnedMeshRenderer>();
                var blendShapeIndex = renderer == null || renderer.sharedMesh == null
                    ? -1
                    : renderer.sharedMesh.GetBlendShapeIndex(morphs.names[index]);
                if (blendShapeIndex < 0)
                {
                    continue;
                }
                result.Add(new MorphBinding
                {
                    renderer = renderer,
                    blendShapeIndex = blendShapeIndex,
                    curve = morphs.curves[index],
                    baselineWeight = renderer.GetBlendShapeWeight(blendShapeIndex)
                });
            }
            return result.ToArray();
        }

        private static MorphBinding[] MergeMorphBindings(MorphBinding[] primary, MorphBinding[] supplemental)
        {
            if (primary == null || primary.Length == 0) return supplemental ?? Array.Empty<MorphBinding>();
            if (supplemental == null || supplemental.Length == 0) return primary;

            var merged = new Dictionary<string, MorphBinding>(StringComparer.Ordinal);
            foreach (var binding in primary)
            {
                merged[GetMorphKey(binding)] = binding;
            }
            foreach (var binding in supplemental)
            {
                merged[GetMorphKey(binding)] = binding;
            }
            return merged.Values.ToArray();
        }

        private static string GetMorphKey(MorphBinding binding)
        {
            return (binding.renderer == null ? string.Empty : binding.renderer.GetInstanceID().ToString()) + ":" + binding.blendShapeIndex;
        }

        private void RestoreBindings()
        {
            foreach (var binding in boneBindings)
            {
                if (binding.target == null)
                {
                    continue;
                }
                binding.target.localPosition = binding.baselinePosition;
                binding.target.localRotation = binding.baselineRotation;
            }
            foreach (var binding in morphBindings)
            {
                if (binding.renderer != null)
                {
                    binding.renderer.SetBlendShapeWeight(binding.blendShapeIndex, binding.baselineWeight);
                }
            }
        }

        private static float Evaluate(AnimationCurve curve, float time, float fallback)
        {
            return curve == null || curve.length == 0 ? fallback : curve.Evaluate(time);
        }

        private static Quaternion Normalize(Quaternion value, Quaternion fallback)
        {
            var magnitude = Mathf.Sqrt(
                value.x * value.x + value.y * value.y + value.z * value.z + value.w * value.w);
            if (magnitude < .00001f || float.IsNaN(magnitude) || float.IsInfinity(magnitude))
            {
                return fallback;
            }
            var inverse = 1f / magnitude;
            return new Quaternion(value.x * inverse, value.y * inverse, value.z * inverse, value.w * inverse);
        }

        private void ReportFailure(string message)
        {
            Debug.LogWarning("[VmdActionLibrary] " + message, this);
            OperationFailed?.Invoke(message);
        }

        private void OnDisable()
        {
            CompleteReturnToIdle();
        }

        private void OnApplicationQuit()
        {
            // Unity can disable this component before OnDestroy while the
            // Avatar hierarchy is already in teardown. Keep cleanup local and
            // suppress cross-component idle notifications during that phase.
            isQuitting = true;
        }

        private void OnDestroy()
        {
            isDestroying = true;
            generation++;
            CompleteReturnToIdle();
        }
    }
}

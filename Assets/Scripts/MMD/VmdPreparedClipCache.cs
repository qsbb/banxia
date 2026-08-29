using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Pure data representation of one prepared VMD clip. It deliberately has
    /// no Transform, renderer, AnimationCurve or other Unity object reference,
    /// so cache serialization can run on a worker thread.
    /// </summary>
    [Serializable]
    public sealed class VmdPreparedClipDto
    {
        public string CacheKey { get; set; } = string.Empty;
        public string ActionId { get; set; } = string.Empty;
        public long SourceByteLength { get; set; }
        public int SourceKeyframeCount { get; set; }
        public uint LastFrame { get; set; }
        public float DurationSeconds { get; set; }
        public bool HasFacialTrack { get; set; }
        public VmdPreparedBoneTrackDto[] Bones { get; set; } = Array.Empty<VmdPreparedBoneTrackDto>();
        public VmdPreparedMorphTrackDto[] Morphs { get; set; } = Array.Empty<VmdPreparedMorphTrackDto>();
    }

    [Serializable]
    public sealed class VmdPreparedBoneTrackDto
    {
        public string Path { get; set; } = string.Empty;
        public VmdPreparedCurveDto[] Curves { get; set; } = Array.Empty<VmdPreparedCurveDto>();
    }

    [Serializable]
    public sealed class VmdPreparedMorphTrackDto
    {
        public string Path { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public VmdPreparedCurveDto Curve { get; set; }
    }

    [Serializable]
    public sealed class VmdPreparedCurveDto
    {
        // Stored as integers to keep this DTO independent from UnityEngine enums.
        public int PreWrapMode { get; set; }
        public int PostWrapMode { get; set; }
        public VmdPreparedKeyframeDto[] Keys { get; set; } = Array.Empty<VmdPreparedKeyframeDto>();
    }

    [Serializable]
    public struct VmdPreparedKeyframeDto
    {
        public float Time { get; set; }
        public float Value { get; set; }
        public float InTangent { get; set; }
        public float OutTangent { get; set; }
        public float InWeight { get; set; }
        public float OutWeight { get; set; }
        public int WeightedMode { get; set; }
    }

    [Serializable]
    public sealed class VmdPreparedClipCachePolicy
    {
        public long maxEntryBytes = 96L * 1024L * 1024L;
        public long maxTotalBytes = 256L * 1024L * 1024L;
        public long minFreeSpaceBytes = 1024L * 1024L * 1024L;
        public int maxEntryCount = 8;
        public int maxBoneTrackCount = 8192;
        public int maxMorphTrackCount = 8192;
        public int maxCurveCount = 100000;
        public int maxCurveKeyframeCount = 3500000;
        public int maxStringBytes = 4096;
        public int maxCacheKeyBytes = 512;
        public bool deleteCorruptEntries = true;
    }

    public enum VmdPreparedClipCacheReadStatus
    {
        Miss,
        Hit
    }

    public sealed class VmdPreparedClipCacheReadResult
    {
        private VmdPreparedClipCacheReadResult(
            VmdPreparedClipCacheReadStatus status,
            VmdPreparedClipDto clip,
            string reason,
            bool corruptEntryDeleted)
        {
            Status = status;
            Clip = clip;
            Reason = reason;
            CorruptEntryDeleted = corruptEntryDeleted;
        }

        public VmdPreparedClipCacheReadStatus Status { get; }
        public VmdPreparedClipDto Clip { get; }
        public string Reason { get; }
        public bool CorruptEntryDeleted { get; }
        public bool IsHit => Status == VmdPreparedClipCacheReadStatus.Hit && Clip != null;

        internal static VmdPreparedClipCacheReadResult Hit(VmdPreparedClipDto clip)
        {
            return new VmdPreparedClipCacheReadResult(
                VmdPreparedClipCacheReadStatus.Hit,
                clip,
                "hit",
                false);
        }

        internal static VmdPreparedClipCacheReadResult Miss(
            string reason,
            bool corruptEntryDeleted = false)
        {
            return new VmdPreparedClipCacheReadResult(
                VmdPreparedClipCacheReadStatus.Miss,
                null,
                reason,
                corruptEntryDeleted);
        }
    }

    public sealed class VmdPreparedClipCacheWriteResult
    {
        private VmdPreparedClipCacheWriteResult(bool written, string reason, long entryBytes)
        {
            Written = written;
            Reason = reason ?? string.Empty;
            EntryBytes = Math.Max(0, entryBytes);
        }

        public bool Written { get; }
        public string Reason { get; }
        public long EntryBytes { get; }

        internal static VmdPreparedClipCacheWriteResult Success(long entryBytes)
        {
            return new VmdPreparedClipCacheWriteResult(true, "written", entryBytes);
        }

        internal static VmdPreparedClipCacheWriteResult Skipped(string reason, long entryBytes = 0)
        {
            return new VmdPreparedClipCacheWriteResult(false, reason, entryBytes);
        }
    }

    /// <summary>
    /// Versioned, integrity-checked disk cache for prepared VMD curve DTOs.
    /// Public read/write methods only touch CLR data and the filesystem; they
    /// are safe to call from a worker thread after construction.
    /// </summary>
    public sealed class VmdPreparedClipCache
    {
        private const int FormatVersion = 1;
        private const int BoneChannelCount = 7;
        private const int Sha256Length = 32;
        private const int HeaderLength = 8 + sizeof(int) + sizeof(long) + Sha256Length;
        private const string EntryExtension = ".vmdclip";
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("BXVMDPC1");
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<int> AllowedWrapModes = new HashSet<int>
        {
            0, // Default
            1, // Once / Clamp
            2, // Loop
            4, // PingPong
            8  // ClampForever
        };

        private readonly object gate = new object();
        private readonly VmdPreparedClipCachePolicy policy;
        private readonly Func<DateTime> utcNow;

        public VmdPreparedClipCache()
            : this(BuildDefaultDirectory(Application.persistentDataPath))
        {
        }

        public VmdPreparedClipCache(
            string directoryPath,
            VmdPreparedClipCachePolicy policy = null,
            Func<DateTime> utcNow = null)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("Cache directory is required.", nameof(directoryPath));
            }

            DirectoryPath = Path.GetFullPath(directoryPath);
            this.policy = CopyPolicy(policy ?? new VmdPreparedClipCachePolicy());
            ValidatePolicy(this.policy);
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public string DirectoryPath { get; }
        public VmdPreparedClipCachePolicy Policy => CopyPolicy(policy);
        public static int CurrentFormatVersion => FormatVersion;

        public static string BuildDefaultDirectory(string persistentDataPath)
        {
            if (string.IsNullOrWhiteSpace(persistentDataPath))
            {
                throw new ArgumentException("Persistent data path is required.", nameof(persistentDataPath));
            }
            return Path.Combine(persistentDataPath, "VmdActionCache", "v1");
        }

        public VmdPreparedClipCacheReadResult TryRead(string cacheKey)
        {
            if (!TryValidateCacheKey(cacheKey))
            {
                return VmdPreparedClipCacheReadResult.Miss("invalid_key");
            }

            lock (gate)
            {
                var path = GetEntryPath(cacheKey);
                if (!File.Exists(path))
                {
                    return VmdPreparedClipCacheReadResult.Miss("not_found");
                }

                try
                {
                    var clip = ReadEntry(path, cacheKey);
                    Touch(path);
                    return VmdPreparedClipCacheReadResult.Hit(clip);
                }
                catch (Exception exception) when (
                    exception is InvalidDataException ||
                    exception is EndOfStreamException ||
                    exception is CryptographicException ||
                    exception is DecoderFallbackException ||
                    exception is OverflowException)
                {
                    var deleted = policy.deleteCorruptEntries && TryDelete(path);
                    return VmdPreparedClipCacheReadResult.Miss("corrupt", deleted);
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException)
                {
                    return VmdPreparedClipCacheReadResult.Miss("io_error");
                }
            }
        }

        public Task<VmdPreparedClipCacheReadResult> TryReadAsync(
            string cacheKey,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => TryRead(cacheKey), cancellationToken);
        }

        public bool TryWrite(VmdPreparedClipDto clip)
        {
            return TryWriteDetailed(clip).Written;
        }

        public VmdPreparedClipCacheWriteResult TryWriteDetailed(VmdPreparedClipDto clip)
        {
            if (clip == null || !TryValidateCacheKey(clip.CacheKey))
            {
                return VmdPreparedClipCacheWriteResult.Skipped("invalid_key");
            }

            byte[] payload;
            try
            {
                payload = SerializePayload(clip);
            }
            catch (Exception exception) when (
                exception is InvalidDataException ||
                exception is ArgumentException ||
                exception is OverflowException)
            {
                return VmdPreparedClipCacheWriteResult.Skipped("invalid_payload");
            }

            long entryLength;
            try
            {
                entryLength = checked((long)HeaderLength + payload.Length);
            }
            catch (OverflowException)
            {
                return VmdPreparedClipCacheWriteResult.Skipped("entry_too_large");
            }
            if (entryLength > policy.maxEntryBytes || entryLength > policy.maxTotalBytes)
            {
                return VmdPreparedClipCacheWriteResult.Skipped("entry_too_large", entryLength);
            }

            lock (gate)
            {
                string temporaryPath = null;
                var writeStage = "directory";
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    writeStage = "space";
                    if (!HasSufficientFreeSpace(entryLength))
                    {
                        PruneNoLock();
                        if (!HasSufficientFreeSpace(entryLength))
                        {
                            return VmdPreparedClipCacheWriteResult.Skipped(
                                "insufficient_space",
                                entryLength);
                        }
                    }
                    var destination = GetEntryPath(clip.CacheKey);
                    temporaryPath = Path.Combine(
                        DirectoryPath,
                        "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
                    writeStage = "temporary";
                    WriteEntry(temporaryPath, payload);
                    writeStage = "commit";
                    ReplaceAtomically(temporaryPath, destination);
                    temporaryPath = null;
                    writeStage = "touch";
                    Touch(destination);
                    writeStage = "prune";
                    PruneNoLock();
                    return File.Exists(destination)
                        ? VmdPreparedClipCacheWriteResult.Success(entryLength)
                        : VmdPreparedClipCacheWriteResult.Skipped("io_error", entryLength);
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is PlatformNotSupportedException ||
                    exception is NotSupportedException)
                {
                    return VmdPreparedClipCacheWriteResult.Skipped(
                        "io_" + writeStage + "_" + exception.GetType().Name.ToLowerInvariant(),
                        entryLength);
                }
                finally
                {
                    if (!string.IsNullOrEmpty(temporaryPath))
                    {
                        TryDelete(temporaryPath);
                    }
                }
            }
        }

        public Task<bool> TryWriteAsync(
            VmdPreparedClipDto clip,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => TryWrite(clip), cancellationToken);
        }

        public Task<VmdPreparedClipCacheWriteResult> TryWriteDetailedAsync(
            VmdPreparedClipDto clip,
            CancellationToken cancellationToken = default)
        {
            return Task.Run(() => TryWriteDetailed(clip), cancellationToken);
        }

        public bool Remove(string cacheKey)
        {
            if (!TryValidateCacheKey(cacheKey))
            {
                return false;
            }
            lock (gate)
            {
                return TryDelete(GetEntryPath(cacheKey));
            }
        }

        public int Prune()
        {
            lock (gate)
            {
                return PruneNoLock();
            }
        }

        public string GetEntryPath(string cacheKey)
        {
            if (!TryValidateCacheKey(cacheKey))
            {
                throw new ArgumentException("Cache key is invalid.", nameof(cacheKey));
            }
            var digest = ComputeSha256(StrictUtf8.GetBytes(cacheKey));
            var name = ToLowerHex(digest) + EntryExtension;
            return Path.Combine(DirectoryPath, name);
        }

        private VmdPreparedClipDto ReadEntry(string path, string requestedCacheKey)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length < HeaderLength || file.Length > policy.maxEntryBytes)
            {
                throw new InvalidDataException("Prepared VMD cache entry size is invalid.");
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            if (!ReadExact(reader, Magic.Length).SequenceEqual(Magic))
            {
                throw new InvalidDataException("Prepared VMD cache magic is invalid.");
            }
            if (reader.ReadInt32() != FormatVersion)
            {
                throw new InvalidDataException("Prepared VMD cache version is unsupported.");
            }
            var payloadLength = reader.ReadInt64();
            if (payloadLength < 0 || payloadLength > policy.maxEntryBytes - HeaderLength ||
                payloadLength != stream.Length - HeaderLength || payloadLength > int.MaxValue)
            {
                throw new InvalidDataException("Prepared VMD cache payload length is invalid.");
            }
            var expectedHash = ReadExact(reader, Sha256Length);
            var payload = ReadExact(reader, checked((int)payloadLength));
            if (stream.Position != stream.Length ||
                !FixedTimeEquals(expectedHash, ComputeSha256(payload)))
            {
                throw new InvalidDataException("Prepared VMD cache payload hash is invalid.");
            }

            var clip = DeserializePayload(payload);
            if (!string.Equals(clip.CacheKey, requestedCacheKey, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Prepared VMD cache key does not match the request.");
            }
            return clip;
        }

        private void WriteEntry(string path, byte[] payload)
        {
            var hash = ComputeSha256(payload);
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write((long)payload.Length);
            writer.Write(hash);
            writer.Write(payload);
            writer.Flush();
            // IL2CPP on Quest does not implement FileStream.Flush(bool).
            // Closing the same-directory temporary file flushes managed data;
            // checksum validation makes a power-loss partial write harmless.
            stream.Flush();
        }

        private byte[] SerializePayload(VmdPreparedClipDto clip)
        {
            ValidateClip(clip);
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                WriteString(writer, clip.CacheKey, policy.maxCacheKeyBytes);
                WriteString(writer, clip.ActionId, policy.maxStringBytes);
                writer.Write(clip.SourceByteLength);
                writer.Write(clip.SourceKeyframeCount);
                writer.Write(clip.LastFrame);
                writer.Write(clip.DurationSeconds);
                writer.Write(clip.HasFacialTrack);
                writer.Write(clip.Bones.Length);
                foreach (var bone in clip.Bones)
                {
                    WriteString(writer, bone.Path, policy.maxStringBytes);
                    writer.Write(bone.Curves.Length);
                    foreach (var curve in bone.Curves)
                    {
                        writer.Write(curve != null);
                        if (curve != null)
                        {
                            WriteCurve(writer, curve);
                        }
                    }
                }
                writer.Write(clip.Morphs.Length);
                foreach (var morph in clip.Morphs)
                {
                    WriteString(writer, morph.Path, policy.maxStringBytes);
                    WriteString(writer, morph.Name, policy.maxStringBytes);
                    WriteCurve(writer, morph.Curve);
                }
            }
            return stream.ToArray();
        }

        private VmdPreparedClipDto DeserializePayload(byte[] payload)
        {
            using var stream = new MemoryStream(payload, false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            var clip = new VmdPreparedClipDto
            {
                CacheKey = ReadString(reader, policy.maxCacheKeyBytes),
                ActionId = ReadString(reader, policy.maxStringBytes),
                SourceByteLength = reader.ReadInt64(),
                SourceKeyframeCount = reader.ReadInt32(),
                LastFrame = reader.ReadUInt32(),
                DurationSeconds = reader.ReadSingle(),
                HasFacialTrack = reader.ReadBoolean()
            };

            var totals = new ReadTotals();
            var boneCount = ReadBoundedCount(reader, policy.maxBoneTrackCount, "bone tracks");
            clip.Bones = new VmdPreparedBoneTrackDto[boneCount];
            for (var index = 0; index < boneCount; index++)
            {
                var bone = new VmdPreparedBoneTrackDto
                {
                    Path = ReadString(reader, policy.maxStringBytes)
                };
                var curveCount = ReadBoundedCount(reader, BoneChannelCount, "bone curves");
                if (curveCount != BoneChannelCount)
                {
                    throw new InvalidDataException("Prepared VMD bone curve layout is invalid.");
                }
                bone.Curves = new VmdPreparedCurveDto[curveCount];
                for (var channel = 0; channel < curveCount; channel++)
                {
                    if (reader.ReadBoolean())
                    {
                        bone.Curves[channel] = ReadCurve(reader, totals);
                    }
                }
                clip.Bones[index] = bone;
            }

            var morphCount = ReadBoundedCount(reader, policy.maxMorphTrackCount, "morph tracks");
            clip.Morphs = new VmdPreparedMorphTrackDto[morphCount];
            for (var index = 0; index < morphCount; index++)
            {
                clip.Morphs[index] = new VmdPreparedMorphTrackDto
                {
                    Path = ReadString(reader, policy.maxStringBytes),
                    Name = ReadString(reader, policy.maxStringBytes),
                    Curve = ReadCurve(reader, totals)
                };
            }

            if (stream.Position != stream.Length)
            {
                throw new InvalidDataException("Prepared VMD cache payload has trailing data.");
            }
            ValidateClip(clip);
            return clip;
        }

        private void WriteCurve(BinaryWriter writer, VmdPreparedCurveDto curve)
        {
            writer.Write(curve.PreWrapMode);
            writer.Write(curve.PostWrapMode);
            writer.Write(curve.Keys.Length);
            foreach (var key in curve.Keys)
            {
                writer.Write(key.Time);
                writer.Write(key.Value);
                writer.Write(key.InTangent);
                writer.Write(key.OutTangent);
                writer.Write(key.InWeight);
                writer.Write(key.OutWeight);
                writer.Write(key.WeightedMode);
            }
        }

        private VmdPreparedCurveDto ReadCurve(BinaryReader reader, ReadTotals totals)
        {
            totals.CurveCount = CheckedAdd(totals.CurveCount, 1, policy.maxCurveCount, "curve count");
            var curve = new VmdPreparedCurveDto
            {
                PreWrapMode = reader.ReadInt32(),
                PostWrapMode = reader.ReadInt32()
            };
            var keyCount = ReadBoundedCount(reader, policy.maxCurveKeyframeCount, "curve keys");
            totals.KeyCount = CheckedAdd(
                totals.KeyCount,
                keyCount,
                policy.maxCurveKeyframeCount,
                "curve key count");
            curve.Keys = new VmdPreparedKeyframeDto[keyCount];
            for (var index = 0; index < keyCount; index++)
            {
                curve.Keys[index] = new VmdPreparedKeyframeDto
                {
                    Time = reader.ReadSingle(),
                    Value = reader.ReadSingle(),
                    InTangent = reader.ReadSingle(),
                    OutTangent = reader.ReadSingle(),
                    InWeight = reader.ReadSingle(),
                    OutWeight = reader.ReadSingle(),
                    WeightedMode = reader.ReadInt32()
                };
            }
            return curve;
        }

        private void ValidateClip(VmdPreparedClipDto clip)
        {
            if (!TryValidateCacheKey(clip.CacheKey) ||
                !IsValidTrackString(clip.ActionId) ||
                clip.SourceByteLength < 0 || clip.SourceKeyframeCount < 0 ||
                clip.SourceKeyframeCount > policy.maxCurveKeyframeCount ||
                !IsFinite(clip.DurationSeconds) || clip.DurationSeconds < 0f ||
                clip.Bones == null || clip.Morphs == null ||
                clip.Bones.Length > policy.maxBoneTrackCount ||
                clip.Morphs.Length > policy.maxMorphTrackCount)
            {
                throw new InvalidDataException("Prepared VMD clip metadata is invalid.");
            }

            var curveCount = 0;
            var keyCount = 0;
            foreach (var bone in clip.Bones)
            {
                if (bone == null || !IsValidTrackString(bone.Path) ||
                    bone.Curves == null || bone.Curves.Length != BoneChannelCount)
                {
                    throw new InvalidDataException("Prepared VMD bone track is invalid.");
                }
                foreach (var curve in bone.Curves)
                {
                    if (curve != null)
                    {
                        ValidateCurve(curve, ref curveCount, ref keyCount);
                    }
                }
            }
            foreach (var morph in clip.Morphs)
            {
                if (morph == null || !IsValidTrackString(morph.Path) ||
                    !IsValidTrackString(morph.Name) || morph.Curve == null)
                {
                    throw new InvalidDataException("Prepared VMD morph track is invalid.");
                }
                ValidateCurve(morph.Curve, ref curveCount, ref keyCount);
            }
        }

        private void ValidateCurve(VmdPreparedCurveDto curve, ref int curveCount, ref int keyCount)
        {
            if (!AllowedWrapModes.Contains(curve.PreWrapMode) ||
                !AllowedWrapModes.Contains(curve.PostWrapMode) || curve.Keys == null)
            {
                throw new InvalidDataException("Prepared VMD curve metadata is invalid.");
            }
            curveCount = CheckedAdd(curveCount, 1, policy.maxCurveCount, "curve count");
            keyCount = CheckedAdd(
                keyCount,
                curve.Keys.Length,
                policy.maxCurveKeyframeCount,
                "curve key count");
            foreach (var key in curve.Keys)
            {
                if (!IsFinite(key.Time) || !IsFinite(key.Value) ||
                    !IsFinite(key.InWeight) || !IsFinite(key.OutWeight) ||
                    float.IsNaN(key.InTangent) || float.IsNaN(key.OutTangent) ||
                    key.WeightedMode < 0 || key.WeightedMode > 3)
                {
                    throw new InvalidDataException("Prepared VMD keyframe is invalid.");
                }
            }
            for (var index = 1; index < curve.Keys.Length; index++)
            {
                if (curve.Keys[index].Time < curve.Keys[index - 1].Time)
                {
                    throw new InvalidDataException("Prepared VMD keyframe times are not ordered.");
                }
            }
        }

        private bool HasSufficientFreeSpace(long pendingBytes)
        {
            if (policy.minFreeSpaceBytes <= 0)
            {
                return true;
            }
#if UNITY_ANDROID && !UNITY_EDITOR
            AndroidJNI.AttachCurrentThread();
            try
            {
                using (var statFs = new AndroidJavaObject("android.os.StatFs", DirectoryPath))
                {
                    var availableBytes = statFs.Call<long>("getAvailableBytes");
                    return availableBytes - pendingBytes >= policy.minFreeSpaceBytes;
                }
            }
            catch (Exception)
            {
                // Failure to query advisory free space must not break motion;
                // hard entry/total caps still prevent unbounded cache growth.
                return true;
            }
            finally
            {
                AndroidJNI.DetachCurrentThread();
            }
#else
            try
            {
                var root = Path.GetPathRoot(DirectoryPath);
                if (string.IsNullOrEmpty(root))
                {
                    return true;
                }
                var drive = new DriveInfo(root);
                return drive.AvailableFreeSpace - pendingBytes >= policy.minFreeSpaceBytes;
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentException ||
                exception is NotSupportedException ||
                exception is PlatformNotSupportedException)
            {
                // Some Android storage providers do not expose DriveInfo. The
                // hard cache caps still prevent unbounded growth in that case.
                return true;
            }
#endif
        }

        private int PruneNoLock()
        {
            if (!Directory.Exists(DirectoryPath))
            {
                return 0;
            }
            try
            {
                var staleTemporaryCutoff = utcNow().ToUniversalTime().AddHours(-1);
                foreach (var temporaryPath in Directory.EnumerateFiles(
                    DirectoryPath,
                    "*.tmp",
                    SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(temporaryPath) <= staleTemporaryCutoff)
                        {
                            TryDelete(temporaryPath);
                        }
                    }
                    catch (Exception exception) when (
                        exception is IOException ||
                        exception is UnauthorizedAccessException ||
                        exception is ArgumentException)
                    {
                        // Another cache instance may still own this file.
                    }
                }
                var entries = Directory.EnumerateFiles(
                        DirectoryPath,
                        "*" + EntryExtension,
                        SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .Where(file => file.Exists)
                    .OrderBy(file => file.LastWriteTimeUtc)
                    .ThenBy(file => file.Name, StringComparer.Ordinal)
                    .ToList();
                var totalBytes = entries.Sum(file => file.Length);
                var removed = 0;
                while (entries.Count > 0 &&
                    (entries.Count > policy.maxEntryCount || totalBytes > policy.maxTotalBytes))
                {
                    var oldest = entries[0];
                    entries.RemoveAt(0);
                    var length = oldest.Length;
                    if (TryDelete(oldest.FullName))
                    {
                        totalBytes = Math.Max(0, totalBytes - length);
                        removed++;
                    }
                }
                return removed;
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                return 0;
            }
        }

        private void Touch(string path)
        {
            try
            {
                File.SetLastWriteTimeUtc(path, utcNow().ToUniversalTime());
            }
            catch (Exception exception) when (
                exception is IOException ||
                exception is UnauthorizedAccessException ||
                exception is ArgumentOutOfRangeException)
            {
                // A failed touch degrades LRU precision but never invalidates a hit.
            }
        }

        private static void ReplaceAtomically(string temporaryPath, string destination)
        {
            if (File.Exists(destination))
            {
                File.Replace(temporaryPath, destination, null);
            }
            else
            {
                File.Move(temporaryPath, destination);
            }
        }

        private bool TryValidateCacheKey(string cacheKey)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(cacheKey) &&
                    cacheKey == cacheKey.Trim() &&
                    StrictUtf8.GetByteCount(cacheKey) <= policy.maxCacheKeyBytes &&
                    cacheKey.IndexOf('\0') < 0;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        private bool IsValidTrackString(string value)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(value) &&
                    StrictUtf8.GetByteCount(value) <= policy.maxStringBytes &&
                    value.IndexOf('\0') < 0;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        private static void ValidatePolicy(VmdPreparedClipCachePolicy value)
        {
            if (value.maxEntryBytes <= HeaderLength ||
                value.maxTotalBytes <= HeaderLength ||
                value.minFreeSpaceBytes < 0 ||
                value.maxEntryCount <= 0 ||
                value.maxBoneTrackCount <= 0 ||
                value.maxMorphTrackCount <= 0 ||
                value.maxCurveCount <= 0 ||
                value.maxCurveKeyframeCount <= 0 ||
                value.maxStringBytes <= 0 || value.maxStringBytes > 1024 * 1024 ||
                value.maxCacheKeyBytes <= 0 || value.maxCacheKeyBytes > 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Prepared VMD cache limits must be positive and bounded.");
            }
        }

        private static VmdPreparedClipCachePolicy CopyPolicy(VmdPreparedClipCachePolicy value)
        {
            return new VmdPreparedClipCachePolicy
            {
                maxEntryBytes = value.maxEntryBytes,
                maxTotalBytes = value.maxTotalBytes,
                minFreeSpaceBytes = value.minFreeSpaceBytes,
                maxEntryCount = value.maxEntryCount,
                maxBoneTrackCount = value.maxBoneTrackCount,
                maxMorphTrackCount = value.maxMorphTrackCount,
                maxCurveCount = value.maxCurveCount,
                maxCurveKeyframeCount = value.maxCurveKeyframeCount,
                maxStringBytes = value.maxStringBytes,
                maxCacheKeyBytes = value.maxCacheKeyBytes,
                deleteCorruptEntries = value.deleteCorruptEntries
            };
        }

        private static int ReadBoundedCount(BinaryReader reader, int maximum, string section)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > maximum)
            {
                throw new InvalidDataException("Prepared VMD " + section + " exceeds the configured limit.");
            }
            return count;
        }

        private static int CheckedAdd(int current, int addition, int maximum, string section)
        {
            var total = checked(current + addition);
            if (total > maximum)
            {
                throw new InvalidDataException("Prepared VMD " + section + " exceeds the configured limit.");
            }
            return total;
        }

        private static void WriteString(BinaryWriter writer, string value, int maximumBytes)
        {
            var bytes = StrictUtf8.GetBytes(value ?? string.Empty);
            if (bytes.Length > maximumBytes)
            {
                throw new InvalidDataException("Prepared VMD string exceeds the configured limit.");
            }
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader, int maximumBytes)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > maximumBytes)
            {
                throw new InvalidDataException("Prepared VMD string length is invalid.");
            }
            var bytes = ReadExact(reader, length);
            return StrictUtf8.GetString(bytes);
        }

        private static byte[] ReadExact(BinaryReader reader, int count)
        {
            if (count < 0 || reader.BaseStream.Length - reader.BaseStream.Position < count)
            {
                throw new EndOfStreamException("Prepared VMD cache entry is truncated.");
            }
            var bytes = reader.ReadBytes(count);
            if (bytes.Length != count)
            {
                throw new EndOfStreamException("Prepared VMD cache entry is truncated.");
            }
            return bytes;
        }

        private static byte[] ComputeSha256(byte[] value)
        {
            using var sha256 = SHA256.Create();
            return sha256.ComputeHash(value);
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            var difference = 0;
            for (var index = 0; index < left.Length; index++)
            {
                difference |= left[index] ^ right[index];
            }
            return difference == 0;
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

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }
                File.Delete(path);
                return !File.Exists(path);
            }
            catch (Exception exception) when (
                exception is IOException || exception is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private sealed class ReadTotals
        {
            internal int CurveCount;
            internal int KeyCount;
        }
    }
}

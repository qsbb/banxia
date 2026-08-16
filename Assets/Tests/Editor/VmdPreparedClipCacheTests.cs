using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class VmdPreparedClipCacheTests
    {
        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(
                Path.GetTempPath(),
                "banxia-vmd-cache-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void RoundTripPreservesCurveKeysWeightsAndWrapModes()
        {
            var cache = new VmdPreparedClipCache(directory);
            var clip = CreateClip("model:motion:options");

            Assert.That(cache.TryWrite(clip), Is.True);
            var result = cache.TryRead(clip.CacheKey);

            Assert.That(result.IsHit, Is.True);
            Assert.That(result.Reason, Is.EqualTo("hit"));
            Assert.That(result.Clip.ActionId, Is.EqualTo("dance"));
            Assert.That(result.Clip.SourceByteLength, Is.EqualTo(123456));
            Assert.That(result.Clip.SourceKeyframeCount, Is.EqualTo(42));
            Assert.That(result.Clip.LastFrame, Is.EqualTo(90));
            Assert.That(result.Clip.DurationSeconds, Is.EqualTo(3f));
            Assert.That(result.Clip.HasFacialTrack, Is.True);
            Assert.That(result.Clip.Bones, Has.Length.EqualTo(1));
            Assert.That(result.Clip.Bones[0].Curves, Has.Length.EqualTo(7));
            Assert.That(result.Clip.Bones[0].Curves[1], Is.Null);

            var curve = result.Clip.Bones[0].Curves[0];
            Assert.That(curve.PreWrapMode, Is.EqualTo(2));
            Assert.That(curve.PostWrapMode, Is.EqualTo(4));
            Assert.That(curve.Keys, Has.Length.EqualTo(2));
            AssertKey(curve.Keys[0], 0f, 1f, float.PositiveInfinity, -2f, .25f, .75f, 3);
            AssertKey(curve.Keys[1], 1.5f, -4f, 2f, float.NegativeInfinity, .1f, .2f, 1);

            var morph = result.Clip.Morphs.Single();
            Assert.That(morph.Path, Is.EqualTo("Body"));
            Assert.That(morph.Name, Is.EqualTo("smile"));
            Assert.That(morph.Curve.PreWrapMode, Is.EqualTo(8));
            Assert.That(morph.Curve.PostWrapMode, Is.Zero);
        }

        [Test]
        public void PayloadCorruptionReturnsMissAndDeletesEntry()
        {
            var cache = new VmdPreparedClipCache(directory);
            var clip = CreateClip("corrupt-me");
            Assert.That(cache.TryWrite(clip), Is.True);
            var path = cache.GetEntryPath(clip.CacheKey);
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length - 1] ^= 0x5a;
            File.WriteAllBytes(path, bytes);

            var result = cache.TryRead(clip.CacheKey);

            Assert.That(result.Status, Is.EqualTo(VmdPreparedClipCacheReadStatus.Miss));
            Assert.That(result.Reason, Is.EqualTo("corrupt"));
            Assert.That(result.CorruptEntryDeleted, Is.True);
            Assert.That(File.Exists(path), Is.False);
        }

        [Test]
        public void InvalidMagicReturnsMissAndDeletesEntry()
        {
            AssertCorruptEntryFallsBack("bad-magic", bytes =>
            {
                bytes[0] ^= 0x7f;
                return bytes;
            });
        }

        [Test]
        public void TruncatedEntryReturnsMissAndDeletesEntry()
        {
            AssertCorruptEntryFallsBack(
                "truncated",
                bytes => bytes.Take(bytes.Length - 7).ToArray());
        }

        [Test]
        public void UnknownFormatVersionReturnsMissAndDeletesEntry()
        {
            AssertCorruptEntryFallsBack("unknown-version", bytes =>
            {
                BitConverter.GetBytes(VmdPreparedClipCache.CurrentFormatVersion + 1)
                    .CopyTo(bytes, 8);
                return bytes;
            });
        }

        [Test]
        public void EmbeddedCacheKeyPreventsEntrySubstitution()
        {
            var cache = new VmdPreparedClipCache(directory);
            var original = CreateClip("cache-key-a");
            Assert.That(cache.TryWrite(original), Is.True);
            var substitutedPath = cache.GetEntryPath("cache-key-b");
            File.Copy(cache.GetEntryPath(original.CacheKey), substitutedPath);

            var result = cache.TryRead("cache-key-b");

            Assert.That(result.IsHit, Is.False);
            Assert.That(result.Reason, Is.EqualTo("corrupt"));
            Assert.That(File.Exists(substitutedPath), Is.False);
            Assert.That(cache.TryRead(original.CacheKey).IsHit, Is.True);
        }

        [Test]
        public void StrictAggregateKeyLimitRejectsWriteBeforeCreatingEntry()
        {
            var policy = new VmdPreparedClipCachePolicy
            {
                maxCurveKeyframeCount = 1
            };
            var cache = new VmdPreparedClipCache(directory, policy);
            var clip = CreateClip("too-many-keys");

            var result = cache.TryWriteDetailed(clip);
            Assert.That(result.Written, Is.False);
            Assert.That(result.Reason, Is.EqualTo("invalid_payload"));
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }

        [Test]
        public void DetailedWriteReportsEntrySizeLimitWithoutCreatingAFile()
        {
            var policy = new VmdPreparedClipCachePolicy
            {
                maxEntryBytes = 64,
                minFreeSpaceBytes = 0
            };
            var cache = new VmdPreparedClipCache(directory, policy);

            var result = cache.TryWriteDetailed(CreateClip("too-large"));

            Assert.That(result.Written, Is.False);
            Assert.That(result.Reason, Is.EqualTo("entry_too_large"));
            Assert.That(result.EntryBytes, Is.GreaterThan(64));
            Assert.That(Directory.GetFiles(directory), Is.Empty);
        }

        [Test]
        public void LruPruningTouchesHitsAndEvictsOldestEntry()
        {
            var now = new DateTime(2026, 8, 17, 0, 0, 0, DateTimeKind.Utc);
            var policy = new VmdPreparedClipCachePolicy { maxEntryCount = 2 };
            var cache = new VmdPreparedClipCache(directory, policy, () => now);
            var first = CreateClip("first");
            var second = CreateClip("second");
            var third = CreateClip("third");

            Assert.That(cache.TryWrite(first), Is.True);
            now = now.AddMinutes(1);
            Assert.That(cache.TryWrite(second), Is.True);
            now = now.AddMinutes(1);
            Assert.That(cache.TryRead(first.CacheKey).IsHit, Is.True);
            now = now.AddMinutes(1);
            Assert.That(cache.TryWrite(third), Is.True);

            Assert.That(cache.TryRead(first.CacheKey).IsHit, Is.True);
            Assert.That(cache.TryRead(second.CacheKey).IsHit, Is.False);
            Assert.That(cache.TryRead(third.CacheKey).IsHit, Is.True);
            Assert.That(Directory.GetFiles(directory, "*.vmdclip"), Has.Length.EqualTo(2));
        }

        [Test]
        public void AsyncReadWriteUsesTheSamePureDtoProtocol()
        {
            var cache = new VmdPreparedClipCache(directory);
            var clip = CreateClip("async");

            Assert.That(cache.TryWriteAsync(clip).GetAwaiter().GetResult(), Is.True);
            var result = cache.TryReadAsync(clip.CacheKey).GetAwaiter().GetResult();

            Assert.That(result.IsHit, Is.True);
            Assert.That(result.Clip.CacheKey, Is.EqualTo(clip.CacheKey));
        }

        [Test]
        public void ReplacingAnEntryIsAtomicAndLeavesNoTemporaryFiles()
        {
            var cache = new VmdPreparedClipCache(directory);
            var clip = CreateClip("replace");
            Assert.That(cache.TryWrite(clip), Is.True);
            clip.DurationSeconds = 9f;

            Assert.That(cache.TryWrite(clip), Is.True);
            Assert.That(cache.TryRead(clip.CacheKey).Clip.DurationSeconds, Is.EqualTo(9f));
            Assert.That(Directory.GetFiles(directory, "*.tmp"), Is.Empty);
        }

        [Test]
        public void DefaultDirectoryIsVersionedBelowPersistentDataRoot()
        {
            var path = VmdPreparedClipCache.BuildDefaultDirectory("persistent-root");

            Assert.That(
                path,
                Is.EqualTo(Path.Combine("persistent-root", "VmdActionCache", "v1")));
            Assert.That(VmdPreparedClipCache.CurrentFormatVersion, Is.EqualTo(1));
        }

        [Test]
        public void DefaultQuestCapacityPolicyIsBounded()
        {
            var policy = new VmdPreparedClipCachePolicy();

            Assert.That(policy.maxEntryBytes, Is.EqualTo(96L * 1024L * 1024L));
            Assert.That(policy.maxTotalBytes, Is.EqualTo(256L * 1024L * 1024L));
            Assert.That(policy.maxEntryCount, Is.EqualTo(8));
            Assert.That(policy.minFreeSpaceBytes, Is.EqualTo(1024L * 1024L * 1024L));
        }

        [Test]
        public void CacheDefensivelyCopiesFreeSpacePolicy()
        {
            var source = new VmdPreparedClipCachePolicy { minFreeSpaceBytes = 0 };
            var cache = new VmdPreparedClipCache(directory, source);
            source.minFreeSpaceBytes = 123;

            var exposed = cache.Policy;
            Assert.That(exposed.minFreeSpaceBytes, Is.Zero);
            exposed.minFreeSpaceBytes = 456;
            Assert.That(cache.Policy.minFreeSpaceBytes, Is.Zero);
        }

        private static VmdPreparedClipDto CreateClip(string cacheKey)
        {
            var boneCurves = new VmdPreparedCurveDto[7];
            boneCurves[0] = new VmdPreparedCurveDto
            {
                PreWrapMode = 2,
                PostWrapMode = 4,
                Keys = new[]
                {
                    new VmdPreparedKeyframeDto
                    {
                        Time = 0f,
                        Value = 1f,
                        InTangent = float.PositiveInfinity,
                        OutTangent = -2f,
                        InWeight = .25f,
                        OutWeight = .75f,
                        WeightedMode = 3
                    },
                    new VmdPreparedKeyframeDto
                    {
                        Time = 1.5f,
                        Value = -4f,
                        InTangent = 2f,
                        OutTangent = float.NegativeInfinity,
                        InWeight = .1f,
                        OutWeight = .2f,
                        WeightedMode = 1
                    }
                }
            };
            return new VmdPreparedClipDto
            {
                CacheKey = cacheKey,
                ActionId = "dance",
                SourceByteLength = 123456,
                SourceKeyframeCount = 42,
                LastFrame = 90,
                DurationSeconds = 3f,
                HasFacialTrack = true,
                Bones = new[]
                {
                    new VmdPreparedBoneTrackDto
                    {
                        Path = "センター/上半身",
                        Curves = boneCurves
                    }
                },
                Morphs = new[]
                {
                    new VmdPreparedMorphTrackDto
                    {
                        Path = "Body",
                        Name = "smile",
                        Curve = new VmdPreparedCurveDto
                        {
                            PreWrapMode = 8,
                            PostWrapMode = 0,
                            Keys = new[]
                            {
                                new VmdPreparedKeyframeDto
                                {
                                    Time = 0f,
                                    Value = 0f,
                                    InTangent = 0f,
                                    OutTangent = 0f,
                                    InWeight = 0f,
                                    OutWeight = 0f,
                                    WeightedMode = 0
                                }
                            }
                        }
                    }
                }
            };
        }

        private void AssertCorruptEntryFallsBack(
            string cacheKey,
            Func<byte[], byte[]> mutate)
        {
            var cache = new VmdPreparedClipCache(directory);
            var clip = CreateClip(cacheKey);
            Assert.That(cache.TryWrite(clip), Is.True);
            var path = cache.GetEntryPath(clip.CacheKey);
            File.WriteAllBytes(path, mutate(File.ReadAllBytes(path)));

            var result = cache.TryRead(clip.CacheKey);

            Assert.That(result.Status, Is.EqualTo(VmdPreparedClipCacheReadStatus.Miss));
            Assert.That(result.Reason, Is.EqualTo("corrupt"));
            Assert.That(result.CorruptEntryDeleted, Is.True);
            Assert.That(File.Exists(path), Is.False);
        }

        private static void AssertKey(
            VmdPreparedKeyframeDto key,
            float time,
            float value,
            float inTangent,
            float outTangent,
            float inWeight,
            float outWeight,
            int weightedMode)
        {
            Assert.That(key.Time, Is.EqualTo(time));
            Assert.That(key.Value, Is.EqualTo(value));
            Assert.That(key.InTangent, Is.EqualTo(inTangent));
            Assert.That(key.OutTangent, Is.EqualTo(outTangent));
            Assert.That(key.InWeight, Is.EqualTo(inWeight));
            Assert.That(key.OutWeight, Is.EqualTo(outWeight));
            Assert.That(key.WeightedMode, Is.EqualTo(weightedMode));
        }
    }
}

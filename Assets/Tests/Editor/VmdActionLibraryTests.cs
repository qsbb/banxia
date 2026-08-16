using System;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class VmdActionLibraryTests
    {
        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "quest-vmd-" + Guid.NewGuid().ToString("N"));
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
        public void ActionPathAllowsOnlyDirectSafeVmdFile()
        {
            Assert.That(VmdActionFilePolicy.TryResolveActionPath(directory, "wave", out var path), Is.True);
            Assert.That(path, Is.EqualTo(Path.Combine(directory, "wave.vmd")));
            Assert.That(VmdActionFilePolicy.TryResolveActionPath(directory, "../escape", out _), Is.False);
            Assert.That(VmdActionFilePolicy.TryResolveActionPath(directory, "nested/action", out _), Is.False);
            Assert.That(VmdActionFilePolicy.TryResolveActionPath(directory, "trailing. ", out _), Is.False);
        }

        [Test]
        public void InspectorCountsFramesAndDurationBeforeUmtParsing()
        {
            var path = Path.Combine(directory, "greeting.vmd");
            WriteVmd(path, new uint[] { 0, 90 }, new uint[] { 30 });

            var info = VmdActionFilePolicy.Inspect(path, "greeting");

            Assert.That(info.Id, Is.EqualTo("greeting"));
            Assert.That(info.KeyframeCount, Is.EqualTo(3));
            Assert.That(info.LastFrame, Is.EqualTo(90));
            Assert.That(info.DurationSeconds, Is.EqualTo(3f).Within(.0001f));
        }

        [Test]
        public void InspectorRejectsMaliciousDeclaredCountBeforeAllocation()
        {
            var path = Path.Combine(directory, "bad.vmd");
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream, Encoding.ASCII, false))
            {
                WriteFixedAscii(writer, "Vocaloid Motion Data 0002", 30);
                WriteFixedAscii(writer, "test", 20);
                writer.Write(uint.MaxValue);
            }

            Assert.Throws<InvalidDataException>(() => VmdActionFilePolicy.Inspect(path, "bad"));
        }

        [Test]
        public void InspectorRejectsDurationAndAggregateKeyframeLimits()
        {
            var durationPath = Path.Combine(directory, "long.vmd");
            WriteVmd(durationPath, new uint[] { 301 }, Array.Empty<uint>());
            Assert.Throws<InvalidDataException>(() => VmdActionFilePolicy.Inspect(
                durationPath,
                "long",
                new VmdActionLimits { maxDurationSeconds = 10f }));

            var countPath = Path.Combine(directory, "busy.vmd");
            WriteVmd(countPath, new uint[] { 0, 1 }, new uint[] { 0, 1 });
            Assert.Throws<InvalidDataException>(() => VmdActionFilePolicy.Inspect(
                countPath,
                "busy",
                new VmdActionLimits { maxKeyframeCount = 3 }));
        }

        [Test]
        public void PackagePathRequiresMotionAndAcceptsOptionalFacialTrack()
        {
            var package = Path.Combine(directory, "cream_soda");
            Directory.CreateDirectory(package);
            var motionPath = Path.Combine(package, "motion.vmd");
            var facialPath = Path.Combine(package, "facial.vmd");

            Assert.That(VmdActionFilePolicy.TryResolvePackagePaths(directory, "cream_soda", out _, out _), Is.False);
            File.WriteAllBytes(motionPath, new byte[] { 1 });
            Assert.That(VmdActionFilePolicy.TryResolvePackagePaths(directory, "cream_soda", out var resolvedMotion, out var resolvedFacial), Is.True);
            Assert.That(resolvedMotion, Is.EqualTo(motionPath));
            Assert.That(resolvedFacial, Is.Empty);

            File.WriteAllBytes(facialPath, new byte[] { 2 });
            Assert.That(VmdActionFilePolicy.TryResolvePackagePaths(directory, "cream_soda", out resolvedMotion, out resolvedFacial), Is.True);
            Assert.That(resolvedFacial, Is.EqualTo(facialPath));
            Assert.That(VmdActionFilePolicy.TryResolvePackagePaths(directory, "../escape", out _, out _), Is.False);
        }

        [Test]
        public void VmdPlaybackRunsAfterUmtAndTemporarilyOwnsTheSkeleton()
        {
            var modelRoot = new GameObject("MMD Model");
            var libraryRoot = new GameObject("VMD Library");
            try
            {
                var manager = modelRoot.AddComponent<MMDTransformManager>();
                manager.transformEnabled = true;
                manager.livePhysics = true;
                var library = libraryRoot.AddComponent<VmdActionLibrary>();
                typeof(VmdActionLibrary).GetField("transformManager", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(library, manager);

                typeof(VmdActionLibrary).GetMethod("BeginPhysicsArbitration", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(library, null);
                Assert.That(manager.transformEnabled, Is.False);
                Assert.That(manager.livePhysics, Is.False);

                typeof(VmdActionLibrary).GetMethod("EndPhysicsArbitration", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(library, null);
                Assert.That(manager.transformEnabled, Is.True);
                Assert.That(manager.livePhysics, Is.True);

                var executionOrder = typeof(VmdActionLibrary).GetCustomAttribute<DefaultExecutionOrder>();
                Assert.That(executionOrder.order, Is.GreaterThan(10000));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(libraryRoot);
                UnityEngine.Object.DestroyImmediate(modelRoot);
            }
        }

        [Test]
        public void ReturnToIdleBlendUsesSmoothBoundedProgress()
        {
            Assert.That(VmdActionLibrary.SmoothReturnProgress(0f), Is.Zero);
            Assert.That(VmdActionLibrary.SmoothReturnProgress(.5f), Is.EqualTo(.5f).Within(.0001f));
            Assert.That(VmdActionLibrary.SmoothReturnProgress(1f), Is.EqualTo(1f).Within(.0001f));
            Assert.That(VmdActionLibrary.SmoothReturnProgress(2f), Is.EqualTo(1f).Within(.0001f));
        }

        [TestCase(0f, 1f, false)]
        [TestCase(.999f, 1f, false)]
        [TestCase(1f, 1f, true)]
        [TestCase(1.5f, 1f, true)]
        public void EndPoseHoldCompletesOnlyAfterConfiguredDelay(
            float elapsedSeconds,
            float holdSeconds,
            bool expected)
        {
            Assert.That(
                VmdActionLibrary.IsEndPoseHoldComplete(elapsedSeconds, holdSeconds),
                Is.EqualTo(expected));
        }

        [Test]
        public void PreparedActionCacheExpiresOnlyAfterRetentionWindow()
        {
            Assert.IsFalse(VmdActionLibrary.IsPreparedActionExpired(10f, 39.9f, 30f));
            Assert.IsTrue(VmdActionLibrary.IsPreparedActionExpired(10f, 40f, 30f));
            Assert.IsFalse(VmdActionLibrary.IsPreparedActionExpired(10f, 9f, 30f));
        }

        [Test]
        public void PlaybackDiagnosticsStartFromSafeDefaults()
        {
            var host = new GameObject("VMD diagnostics");
            try
            {
                var library = host.AddComponent<VmdActionLibrary>();

                Assert.That(library.PlaybackPhase, Is.EqualTo(VmdPlaybackPhase.Idle));
                Assert.That(library.CacheHitCount, Is.Zero);
                Assert.That(library.CacheMissCount, Is.Zero);
                Assert.That(library.CacheEvictionCount, Is.Zero);
                Assert.That(library.LastPrepareMilliseconds, Is.EqualTo(-1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void QuestActionPreparationKeepsAHeadroomSizedFrameSlice()
        {
            var host = new GameObject("VMD frame budget policy");
            try
            {
                var library = host.AddComponent<VmdActionLibrary>();
                var field = typeof(VmdActionLibrary).GetField(
                    "frameBudgetMilliseconds",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.That(field, Is.Not.Null);
                Assert.That((float)field.GetValue(library), Is.InRange(.5f, 3f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void FacialTrackConversionDoesNotRebakeBodyPhysics()
        {
            var motion = InvokeConversionOptions("CreateMotionConversionOptions", 30f, .2f);
            var facial = InvokeConversionOptions("CreateFacialConversionOptions", 30f);

            Assert.That(motion.bakeIKToFK, Is.True);
            Assert.That(motion.bakePhysicsToFK, Is.True);
            Assert.That(facial.bakeIKToFK, Is.False);
            Assert.That(facial.bakePhysicsToFK, Is.False);
        }

        [Test]
        public void PreparationPhysicsLeaseRestoresOnlyStateItChanged()
        {
            var owner = new GameObject("VMD preparation physics policy");
            try
            {
                var manager = owner.AddComponent<MMDTransformManager>();
                var suspend = typeof(VmdActionLibrary).GetMethod(
                    "SuspendLivePhysicsForPreparation",
                    BindingFlags.Static | BindingFlags.NonPublic);
                var restore = typeof(VmdActionLibrary).GetMethod(
                    "RestoreLivePhysicsAfterPreparation",
                    BindingFlags.Static | BindingFlags.NonPublic);

                manager.livePhysics = true;
                var owned = (bool)suspend.Invoke(null, new object[] { manager });
                Assert.That(owned, Is.True);
                Assert.That(manager.livePhysics, Is.False);
                var restoreArguments = new object[] { manager, owned };
                restore.Invoke(null, restoreArguments);
                Assert.That(manager.livePhysics, Is.True);
                Assert.That((bool)restoreArguments[1], Is.False);

                manager.livePhysics = false;
                owned = (bool)suspend.Invoke(null, new object[] { manager });
                Assert.That(owned, Is.False);
                restoreArguments = new object[] { manager, owned };
                restore.Invoke(null, restoreArguments);
                Assert.That(manager.livePhysics, Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        [TestCase(10f, 10.125f, 125)]
        [TestCase(2f, 1f, 0)]
        public void ModelLoadElapsedTimeIsBounded(float startedAt, float now, int expected)
        {
            Assert.That(RuntimeMmdModelLoader.ElapsedMilliseconds(startedAt, now), Is.EqualTo(expected));
        }

        [Test]
        public void RecommendedDanceFallsBackToValidatedCustomActionWhenNameIsGeneric()
        {
            var selected = VmdActionLibrary.SelectRecommendedDance(new[]
            {
                new VmdActionInfo("greeting_motion", 10, 1, 30, 1f),
                new VmdActionInfo("wave_dance", 10, 1, 30, 1f)
            });

            Assert.That(selected, Is.Not.Null);
            Assert.That(selected.Id, Is.EqualTo("wave_dance"));

            selected = VmdActionLibrary.SelectRecommendedDance(new[]
            {
                new VmdActionInfo("greeting_motion", 10, 1, 30, 1f)
            });
            Assert.That(selected.Id, Is.EqualTo("greeting_motion"));
        }

        [Test]
        public void NextDanceCyclesAcrossValidatedImportedActions()
        {
            var actions = new[]
            {
                new VmdActionInfo("greeting_motion", 10, 1, 30, 1f),
                new VmdActionInfo("dance_alpha", 10, 1, 30, 1f),
                new VmdActionInfo("dance_beta", 10, 1, 30, 1f)
            };

            Assert.That(VmdActionLibrary.SelectNextDance(actions, "dance_alpha").Id, Is.EqualTo("dance_beta"));
            Assert.That(VmdActionLibrary.SelectNextDance(actions, "dance_beta").Id, Is.EqualTo("greeting_motion"));
            Assert.That(VmdActionLibrary.SelectNextDance(actions, "missing").Id, Is.EqualTo("dance_alpha"));
        }
        private static void WriteVmd(string path, uint[] boneFrames, uint[] morphFrames)
        {
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.ASCII, false);
            WriteFixedAscii(writer, "Vocaloid Motion Data 0002", 30);
            WriteFixedAscii(writer, "test", 20);
            writer.Write((uint)boneFrames.Length);
            foreach (var frame in boneFrames)
            {
                WriteFixedAscii(writer, "center", 15);
                writer.Write(frame);
                for (var index = 0; index < 7; index++) writer.Write(index == 6 ? 1f : 0f);
                writer.Write(new byte[64]);
            }
            writer.Write((uint)morphFrames.Length);
            foreach (var frame in morphFrames)
            {
                WriteFixedAscii(writer, "smile", 15);
                writer.Write(frame);
                writer.Write(1f);
            }
        }

        private static VMDAnimationClipOptions InvokeConversionOptions(
            string methodName,
            params object[] arguments)
        {
            var method = typeof(VmdActionLibrary).GetMethod(
                methodName,
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (VMDAnimationClipOptions)method.Invoke(null, arguments);
        }

        private static void WriteFixedAscii(BinaryWriter writer, string value, int length)
        {
            var bytes = Encoding.ASCII.GetBytes(value);
            writer.Write(bytes, 0, Math.Min(bytes.Length, length));
            if (bytes.Length < length)
            {
                writer.Write(new byte[length - bytes.Length]);
            }
        }
    }
}

#if UNITY_EDITOR
using System.IO;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using QuestMmdPlayer.Editor;
using UnityEditor.Build;

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

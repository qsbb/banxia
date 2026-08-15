using NUnit.Framework;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestFileImportServiceTests
    {
        private string temporaryDirectory;

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(temporaryDirectory) && Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, true);
            }
        }

        [Test]
        public void SupportedExtensionsAreRestrictedToModelAndMotionFormats()
        {
            Assert.That(QuestFileImportService.IsSupportedImportExtension(".pmx"), Is.True);
            Assert.That(QuestFileImportService.IsSupportedImportExtension(".VMD"), Is.True);
            Assert.That(QuestFileImportService.IsSupportedImportExtension(".zip"), Is.True);
            Assert.That(QuestFileImportService.IsSupportedImportExtension(".exe"), Is.False);
            Assert.That(QuestFileImportService.IsSupportedImportExtension("pmx"), Is.False);
        }

        [Test]
        public void ImportedNamesAreBoundedAndCannotEscapeDirectory()
        {
            var result = QuestFileImportService.SanitizeImportedName("../model\\texture:.pmx");
            Assert.That(result, Is.EqualTo(".._model_texture_.pmx"));
            Assert.That(result.Length, Is.LessThanOrEqualTo(80));
            Assert.That(result, Does.Not.Contain("/"));
            Assert.That(result, Does.Not.Contain("\\"));
        }

        [Test]
        public void EmptyImportedNameUsesStableFallback()
        {
            Assert.That(
                QuestFileImportService.SanitizeImportedName("...", "ImportedModel"),
                Is.EqualTo("ImportedModel"));
            Assert.That(
                QuestFileImportService.SanitizeImportedName(null, "ImportedMotion"),
                Is.EqualTo("ImportedMotion"));
        }

        [Test]
        public void ModelPackagePrefersPmxMatchingArchiveName()
        {
            var selected = SelectPrimaryPmxCandidate(
                new[]
                {
                    Path.Combine("包", "裸足.pmx"),
                    Path.Combine("包", "休日冒险.PMX")
                },
                "休日冒险_by_卡拉彼丘");

            Assert.That(selected, Does.EndWith("休日冒险.PMX"));
        }

        [Test]
        public void ModelPackageFallsBackToStableCandidateOrder()
        {
            var selected = SelectPrimaryPmxCandidate(
                new[] { Path.Combine("包", "z.pmx"), Path.Combine("包", "A.pmx") },
                "不同的包名");

            Assert.That(selected, Does.EndWith("A.pmx"));
        }

        [Test]
        public void EmptyModelPackageIsRejected()
        {
            Assert.Throws<InvalidDataException>(() =>
                SelectPrimaryPmxCandidate(new string[0], "空包"));
        }

        [Test]
        public void ArchiveExtractionKeepsNestedMultiPmxPackageAndUppercaseExtension()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-model-zip-" + System.Guid.NewGuid());
            Directory.CreateDirectory(temporaryDirectory);
            var archivePath = Path.Combine(temporaryDirectory, "模型包.zip");
            var extracted = Path.Combine(temporaryDirectory, "extracted");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "模型包/标准/角色.pmx", new byte[] { 1 });
                WriteEntry(archive, "模型包/服装/角色.PMX", new byte[] { 2 });
                WriteEntry(archive, "模型包/贴图/face.png", new byte[] { 3 });
            }

            ExtractArchiveSafely(archivePath, extracted);

            var models = Directory.GetFiles(extracted, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetExtension(path), ".pmx", System.StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, System.StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.That(models.Length, Is.EqualTo(2));
            Assert.That(models.Any(path => path.EndsWith("角色.PMX", System.StringComparison.Ordinal)), Is.True);
            Assert.That(File.Exists(Path.Combine(extracted, "模型包", "贴图", "face.png")), Is.True);
        }

        [Test]
        public void ArchiveExtractionRejectsPathTraversalWithoutWritingOutsideTarget()
        {
            temporaryDirectory = Path.Combine(Path.GetTempPath(), "banxia-model-zipslip-" + System.Guid.NewGuid());
            Directory.CreateDirectory(temporaryDirectory);
            var archivePath = Path.Combine(temporaryDirectory, "恶意模型包.zip");
            var extracted = Path.Combine(temporaryDirectory, "extracted");
            var escaped = Path.Combine(temporaryDirectory, "escaped.pmx");
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                WriteEntry(archive, "../escaped.pmx", new byte[] { 1 });
            }

            Assert.Throws<InvalidDataException>(() => ExtractArchiveSafely(archivePath, extracted));
            Assert.That(File.Exists(escaped), Is.False);
        }

        [Test]
        public void LegacyGbkZipNamesDecodeWithoutReplacementCharacters()
        {
            var first = new byte[] { 0xD0, 0xDD, 0xC8, 0xD5, 0xC3, 0xB0, 0xCF, 0xD5, 0x2E, 0x70, 0x6D, 0x78 };
            var second = new byte[] { 0xC2, 0xE3, 0xD7, 0xE3, 0x2E, 0x70, 0x6D, 0x78 };

            Assert.That(LegacyZipEntryEncoding.Decode(first, 0, first.Length), Is.EqualTo("休日冒险.pmx"));
            Assert.That(LegacyZipEntryEncoding.Decode(second, 0, second.Length), Is.EqualTo("裸足.pmx"));
        }

        [Test]
        public void LegacyZipDecoderKeepsAsciiNamesStable()
        {
            var name = Encoding.ASCII.GetBytes("textures/face.png");
            Assert.That(LegacyZipEntryEncoding.Decode(name, 0, name.Length), Is.EqualTo("textures/face.png"));
        }

        private static string SelectPrimaryPmxCandidate(string[] candidates, string preferredName)
        {
            var method = typeof(QuestFileImportService).GetMethod(
                "SelectPrimaryPmxCandidate",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            try
            {
                return (string)method.Invoke(null, new object[] { candidates, preferredName });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw exception.InnerException;
            }
        }

        private static void ExtractArchiveSafely(string archivePath, string targetDirectory)
        {
            var method = typeof(QuestFileImportService).GetMethod(
                "ExtractArchiveSafely",
                BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            try
            {
                method.Invoke(null, new object[] { archivePath, targetDirectory });
            }
            catch (TargetInvocationException exception) when (exception.InnerException != null)
            {
                throw exception.InnerException;
            }
        }

        private static void WriteEntry(ZipArchive archive, string name, byte[] contents)
        {
            var entry = archive.CreateEntry(name);
            using (var output = entry.Open())
            {
                output.Write(contents, 0, contents.Length);
            }
        }
    }
}

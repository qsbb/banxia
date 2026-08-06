using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestFileImportServiceTests
    {
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
    }
}
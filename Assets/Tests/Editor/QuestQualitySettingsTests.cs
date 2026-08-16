#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestQualitySettingsTests
    {
        [TestCase(MmdPhysicsPreset.Performance, 60, 2, 1, false)]
        [TestCase(MmdPhysicsPreset.Balanced, 60, 2, 1, true)]
        [TestCase(MmdPhysicsPreset.Fine, 120, 4, 2, true)]
        public void PhysicsPresetsMapToFixedPolicies(
            MmdPhysicsPreset preset,
            int frequency,
            int substeps,
            int reinforcement,
            bool fullHandContact)
        {
            QuestQualitySettings.GetPhysicsPolicy(
                preset,
                out var actualFrequency,
                out var actualSubsteps,
                out var actualReinforcement,
                out var actualFullHandContact);

            Assert.That(actualFrequency, Is.EqualTo(frequency));
            Assert.That(actualSubsteps, Is.EqualTo(substeps));
            Assert.That(actualReinforcement, Is.EqualTo(reinforcement));
            Assert.That(actualFullHandContact, Is.EqualTo(fullHandContact));
        }

        [TestCase(72f, 72)]
        [TestCase(90f, 90)]
        [TestCase(59.6f, 60)]
        [TestCase(240f, 120)]
        [TestCase(0f, 72)]
        public void RefreshRateIsNormalizedToAValidApplicationTarget(float refreshRate, int expected)
        {
            Assert.That(QuestQualitySettings.NormalizeRefreshRate(refreshRate), Is.EqualTo(expected));
        }
    }
}
#endif

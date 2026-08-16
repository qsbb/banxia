#if UNITY_EDITOR
using NUnit.Framework;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestQualitySettingsTests
    {
        [TestCase(MmdPhysicsPreset.Performance, 60, 2, 1, false)]
        [TestCase(MmdPhysicsPreset.Balanced, 72, 2, 1, true)]
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
    }
}
#endif

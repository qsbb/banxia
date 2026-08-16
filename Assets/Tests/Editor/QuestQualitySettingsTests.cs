#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestQualitySettingsTests
    {
        private const string PhysicsPresetPreferenceKey = "quest.physics.preset";

        [TestCase(MmdPhysicsPreset.Performance, 60, 2, 0, false)]
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

        [TestCase(60f, 72f, false)]
        [TestCase(71.6f, 72f, true)]
        [TestCase(72f, 72f, true)]
        [TestCase(72.6f, 72f, false)]
        [TestCase(float.NaN, 72f, false)]
        public void TransientReportedRefreshRateCannotReplaceTheRequestedCadence(
            float reported,
            float requested,
            bool expected)
        {
            Assert.That(
                QuestQualitySettings.IsRequestedRefreshRateActive(reported, requested),
                Is.EqualTo(expected));
        }

        [Test]
        public void PerformanceQaPhysicsOverrideDoesNotPersistOverUserPreference()
        {
            var hadPreference = PlayerPrefs.HasKey(PhysicsPresetPreferenceKey);
            var previousPreference = PlayerPrefs.GetInt(PhysicsPresetPreferenceKey);
            var owner = new GameObject("Performance QA Quality Settings");
            try
            {
                PlayerPrefs.SetInt(
                    PhysicsPresetPreferenceKey,
                    (int)MmdPhysicsPreset.Balanced);
                PlayerPrefs.Save();
                var quality = owner.AddComponent<QuestQualitySettings>();

                var applyQaPreset = typeof(QuestQualitySettings).GetMethod(
                    "ApplyPhysicsPresetForQa",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(applyQaPreset, Is.Not.Null);
                applyQaPreset.Invoke(
                    quality,
                    new object[] { MmdPhysicsPreset.Performance });

                Assert.That(
                    quality.CurrentPhysicsPreset,
                    Is.EqualTo(MmdPhysicsPreset.Performance));
                Assert.That(
                    PlayerPrefs.GetInt(PhysicsPresetPreferenceKey),
                    Is.EqualTo((int)MmdPhysicsPreset.Balanced));
                applyQaPreset.Invoke(
                    quality,
                    new object[] { MmdPhysicsPreset.Balanced });
            }
            finally
            {
                Object.DestroyImmediate(owner);
                if (hadPreference)
                {
                    PlayerPrefs.SetInt(PhysicsPresetPreferenceKey, previousPreference);
                }
                else
                {
                    PlayerPrefs.DeleteKey(PhysicsPresetPreferenceKey);
                }
                PlayerPrefs.Save();
            }
        }
    }
}
#endif

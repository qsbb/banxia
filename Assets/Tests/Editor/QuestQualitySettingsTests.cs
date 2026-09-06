#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestQualitySettingsTests
    {
        private const string PhysicsPresetPreferenceKey = "quest.physics.preset";
        private const string TargetFpsPreferenceKey = QuestQualitySettings.TargetFpsPreferenceKey;
        private const string VolumePreferenceKey = QuestQualitySettings.VolumePreferenceKey;

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
        [TestCase(0f, 120)]
        public void RefreshRateIsNormalizedToAValidDisplayRequest(float refreshRate, int expected)
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
        public void StartupTargetFrameRateDefaultsTo120AndHonorsOnlySupportedPreferences()
        {
            var hadPreference = PlayerPrefs.HasKey(TargetFpsPreferenceKey);
            var previousPreference = PlayerPrefs.GetInt(TargetFpsPreferenceKey);
            try
            {
                PlayerPrefs.DeleteKey(TargetFpsPreferenceKey);
                Assert.That(QuestQualitySettings.ResolveStartupTargetFrameRate(), Is.EqualTo(120));

                foreach (var fps in new[] { 30, 60, 120 })
                {
                    PlayerPrefs.SetInt(TargetFpsPreferenceKey, fps);
                    Assert.That(QuestQualitySettings.ResolveStartupTargetFrameRate(), Is.EqualTo(fps));
                }

                PlayerPrefs.SetInt(TargetFpsPreferenceKey, 72);
                Assert.That(QuestQualitySettings.ResolveStartupTargetFrameRate(), Is.EqualTo(120));
            }
            finally
            {
                if (hadPreference)
                {
                    PlayerPrefs.SetInt(TargetFpsPreferenceKey, previousPreference);
                }
                else
                {
                    PlayerPrefs.DeleteKey(TargetFpsPreferenceKey);
                }
                PlayerPrefs.Save();
            }
        }

        [Test]
        public void StartupVolumeIsClampedFromSharedPreference()
        {
            var hadPreference = PlayerPrefs.HasKey(VolumePreferenceKey);
            var previousPreference = PlayerPrefs.GetFloat(VolumePreferenceKey);
            try
            {
                PlayerPrefs.SetFloat(VolumePreferenceKey, 1.5f);
                Assert.That(QuestQualitySettings.ResolveStartupVolume(), Is.EqualTo(1f));
                PlayerPrefs.SetFloat(VolumePreferenceKey, -.5f);
                Assert.That(QuestQualitySettings.ResolveStartupVolume(), Is.EqualTo(0f));
            }
            finally
            {
                if (hadPreference)
                {
                    PlayerPrefs.SetFloat(VolumePreferenceKey, previousPreference);
                }
                else
                {
                    PlayerPrefs.DeleteKey(VolumePreferenceKey);
                }
                PlayerPrefs.Save();
            }
        }

        [Test]
        public void SetUserTargetFrameRateAppliesAndPersistsTheSharedChoice()
        {
            var hadPreference = PlayerPrefs.HasKey(TargetFpsPreferenceKey);
            var previousPreference = PlayerPrefs.GetInt(TargetFpsPreferenceKey);
            var previousTarget = Application.targetFrameRate;
            var owner = new GameObject("Target FPS Quality Settings");
            try
            {
                var quality = owner.AddComponent<QuestQualitySettings>();
                quality.SetUserTargetFrameRate(30);
                Assert.That(quality.ApplicationTargetFrameRate, Is.EqualTo(30));
                Assert.That(Application.targetFrameRate, Is.EqualTo(30));
                Assert.That(PlayerPrefs.GetInt(TargetFpsPreferenceKey), Is.EqualTo(30));

                quality.SetUserTargetFrameRate(72);
                Assert.That(quality.ApplicationTargetFrameRate, Is.EqualTo(30));
                Assert.That(PlayerPrefs.GetInt(TargetFpsPreferenceKey), Is.EqualTo(30));
            }
            finally
            {
                Object.DestroyImmediate(owner);
                Application.targetFrameRate = previousTarget;
                if (hadPreference)
                {
                    PlayerPrefs.SetInt(TargetFpsPreferenceKey, previousPreference);
                }
                else
                {
                    PlayerPrefs.DeleteKey(TargetFpsPreferenceKey);
                }
                PlayerPrefs.Save();
            }
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

using System;
using System.Collections;
using System.Collections.Generic;
using UMT;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features.Meta;

namespace QuestMmdPlayer
{
    public enum QuestQualityPreset
    {
        Performance = 0,
        Balanced = 1,
        Clear = 2
    }

    public enum MmdPhysicsPreset
    {
        Performance = 0,
        Balanced = 1,
        Fine = 2
    }

    [DisallowMultipleComponent]
    public sealed class QuestQualitySettings : MonoBehaviour
    {
        private const string PresetKey = "quest.quality.preset";
        private const string PhysicsPresetKey = "quest.physics.preset";
        private const float MinimumRenderScale = .7f;
        private const float MaximumRenderScale = 1.2f;
        private const float PreferredRefreshRate = 72f;

        [SerializeField] private QuestQualityPreset defaultPreset = QuestQualityPreset.Balanced;
        [SerializeField] private MmdPhysicsPreset defaultPhysicsPreset = MmdPhysicsPreset.Balanced;

        public event Action<QuestQualityPreset> QualityChanged;
        public QuestQualityPreset CurrentPreset { get; private set; }
        public MmdPhysicsPreset CurrentPhysicsPreset { get; private set; }
        public string Status { get; private set; } = "画质尚未应用";
        public float RenderScale { get; private set; } = 1f;
        public int AntiAliasing { get; private set; } = 4;
        public int PhysicsFrequencyHz { get; private set; } = 60;
        public int PhysicsMaximumSubsteps { get; private set; } = 2;
        public int PhysicsReinforcement { get; private set; } = 1;
        public bool FullHandContact { get; private set; } = true;
        public string RefreshRateStatus { get; private set; } = "等待 XR 显示器";

        private void Awake()
        {
            var saved = PlayerPrefs.GetInt(PresetKey, (int)defaultPreset);
            var savedPhysics = PlayerPrefs.GetInt(PhysicsPresetKey, (int)defaultPhysicsPreset);
            ApplyRenderPreset(ParsePreset(saved), false);
            ApplyPhysicsPreset(ParsePhysicsPreset(savedPhysics), false);
        }

        private void OnEnable()
        {
            StartCoroutine(RequestPreferredRefreshRate());
        }

        public void ApplyPreset(QuestQualityPreset preset)
        {
            ApplyRenderPreset(preset, true);
        }

        public void ApplyPhysicsPreset(MmdPhysicsPreset preset)
        {
            ApplyPhysicsPreset(preset, true);
        }

        public void ResetToDefault()
        {
            ApplyPreset(defaultPreset);
            ApplyPhysicsPreset(defaultPhysicsPreset);
        }

        public static string GetDisplayName(QuestQualityPreset preset)
        {
            switch (preset)
            {
                case QuestQualityPreset.Performance:
                    return "性能";
                case QuestQualityPreset.Clear:
                    return "清晰";
                default:
                    return "平衡";
            }
        }

        public static string GetPhysicsDisplayName(MmdPhysicsPreset preset)
        {
            switch (preset)
            {
                case MmdPhysicsPreset.Performance:
                    return "性能";
                case MmdPhysicsPreset.Fine:
                    return "精细";
                default:
                    return "平衡";
            }
        }

        private void ApplyRenderPreset(QuestQualityPreset preset, bool persist)
        {
            CurrentPreset = preset;
            switch (preset)
            {
                case QuestQualityPreset.Performance:
                    RenderScale = .8f;
                    AntiAliasing = 2;
                    break;
                case QuestQualityPreset.Clear:
                    RenderScale = 1.15f;
                    AntiAliasing = 4;
                    break;
                default:
                    RenderScale = 1f;
                    AntiAliasing = 2;
                    break;
            }

            RenderScale = Mathf.Clamp(RenderScale, MinimumRenderScale, MaximumRenderScale);
            XRSettings.eyeTextureResolutionScale = RenderScale;
            XRSettings.renderViewportScale = 1f;
            QualitySettings.antiAliasing = AntiAliasing;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline)
            {
                pipeline.renderScale = 1f;
                pipeline.msaaSampleCount = AntiAliasing;
                pipeline.shadowDistance = preset == QuestQualityPreset.Performance
                    ? 12f
                    : preset == QuestQualityPreset.Clear ? 40f : 22f;
            }
            if (persist)
            {
                PlayerPrefs.SetInt(PresetKey, (int)preset);
                PlayerPrefs.Save();
            }

            UpdateStatus();
            QualityChanged?.Invoke(preset);
        }

        private void ApplyPhysicsPreset(MmdPhysicsPreset preset, bool persist)
        {
            CurrentPhysicsPreset = preset;
            GetPhysicsPolicy(
                preset,
                out var frequencyHz,
                out var maximumSubsteps,
                out var reinforcement,
                out var fullHandContact);
            PhysicsFrequencyHz = frequencyHz;
            PhysicsMaximumSubsteps = maximumSubsteps;
            PhysicsReinforcement = reinforcement;
            FullHandContact = fullHandContact;

            MMDPhysicsManager.ConfigureRuntimeQuality(
                PhysicsFrequencyHz,
                PhysicsMaximumSubsteps,
                PhysicsReinforcement);
            ApplyPhysicsPolicyToLoadedModels(persist, FullHandContact);
            if (persist)
            {
                PlayerPrefs.SetInt(PhysicsPresetKey, (int)preset);
                PlayerPrefs.Save();
            }
            UpdateStatus();
            QualityChanged?.Invoke(CurrentPreset);
        }

        public static void GetPhysicsPolicy(
            MmdPhysicsPreset preset,
            out int frequencyHz,
            out int maximumSubsteps,
            out int reinforcement,
            out bool fullHandContact)
        {
            switch (preset)
            {
                case MmdPhysicsPreset.Performance:
                    frequencyHz = 60;
                    maximumSubsteps = 2;
                    reinforcement = 1;
                    fullHandContact = false;
                    return;
                case MmdPhysicsPreset.Fine:
                    frequencyHz = 120;
                    maximumSubsteps = 4;
                    reinforcement = 2;
                    fullHandContact = true;
                    return;
                default:
                    frequencyHz = 72;
                    maximumSubsteps = 2;
                    reinforcement = 1;
                    fullHandContact = true;
                    return;
            }
        }

        public void ApplyHandContactPolicy(AvatarMmdPhysicsAdapter adapter)
        {
            adapter?.SetHighFrequencyContact(FullHandContact);
        }

        private static void ApplyPhysicsPolicyToLoadedModels(bool rebuildPhysics, bool fullHandContact)
        {
            if (rebuildPhysics)
            {
                var managers = FindObjectsOfType<MMDPhysicsManager>(true);
                for (var index = 0; index < managers.Length; index++)
                {
                    managers[index]?.ApplyConfiguredRuntimeQuality();
                }
            }

            var adapters = FindObjectsOfType<AvatarMmdPhysicsAdapter>(true);
            for (var index = 0; index < adapters.Length; index++)
            {
                adapters[index]?.SetHighFrequencyContact(fullHandContact);
            }
        }

        private void UpdateStatus()
        {
            Status = GetDisplayName(CurrentPreset) + "画质 · 渲染比例 " + RenderScale.ToString("F2") +
                " · " + GetPhysicsDisplayName(CurrentPhysicsPreset) + "物理 " +
                PhysicsFrequencyHz + "Hz/" + PhysicsMaximumSubsteps + "步" +
                (FullHandContact ? " · 完整手部接触" : " · 低频手部接触");
        }

        private IEnumerator RequestPreferredRefreshRate()
        {
            var displays = new List<XRDisplaySubsystem>();
            for (var attempt = 0; attempt < 120; attempt++)
            {
                displays.Clear();
                SubsystemManager.GetInstances(displays);
                for (var index = 0; index < displays.Count; index++)
                {
                    var display = displays[index];
                    if (display == null || !display.running)
                    {
                        continue;
                    }
                    var requested = display.TryRequestDisplayRefreshRate(PreferredRefreshRate);
                    RefreshRateStatus = requested ? "已请求 72Hz" : "运行时未接受 72Hz 请求";
                    yield break;
                }
                yield return null;
            }
            RefreshRateStatus = "XR 显示器不可用";
        }

        private static QuestQualityPreset ParsePreset(int value)
        {
            return Enum.IsDefined(typeof(QuestQualityPreset), value)
                ? (QuestQualityPreset)value
                : QuestQualityPreset.Balanced;
        }

        private static MmdPhysicsPreset ParsePhysicsPreset(int value)
        {
            return Enum.IsDefined(typeof(MmdPhysicsPreset), value)
                ? (MmdPhysicsPreset)value
                : MmdPhysicsPreset.Balanced;
        }
    }
}

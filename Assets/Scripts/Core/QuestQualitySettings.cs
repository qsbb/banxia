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

    [DisallowMultipleComponent]
    public sealed class QuestQualitySettings : MonoBehaviour
    {
        private const string PresetKey = "quest.quality.preset";
        private const float MinimumRenderScale = .7f;
        private const float MaximumRenderScale = 1.2f;
        private const float PreferredRefreshRate = 72f;

        [SerializeField] private QuestQualityPreset defaultPreset = QuestQualityPreset.Balanced;

        public event Action<QuestQualityPreset> QualityChanged;
        public QuestQualityPreset CurrentPreset { get; private set; }
        public string Status { get; private set; } = "画质尚未应用";
        public float RenderScale { get; private set; } = 1f;
        public int AntiAliasing { get; private set; } = 4;
        public int PhysicsFrequencyHz { get; private set; } = 60;
        public int PhysicsMaximumSubsteps { get; private set; } = 2;
        public int PhysicsReinforcement { get; private set; } = 1;
        public string RefreshRateStatus { get; private set; } = "等待 XR 显示器";

        private void Awake()
        {
            var saved = PlayerPrefs.GetInt(PresetKey, (int)defaultPreset);
            ApplyPreset(ParsePreset(saved), false);
        }

        private void OnEnable()
        {
            StartCoroutine(RequestPreferredRefreshRate());
        }

        public void ApplyPreset(QuestQualityPreset preset)
        {
            ApplyPreset(preset, true);
        }

        public void ResetToDefault()
        {
            ApplyPreset(defaultPreset);
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

        private void ApplyPreset(QuestQualityPreset preset, bool persist)
        {
            CurrentPreset = preset;
            switch (preset)
            {
                case QuestQualityPreset.Performance:
                    RenderScale = .8f;
                    AntiAliasing = 2;
                    PhysicsFrequencyHz = 60;
                    PhysicsMaximumSubsteps = 2;
                    PhysicsReinforcement = 1;
                    break;
                case QuestQualityPreset.Clear:
                    RenderScale = 1.15f;
                    AntiAliasing = 4;
                    PhysicsFrequencyHz = 120;
                    PhysicsMaximumSubsteps = 4;
                    PhysicsReinforcement = 2;
                    break;
                default:
                    RenderScale = 1f;
                    AntiAliasing = 2;
                    PhysicsFrequencyHz = 60;
                    PhysicsMaximumSubsteps = 2;
                    PhysicsReinforcement = 1;
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
            MMDPhysicsManager.ConfigureRuntimeQuality(
                PhysicsFrequencyHz,
                PhysicsMaximumSubsteps,
                PhysicsReinforcement);
            if (persist)
            {
                ApplyPhysicsPolicyToLoadedModels();
            }
            if (persist)
            {
                PlayerPrefs.SetInt(PresetKey, (int)preset);
                PlayerPrefs.Save();
            }

            Status = GetDisplayName(preset) + "画质 · 渲染比例 " + RenderScale.ToString("F2") +
                " · MMD物理 " + PhysicsFrequencyHz + "Hz/" + PhysicsMaximumSubsteps + "步";
            QualityChanged?.Invoke(preset);
        }

        private static void ApplyPhysicsPolicyToLoadedModels()
        {
            var managers = FindObjectsOfType<MMDPhysicsManager>(true);
            for (var index = 0; index < managers.Length; index++)
            {
                managers[index]?.ApplyConfiguredRuntimeQuality();
            }

            var adapters = FindObjectsOfType<AvatarMmdPhysicsAdapter>(true);
            var highFrequencyContacts = MMDPhysicsManager.simulationFrequencyHz > 60;
            for (var index = 0; index < adapters.Length; index++)
            {
                adapters[index]?.SetHighFrequencyContact(highFrequencyContacts);
            }
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
    }
}

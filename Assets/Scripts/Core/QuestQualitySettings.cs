using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

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

        [SerializeField] private QuestQualityPreset defaultPreset = QuestQualityPreset.Balanced;

        public event Action<QuestQualityPreset> QualityChanged;
        public QuestQualityPreset CurrentPreset { get; private set; }
        public string Status { get; private set; } = "画质尚未应用";
        public float RenderScale { get; private set; } = 1f;
        public int AntiAliasing { get; private set; } = 4;

        private void Awake()
        {
            var saved = PlayerPrefs.GetInt(PresetKey, (int)defaultPreset);
            ApplyPreset(ParsePreset(saved), false);
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
                    break;
                case QuestQualityPreset.Clear:
                    RenderScale = 1.15f;
                    AntiAliasing = 4;
                    break;
                default:
                    RenderScale = 1f;
                    AntiAliasing = 4;
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
            }
            if (persist)
            {
                PlayerPrefs.SetInt(PresetKey, (int)preset);
                PlayerPrefs.Save();
            }

            Status = GetDisplayName(preset) + "画质 · 渲染比例 " + RenderScale.ToString("F2");
            QualityChanged?.Invoke(preset);
        }

        private static QuestQualityPreset ParsePreset(int value)
        {
            return Enum.IsDefined(typeof(QuestQualityPreset), value)
                ? (QuestQualityPreset)value
                : QuestQualityPreset.Balanced;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuestMmdPlayer
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10700)]
    public sealed class AvatarOutlineController : MonoBehaviour
    {
        private const string EnabledPreference = "quest_avatar_outline_enabled_v1";
        private const string WidthPreference = "quest_avatar_outline_width_v1";
        private const float MinimumWidth = .00035f;
        private const float MaximumWidth = .003f;
        private const float WidthStep = .00025f;
        private const float ReferenceWidth = .0011f;
        private static readonly int OutlineWidthProperty = Shader.PropertyToID("_OutlineWidth");
        private static readonly int OutlineColorProperty = Shader.PropertyToID("_OutlineColor");
        private static readonly int UseOutlineProperty = Shader.PropertyToID("_UseOutline");

        [SerializeField] private bool enabledByDefault = true;
        [SerializeField, Range(MinimumWidth, MaximumWidth)] private float outlineWidth = ReferenceWidth;
        [SerializeField] private Color outlineColor = new Color(.025f, .03f, .035f, .88f);

        private readonly List<MaterialBinding> materials = new List<MaterialBinding>();
        private AvatarController avatar;

        private sealed class MaterialBinding
        {
            internal Material material;
            internal float originalWidth;
            internal Color originalColor;
            internal float originalUseOutline;
            internal bool supportsUseOutline;
        }

        public event Action SettingsChanged;

        public bool OutlineEnabled { get; private set; }
        public float OutlineWidth => outlineWidth;
        // Kept for compatibility with diagnostics. It now counts native
        // outline materials rather than duplicated shell renderers.
        public int ShellCount => materials.Count;
        public string Status => ShellCount == 0
            ? "当前模型没有可调节的原生描边材质"
            : $"描边 {(OutlineEnabled ? "开启" : "关闭")} | {outlineWidth * 1000f:F2} mm · 单渲染器";

        private void Awake()
        {
            OutlineEnabled = PlayerPrefs.GetInt(EnabledPreference, enabledByDefault ? 1 : 0) != 0;
            outlineWidth = Mathf.Clamp(PlayerPrefs.GetFloat(WidthPreference, outlineWidth), MinimumWidth, MaximumWidth);
        }

        public void Bind(AvatarController target)
        {
            if (avatar == target && (target == null || materials.Count > 0))
            {
                return;
            }
            RestoreAndClearMaterials();
            avatar = target;
            if (avatar == null || avatar.VisualRoot == null)
            {
                SettingsChanged?.Invoke();
                return;
            }

            var seen = new HashSet<Material>();
            var renderers = avatar.VisualRoot.GetComponentsInChildren<Renderer>(true);
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var shared = renderers[rendererIndex].sharedMaterials;
                for (var materialIndex = 0; materialIndex < shared.Length; materialIndex++)
                {
                    var material = shared[materialIndex];
                    if (material == null || !seen.Add(material) ||
                        !material.HasProperty(OutlineWidthProperty) ||
                        !material.HasProperty(OutlineColorProperty))
                    {
                        continue;
                    }
                    var supportsUseOutline = material.HasProperty(UseOutlineProperty);
                    materials.Add(new MaterialBinding
                    {
                        material = material,
                        originalWidth = material.GetFloat(OutlineWidthProperty),
                        originalColor = material.GetColor(OutlineColorProperty),
                        originalUseOutline = supportsUseOutline ? material.GetFloat(UseOutlineProperty) : 1f,
                        supportsUseOutline = supportsUseOutline
                    });
                }
            }
            ApplySettings(false);
            Debug.Log($"[AvatarOutline] Bound {ShellCount} native outline materials without duplicate renderers.", this);
        }

        public void Toggle() => SetEnabled(!OutlineEnabled);

        public void SetEnabled(bool value)
        {
            if (OutlineEnabled == value)
            {
                return;
            }
            OutlineEnabled = value;
            ApplySettings(true);
        }

        public void IncreaseWidth() => SetWidth(outlineWidth + WidthStep);

        public void DecreaseWidth() => SetWidth(outlineWidth - WidthStep);

        public void SetWidth(float value)
        {
            var next = Mathf.Clamp(value, MinimumWidth, MaximumWidth);
            if (Mathf.Abs(next - outlineWidth) < .00001f)
            {
                return;
            }
            outlineWidth = next;
            ApplySettings(true);
        }

        private void ApplySettings(bool save)
        {
            var widthScale = outlineWidth / ReferenceWidth;
            for (var index = 0; index < materials.Count; index++)
            {
                var binding = materials[index];
                if (binding.material == null)
                {
                    continue;
                }
                binding.material.SetFloat(
                    OutlineWidthProperty,
                    OutlineEnabled ? Mathf.Max(0f, binding.originalWidth) * widthScale : 0f);
                binding.material.SetColor(OutlineColorProperty, outlineColor);
                if (binding.supportsUseOutline)
                {
                    binding.material.SetFloat(UseOutlineProperty, OutlineEnabled ? 1f : 0f);
                }
            }
            if (save)
            {
                PlayerPrefs.SetInt(EnabledPreference, OutlineEnabled ? 1 : 0);
                PlayerPrefs.SetFloat(WidthPreference, outlineWidth);
                PlayerPrefs.Save();
            }
            SettingsChanged?.Invoke();
        }

        private void RestoreAndClearMaterials()
        {
            for (var index = 0; index < materials.Count; index++)
            {
                var binding = materials[index];
                if (binding.material == null)
                {
                    continue;
                }
                binding.material.SetFloat(OutlineWidthProperty, binding.originalWidth);
                binding.material.SetColor(OutlineColorProperty, binding.originalColor);
                if (binding.supportsUseOutline)
                {
                    binding.material.SetFloat(UseOutlineProperty, binding.originalUseOutline);
                }
            }
            materials.Clear();
        }

        private void OnDestroy()
        {
            RestoreAndClearMaterials();
        }
    }
}

using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;
using UnityEngine.Rendering;

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
        public const string OutlineTagName = "BanxiaOutline";
        private static readonly int OutlineWidthProperty = Shader.PropertyToID("_OutlineWidth");
        private static readonly int OutlineColorProperty = Shader.PropertyToID("_OutlineColor");
        private static readonly List<AvatarOutlineController> ActiveControllers = new List<AvatarOutlineController>();

        [SerializeField] private bool enabledByDefault = true;
        [SerializeField, Range(MinimumWidth, MaximumWidth)] private float outlineWidth = ReferenceWidth;
        [SerializeField] private Color outlineColor = new Color(.025f, .03f, .035f, .88f);

        private readonly List<RendererBinding> renderers = new List<RendererBinding>();
        private AvatarController avatar;
        private Material outlineMaterial;

        private sealed class RendererBinding
        {
            internal Renderer renderer;
            internal int submeshIndex;
        }

        public event Action SettingsChanged;

        public bool OutlineEnabled { get; private set; }
        public float OutlineWidth => outlineWidth;
        // Kept for compatibility with diagnostics. It counts submeshes drawn
        // by the URP pass rather than duplicated shell renderers.
        public int ShellCount => renderers.Count;
        public string Status => ShellCount == 0
            ? "当前模型没有声明可描边材质"
            : $"描边 {(OutlineEnabled ? "开启" : "关闭")} | {outlineWidth * 1000f:F2} mm · URP额外Pass";
        public static float LastRenderSubmissionMilliseconds { get; private set; }
        public static int LastRenderedSubmeshCount { get; private set; }

        private void Awake()
        {
            OutlineEnabled = PlayerPrefs.GetInt(EnabledPreference, enabledByDefault ? 1 : 0) != 0;
            outlineWidth = Mathf.Clamp(PlayerPrefs.GetFloat(WidthPreference, outlineWidth), MinimumWidth, MaximumWidth);
        }

        private void OnEnable()
        {
            if (!ActiveControllers.Contains(this))
            {
                ActiveControllers.Add(this);
            }
        }

        private void OnDisable()
        {
            ActiveControllers.Remove(this);
        }

        public void Bind(AvatarController target)
        {
            if (avatar == target && (target == null || renderers.Count > 0))
            {
                return;
            }
            ClearBindings();
            avatar = target;
            if (avatar == null || avatar.VisualRoot == null)
            {
                SettingsChanged?.Invoke();
                return;
            }

            EnsureOutlineMaterial();
            var avatarRenderers = avatar.VisualRoot.GetComponentsInChildren<Renderer>(true);
            for (var rendererIndex = 0; rendererIndex < avatarRenderers.Length; rendererIndex++)
            {
                var renderer = avatarRenderers[rendererIndex];
                var shared = renderer.sharedMaterials;
                for (var materialIndex = 0; materialIndex < shared.Length; materialIndex++)
                {
                    var material = shared[materialIndex];
                    if (material == null ||
                        material.GetTag(OutlineTagName, false, "0") != "1")
                    {
                        continue;
                    }
                    renderers.Add(new RendererBinding
                    {
                        renderer = renderer,
                        submeshIndex = materialIndex
                    });
                }
            }
            ApplySettings(false);
            Debug.Log($"[AvatarOutline] Bound {ShellCount} submeshes to URP outline pass without duplicate renderers.", this);
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

        internal void SetEnabledForQa(bool value)
        {
            if (OutlineEnabled == value)
            {
                return;
            }
            OutlineEnabled = value;
            ApplySettings(false);
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
            EnsureOutlineMaterial();
            if (outlineMaterial != null)
            {
                outlineMaterial.SetFloat(OutlineWidthProperty, outlineWidth);
                outlineMaterial.SetColor(OutlineColorProperty, outlineColor);
            }
            if (save)
            {
                PlayerPrefs.SetInt(EnabledPreference, OutlineEnabled ? 1 : 0);
                PlayerPrefs.SetFloat(WidthPreference, outlineWidth);
                PlayerPrefs.Save();
            }
            SettingsChanged?.Invoke();
        }

        private void EnsureOutlineMaterial()
        {
            if (outlineMaterial != null)
            {
                return;
            }
            var shader = Shader.Find("QuestMmdPlayer/Avatar Outline");
            if (shader != null)
            {
                outlineMaterial = new Material(shader) { name = "Banxia Runtime Avatar Outline" };
            }
        }

        private void ClearBindings()
        {
            renderers.Clear();
        }

        internal static void DrawRegistered(CommandBuffer commandBuffer)
        {
            var startedAt = Stopwatch.GetTimestamp();
            var rendered = 0;
            for (var controllerIndex = 0; controllerIndex < ActiveControllers.Count; controllerIndex++)
            {
                var controller = ActiveControllers[controllerIndex];
                if (controller == null || !controller.isActiveAndEnabled ||
                    !controller.OutlineEnabled || controller.outlineMaterial == null)
                {
                    continue;
                }
                for (var bindingIndex = 0; bindingIndex < controller.renderers.Count; bindingIndex++)
                {
                    var binding = controller.renderers[bindingIndex];
                    if (binding.renderer == null || !binding.renderer.enabled ||
                        !binding.renderer.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    commandBuffer.DrawRenderer(
                        binding.renderer,
                        controller.outlineMaterial,
                        binding.submeshIndex,
                        0);
                    rendered++;
                }
            }
            LastRenderedSubmeshCount = rendered;
            LastRenderSubmissionMilliseconds = (float)(
                (Stopwatch.GetTimestamp() - startedAt) * 1000d / Stopwatch.Frequency);
        }

        private void OnDestroy()
        {
            ActiveControllers.Remove(this);
            ClearBindings();
            if (outlineMaterial != null)
            {
                if (Application.isPlaying) Destroy(outlineMaterial);
                else DestroyImmediate(outlineMaterial);
                outlineMaterial = null;
            }
        }
    }
}

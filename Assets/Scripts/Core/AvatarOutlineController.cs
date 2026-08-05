using System;
using System.Collections.Generic;
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
        private const string ShaderName = "QuestMmdPlayer/Avatar Outline";
        private const float MinimumWidth = .00035f;
        private const float MaximumWidth = .003f;
        private const float WidthStep = .00025f;

        [SerializeField] private bool enabledByDefault = true;
        [SerializeField, Range(MinimumWidth, MaximumWidth)] private float outlineWidth = .0011f;
        [SerializeField] private Color outlineColor = new Color(.025f, .03f, .035f, .88f);

        private readonly List<SkinnedShell> skinnedShells = new List<SkinnedShell>();
        private readonly List<GameObject> shellObjects = new List<GameObject>();
        private AvatarController avatar;
        private Material outlineMaterial;

        public event Action SettingsChanged;

        public bool OutlineEnabled { get; private set; }
        public float OutlineWidth => outlineWidth;
        public int ShellCount => shellObjects.Count;
        public string Status => ShellCount == 0
            ? "\u5f53\u524d\u6a21\u578b\u6ca1\u6709\u53ef\u63cf\u8fb9\u7f51\u683c"
            : $"\u63cf\u8fb9 {(OutlineEnabled ? "\u5f00\u542f" : "\u5173\u95ed")} | {outlineWidth * 1000f:F2} mm";

        private sealed class SkinnedShell
        {
            internal SkinnedMeshRenderer source;
            internal SkinnedMeshRenderer shell;
        }

        private void Awake()
        {
            OutlineEnabled = PlayerPrefs.GetInt(EnabledPreference, enabledByDefault ? 1 : 0) != 0;
            outlineWidth = Mathf.Clamp(PlayerPrefs.GetFloat(WidthPreference, outlineWidth), MinimumWidth, MaximumWidth);
        }

        public void Bind(AvatarController target)
        {
            if (avatar == target && (target == null || shellObjects.Count > 0))
            {
                return;
            }
            ClearShells();
            avatar = target;
            if (avatar == null || avatar.VisualRoot == null)
            {
                SettingsChanged?.Invoke();
                return;
            }

            EnsureMaterial();
            BuildSkinnedShells(avatar.VisualRoot);
            BuildStaticShells(avatar.VisualRoot);
            ApplySettings(false);
            Debug.Log($"[AvatarOutline] Bound {ShellCount} outline shells.", this);
        }

        public void Toggle()
        {
            SetEnabled(!OutlineEnabled);
        }

        public void SetEnabled(bool value)
        {
            if (OutlineEnabled == value)
            {
                return;
            }
            OutlineEnabled = value;
            ApplySettings(true);
        }

        public void IncreaseWidth()
        {
            SetWidth(outlineWidth + WidthStep);
        }

        public void DecreaseWidth()
        {
            SetWidth(outlineWidth - WidthStep);
        }

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

        private void EnsureMaterial()
        {
            if (outlineMaterial != null)
            {
                return;
            }
            var shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError("[AvatarOutline] Required outline shader was not found.", this);
                return;
            }
            outlineMaterial = new Material(shader)
            {
                name = "Quest Avatar Outline Runtime",
                hideFlags = HideFlags.DontSave
            };
        }

        private void BuildSkinnedShells(Transform root)
        {
            if (outlineMaterial == null)
            {
                return;
            }
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (var index = 0; index < renderers.Length; index++)
            {
                var source = renderers[index];
                if (source == null || source.sharedMesh == null || source.name.EndsWith(" Outline Shell", StringComparison.Ordinal))
                {
                    continue;
                }

                var shellObject = new GameObject(source.name + " Outline Shell");
                shellObject.transform.SetParent(source.transform, false);
                var shell = shellObject.AddComponent<SkinnedMeshRenderer>();
                shell.sharedMesh = source.sharedMesh;
                shell.bones = source.bones;
                shell.rootBone = source.rootBone;
                shell.localBounds = source.localBounds;
                shell.quality = source.quality;
                shell.updateWhenOffscreen = source.updateWhenOffscreen;
                shell.skinnedMotionVectors = false;
                shell.shadowCastingMode = ShadowCastingMode.Off;
                shell.receiveShadows = false;
                shell.sortingLayerID = source.sortingLayerID;
                shell.sortingOrder = source.sortingOrder;
                shell.sharedMaterials = RepeatMaterial(outlineMaterial, Mathf.Max(1, source.sharedMesh.subMeshCount));
                skinnedShells.Add(new SkinnedShell { source = source, shell = shell });
                shellObjects.Add(shellObject);
            }
        }

        private void BuildStaticShells(Transform root)
        {
            if (outlineMaterial == null)
            {
                return;
            }
            var filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (var index = 0; index < filters.Length; index++)
            {
                var sourceFilter = filters[index];
                var sourceRenderer = sourceFilter == null ? null : sourceFilter.GetComponent<MeshRenderer>();
                if (sourceFilter == null || sourceFilter.sharedMesh == null || sourceRenderer == null)
                {
                    continue;
                }

                var shellObject = new GameObject(sourceRenderer.name + " Outline Shell");
                shellObject.transform.SetParent(sourceFilter.transform, false);
                var filter = shellObject.AddComponent<MeshFilter>();
                filter.sharedMesh = sourceFilter.sharedMesh;
                var shell = shellObject.AddComponent<MeshRenderer>();
                shell.shadowCastingMode = ShadowCastingMode.Off;
                shell.receiveShadows = false;
                shell.sortingLayerID = sourceRenderer.sortingLayerID;
                shell.sortingOrder = sourceRenderer.sortingOrder;
                shell.sharedMaterials = RepeatMaterial(outlineMaterial, Mathf.Max(1, sourceFilter.sharedMesh.subMeshCount));
                shellObjects.Add(shellObject);
            }
        }

        private void LateUpdate()
        {
            for (var rendererIndex = 0; rendererIndex < skinnedShells.Count; rendererIndex++)
            {
                var pair = skinnedShells[rendererIndex];
                if (pair.source == null || pair.shell == null || pair.source.sharedMesh == null)
                {
                    continue;
                }
                pair.shell.enabled = OutlineEnabled && pair.source.enabled && pair.source.gameObject.activeInHierarchy;
                var count = pair.source.sharedMesh.blendShapeCount;
                for (var blendShape = 0; blendShape < count; blendShape++)
                {
                    var weight = pair.source.GetBlendShapeWeight(blendShape);
                    if (!Mathf.Approximately(weight, pair.shell.GetBlendShapeWeight(blendShape)))
                    {
                        pair.shell.SetBlendShapeWeight(blendShape, weight);
                    }
                }
            }
        }

        private void ApplySettings(bool save)
        {
            if (outlineMaterial != null)
            {
                outlineMaterial.SetFloat("_OutlineWidth", outlineWidth);
                outlineMaterial.SetColor("_OutlineColor", outlineColor);
            }
            for (var index = 0; index < shellObjects.Count; index++)
            {
                if (shellObjects[index] != null)
                {
                    shellObjects[index].SetActive(OutlineEnabled);
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

        private static Material[] RepeatMaterial(Material material, int count)
        {
            var result = new Material[Mathf.Max(1, count)];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = material;
            }
            return result;
        }

        private void ClearShells()
        {
            skinnedShells.Clear();
            for (var index = 0; index < shellObjects.Count; index++)
            {
                if (shellObjects[index] != null)
                {
                    Destroy(shellObjects[index]);
                }
            }
            shellObjects.Clear();
        }

        private void OnDestroy()
        {
            ClearShells();
            if (outlineMaterial != null)
            {
                Destroy(outlineMaterial);
                outlineMaterial = null;
            }
        }
    }
}
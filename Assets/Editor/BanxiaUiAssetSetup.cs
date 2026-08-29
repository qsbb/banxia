using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace QuestMmdPlayer.Editor
{
    /// <summary>
    /// 生成伴夏 UI Toolkit 运行时资产：BanxiaRuntimeTheme.tss（默认运行时主题）+
    /// Resources/BanxiaPanelSettings.asset（面板设置，引用主题）。
    /// 构建方法（QuestMmdPlayerBuild）开跑前调用 EnsureUiAssets()，幂等。
    /// </summary>
    public static class BanxiaUiAssetSetup
    {
        private const string ThemePath = "Assets/UI/BanxiaRuntimeTheme.tss";
        private const string PanelPath = "Assets/UI/Resources/BanxiaPanelSettings.asset";
        private const string PanelMaterialPath = "Assets/UI/Resources/BanxiaPanelMaterial.mat";

        // UI Toolkit/TextCore 在 IL2CPP+Stripping 下容易被裁剪，这里用“引用材质”
        // 把这几个隐藏 shader 钉进 build，防止 Label 文本渲染 NRE/空白。
        private static readonly (string shaderName, string assetPath)[] TextCoreShaders =
        {
            ("Hidden/TextCore/Distance Field SSD", "Assets/UI/Resources/BanxiaTextCoreSdfMaterial.mat"),
            ("Hidden/Internal-GUITextureClipText", "Assets/UI/Resources/BanxiaTextCoreBitmapMaterial.mat"),
            ("Hidden/TextCore/Sprite", "Assets/UI/Resources/BanxiaTextCoreSpriteMaterial.mat"),
        };

        public static void EnsureUiAssets()
        {
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            if (theme == null)
            {
                if (!File.Exists(ThemePath))
                {
                    File.WriteAllText(ThemePath, "@import url(\"unity-theme://default\");\n");
                }
                AssetDatabase.ImportAsset(ThemePath);
                theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            }

            var panel = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelPath);
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(panel, PanelPath);
            }

            bool dirty = false;
            if (panel.themeStyleSheet == null && theme != null)
            {
                panel.themeStyleSheet = theme;
                dirty = true;
            }
            if (panel.scaleMode != PanelScaleMode.ScaleWithScreenSize)
            {
                panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                dirty = true;
            }
            if (panel.screenMatchMode != PanelScreenMatchMode.MatchWidthOrHeight)
            {
                panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                dirty = true;
            }
            if (panel.referenceResolution != new Vector2Int(1080, 1920))
            {
                panel.referenceResolution = new Vector2Int(1080, 1920);
                dirty = true;
            }
            if (Mathf.Abs(panel.match) > 0.0001f)
            {
                panel.match = 0f;
                dirty = true;
            }
            if (dirty)
            {
                EditorUtility.SetDirty(panel);
                AssetDatabase.SaveAssets();
            }

            var panelMaterial = AssetDatabase.LoadAssetAtPath<Material>(PanelMaterialPath);
            if (panelMaterial == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit");
                panelMaterial = new Material(shader);
                if (panelMaterial.HasProperty("_Surface"))
                {
                    panelMaterial.SetFloat("_Surface", 0f);
                }
                if (panelMaterial.HasProperty("_Cull"))
                {
                    panelMaterial.SetFloat("_Cull", 0f);
                }
                if (panelMaterial.HasProperty("_BaseColor"))
                {
                    panelMaterial.SetColor("_BaseColor", Color.white);
                }
                AssetDatabase.CreateAsset(panelMaterial, PanelMaterialPath);
            }

            foreach (var (shaderName, assetPath) in TextCoreShaders)
            {
                if (AssetDatabase.LoadAssetAtPath<Material>(assetPath) != null)
                {
                    continue;
                }
                var textShader = Shader.Find(shaderName);
                if (textShader == null)
                {
                    Debug.LogWarning("[BanxiaUi] TextCore shader missing: " + shaderName);
                    continue;
                }
                AssetDatabase.CreateAsset(new Material(textShader), assetPath);
            }

            Debug.Log("[BanxiaUi] UI assets ensured: theme=" + (theme != null) + " panel=" + (panel != null) + " material=" + (panelMaterial != null));
        }
    }
}

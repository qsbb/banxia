#if UNITY_EDITOR
using System;
using System.IO;
using UMT;
using UnityEditor;
using UnityEngine;

namespace QuestMmdPlayer.Editor
{
    public static class QuestMmdPlayerRuntimeSmokeTest
    {
        [MenuItem("伴夏/Run Runtime PMX Smoke Test")]
        public static void Run()
        {
            string pmxPath = EditorUtility.OpenFilePanel("Select a local PMX model", string.Empty, "pmx");
            if (string.IsNullOrWhiteSpace(pmxPath)) return;
            string textureDirectory = Path.GetDirectoryName(pmxPath);
            PMXImportResult result = null;

            try
            {
                if (!File.Exists(pmxPath))
                {
                    throw new FileNotFoundException("Selected PMX model was not found.", pmxPath);
                }

                var options = new PMXImportOptions
                {
                    sourcePath = pmxPath,
                    textureBaseDirectory = textureDirectory,
                    applyRenames = false,
                    createAvatar = false,
                    strictVersion = true
                };

                result = PMXImporter.Import(pmxPath, options);
                if (result.root == null || result.meshes.Count == 0)
                {
                    throw new InvalidOperationException("UMT returned an empty PMX import result.");
                }

                int decodedTextures = 0;
                foreach (var texture in result.textures)
                {
                    if (texture != null)
                    {
                        decodedTextures++;
                    }
                }

                Debug.Log($"[Runtime PMX Smoke Test] PASS: root={result.root.name}, meshes={result.meshes.Count}, materials={result.materials.Count}, textures={decodedTextures}, bones={result.bones.Length}, rigidBodies={result.mmdTransformResult.rigidBodies.Length}");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                throw;
            }
            finally
            {
                if (result != null)
                {
                    if (result.root != null)
                    {
                        UnityEngine.Object.DestroyImmediate(result.root);
                    }

                    foreach (var texture in result.textures)
                    {
                        if (texture != null)
                        {
                            UnityEngine.Object.DestroyImmediate(texture);
                        }
                    }

                    if (result.model != null)
                    {
                        UnityEngine.Object.DestroyImmediate(result.model);
                    }
                }
            }
        }
    }
}
#endif

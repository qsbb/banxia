using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UMT;
using UnityEngine;
using UnityEngine.Networking;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Loads PMX models directly at runtime. The bundled sample is extracted to
    /// persistent storage first so UMT can resolve the PMX texture sidecars on
    /// Android exactly as it does on desktop.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class RuntimeMmdModelLoader : MonoBehaviour
    {
        private static readonly string[] BundledSampleFiles =
        {
            "ForestBerry.pmx",
            "T_KokonaShiki_Body_104_D.png",
            "T_KokonaShiki_Face.png",
            "T_KokonaShiki_Hair.TGA",
            "T_KokonaShiki_Hair_104_D.png"
        };

        [SerializeField] private bool loadBundledSampleOnStart = true;
        [SerializeField] private string bundledSampleDirectory = "MmdSamples/ForestBerry";
        [SerializeField] private string bundledSamplePmx = "ForestBerry.pmx";
        [SerializeField, Range(1f, 12f)] private float frameBudgetMilliseconds = 4f;
        [SerializeField] private Vector3 spawnPosition = new Vector3(0f, 0f, 2.2f);

        private CancellationTokenSource loadCancellation;
        private PMXImportResult currentResult;
        private AvatarController currentAvatar;

        public event Action<AvatarController> AvatarLoaded;
        public event Action ModelWillUnload;
        public event Action<string> LoadFailed;
        public event Action<string> ProgressChanged;

        public AvatarController CurrentAvatar => currentAvatar;
        public GameObject CurrentModel => currentResult == null ? null : currentResult.root;
        public PMXModel CurrentMmdModel => currentResult == null ? null : currentResult.model;
        public bool IsLoading { get; private set; }
        public string CurrentModelPath { get; private set; }

        private async void Start()
        {
            if (!loadBundledSampleOnStart)
            {
                return;
            }

            string pmxPath;
            try
            {
                pmxPath = await PrepareBundledSampleAsync();
            }
            catch (OperationCanceledException)
            {
                // Object destruction canceled extraction before an import began.
                return;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                LoadFailed?.Invoke(exception.Message);
                return;
            }

            try
            {
                await LoadFromFileAsync(pmxPath);
            }
            catch (OperationCanceledException)
            {
                // A new import or object destruction canceled the previous load.
            }
            catch (Exception exception)
            {
                // LoadFromFileAsync already notified listeners with the original error.
                Debug.LogException(exception, this);
            }
        }
        /// <summary>
        /// Loads a PMX file and its adjacent texture directory. This is the API
        /// that a file picker, network transfer, or AstrBot bridge can call later.
        /// </summary>
        public async Task<AvatarController> LoadFromFileAsync(string pmxPath, string textureBaseDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(pmxPath))
            {
                throw new ArgumentException("A PMX file path is required.", nameof(pmxPath));
            }

            if (!File.Exists(pmxPath))
            {
                throw new FileNotFoundException("The PMX file was not found.", pmxPath);
            }

            loadCancellation?.Cancel();
            loadCancellation?.Dispose();
            loadCancellation = new CancellationTokenSource();
            var token = loadCancellation.Token;
            IsLoading = true;
            ProgressChanged?.Invoke("Reading PMX");

            try
            {
                var result = await ImportAsync(pmxPath, textureBaseDirectory, token);
                token.ThrowIfCancellationRequested();

                var modelRoot = result.root;
                var avatarHost = new GameObject(modelRoot.name + "_Avatar");
                avatarHost.transform.SetPositionAndRotation(spawnPosition, Quaternion.identity);
                modelRoot.transform.SetParent(avatarHost.transform, false);
                var avatar = avatarHost.AddComponent<AvatarController>();
                avatar.Initialize(modelRoot.transform);

                UnloadCurrentModel();
                currentResult = result;
                currentAvatar = avatar;
                CurrentModelPath = pmxPath;
                ProgressChanged?.Invoke("Model ready");
                AvatarLoaded?.Invoke(avatar);
                return avatar;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                LoadFailed?.Invoke(exception.Message);
                throw;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void CancelLoad()
        {
            loadCancellation?.Cancel();
        }

        private async Task<PMXImportResult> ImportAsync(string pmxPath, string textureBaseDirectory, CancellationToken token)
        {
            var budget = new UMTFrameBudget(frameBudgetMilliseconds);
            PMXModel model;
            using (var stream = File.OpenRead(pmxPath))
            {
                model = await PMXReader.ReadAsync(budget, stream, true);
            }

            token.ThrowIfCancellationRequested();
            PreserveOriginalNames(model);
            ProgressChanged?.Invoke("Building MMD materials and skeleton");
            var options = new PMXImportOptions
            {
                sourcePath = pmxPath,
                textureBaseDirectory = string.IsNullOrWhiteSpace(textureBaseDirectory)
                    ? Path.GetDirectoryName(pmxPath)
                    : textureBaseDirectory,
                applyRenames = false,
                createAvatar = false
            };

            // Runtime loading deliberately keeps source names. UMT's editor-only
            // rename/romanization pass needs optional dictionary assets and is not
            // required to build meshes, textures, MMD physics, or morph data.
            token.ThrowIfCancellationRequested();
            var result = await PMXImporter.BuildUnityObjectsAsync(budget, model, options);
            if (result.warnings.Count > 0)
            {
                foreach (var warning in result.warnings)
                {
                    Debug.LogWarning($"[RuntimeMmdModelLoader] {warning}", result.root);
                }
            }

            return result;
        }

        private static void PreserveOriginalNames(PMXModel model)
        {
            if (model == null)
            {
                return;
            }

            // The runtime importer skips UMT's optional rename dictionary. Keep
            // source names so semantic interaction can find bones and morphs.
            for (var i = 0; i < model.bones.Length; i++)
            {
                var bone = model.bones[i];
                bone.renamedName = bone.originalName;
                model.bones[i] = bone;
            }

            for (var i = 0; i < model.morphs.Length; i++)
            {
                var morph = model.morphs[i];
                morph.renamedName = morph.originalName;
                model.morphs[i] = morph;
            }
        }
        private async Task<string> PrepareBundledSampleAsync()
        {
            var targetDirectory = Path.Combine(Application.persistentDataPath, "MmdModels", "ForestBerry");
            Directory.CreateDirectory(targetDirectory);
            ProgressChanged?.Invoke("Preparing bundled PMX sample");

            foreach (var fileName in BundledSampleFiles)
            {
                var targetPath = Path.Combine(targetDirectory, fileName);
                if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
                {
                    continue;
                }

                var relativePath = $"{bundledSampleDirectory}/{fileName}";
                await CopyStreamingAssetAsync(relativePath, targetPath);
            }

            return Path.Combine(targetDirectory, bundledSamplePmx);
        }

        private static async Task CopyStreamingAssetAsync(string relativePath, string targetPath)
        {
            var sourcePath = Path.Combine(Application.streamingAssetsPath, relativePath).Replace('\\', '/');
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var request = UnityWebRequest.Get(sourcePath))
            {
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    await Task.Yield();
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new IOException($"Could not extract StreamingAsset '{relativePath}': {request.error}");
                }

                File.WriteAllBytes(targetPath, request.downloadHandler.data);
            }
#else
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Bundled StreamingAsset was not found.", sourcePath);
            }

            File.Copy(sourcePath, targetPath, true);
            await Task.Yield();
#endif
        }

        private void UnloadCurrentModel()
        {
            if (currentResult == null)
            {
                currentAvatar = null;
                CurrentModelPath = null;
                return;
            }

            ModelWillUnload?.Invoke();

            if (currentAvatar != null && currentAvatar.gameObject != null)
            {
                Destroy(currentAvatar.gameObject);
            }
            else if (currentResult.root != null)
            {
                Destroy(currentResult.root);
            }

            foreach (var texture in currentResult.textures)
            {
                if (texture != null)
                {
                    Destroy(texture);
                }
            }

            if (currentResult.model != null)
            {
                Destroy(currentResult.model);
            }

            currentResult = null;
            currentAvatar = null;
            CurrentModelPath = null;
        }

        private void OnDestroy()
        {
            loadCancellation?.Cancel();
            loadCancellation?.Dispose();
            UnloadCurrentModel();
        }
    }
}
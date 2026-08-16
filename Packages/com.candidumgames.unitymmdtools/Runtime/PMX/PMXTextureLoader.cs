using B83.Image.BMP;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace UMT
{
    /// <summary>
    /// Loads PMX textures from disk into a texture-index map, decoding PNG/JPG via Unity and TGA/BMP via the bundled loaders, caching each resolved file so it is decoded only once.
    /// </summary>
    public static class PMXTextureLoader
    {
        /// <summary>
        /// Loads every PMX texture once into an array indexed by PMX texture index.
        /// </summary>
        /// <param name="model">PMX model providing relative texture paths.</param>
        /// <param name="options">Import options providing the texture base directory and source path.</param>
        /// <param name="result">Import result that collects created textures and warnings.</param>
        /// <returns>Textures indexed by PMX texture index; entries are null when a texture is missing or undecodable.</returns>
        public static Texture2D[] Load(PMXModel model, PMXImportOptions options, PMXImportResult result)
        {
            Texture2D[] texturesByIndex = new Texture2D[model.texturePaths.Length];

            Dictionary<string, Texture2D> loadedTextures = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < model.texturePaths.Length; ++i)
            {
                texturesByIndex[i] = LoadTexture(i, model.texturePaths[i].ToString(), loadedTextures, options, result);
            }

            return texturesByIndex;
        }

        /// <summary>
        /// Runtime-friendly texture loading that yields between individual
        /// source files. Image decoding remains on Unity's main thread, but a
        /// model with several textures no longer decodes all of them in one
        /// uninterrupted frame.
        /// </summary>
        public static async Task<Texture2D[]> LoadAsync(
            UMTFrameBudget frameBudget,
            PMXModel model,
            PMXImportOptions options,
            PMXImportResult result,
            CancellationToken cancellationToken = default,
            IDictionary<int, PMXMaterialBuilder.SourcePixels> predecodedSourcePixels = null)
        {
            if (frameBudget == null)
            {
                throw new ArgumentNullException(nameof(frameBudget));
            }
            Texture2D[] texturesByIndex = new Texture2D[model.texturePaths.Length];
            Dictionary<string, Texture2D> loadedTextures =
                new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, PMXMaterialBuilder.SourcePixels> loadedSourcePixels =
                new Dictionary<string, PMXMaterialBuilder.SourcePixels>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < model.texturePaths.Length; ++i)
            {
                cancellationToken.ThrowIfCancellationRequested();
                texturesByIndex[i] = await LoadTextureAsync(
                    i,
                    model.texturePaths[i].ToString(),
                    loadedTextures,
                    loadedSourcePixels,
                    options,
                    result,
                    cancellationToken,
                    predecodedSourcePixels);
                await frameBudget.YieldIfNeeded();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return texturesByIndex;
        }

        private static async Task<Texture2D> LoadTextureAsync(
            int index,
            string texturePath,
            Dictionary<string, Texture2D> loadedTextures,
            Dictionary<string, PMXMaterialBuilder.SourcePixels> loadedSourcePixels,
            PMXImportOptions options,
            PMXImportResult result,
            CancellationToken cancellationToken,
            IDictionary<int, PMXMaterialBuilder.SourcePixels> predecodedSourcePixels)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                return null;
            }
            string textureFullPath = await ResolveTexturePathAsync(
                texturePath,
                options,
                cancellationToken);
            if (string.IsNullOrEmpty(textureFullPath))
            {
                PMXUtilities.AddWarning(result, $"Texture was not found: {texturePath}");
                return null;
            }
            if (loadedTextures.TryGetValue(textureFullPath, out Texture2D cachedTexture))
            {
                if (predecodedSourcePixels != null &&
                    loadedSourcePixels.TryGetValue(
                        textureFullPath,
                        out PMXMaterialBuilder.SourcePixels cachedPixels))
                {
                    predecodedSourcePixels[index] = cachedPixels;
                }
                return cachedTexture;
            }

            Texture2D texture;
            if (string.Equals(
                Path.GetExtension(textureFullPath),
                ".tga",
                StringComparison.OrdinalIgnoreCase))
            {
                DecodedTga decoded = await Task.Run(
                    () => DecodeTga(textureFullPath),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                PMXMaterialBuilder.SourcePixels sourcePixels =
                    new PMXMaterialBuilder.SourcePixels(
                        decoded.pixels,
                        decoded.width,
                        decoded.height);
                texture = new Texture2D(decoded.width, decoded.height, TextureFormat.RGBA32, false);
                texture.SetPixelData(decoded.pixels, 0);
                texture.Apply(false, predecodedSourcePixels != null);
                loadedSourcePixels[textureFullPath] = sourcePixels;
                if (predecodedSourcePixels != null)
                {
                    predecodedSourcePixels[index] = sourcePixels;
                }
            }
            else if (IsUnityWebTexture(textureFullPath))
            {
                texture = await LoadUnityWebTextureAsync(textureFullPath, cancellationToken);
            }
            else
            {
                byte[] textureBytes = await Task.Run(
                    () => File.ReadAllBytes(textureFullPath),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                texture = DecodeTexture(textureFullPath, textureBytes);
            }
            if (texture == null)
            {
                PMXUtilities.AddWarning(result, $"Texture could not be decoded: {textureFullPath}");
                return null;
            }
            texture.name = PMXUtilities.SanitizeFileName(
                Path.GetFileNameWithoutExtension(textureFullPath),
                index);
            result.textures.Add(texture);
            loadedTextures[textureFullPath] = texture;
            return texture;
        }

        private static bool IsUnityWebTexture(string textureFullPath)
        {
            string extension = Path.GetExtension(textureFullPath);
            return string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<Texture2D> LoadUnityWebTextureAsync(
            string textureFullPath,
            CancellationToken cancellationToken)
        {
            string uri = new Uri(textureFullPath).AbsoluteUri;
            using (UnityWebRequest request = UnityWebRequestTexture.GetTexture(uri, false))
            {
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    return null;
                }
                return DownloadHandlerTexture.GetContent(request);
            }
        }

        private readonly struct DecodedTga
        {
            public DecodedTga(Color32[] pixels, int width, int height)
            {
                this.pixels = pixels;
                this.width = width;
                this.height = height;
            }

            public readonly Color32[] pixels;
            public readonly int width;
            public readonly int height;
        }

        private static DecodedTga DecodeTga(string textureFullPath)
        {
            using (FileStream stream = File.OpenRead(textureFullPath))
            {
                Color32[] pixels = ThirdParty.TGALoader.LoadTGA(
                    stream,
                    out int width,
                    out int height,
                    out int _);
                return new DecodedTga(pixels, width, height);
            }
        }

        private static async Task<string> ResolveTexturePathAsync(
            string texturePath,
            PMXImportOptions options,
            CancellationToken cancellationToken)
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            cancellationToken.ThrowIfCancellationRequested();
            return ResolveTexturePath(texturePath, options);
#else
            string resolved = await Task.Run(
                () => ResolveTexturePath(texturePath, options),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return resolved;
#endif
        }

        private static Texture2D LoadTexture(int index, string texturePath, Dictionary<string, Texture2D> loadedTextures, PMXImportOptions options, PMXImportResult result)
        {
            if (string.IsNullOrWhiteSpace(texturePath))
            {
                return null;
            }

            string textureFullPath = ResolveTexturePath(texturePath, options);
            if (string.IsNullOrEmpty(textureFullPath))
            {
                PMXUtilities.AddWarning(result, $"Texture was not found: {texturePath}");
                return null;
            }

            if (loadedTextures.TryGetValue(textureFullPath, out Texture2D cachedTexture))
            {
                return cachedTexture;
            }

            byte[] textureBytes = File.ReadAllBytes(textureFullPath);
            Texture2D texture = DecodeTexture(textureFullPath, textureBytes);
            if (texture == null)
            {
                PMXUtilities.AddWarning(result, $"Texture could not be decoded: {textureFullPath}");
                return null;
            }

            texture.name = PMXUtilities.SanitizeFileName(Path.GetFileNameWithoutExtension(textureFullPath), index);
            result.textures.Add(texture);
            loadedTextures[textureFullPath] = texture;

            return texture;
        }

        private static Texture2D DecodeTexture(string textureFullPath, byte[] textureBytes)
        {
            string extension = Path.GetExtension(textureFullPath);
            if (string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase))
            {
                using MemoryStream stream = new MemoryStream(textureBytes, false);
                return ThirdParty.TGALoader.LoadTGA(stream);
            }

            if (string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase))
            {
                BMPImage bmp = new BMPLoader().LoadBMP(textureBytes);
                return bmp?.ToTexture2D();
            }

            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (ImageConversion.LoadImage(texture, textureBytes))
            {
                return texture;
            }

            UnityEngine.Object.Destroy(texture);
            return null;
        }

        internal static string ResolveTexturePath(string texturePath, PMXImportOptions options)
        {
            string baseDirectory = !string.IsNullOrEmpty(options.textureBaseDirectory) ? options.textureBaseDirectory : Path.GetDirectoryName(options.sourcePath);
            if (string.IsNullOrEmpty(baseDirectory) || !Directory.Exists(baseDirectory))
            {
                return null;
            }

            string[] candidates = new[]
            {
                Path.Combine(baseDirectory, texturePath),
                Path.Combine(baseDirectory, texturePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)),
            };

            HashSet<string> visitedCandidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (string candidate in candidates)
            {
                if (!visitedCandidates.Add(candidate))
                {
                    continue;
                }

                string fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }

            string fileName = Path.GetFileName(texturePath);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            string recursiveMatch = null;
            foreach (string file in Directory.GetFiles(baseDirectory, "*", SearchOption.AllDirectories))
            {
                if (string.Equals(Path.GetFileName(file), fileName, StringComparison.OrdinalIgnoreCase))
                {
                    if (recursiveMatch != null &&
                        !string.Equals(recursiveMatch, file, StringComparison.OrdinalIgnoreCase))
                    {
                        // Ambiguous basename fallback must fail closed. Picking
                        // the first file can cross-load a sibling PMX variant.
                        return null;
                    }
                    recursiveMatch = file;
                }
            }

            return recursiveMatch;
        }
    }
}

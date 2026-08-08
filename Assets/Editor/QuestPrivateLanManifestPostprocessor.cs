#if UNITY_EDITOR
using System.IO;
using System.Text;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

namespace QuestMmdPlayer.Editor
{
    /// <summary>
    /// Android 9+ blocks all cleartext traffic by default. The runtime protocol
    /// still rejects HTTP unless the user explicitly enables it and the host is
    /// a literal RFC1918/link-local IP; this manifest flag only lets that narrow
    /// development fallback reach a private AstrBot on the local network.
    /// </summary>
    public sealed class QuestPrivateLanManifestPostprocessor : IPostGenerateGradleAndroidProject
    {
        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        public int callbackOrder => 10000;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            var manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
            var document = new XmlDocument { PreserveWhitespace = true };
            document.Load(manifestPath);
            var application = document.DocumentElement?.SelectSingleNode("application") as XmlElement;
            if (application == null)
            {
                throw new InvalidDataException("Generated AndroidManifest.xml has no application element.");
            }

            application.SetAttribute("usesCleartextTraffic", AndroidNamespace, "true");
            EnsureUnityPlayerActivityLabel(application);
            EnsureAppLabelResource(path);
            EnsurePermission(document, "horizonos.permission.HAND_TRACKING");
            document.Save(manifestPath);
            ConfigureFilePickerModule(path);
            Debug.Log("[QuestBuild] Enabled private-LAN HTTP and Horizon OS hand tracking permission.");
        }

        private static void ConfigureFilePickerModule(string unityLibraryPath)
        {
            var gradlePath = Path.Combine(
                unityLibraryPath,
                "BanxiaFilePicker.androidlib",
                "build.gradle");
            if (!File.Exists(gradlePath))
            {
                throw new FileNotFoundException(
                    "Generated Banxia file picker Gradle module was not found.",
                    gradlePath);
            }

            var source = File.ReadAllText(gradlePath);
            const string applicationNamespace = "namespace \"com.lingxi.banxia\"";
            const string libraryNamespace = "namespace \"com.lingxi.banxia.filepicker\"";
            if (source.Contains(applicationNamespace))
            {
                source = source.Replace(applicationNamespace, libraryNamespace);
            }
            else if (!source.Contains(libraryNamespace))
            {
                throw new InvalidDataException("Unexpected Banxia file picker Gradle namespace.");
            }

            const string buildConfigSetting = "buildConfig = false";
            if (!source.Contains(buildConfigSetting))
            {
                const string androidBlock = "android {";
                var first = source.IndexOf(androidBlock, System.StringComparison.Ordinal);
                var second = first < 0
                    ? -1
                    : source.IndexOf(
                        androidBlock,
                        first + androidBlock.Length,
                        System.StringComparison.Ordinal);
                if (first < 0 || second >= 0)
                {
                    throw new InvalidDataException("Unexpected Banxia file picker Gradle structure.");
                }

                var insertion = androidBlock + System.Environment.NewLine +
                    "    buildFeatures { buildConfig = false }";
                source = source.Substring(0, first) +
                    source.Substring(first).Replace(androidBlock, insertion);
            }

            File.WriteAllText(gradlePath, source, new UTF8Encoding(false));
        }

        internal static void EnsureUnityPlayerActivityLabel(XmlElement application)
        {
            if (application == null)
            {
                throw new System.ArgumentNullException(nameof(application));
            }

            // Keep the task/application label independent of Unity's generated
            // app_name resource, which can be absent on Quest merge variants.
            const string applicationLabel = "伴夏";

            var activities = application.SelectNodes("activity");
            if (activities == null)
            {
                throw new InvalidDataException("Generated AndroidManifest.xml has no activity elements.");
            }

            foreach (XmlNode node in activities)
            {
                if (!(node is XmlElement activity) ||
                    activity.GetAttribute("name", AndroidNamespace) !=
                    "com.unity3d.player.UnityPlayerActivity")
                {
                    continue;
                }

                activity.SetAttribute("label", AndroidNamespace, applicationLabel);
                return;
            }

            throw new InvalidDataException(
                "Generated AndroidManifest.xml has no UnityPlayerActivity element.");
        }

        private static void EnsureAppLabelResource(string unityLibraryPath)
        {
            var valuesDirectory = Path.Combine(unityLibraryPath, "src", "main", "res", "values");
            Directory.CreateDirectory(valuesDirectory);
            var resourcePath = Path.Combine(valuesDirectory, "banxia_strings.xml");
            File.WriteAllText(
                resourcePath,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + System.Environment.NewLine +
                "<resources><string name=\"banxia_app_name\">伴夏</string></resources>" +
                System.Environment.NewLine,
                new UTF8Encoding(false));
        }

        private static void EnsurePermission(XmlDocument document, string permissionName)
        {
            var manifest = document.DocumentElement;
            if (manifest == null)
            {
                throw new InvalidDataException("Generated AndroidManifest.xml has no manifest element.");
            }

            var nodes = manifest.SelectNodes("uses-permission");
            if (nodes != null)
            {
                foreach (XmlNode node in nodes)
                {
                    if (node is XmlElement element &&
                        element.GetAttribute("name", AndroidNamespace) == permissionName)
                    {
                        return;
                    }
                }
            }

            var permission = document.CreateElement("uses-permission");
            permission.SetAttribute("name", AndroidNamespace, permissionName);
            manifest.InsertBefore(permission, manifest.SelectSingleNode("application"));
        }
    }
}
#endif

#if UNITY_EDITOR
using System.IO;
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
            EnsurePermission(document, "horizonos.permission.HAND_TRACKING");
            document.Save(manifestPath);
            Debug.Log("[QuestBuild] Enabled private-LAN HTTP and Horizon OS hand tracking permission.");
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

#if UNITY_EDITOR
using System.Xml;
using NUnit.Framework;
using QuestMmdPlayer.Editor;

namespace QuestMmdPlayer.Tests
{
    public sealed class QuestPrivateLanManifestPostprocessorTests
    {
        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";

        [Test]
        public void UnityPlayerActivityReceivesApplicationLabel()
        {
            var document = new XmlDocument();
            document.LoadXml(
                "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">" +
                "<application android:label=\"@string/app_name\">" +
                "<activity android:name=\"com.unity3d.player.UnityPlayerActivity\" />" +
                "</application></manifest>");
            var application = (XmlElement)document.DocumentElement.SelectSingleNode("application");

            QuestPrivateLanManifestPostprocessor.EnsureUnityPlayerActivityLabel(application);

            var activity = (XmlElement)application.SelectSingleNode("activity");
            Assert.That(activity.GetAttribute("label", AndroidNamespace), Is.EqualTo("伴夏"));
        }

        [Test]
        public void UnityPlayerActivityUsesStableFallbackWhenApplicationLabelIsMissing()
        {
            var document = new XmlDocument();
            document.LoadXml(
                "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">" +
                "<application>" +
                "<activity android:name=\"com.unity3d.player.UnityPlayerActivity\" />" +
                "</application></manifest>");
            var application = (XmlElement)document.DocumentElement.SelectSingleNode("application");

            QuestPrivateLanManifestPostprocessor.EnsureUnityPlayerActivityLabel(application);

            var activity = (XmlElement)application.SelectSingleNode("activity");
            Assert.That(activity.GetAttribute("label", AndroidNamespace), Is.EqualTo("伴夏"));
        }
    }
}
#endif

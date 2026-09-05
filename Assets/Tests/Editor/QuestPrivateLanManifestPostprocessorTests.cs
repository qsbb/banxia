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

        [Test]
        public void UnityPlayerActivityHardwareAccelerationCanBeEnabledForPhone()
        {
            var document = new XmlDocument();
            document.LoadXml(
                "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">" +
                "<application>" +
                "<activity android:name=\"com.unity3d.player.UnityPlayerActivity\" " +
                "android:hardwareAccelerated=\"false\" />" +
                "</application></manifest>");
            var application = (XmlElement)document.DocumentElement.SelectSingleNode("application");

            QuestPrivateLanManifestPostprocessor.EnsureUnityPlayerActivityHardwareAcceleration(
                application,
                true);

            var activity = (XmlElement)application.SelectSingleNode("activity");
            Assert.That(
                activity.GetAttribute("hardwareAccelerated", AndroidNamespace),
                Is.EqualTo("true"));
        }

        [Test]
        public void UnityPlayerActivityHardwareAccelerationCanRemainDisabledForQuest()
        {
            var document = new XmlDocument();
            document.LoadXml(
                "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">" +
                "<application>" +
                "<activity android:name=\"com.unity3d.player.UnityPlayerActivity\" />" +
                "</application></manifest>");
            var application = (XmlElement)document.DocumentElement.SelectSingleNode("application");

            QuestPrivateLanManifestPostprocessor.EnsureUnityPlayerActivityHardwareAcceleration(
                application,
                false);

            var activity = (XmlElement)application.SelectSingleNode("activity");
            Assert.That(
                activity.GetAttribute("hardwareAccelerated", AndroidNamespace),
                Is.EqualTo("false"));
        }

        [Test]
        public void RequiredPermissionIsRestoredWithoutDuplicatingIt()
        {
            var document = new XmlDocument();
            document.LoadXml(
                "<manifest xmlns:android=\"http://schemas.android.com/apk/res/android\">" +
                "<application /></manifest>");

            QuestPrivateLanManifestPostprocessor.EnsurePermission(
                document,
                "android.permission.INTERNET");
            QuestPrivateLanManifestPostprocessor.EnsurePermission(
                document,
                "android.permission.INTERNET");

            var permissions = document.DocumentElement.SelectNodes("uses-permission");
            Assert.That(permissions.Count, Is.EqualTo(1));
            Assert.That(
                ((XmlElement)permissions[0]).GetAttribute("name", AndroidNamespace),
                Is.EqualTo("android.permission.INTERNET"));
        }
    }
}
#endif

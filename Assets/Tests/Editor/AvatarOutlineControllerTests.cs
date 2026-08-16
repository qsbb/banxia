#if UNITY_EDITOR
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarOutlineControllerTests
    {
        private GameObject avatarObject;
        private GameObject serviceObject;
        private Material sourceMaterial;

        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey("quest_avatar_outline_enabled_v1");
            PlayerPrefs.DeleteKey("quest_avatar_outline_width_v1");
            if (serviceObject != null) Object.DestroyImmediate(serviceObject);
            if (avatarObject != null) Object.DestroyImmediate(avatarObject);
            if (sourceMaterial != null) Object.DestroyImmediate(sourceMaterial);
        }

        [Test]
        public void BindUsesUrpPassWithoutDuplicatingRenderer()
        {
            avatarObject = new GameObject("Outline Avatar");
            var meshObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            meshObject.name = "Body";
            meshObject.transform.SetParent(avatarObject.transform, false);
            var sourceShader = Shader.Find("Universal Render Pipeline/Unlit");
            Assert.That(sourceShader, Is.Not.Null);
            sourceMaterial = new Material(sourceShader);
            sourceMaterial.SetOverrideTag(AvatarOutlineController.OutlineTagName, "1");
            meshObject.GetComponent<Renderer>().sharedMaterial = sourceMaterial;
            var avatar = avatarObject.AddComponent<AvatarController>();
            avatar.Initialize(avatarObject.transform);

            serviceObject = new GameObject("Outline Service");
            var outline = serviceObject.AddComponent<AvatarOutlineController>();
            outline.SetEnabled(true);
            outline.Bind(avatar);

            Assert.That(outline.ShellCount, Is.EqualTo(1));
            Assert.That(outline.Status, Does.Contain("描边 开启"));
            Assert.That(outline.Status, Does.Contain("URP额外Pass"));
            var renderers = avatarObject.GetComponentsInChildren<MeshRenderer>(true);
            Assert.That(renderers.Length, Is.EqualTo(1));

            outline.SetWidth(99f);
            Assert.That(outline.OutlineWidth, Is.EqualTo(.003f).Within(.00001f));
            outline.Toggle();
            Assert.That(outline.OutlineEnabled, Is.False);
        }

        [Test]
        public void PerformanceQaOutlineOverrideDoesNotPersistOverUserPreference()
        {
            PlayerPrefs.SetInt("quest_avatar_outline_enabled_v1", 1);
            PlayerPrefs.Save();
            serviceObject = new GameObject("Outline QA Service");
            var outline = serviceObject.AddComponent<AvatarOutlineController>();

            var setEnabledForQa = typeof(AvatarOutlineController).GetMethod(
                "SetEnabledForQa",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(setEnabledForQa, Is.Not.Null);
            setEnabledForQa.Invoke(outline, new object[] { false });

            Assert.That(outline.OutlineEnabled, Is.False);
            Assert.That(
                PlayerPrefs.GetInt("quest_avatar_outline_enabled_v1"),
                Is.EqualTo(1));
        }
    }
}
#endif

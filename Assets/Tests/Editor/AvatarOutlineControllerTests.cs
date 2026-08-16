#if UNITY_EDITOR
using NUnit.Framework;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarOutlineControllerTests
    {
        private GameObject avatarObject;
        private GameObject serviceObject;

        [TearDown]
        public void TearDown()
        {
            PlayerPrefs.DeleteKey("quest_avatar_outline_enabled_v1");
            PlayerPrefs.DeleteKey("quest_avatar_outline_width_v1");
            if (serviceObject != null) Object.DestroyImmediate(serviceObject);
            if (avatarObject != null) Object.DestroyImmediate(avatarObject);
        }

        [Test]
        public void BindUsesNativeOutlineMaterialWithoutDuplicatingRenderer()
        {
            avatarObject = new GameObject("Outline Avatar");
            var meshObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
            meshObject.name = "Body";
            meshObject.transform.SetParent(avatarObject.transform, false);
            var outlineShader = Shader.Find("QuestMmdPlayer/Avatar Outline");
            Assert.That(outlineShader, Is.Not.Null);
            meshObject.GetComponent<Renderer>().sharedMaterial = new Material(outlineShader);
            var avatar = avatarObject.AddComponent<AvatarController>();
            avatar.Initialize(avatarObject.transform);

            serviceObject = new GameObject("Outline Service");
            var outline = serviceObject.AddComponent<AvatarOutlineController>();
            outline.SetEnabled(true);
            outline.Bind(avatar);

            Assert.That(outline.ShellCount, Is.EqualTo(1));
            Assert.That(outline.Status, Does.Contain("描边 开启"));
            var renderers = avatarObject.GetComponentsInChildren<MeshRenderer>(true);
            Assert.That(renderers.Length, Is.EqualTo(1));

            outline.SetWidth(99f);
            Assert.That(outline.OutlineWidth, Is.EqualTo(.003f).Within(.00001f));
            outline.Toggle();
            Assert.That(outline.OutlineEnabled, Is.False);
            Assert.That(renderers[0].sharedMaterial.GetFloat("_OutlineWidth"), Is.Zero);
        }
    }
}
#endif

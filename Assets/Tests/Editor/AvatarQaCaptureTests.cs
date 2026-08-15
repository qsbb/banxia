using NUnit.Framework;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer.Tests
{
    public sealed class AvatarQaCaptureTests
    {
        [TestCase("head")]
        [TestCase("Head Bone")]
        [TestCase("\u982D")]
        [TestCase("\u5934")]
        public void FindHeadBoneRecognizesCommonPmxNames(string name)
        {
            var avatar = new GameObject("Avatar");
            var head = new GameObject("Bone");
            try
            {
                head.transform.SetParent(avatar.transform, false);
                head.AddComponent<MMDBoneTransform>().boneName = name;

                Assert.That(AvatarQaCapture.FindHeadBone(avatar), Is.SameAs(head.transform));
            }
            finally
            {
                Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void CalculateCameraPoseUsesHeadAndViewerInsteadOfWorldAxis()
        {
            var avatar = new GameObject("Avatar");
            var head = new GameObject("Head");
            var viewerObject = new GameObject("Viewer");
            try
            {
                avatar.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                head.transform.SetParent(avatar.transform, false);
                head.transform.localPosition = new Vector3(0f, 1.55f, 0f);
                head.AddComponent<MMDBoneTransform>().boneName = "\u982D";
                var viewer = viewerObject.AddComponent<Camera>();
                viewer.transform.position = head.transform.position + new Vector3(2f, .2f, 1f);
                var bounds = new Bounds(avatar.transform.position + Vector3.up * .9f, new Vector3(.6f, 1.8f, .5f));

                var pose = AvatarQaCapture.CalculateCameraPose(avatar, bounds, viewer);
                var horizontalToViewer = Vector3.ProjectOnPlane(viewer.transform.position - head.transform.position, Vector3.up).normalized;
                var horizontalFromHead = Vector3.ProjectOnPlane(pose.position - head.transform.position, Vector3.up).normalized;

                Assert.That(Vector3.Dot(horizontalFromHead, horizontalToViewer), Is.GreaterThan(.999f));
                Assert.That(Vector3.Dot(pose.rotation * Vector3.forward, head.transform.position - pose.position), Is.GreaterThan(0f));
                Assert.That(pose.position.y, Is.GreaterThan(1.4f));
            }
            finally
            {
                Object.DestroyImmediate(avatar);
                Object.DestroyImmediate(viewerObject);
            }
        }
    }
}

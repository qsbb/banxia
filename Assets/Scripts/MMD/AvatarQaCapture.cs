using System;
using System.Collections;
using System.IO;
using UMT;
using UnityEngine;

namespace QuestMmdPlayer
{
    public static class AvatarQaCapture
    {
        public const string FileName = "banxia-avatar-qa.png";

        internal static IEnumerator Capture(GameObject avatar, Action<string> completed)
        {
            if (avatar == null)
            {
                Debug.LogWarning("[AvatarQaCapture] Avatar is unavailable.");
                yield break;
            }

            yield return new WaitForEndOfFrame();
            var bounds = CalculateBounds(avatar);
            var target = new RenderTexture(1024, 1024, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
            var cameraObject = new GameObject("Avatar QA Camera");
            var lightObject = new GameObject("Avatar QA Light");
            var previousActive = RenderTexture.active;
            var previousAmbientMode = RenderSettings.ambientMode;
            var previousAmbientLight = RenderSettings.ambientLight;
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(.08f, .09f, .11f, 1f);
                camera.targetTexture = target;
                camera.fieldOfView = 24f;
                camera.nearClipPlane = .01f;
                camera.farClipPlane = 20f;

                var pose = CalculateCameraPose(avatar, bounds, Camera.main);
                cameraObject.transform.SetPositionAndRotation(pose.position, pose.rotation);

                var light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.35f;
                light.color = new Color(1f, .96f, .92f);
                lightObject.transform.rotation = Quaternion.Euler(35f, -28f, 0f);
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                RenderSettings.ambientLight = new Color(.48f, .5f, .56f);

                camera.Render();
                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                texture.Apply(false, false);
                var path = ResolveExternalFilesPath(FileName);
                File.WriteAllBytes(path, texture.EncodeToPNG());
                Debug.Log("[AvatarQaCapture] saved file=" + FileName + " bytes=" + new FileInfo(path).Length);
                completed?.Invoke(path);
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderSettings.ambientMode = previousAmbientMode;
                RenderSettings.ambientLight = previousAmbientLight;
                target.Release();
                UnityEngine.Object.Destroy(target);
                UnityEngine.Object.Destroy(texture);
                UnityEngine.Object.Destroy(cameraObject);
                UnityEngine.Object.Destroy(lightObject);
            }
        }

        public static Bounds CalculateBounds(GameObject avatar)
        {
            var renderers = avatar.GetComponentsInChildren<Renderer>(true);
            var found = false;
            var bounds = new Bounds(avatar.transform.position, Vector3.one);
            for (var index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];
                if (renderer == null || !renderer.enabled || renderer.name.EndsWith(" Outline Shell", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!found)
                {
                    bounds = renderer.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }
            return bounds;
        }

        public static Pose CalculateCameraPose(GameObject avatar, Bounds bounds, Camera viewerCamera)
        {
            var up = avatar.transform.up.normalized;
            if (up.sqrMagnitude < .0001f)
            {
                up = Vector3.up;
            }

            var head = FindHeadBone(avatar);
            var headCenter = head == null
                ? bounds.center + up * bounds.extents.y * .3f
                : head.position;
            var target = headCenter + up * Mathf.Clamp(bounds.size.y * .04f, .03f, .08f);
            var viewDirection = viewerCamera == null
                ? -avatar.transform.forward
                : viewerCamera.transform.position - headCenter;
            viewDirection = Vector3.ProjectOnPlane(viewDirection, up);
            if (viewDirection.sqrMagnitude < .0001f)
            {
                viewDirection = Vector3.ProjectOnPlane(-avatar.transform.forward, up);
            }
            if (viewDirection.sqrMagnitude < .0001f)
            {
                viewDirection = Vector3.back;
            }

            viewDirection.Normalize();
            // A 24-degree square camera sees only 0.43 * distance vertically.
            // Keep enough headroom for hair and a shoulder line instead of
            // letting a full-body AABB produce a face-only crop.
            var distance = Mathf.Clamp(bounds.size.y * .95f, .8f, 2.2f);
            var position = target + viewDirection * distance + up * Mathf.Clamp(bounds.size.y * .018f, .015f, .035f);
            return new Pose(position, Quaternion.LookRotation(target - position, up));
        }

        public static Transform FindHeadBone(GameObject avatar)
        {
            if (avatar == null)
            {
                return null;
            }

            var bones = avatar.GetComponentsInChildren<MMDBoneTransform>(true);
            var aliases = new[] { "head", "\u982d", "\u5934" };
            for (var pass = 0; pass < 2; pass++)
            {
                for (var aliasIndex = 0; aliasIndex < aliases.Length; aliasIndex++)
                {
                    var alias = NormalizeBoneName(aliases[aliasIndex]);
                    for (var boneIndex = 0; boneIndex < bones.Length; boneIndex++)
                    {
                        var bone = bones[boneIndex];
                        if (bone == null)
                        {
                            continue;
                        }

                        var name = NormalizeBoneName(bone.boneName);
                        if (pass == 0 ? name == alias : name.Contains(alias))
                        {
                            return bone.transform;
                        }
                    }
                }
            }

            return null;
        }

        private static string NormalizeBoneName(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim().Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
        }

        private static string ResolveExternalFilesPath(string fileName)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var directory = activity.Call<AndroidJavaObject>("getExternalFilesDir", new object[] { null }))
            {
                return Path.Combine(directory.Call<string>("getAbsolutePath"), fileName);
            }
#else
            return Path.Combine(Application.temporaryCachePath, fileName);
#endif
        }
    }
}

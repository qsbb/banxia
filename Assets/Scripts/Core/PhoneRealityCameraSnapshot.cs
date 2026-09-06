using System;
using System.Threading.Tasks;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// 摄像头单帧采集（手机端随身相机，BANXIA_PHONE 场景使用；Quest 端
    /// WebCamTexture 无设备，调用会得到如实的失败回执）。
    ///
    /// 隐私红线（PHONE_PORT_PLAN_CN.md 3.5 节）：
    /// - 仅用户本轮明确请求时拍一帧；不持续录像；
    /// - 单帧全程只在内存中转（WebCamTexture → Texture2D → JPEG byte[] →
    ///   base64 字符串），不写任何文件、不进 PlayerPrefs；
    /// - 采集结束立即销毁全部 GPU/纹理资源；
    /// - 任何失败都以字符串原因如实返回，由上层生成 must_not_claim_observed
    ///   式回执（见 RealityCameraTurn.ComposeFailureReceipt）。
    /// </summary>
    public sealed class RealityCaptureFrame
    {
        public string JpegBase64;
        public string Purpose;
        public int Width;
        public int Height;
    }

    public static class PhoneRealityCameraSnapshot
    {
        private const int LongestEdge = 1280;
        private const int JpegQuality = 80;
        private const float StartTimeoutSeconds = 5f;

        /// <summary>
        /// 拍摄单帧并编码为 JPEG base64。返回 null 表示失败（原因经
        /// onFailure 之外的返回值无法表达，改用 (frame, failureReason) 二元
        /// 结果：frame == null 时 failureReason 非空）。
        /// </summary>
        public static async Task<(RealityCaptureFrame frame, string failureReason)> CaptureSingleFrameAsync()
        {
            // 1) 运行时权限（Android 6+；未声明 CAMERA 权限时请求会被系统
            //    直接拒绝，因此 AndroidManifest 必须包含 CAMERA 声明）。
            var permissionFailure = await EnsureCameraPermissionAsync();
            if (permissionFailure != null)
            {
                return (null, permissionFailure);
            }

            // 2) 设备选择：优先后置（"看看我今天穿的"是环境/人物视角）。
            var devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
            {
                return (null, "此设备没有可用摄像头");
            }
            var chosen = devices[0];
            foreach (var device in devices)
            {
                if (!device.isFrontFacing)
                {
                    chosen = device;
                    break;
                }
            }

            // 3) 启动并等待第一帧（WebCamTexture.Play 后需要数帧才有内容）。
            var texture = new WebCamTexture(chosen.name, 1280, 720, 30);
            var startResult = await WaitFirstFrameAsync(texture);
            if (startResult != null)
            {
                StopAndRelease(texture, null);
                return (null, startResult);
            }

            Texture2D captured = null;
            try
            {
                // 4) 读帧 + 依据 videoRotationAngle 摆正（手机竖拍通常是 90°）。
                captured = ReadRotatedFrame(texture);
                if (captured == null)
                {
                    return (null, "摄像头画面读取失败");
                }

                // 5) 长边压到 1280（GPU blit 重采样），JPEG 编码后只留字符串。
                var scaled = DownscaleLongestEdge(captured, LongestEdge);
                var final = scaled != null ? scaled : captured;
                var jpeg = final.EncodeToJPG(JpegQuality);
                var frame = new RealityCaptureFrame
                {
                    JpegBase64 = Convert.ToBase64String(jpeg),
                    Purpose = string.Empty,
                    Width = final.width,
                    Height = final.height
                };
                if (scaled != null)
                {
                    UnityEngine.Object.Destroy(scaled);
                }
                return (frame, null);
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "camera.capture");
                QuestDebugMode.RethrowIfEnabled(exception, "camera.capture");
                return (null, "拍摄失败（" + exception.Message + "）");
            }
            finally
            {
                StopAndRelease(texture, captured);
            }
        }

        public static async Task<string> EnsureCameraPermissionAsync()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // UnityEngine.Android.Permission 的权限名是字符串常量（Permission.Camera），
            // 检查/请求 API 为 HasUserAuthorizedPermission / RequestUserPermission。
            var cameraPermission = UnityEngine.Android.Permission.Camera;
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(cameraPermission))
            {
                return null;
            }
            // RequestUserPermission 立即返回（系统对话框异步呈现），轮询等待
            // 用户响应；已永久拒绝时同样走超时，以失败回执如实上报。
            UnityEngine.Android.Permission.RequestUserPermission(cameraPermission);
            var deadline = Time.realtimeSinceStartup + 15f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(cameraPermission))
                {
                    return null;
                }
                await Task.Yield();
            }
            return "未获得相机权限，请在系统设置中允许伴夏使用相机";
#else
            await Task.Yield();
            return null;
#endif
        }

        private static async Task<string> WaitFirstFrameAsync(WebCamTexture texture)
        {
            texture.Play();
            var startedAt = Time.realtimeSinceStartup;
            var deadline = startedAt + StartTimeoutSeconds;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (texture.didUpdateThisFrame && texture.width > 16 && texture.height > 16)
                {
                    return null;
                }
                // Play() 后头几帧 isPlaying 仍可能为 false，给 1 秒宽限再判失败。
                if (!texture.isPlaying && Time.realtimeSinceStartup - startedAt > 1f)
                {
                    return "摄像头启动失败";
                }
                await Task.Yield();
            }
            return "摄像头启动超时";
        }

        private static Texture2D ReadRotatedFrame(WebCamTexture texture)
        {
            var source = texture.GetPixels32();
            var width = texture.width;
            var height = texture.height;
            var angle = ((texture.videoRotationAngle % 360) + 360) % 360;
            var flip = texture.videoVerticallyMirrored;

            Texture2D result;
            if (angle == 0 || angle == 180)
            {
                result = new Texture2D(width, height, TextureFormat.RGB24, false);
            }
            else
            {
                result = new Texture2D(height, width, TextureFormat.RGB24, false);
            }

            var destination = new Color32[source.Length];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var sourceColor = source[y * width + x];
                    if (flip)
                    {
                        sourceColor = source[(height - 1 - y) * width + x];
                    }
                    int destinationX;
                    int destinationY;
                    switch (angle)
                    {
                        case 90:
                            // 顺时针 90°：(x,y) → (H-1-y, x)
                            destinationX = height - 1 - y;
                            destinationY = x;
                            break;
                        case 180:
                            destinationX = width - 1 - x;
                            destinationY = height - 1 - y;
                            break;
                        case 270:
                            destinationX = y;
                            destinationY = width - 1 - x;
                            break;
                        default:
                            destinationX = x;
                            destinationY = y;
                            break;
                    }
                    destination[destinationY * result.width + destinationX] = sourceColor;
                }
            }
            result.SetPixels32(destination);
            result.Apply(false, false);
            return result;
        }

        private static Texture2D DownscaleLongestEdge(Texture2D source, int longestEdge)
        {
            var longest = Mathf.Max(source.width, source.height);
            if (longest <= longestEdge)
            {
                return null;
            }
            var scale = (float)longestEdge / longest;
            var targetWidth = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            var targetHeight = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            var renderTexture = RenderTexture.GetTemporary(targetWidth, targetHeight, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, renderTexture);
                RenderTexture.active = renderTexture;
                var scaled = new Texture2D(targetWidth, targetHeight, TextureFormat.RGB24, false);
                scaled.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
                scaled.Apply(false, false);
                return scaled;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static void StopAndRelease(WebCamTexture texture, Texture2D captured)
        {
            if (texture != null)
            {
                texture.Stop();
                UnityEngine.Object.Destroy(texture);
            }
            if (captured != null)
            {
                UnityEngine.Object.Destroy(captured);
            }
        }
    }
}

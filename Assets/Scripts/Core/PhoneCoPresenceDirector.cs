using System;
using System.Collections;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// 同框三模式导演（手机端）：虚拟场景（伪 AR）/ 视频通话（半身）/ 同框现实（真 AR L1）。
    /// - 虚拟场景：4 套预设环境（背景色 + 地板 + 双灯光），直接驱动主相机与灯光。
    /// - 视频通话：胸像构图（轨道相机 preset）+ 通话计时。
    /// - 同框现实：后置相机 WebCamTexture 全屏背景 Quad（跟随相机）+ 点地放置（射线交固定地面）。
    /// 端点协议零分支：三种模式只是呈现层，对话/动作/表情系统全模式可用。
    /// </summary>
    public sealed class PhoneCoPresenceDirector : MonoBehaviour
    {
        public enum CoPresenceMode
        {
            VirtualScene = 0,
            VideoCall = 1,
            ArReality = 2,
        }

        public enum VirtualEnvironment
        {
            NightStreet = 0,  // 夜街（默认）
            StarrySky = 1,    // 星空
            Bedroom = 2,      // 卧室
            Seaside = 3,      // 海边
        }

        private const string PrefsKey = "banxia.phone.copresence.";
        private const float GroundHeight = 0f;
        private const float ArBackgroundDepth = 8f;
        private const float VideoCallBustHeight = 1.15f;
        private const float VideoCallBustDistance = 1.35f;

        private Camera mainCamera;
        private PhoneOrbitCamera orbitCamera;
        private Transform avatarRoot;

        private CoPresenceMode mode = CoPresenceMode.VirtualScene;
        private VirtualEnvironment environment = VirtualEnvironment.NightStreet;

        private GameObject environmentRoot;
        private GameObject arBackgroundRoot;
        private WebCamTexture arWebCam;
        private Material arBackgroundMaterial;
        private Material floorMatInstance;
        private Light environmentLight;
        private Light keyLight;

        private bool savedOrbitState;
        private Vector3 savedOrbitTarget;
        private float savedOrbitDistance;
        private float savedOrbitYaw;
        private float savedOrbitPitch;

        private float callStartedAt = -1f;
        private bool arRoutineRunning;

        /// <summary>模式切换（成功）后触发；参数 = 新模式。</summary>
        public event Action<CoPresenceMode> ModeChanged;

        /// <summary>环境切换后触发；参数 = 新环境。</summary>
        public event Action<VirtualEnvironment> EnvironmentChanged;

        public CoPresenceMode CurrentMode => mode;
        public VirtualEnvironment CurrentEnvironment => environment;
        public bool ArCameraAvailable { get; private set; }
        public bool ArActive => mode == CoPresenceMode.ArReality && arBackgroundRoot != null
            && arBackgroundRoot.activeSelf && arWebCam != null && arWebCam.isPlaying;
        public bool VideoCallActive => mode == CoPresenceMode.VideoCall && callStartedAt >= 0f;

        public string CallDurationText => !VideoCallActive
            ? "00:00"
            : FormatDuration(Time.unscaledTime - callStartedAt);

        public static string FormatDuration(float seconds)
        {
            var span = TimeSpan.FromSeconds(Mathf.Max(0f, seconds));
            return span.Hours > 0
                ? $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }

        public void Initialize(Camera camera, PhoneOrbitCamera orbit)
        {
            mainCamera = camera;
            orbitCamera = orbit;
            mode = (CoPresenceMode)Mathf.Clamp(
                PlayerPrefs.GetInt(PrefsKey + "mode", (int)CoPresenceMode.VirtualScene), 0, 2);
            environment = (VirtualEnvironment)Mathf.Clamp(
                PlayerPrefs.GetInt(PrefsKey + "env", (int)VirtualEnvironment.NightStreet), 0, 3);
            ArCameraAvailable = DetectRearCamera();
            if (!ArCameraAvailable && mode == CoPresenceMode.ArReality)
            {
                mode = CoPresenceMode.VirtualScene;
            }
        }

        public void SetAvatar(Transform avatar)
        {
            avatarRoot = avatar;
        }

        /// <summary>进入场景时应用记忆的模式（UI 层在 EnterScene 成功后调用）。</summary>
        public void ApplyOnEnterScene()
        {
            if (mode == CoPresenceMode.VideoCall)
            {
                EnterVideoCall();
            }
            else if (mode == CoPresenceMode.ArReality && ArCameraAvailable)
            {
                EnterArReality();
            }
            else
            {
                mode = CoPresenceMode.VirtualScene;
                EnterVirtualScene();
            }
        }

        /// <summary>退出场景回菜单：停相机流省电、还原视角（模式记忆保留）。</summary>
        public void Suspend()
        {
            TeardownArBackground();
            ExitVideoCall();
            RestoreOrbitState();
            if (environmentRoot != null)
            {
                environmentRoot.SetActive(false);
            }
        }

        public void SwitchMode(CoPresenceMode next)
        {
            if (next == mode || arRoutineRunning)
            {
                return;
            }
            if (next == CoPresenceMode.ArReality && !ArCameraAvailable)
            {
                return;
            }
            if (mode == CoPresenceMode.VideoCall)
            {
                ExitVideoCall();
                RestoreOrbitState();
            }
            else
            {
                TeardownArBackground();
            }
            mode = next;
            PlayerPrefs.SetInt(PrefsKey + "mode", (int)mode);
            PlayerPrefs.Save();
            if (mode == CoPresenceMode.VideoCall)
            {
                EnterVideoCall();
            }
            else if (mode == CoPresenceMode.ArReality)
            {
                EnterArReality();
            }
            else
            {
                EnterVirtualScene();
            }
            ModeChanged?.Invoke(mode);
        }

        public void SwitchEnvironment(VirtualEnvironment next)
        {
            if (next == environment)
            {
                return;
            }
            environment = next;
            PlayerPrefs.SetInt(PrefsKey + "env", (int)environment);
            PlayerPrefs.Save();
            if (mode == CoPresenceMode.VirtualScene)
            {
                ApplyEnvironmentVisuals();
            }
            EnvironmentChanged?.Invoke(environment);
        }

        // ───────────────────────── 虚拟场景（伪 AR）─────────────────────────

        private void EnterVirtualScene()
        {
            TeardownArBackground();
            RestoreOrbitState();
            if (mainCamera != null)
            {
                mainCamera.clearFlags = CameraClearFlags.SolidColor;
            }
            EnsureEnvironmentRoot(active: true);
            ApplyEnvironmentVisuals();
        }

        private void EnsureEnvironmentRoot(bool active)
        {
            if (environmentRoot == null)
            {
                environmentRoot = new GameObject("CoPresence Environment");
                environmentRoot.transform.SetParent(transform, false);

                var lightGo = new GameObject("Environment Light");
                lightGo.transform.SetParent(environmentRoot.transform, false);
                lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
                environmentLight = lightGo.AddComponent<Light>();
                environmentLight.type = LightType.Directional;
                environmentLight.intensity = 1f;

                var ambientGo = new GameObject("Key Light");
                ambientGo.transform.SetParent(environmentRoot.transform, false);
                ambientGo.transform.rotation = Quaternion.Euler(-20f, 150f, 0f);
                keyLight = ambientGo.AddComponent<Light>();
                keyLight.type = LightType.Directional;
                keyLight.intensity = 0.35f;

                var floor = GameObject.CreatePrimitive(PrimitiveType.Plane);
                floor.name = "Environment Floor";
                floor.transform.SetParent(environmentRoot.transform, false);
                floor.transform.localScale = new Vector3(12f, 1f, 12f);
                floor.transform.position = new Vector3(0f, GroundHeight - 0.02f, 0f);
                var floorCollider = floor.GetComponent<Collider>();
                if (floorCollider != null)
                {
                    Destroy(floorCollider);
                }
                floorMatInstance = CreateUnlitMaterial(Color.gray);
                floor.GetComponent<MeshRenderer>().sharedMaterial = floorMatInstance;
            }
            environmentRoot.SetActive(active);
        }

        private void ApplyEnvironmentVisuals()
        {
            Color skyTop, skyBottom, floorColor, lightColor;
            float lightIntensity, keyIntensity;
            switch (environment)
            {
                case VirtualEnvironment.StarrySky:
                    skyTop = new Color(0.05f, 0.055f, 0.14f);
                    skyBottom = new Color(0.10f, 0.11f, 0.22f);
                    floorColor = new Color(0.13f, 0.14f, 0.22f);
                    lightColor = new Color(0.75f, 0.80f, 1.00f);
                    lightIntensity = 0.9f;
                    keyIntensity = 0.3f;
                    break;
                case VirtualEnvironment.Bedroom:
                    skyTop = new Color(0.85f, 0.78f, 0.70f);
                    skyBottom = new Color(0.95f, 0.88f, 0.80f);
                    floorColor = new Color(0.55f, 0.42f, 0.34f);
                    lightColor = new Color(1.00f, 0.90f, 0.78f);
                    lightIntensity = 1.1f;
                    keyIntensity = 0.45f;
                    break;
                case VirtualEnvironment.Seaside:
                    skyTop = new Color(0.45f, 0.70f, 0.90f);
                    skyBottom = new Color(0.80f, 0.90f, 0.95f);
                    floorColor = new Color(0.85f, 0.82f, 0.74f);
                    lightColor = new Color(0.95f, 0.97f, 1.00f);
                    lightIntensity = 1.25f;
                    keyIntensity = 0.5f;
                    break;
                default: // NightStreet 夜街
                    skyTop = new Color(0.06f, 0.07f, 0.14f);
                    skyBottom = new Color(0.16f, 0.13f, 0.20f);
                    floorColor = new Color(0.20f, 0.20f, 0.26f);
                    lightColor = new Color(1.00f, 0.85f, 0.65f);
                    lightIntensity = 1.0f;
                    keyIntensity = 0.4f;
                    break;
            }
            var mid = Color.Lerp(skyTop, skyBottom, 0.5f);
            if (mainCamera != null)
            {
                mainCamera.backgroundColor = mid;
            }
            if (floorMatInstance != null)
            {
                SetUnlitColor(floorMatInstance, floorColor);
            }
            if (environmentLight != null)
            {
                environmentLight.color = lightColor;
                environmentLight.intensity = lightIntensity;
            }
            if (keyLight != null)
            {
                keyLight.color = lightColor;
                keyLight.intensity = keyIntensity;
            }
            RenderSettings.ambientLight = mid;
        }

        // ───────────────────────── 视频通话（半身）─────────────────────────

        private void EnterVideoCall()
        {
            TeardownArBackground();
            if (environmentRoot != null)
            {
                environmentRoot.SetActive(false);
            }
            if (mainCamera != null)
            {
                mainCamera.clearFlags = CameraClearFlags.SolidColor;
                mainCamera.backgroundColor = new Color(0.894f, 0.925f, 0.969f); // #E4ECF7
            }
            SaveOrbitState();
            if (orbitCamera != null)
            {
                // 胸像构图：轨道目标 = 角色所在水平位置的胸高（MMD 角色 y≈1.15m），
                // 距离 1.35m 产生半身特写；俯仰归零为平视通话感。
                var focus = avatarRoot != null
                    ? new Vector3(avatarRoot.position.x, VideoCallBustHeight, avatarRoot.position.z)
                    : new Vector3(0f, VideoCallBustHeight, 0f);
                orbitCamera.SetOrbitTarget(focus);
                orbitCamera.OrbitPitchAngle = 0f;
                orbitCamera.OrbitDistance = VideoCallBustDistance;
            }
            callStartedAt = Time.unscaledTime;
        }

        private void ExitVideoCall()
        {
            callStartedAt = -1f;
        }

        // ───────────────────────── 同框现实（真 AR L1）─────────────────────────

        private bool DetectRearCamera()
        {
            try
            {
                var devices = WebCamTexture.devices;
                if (devices == null || devices.Length == 0)
                {
                    return false;
                }
                foreach (var device in devices)
                {
                    if (!device.isFrontFacing)
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                return false;
            }
            return false;
        }

        private void EnterArReality()
        {
            if (environmentRoot != null)
            {
                environmentRoot.SetActive(false);
            }
            if (mainCamera != null)
            {
                mainCamera.clearFlags = CameraClearFlags.SolidColor;
                mainCamera.backgroundColor = Color.black;
            }
            SaveOrbitState();
            if (!arRoutineRunning)
            {
                StartCoroutine(StartArBackgroundRoutine());
            }
        }

        private IEnumerator StartArBackgroundRoutine()
        {
            arRoutineRunning = true;
            try
            {
                var permissionTask = PhoneRealityCameraSnapshot.EnsureCameraPermissionAsync();
                while (!permissionTask.IsCompleted)
                {
                    yield return null;
                }
                if (permissionTask.IsFaulted || permissionTask.Result != null)
                {
                    FallbackToVirtualScene();
                    yield break;
                }
                string deviceName = null;
                try
                {
                    var devices = WebCamTexture.devices;
                    foreach (var device in devices)
                    {
                        if (!device.isFrontFacing)
                        {
                            deviceName = device.name;
                            break;
                        }
                    }
                }
                catch (Exception)
                {
                    deviceName = null;
                }
                if (string.IsNullOrEmpty(deviceName))
                {
                    FallbackToVirtualScene();
                    yield break;
                }
                if (arWebCam == null)
                {
                    arWebCam = new WebCamTexture(deviceName, 1280, 720, 30);
                }
                if (!arWebCam.isPlaying)
                {
                    arWebCam.Play();
                }
                var waitUntil = Time.realtimeSinceStartup + 5f;
                while (arWebCam.isPlaying && Time.realtimeSinceStartup < waitUntil
                    && (arWebCam.width <= 16 || arWebCam.height <= 16))
                {
                    yield return null;
                }
                if (!arWebCam.isPlaying)
                {
                    FallbackToVirtualScene();
                    yield break;
                }
                BuildArBackgroundQuad();
            }
            finally
            {
                arRoutineRunning = false;
            }
        }

        private void FallbackToVirtualScene()
        {
            mode = CoPresenceMode.VirtualScene;
            PlayerPrefs.SetInt(PrefsKey + "mode", (int)mode);
            PlayerPrefs.Save();
            EnterVirtualScene();
            ModeChanged?.Invoke(mode);
        }

        private void BuildArBackgroundQuad()
        {
            if (arBackgroundRoot != null)
            {
                arBackgroundRoot.SetActive(true);
                return;
            }
            arBackgroundRoot = new GameObject("AR Background");
            arBackgroundRoot.transform.SetParent(transform, false);
            var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "Camera Feed";
            quad.transform.SetParent(arBackgroundRoot.transform, false);
            var quadCollider = quad.GetComponent<Collider>();
            if (quadCollider != null)
            {
                Destroy(quadCollider);
            }
            arBackgroundMaterial = CreateUnlitMaterial(Color.white);
            if (arBackgroundMaterial.HasProperty("_BaseMap"))
            {
                arBackgroundMaterial.SetTexture("_BaseMap", arWebCam);
            }
            else
            {
                arBackgroundMaterial.mainTexture = arWebCam;
            }
            quad.GetComponent<MeshRenderer>().sharedMaterial = arBackgroundMaterial;
            SyncArBackgroundTransform();
        }

        private void SyncArBackgroundTransform()
        {
            if (arBackgroundRoot == null || mainCamera == null)
            {
                return;
            }
            var quad = arBackgroundRoot.transform.GetChild(0);
            var camT = mainCamera.transform;
            quad.position = camT.position + camT.forward * ArBackgroundDepth;
            quad.rotation = Quaternion.LookRotation(-camT.forward, camT.up);
            // 手机竖屏：相机纹理通常带 90/270 旋转，需绕法线回正
            float rotation = arWebCam != null ? arWebCam.videoRotationAngle : 0f;
            if (Mathf.Abs(rotation) > 0.5f)
            {
                quad.Rotate(Vector3.forward, -rotation, Space.Self);
            }
            float texAspect = (arWebCam != null && arWebCam.width > 0 && arWebCam.height > 0)
                ? (float)arWebCam.width / arWebCam.height
                : 16f / 9f;
            bool swapped = Mathf.Abs(Mathf.Abs(rotation) - 90f) < 0.5f
                || Mathf.Abs(Mathf.Abs(rotation) - 270f) < 0.5f;
            if (swapped)
            {
                texAspect = 1f / texAspect;
            }
            float frustumHeight = 2f * ArBackgroundDepth
                * Mathf.Tan(mainCamera.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float frustumWidth = frustumHeight * mainCamera.aspect;
            // cover 语义：铺满视野取较大缩放
            float scale = Mathf.Max(frustumHeight, frustumWidth / texAspect);
            quad.localScale = new Vector3(scale * texAspect, scale, 1f);
        }

        /// <summary>URP 优先的 Unlit 材质（项目为 URP，内置 Unlit/Color 不被渲染）。</summary>
        private static Material CreateUnlitMaterial(Color color)
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit");
            Material material;
            if (shader != null)
            {
                material = new Material(shader);
                if (material.HasProperty("_BaseColor"))
                {
                    material.SetColor("_BaseColor", color);
                }
                else
                {
                    material.color = color;
                }
                // URP Unlit 默认可能受雾影响；关闭保持纯色。
                if (material.HasProperty("_Surface"))
                {
                    material.SetFloat("_Surface", 0f);
                }
            }
            else
            {
                material = new Material(Shader.Find("Unlit/Color")) { color = color };
            }
            return material;
        }

        private static void SetUnlitColor(Material material, Color color)
        {
            if (material == null)
            {
                return;
            }
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }
            else
            {
                material.color = color;
            }
        }

        private void TeardownArBackground()
        {
            if (arWebCam != null && arWebCam.isPlaying)
            {
                arWebCam.Stop();
            }
            if (arBackgroundRoot != null)
            {
                arBackgroundRoot.SetActive(false);
            }
        }

        private void TeardownArCompletely()
        {
            TeardownArBackground();
            arWebCam = null;
            if (arBackgroundRoot != null)
            {
                Destroy(arBackgroundRoot);
                arBackgroundRoot = null;
            }
            if (arBackgroundMaterial != null)
            {
                Destroy(arBackgroundMaterial);
                arBackgroundMaterial = null;
            }
        }

        // ───────────────────────── AR 点地放置 ─────────────────────────

        /// <summary>点按屏幕放置/移动角色（真 AR L1 手动放置）。返回是否命中地面。</summary>
        public bool PlaceAvatarAtScreenPoint(Vector2 screenPos)
        {
            if (mode != CoPresenceMode.ArReality || mainCamera == null || avatarRoot == null)
            {
                return false;
            }
            var ray = mainCamera.ScreenPointToRay(new Vector3(screenPos.x, screenPos.y, 0f));
            var groundPlane = new Plane(Vector3.up, new Vector3(0f, GroundHeight, 0f));
            if (!groundPlane.Raycast(ray, out var enter))
            {
                return false;
            }
            var hit = ray.GetPoint(enter);
            avatarRoot.position = new Vector3(hit.x, GroundHeight, hit.z);
            return true;
        }

        public void Teardown()
        {
            TeardownArCompletely();
            if (environmentRoot != null)
            {
                Destroy(environmentRoot);
                environmentRoot = null;
            }
            if (floorMatInstance != null)
            {
                Destroy(floorMatInstance);
                floorMatInstance = null;
            }
            RestoreOrbitState();
        }

        // ───────────────────────── 轨道相机状态 ─────────────────────────

        private void SaveOrbitState()
        {
            if (orbitCamera == null || savedOrbitState)
            {
                return;
            }
            savedOrbitTarget = orbitCamera.OrbitTargetPoint;
            savedOrbitDistance = orbitCamera.OrbitDistance;
            savedOrbitYaw = orbitCamera.OrbitYawAngle;
            savedOrbitPitch = orbitCamera.OrbitPitchAngle;
            savedOrbitState = true;
        }

        private void RestoreOrbitState()
        {
            if (orbitCamera == null || !savedOrbitState)
            {
                return;
            }
            orbitCamera.SetOrbitTarget(savedOrbitTarget);
            orbitCamera.OrbitDistance = savedOrbitDistance;
            orbitCamera.OrbitYawAngle = savedOrbitYaw;
            orbitCamera.OrbitPitchAngle = savedOrbitPitch;
            savedOrbitState = false;
        }

        private void Update()
        {
            if (ArActive)
            {
                SyncArBackgroundTransform();
            }
        }

        private void OnDestroy()
        {
            TeardownArCompletely();
        }
    }
}

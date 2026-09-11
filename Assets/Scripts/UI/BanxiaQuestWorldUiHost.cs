using System;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;
using UnityEngine.XR;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Quest 世界空间 UI Toolkit 宿主：复用手机端 BanxiaShell/BanxiaTheme，
    /// 通过 PanelSettings.targetTexture 输出到 RenderTexture，再贴到世界空间面板。
    /// 旧 CompanionWorldMenu 继续承载 Quest 独占硬件入口；此宿主承载双端共通 UI。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class BanxiaQuestWorldUiHost : MonoBehaviour
    {
        [SerializeField] private int textureWidth = 1080;
        [SerializeField] private int textureHeight = 1920;
        [SerializeField] private float panelHeightMeters = 0.95f;
        [SerializeField] private float distanceFromHead = 1.05f;
        [SerializeField] private float verticalOffset = -0.03f;
        [SerializeField] private float pointerLength = 3.5f;
        [SerializeField] private float triggerThreshold = 0.65f;

        private readonly PointerState leftPointer = new PointerState(XRNode.LeftHand);
        private readonly PointerState rightPointer = new PointerState(XRNode.RightHand);

        private QuestMmdPlayerBootstrap owner;
        private GameObject panelRoot;
        private Transform panelSurface;
        private Collider panelCollider;
        private RenderTexture renderTexture;
        private PanelSettings panelSettings;
        private UIDocument document;
        private BanxiaUiShell shell;
        private Material panelMaterial;
        private Material pointerMaterial;
        private Transform trackingSpace;
        private const float InitializationTimeoutSeconds = 8f;

        private bool initializationFailed;
        private bool initializationFailureLogged;
        private bool showWhenReady;
        private float initializationStartedAt;
        private string initializationDiagnostic = "未初始化";

        public bool IsOpen => panelRoot != null && panelRoot.activeSelf;
        public bool RenderTextureCreated => renderTexture != null && renderTexture.IsCreated();
        public bool HasTargetTexture => panelSettings != null && panelSettings.targetTexture == renderTexture;
        public bool HasRootVisualElement => document != null && document.rootVisualElement != null;
        public bool ShellBuilt => shell != null && shell.IsBuilt;
        public bool InitializationFailed => initializationFailed || (shell != null && shell.BuildFailed);
        public bool IsPending => !InitializationFailed && panelRoot != null && !IsHealthy;
        public bool IsHealthy => !InitializationFailed &&
                                  RenderTextureCreated &&
                                  HasTargetTexture &&
                                  HasRootVisualElement &&
                                  ShellBuilt && panelRoot != null &&
                                  panelSurface != null && panelCollider != null &&
                                  panelMaterial != null && panelMaterial.shader != null &&
                                  pointerMaterial != null && pointerMaterial.shader != null &&
                                  leftPointer.Line != null && rightPointer.Line != null;
        public string Diagnostic => BuildDiagnostic();

        public bool Initialize(QuestMmdPlayerBootstrap bootstrap)
        {
            owner = bootstrap;
            initializationStartedAt = Time.realtimeSinceStartup;
            showWhenReady = true;
            if (!EnsurePanel())
            {
                return false;
            }

            Hide(cancelPendingOpen: false);
            initializationDiagnostic = IsHealthy ? "healthy/ready" : "waiting-for-ui-shell";
            Debug.Log("[BanxiaWorldUi] Created; waiting for the shared UiShell before opening the Quest default entry.", this);
            return !InitializationFailed;
        }

        private void Update()
        {
            // QA/无控制器自动化：F2 可直接开关新 UI。
            // 当前项目使用 Input System 包，必须走 Keyboard API，否则每帧抛异常。
            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard.f2Key.wasPressedThisFrame)
            {
                Toggle();
            }
            if (keyboard != null && keyboard.f3Key.wasPressedThisFrame && IsOpen)
            {
                CapturePanelToPngForQa();
            }

            if (InitializationFailed)
            {
                if (!initializationFailed)
                {
                    FailInitialization("UiShell build failed: " + (shell?.BuildFailureDiagnostic ?? "unknown"));
                    owner?.HandleWorldUiFailure("Quest WorldUi 构建失败：" + Diagnostic);
                }
                return;
            }

            if (!IsHealthy)
            {
                if (showWhenReady && Time.realtimeSinceStartup - initializationStartedAt >= InitializationTimeoutSeconds)
                {
                    FailInitialization("UiShell readiness timed out after " + InitializationTimeoutSeconds + "s");
                    owner?.HandleWorldUiFailure("Quest WorldUi 初始化超时：" + Diagnostic);
                }
                return;
            }

            if (showWhenReady)
            {
                showWhenReady = false;
                if (owner != null)
                {
                    owner.OpenWorldUi();
                }
                else
                {
                    ShowInFront();
                }
            }

            if (!IsOpen)
            {
                return;
            }
            trackingSpace = QuestXrInputUtility.ResolveTrackingSpace(trackingSpace);
            UpdatePointer(leftPointer);
            UpdatePointer(rightPointer);
        }

        private void OnDestroy()
        {
            DisposePartial();
        }

        public void RequestCloseFromUser()
        {
            if (shell != null && shell.IsBuilt)
            {
                shell.RequestWorldSpaceClose();
                return;
            }
            Hide();
        }

        public void Toggle()
        {
            if (owner != null)
            {
                owner.ToggleWorldUi();
                return;
            }

            if (IsOpen)
            {
                Hide();
            }
            else
            {
                ShowInFront();
            }
        }

        public void ShowInFront()
        {
            try
            {
                if (!EnsurePanel())
                {
                    return;
                }
                if (InitializationFailed)
                {
                    FailInitialization("ShowInFront rejected after initialization failure");
                    return;
                }
                if (!IsHealthy)
                {
                    showWhenReady = true;
                    initializationDiagnostic = "show-requested/waiting-for-ui-shell";
                    return;
                }

                showWhenReady = false;
                owner?.HideLegacyMenuForWorldUi();
                PositionInFrontOfHead();
                panelRoot.SetActive(true);
                initializationDiagnostic = "healthy/open";
            }
            catch (Exception exception)
            {
                FailInitialization("ShowInFront exception: " + exception.GetType().Name + ": " + exception.Message, exception);
            }
        }

        public void Hide()
        {
            Hide(cancelPendingOpen: true);
        }

        private void Hide(bool cancelPendingOpen)
        {
            if (cancelPendingOpen)
            {
                showWhenReady = false;
            }
            if (panelRoot != null)
            {
                panelRoot.SetActive(false);
            }
            if (leftPointer.Line != null)
            {
                leftPointer.Line.enabled = false;
            }
            if (rightPointer.Line != null)
            {
                rightPointer.Line.enabled = false;
            }
            leftPointer.PreviousSelect = false;
            rightPointer.PreviousSelect = false;
        }

        public string CapturePanelToPngForQa()
        {
            if (!IsHealthy || !IsOpen)
            {
                WriteQaMarker("NULL:not-healthy|" + Diagnostic);
                return null;
            }

            var previousActive = RenderTexture.active;
            var texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
                texture.Apply(false, false);
                var path = Path.Combine(Application.persistentDataPath, "banxia-world-ui-qa.png");
                File.WriteAllBytes(path, texture.EncodeToPNG());
                WriteQaMarker(path + "|built=" + (shell != null && shell.IsBuilt));
                return path;
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "world-ui.qa.capture");
                QuestDebugMode.RethrowIfEnabled(exception, "world-ui.qa.capture");
                WriteQaMarker("NULL:" + exception.GetType().Name + ":" + exception.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previousActive;
                Destroy(texture);
            }
        }

        private static void WriteQaMarker(string content)
        {
            try
            {
                File.WriteAllText(Path.Combine(Application.persistentDataPath, "banxia-world-ui-qa.txt"), content);
            }
            catch (Exception exception)
            {
                QuestDebugMode.Report(exception, "world-ui.qa.marker");
                // QA marker is best-effort only; never break the runtime UI.
            }
        }

        private bool EnsurePanel()
        {
            if (panelRoot != null)
            {
                return !InitializationFailed;
            }
            if (initializationFailed)
            {
                return false;
            }

            try
            {
                renderTexture = new RenderTexture(textureWidth, textureHeight, 24, RenderTextureFormat.ARGB32)
                {
                    name = "BanxiaWorldUiTexture",
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                };
                renderTexture.Create();
                if (!renderTexture.IsCreated())
                {
                    throw new InvalidOperationException("RenderTexture.Create returned without a created texture.");
                }

                panelRoot = new GameObject("Banxia World UI Toolkit Panel");
                panelRoot.transform.SetParent(transform, false);

                var documentObject = new GameObject("UIDocument");
                documentObject.transform.SetParent(panelRoot.transform, false);
                document = documentObject.AddComponent<UIDocument>();
                panelSettings = CreatePanelSettings(renderTexture);
                document.panelSettings = panelSettings;
                shell = documentObject.AddComponent<BanxiaUiShell>();
                shell.ConfigureWorldSpace(
                    owner == null ? (Action)Hide : owner.HandleWorldUiClosed,
                    owner == null ? (Action)Hide : owner.HandleWorldUiSceneEntered);
                shell.Bind(owner, owner?.ModelLoader, owner?.FileImport, owner?.DebugLog);

                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = "PanelSurface";
                quad.transform.SetParent(panelRoot.transform, false);
                panelSurface = quad.transform;
                var widthMeters = panelHeightMeters * textureWidth / textureHeight;
                panelSurface.localScale = new Vector3(widthMeters, panelHeightMeters, 1f);
                panelSurface.localPosition = Vector3.zero;
                panelSurface.localRotation = Quaternion.identity;
                panelMaterial = CreatePanelMaterial(renderTexture);
                var renderer = quad.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = panelMaterial;
                panelCollider = quad.GetComponent<Collider>();
                if (panelCollider == null)
                {
                    panelCollider = quad.AddComponent<BoxCollider>();
                }

                var pointerShader = Shader.Find("Sprites/Default");
                if (pointerShader == null)
                {
                    throw new InvalidOperationException("Sprites/Default shader is unavailable for the Quest pointer.");
                }
                pointerMaterial = new Material(pointerShader);
                pointerMaterial.color = new Color(0.43f, 0.76f, 1f, 0.9f);
                CreatePointerLine(leftPointer, "LeftPointer");
                CreatePointerLine(rightPointer, "RightPointer");
                initializationDiagnostic = IsHealthy ? "healthy/created" : "waiting-for-ui-shell";
                return true;
            }
            catch (Exception exception)
            {
                FailInitialization("EnsurePanel exception: " + exception.GetType().Name + ": " + exception.Message, exception);
                return false;
            }
        }

        private static PanelSettings CreatePanelSettings(RenderTexture texture)
        {
            var shared = Resources.Load<PanelSettings>("BanxiaPanelSettings");
            var settings = shared != null
                ? Instantiate(shared)
                : ScriptableObject.CreateInstance<PanelSettings>();
            settings.targetTexture = texture;
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.referenceResolution = new Vector2Int(texture.width, texture.height);
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 0.5f;
            // 世界空间面板必须有稳定底色；否则空 RT 会透明并继续遮挡射线。
            settings.clearColor = true;
            settings.colorClearValue = new Color(0.949f, 0.949f, 0.969f, 1f); /* #F2F2F7 */
            return settings;
        }

        private string BuildDiagnostic()
        {
            var shellFailure = shell == null ? string.Empty : shell.BuildFailureDiagnostic;
            return "failed=" + InitializationFailed +
                   " pending=" + IsPending +
                   " rt=" + RenderTextureCreated +
                   " target=" + HasTargetTexture +
                   " root=" + HasRootVisualElement +
                   " shell=" + ShellBuilt +
                   " panel=" + (panelRoot != null) +
                   " surface=" + (panelSurface != null) +
                   " collider=" + (panelCollider != null) +
                   " state=" + initializationDiagnostic +
                   (string.IsNullOrEmpty(shellFailure) ? string.Empty : " shellFailure=" + shellFailure);
        }

        private void FailInitialization(string reason, Exception exception = null)
        {
            initializationFailed = true;
            initializationDiagnostic = reason;
            var diagnostic = BuildDiagnostic();
            if (exception != null)
            {
                QuestDebugMode.Report(exception, "world-ui.initialize");
            }
            if (!initializationFailureLogged)
            {
                initializationFailureLogged = true;
                Debug.LogWarning("[BanxiaWorldUi] " + reason + " | " + diagnostic, this);
                WriteQaMarker("NULL:" + reason + "|" + diagnostic);
            }
            DisposePartial();
            if (exception != null)
            {
                QuestDebugMode.RethrowIfEnabled(exception, "world-ui.initialize");
            }
        }

        private void DisposePartial()
        {
            if (panelRoot != null)
            {
                Destroy(panelRoot);
            }
            panelRoot = null;
            panelSurface = null;
            panelCollider = null;
            document = null;
            shell = null;

            if (renderTexture != null)
            {
                renderTexture.Release();
                Destroy(renderTexture);
            }
            renderTexture = null;
            if (panelSettings != null)
            {
                Destroy(panelSettings);
            }
            panelSettings = null;
            if (panelMaterial != null)
            {
                Destroy(panelMaterial);
            }
            panelMaterial = null;
            if (pointerMaterial != null)
            {
                Destroy(pointerMaterial);
            }
            pointerMaterial = null;
            leftPointer.Line = null;
            rightPointer.Line = null;
            leftPointer.PreviousSelect = false;
            rightPointer.PreviousSelect = false;
        }

        private static Material CreatePanelMaterial(Texture texture)
        {
            // 优先用常驻材质资源，避免 build stripping 掉运行时 Shader.Find 的 Shader。
            var material = Resources.Load<Material>("BanxiaPanelMaterial");
            material = material != null ? UnityEngine.Object.Instantiate(material) : null;
            if (material == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Texture");
                if (shader == null)
                {
                    throw new InvalidOperationException("No supported shader is available for the Quest UI surface.");
                }
                material = new Material(shader);
            }
            if (material.shader == null)
            {
                throw new InvalidOperationException("Quest UI surface material has no shader.");
            }
            if (material.HasProperty("_BaseMap"))
            {
                material.SetTexture("_BaseMap", texture);
            }
            else
            {
                material.mainTexture = texture;
            }
            if (material.HasProperty("_Cull"))
            {
                material.SetFloat("_Cull", 0f);
            }
            // 用不透明面板承载 UI，保证 UI Toolkit 没画出来时也不会变成隐形遮挡板。
            if (material.HasProperty("_Surface"))
            {
                material.SetFloat("_Surface", 0f);
            }
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", Color.white);
            }
            return material;
        }

        private void CreatePointerLine(PointerState pointer, string name)
        {
            var lineObject = new GameObject(name);
            lineObject.transform.SetParent(panelRoot.transform, false);
            pointer.Line = lineObject.AddComponent<LineRenderer>();
            pointer.Line.sharedMaterial = pointerMaterial;
            pointer.Line.positionCount = 2;
            pointer.Line.startWidth = 0.006f;
            pointer.Line.endWidth = 0.002f;
            pointer.Line.enabled = false;
        }

        private void PositionInFrontOfHead()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                panelRoot.transform.position = new Vector3(0f, 1.45f, distanceFromHead);
                panelRoot.transform.rotation = Quaternion.identity;
                return;
            }
            var cameraTransform = camera.transform;
            panelRoot.transform.position = cameraTransform.position +
                                           cameraTransform.forward * distanceFromHead +
                                           Vector3.up * verticalOffset;
            // Unity Quad 的正面是本地 +Z；让 +Z 朝向头显。
            panelRoot.transform.rotation = Quaternion.LookRotation(-cameraTransform.forward, Vector3.up);
        }

        private void UpdatePointer(PointerState pointer)
        {
            if (!TryGetPointerPose(pointer.Node, out var pose, out var select))
            {
                pointer.Line.enabled = false;
                pointer.PreviousSelect = false;
                return;
            }

            var ray = new Ray(pose.position, pose.rotation * Vector3.forward);
            RaycastHit hit = default;
            var hitPanel = panelCollider != null && panelCollider.Raycast(ray, out hit, pointerLength);
            var end = hitPanel ? hit.point : ray.origin + ray.direction * pointerLength;
            pointer.Line.enabled = true;
            pointer.Line.SetPosition(0, ray.origin);
            pointer.Line.SetPosition(1, end);
            pointer.Line.startColor = hitPanel ? new Color(0.43f, 0.76f, 1f, 1f) : new Color(0.43f, 0.76f, 1f, 0.3f);
            pointer.Line.endColor = pointer.Line.startColor;

            if (hitPanel && select && !pointer.PreviousSelect)
            {
                SendClick(hit.point);
                SendHaptic(pointer.Node);
            }
            pointer.PreviousSelect = select;
        }

        private bool TryGetPointerPose(XRNode node, out Pose pose, out bool select)
        {
            if (QuestXrInputUtility.TryGetTrackedHandPointer(node, trackingSpace, out pose, out var pinch))
            {
                select = pinch;
                return true;
            }
            if (QuestXrInputUtility.TryGetWorldPose(node, trackingSpace, out pose))
            {
                select = ReadSelect(InputDevices.GetDeviceAtXRNode(node));
                return true;
            }
            select = false;
            return false;
        }

        private bool ReadSelect(UnityEngine.XR.InputDevice device)
        {
            if (!device.isValid)
            {
                return false;
            }
            if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.triggerButton, out var pressed) && pressed)
            {
                return true;
            }
            return device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trigger, out var trigger) && trigger >= triggerThreshold;
        }

        private void SendClick(Vector3 worldPoint)
        {
            var root = document == null ? null : document.rootVisualElement;
            if (root?.panel == null || panelSurface == null)
            {
                return;
            }
            var local = panelSurface.InverseTransformPoint(worldPoint);
            var panelPosition = new Vector2(
                Mathf.Clamp01(local.x + 0.5f) * textureWidth,
                Mathf.Clamp01(0.5f - local.y) * textureHeight);
            var target = root.panel.Pick(panelPosition) ?? root;
            using (var click = ClickEvent.GetPooled())
            {
                target.SendEvent(click);
            }
        }

        private static void SendHaptic(XRNode node)
        {
            var device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid)
            {
                device.SendHapticImpulse(0u, 0.25f, 0.04f);
            }
        }

        private sealed class PointerState
        {
            internal PointerState(XRNode node)
            {
                Node = node;
            }

            internal XRNode Node { get; }
            internal LineRenderer Line { get; set; }
            internal bool PreviousSelect { get; set; }
        }
    }
}

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace QuestMmdPlayer
{
    public enum PassthroughState
    {
        Unavailable,
        Disabled,
        Enabled
    }

    public sealed class PassthroughFacade : MonoBehaviour
    {
        [SerializeField] private bool requestOnStart = true;

        private IPassthroughProvider provider;
        private ARCameraManager cameraManager;
        private ARSession arSession;
        private bool? lastCameraRunning;
        private float nextStatusLogTime;
        private Coroutine restartRoutine;
        private bool requestedEnabled;

        public event Action<PassthroughState> StateChanged;
        public PassthroughState State { get; private set; } = PassthroughState.Unavailable;
        public string Status { get; private set; } = "Meta XR SDK not installed";
        public bool IsCameraSubsystemRunning =>
            cameraManager != null && cameraManager.subsystem != null && cameraManager.subsystem.running;

        private void Awake()
        {
            EnsureCameraConfiguration();
            arSession = GetComponent<ARSession>() ?? gameObject.AddComponent<ARSession>();
            arSession.enabled = true;

#if UNITY_ANDROID && !UNITY_EDITOR
            provider = cameraManager == null
                ? new EditorPassthroughProvider("Quest camera is unavailable")
                : new QuestPassthroughProvider(cameraManager);
#else
            provider = new EditorPassthroughProvider();
#endif
            SetEnabled(requestOnStart);
        }

        private void Update()
        {
            if (State != PassthroughState.Enabled || cameraManager == null)
            {
                return;
            }

            EnsureCameraConfiguration();
            var running = IsCameraSubsystemRunning;
            Status = running ? "Enabled (Quest camera running)" : "Starting Quest camera";
            if (lastCameraRunning == running && Time.unscaledTime < nextStatusLogTime)
            {
                return;
            }

            lastCameraRunning = running;
            nextStatusLogTime = Time.unscaledTime + 5f;
            var camera = cameraManager.GetComponent<Camera>();
            Debug.Log(
                $"[Passthrough] requested=on; subsystem={(running ? "running" : "waiting")}; " +
                $"cameraManager={cameraManager.enabled}; clear={camera?.clearFlags}; alpha={camera?.backgroundColor.a:F2}.",
                this);
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus && requestOnStart && State == PassthroughState.Enabled)
            {
                SetEnabled(true);
            }
        }

        private void EnsureCameraConfiguration()
        {
            var camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            camera.clearFlags = CameraClearFlags.SolidColor;
            var color = camera.backgroundColor;
            color.a = 0f;
            camera.backgroundColor = color;
            cameraManager = camera.GetComponent<ARCameraManager>() ?? camera.gameObject.AddComponent<ARCameraManager>();

            // Meta composites Passthrough behind Unity content and does not use ARCameraBackground.
            var cameraBackground = camera.GetComponent<ARCameraBackground>();
            if (cameraBackground != null)
            {
                cameraBackground.enabled = false;
            }
        }

        public void SetProvider(IPassthroughProvider nextProvider)
        {
            provider = nextProvider ?? new EditorPassthroughProvider();
            SetEnabled(requestOnStart);
        }

        public void SetEnabled(bool enabled)
        {
            EnsureCameraConfiguration();
            requestedEnabled = enabled;
            if (restartRoutine != null)
            {
                StopCoroutine(restartRoutine);
                restartRoutine = null;
            }

            if (arSession != null && !arSession.enabled)
            {
                arSession.enabled = true;
            }

            if (provider == null)
            {
                provider = new EditorPassthroughProvider();
            }

            if (!provider.IsAvailable)
            {
                State = PassthroughState.Unavailable;
                Status = provider.UnavailableReason;
            }
            else if (!enabled)
            {
                provider.SetEnabled(false);
                State = PassthroughState.Disabled;
                Status = "Disabled";
            }
            else
            {
                // Meta Quest can leave the passthrough stream in a stale mux state
                // when ARCameraManager is toggled in the same frame. Restart it over
                // two frames so the camera service receives a real stop/start pair.
                State = PassthroughState.Enabled;
                Status = "Restarting Quest camera";
                restartRoutine = StartCoroutine(RestartCameraAfterToggle());
            }

            lastCameraRunning = null;
            nextStatusLogTime = 0f;
            StateChanged?.Invoke(State);
        }

        private IEnumerator RestartCameraAfterToggle()
        {
            provider.SetEnabled(false);
            if (cameraManager != null)
            {
                cameraManager.enabled = false;
            }
            yield return null;
            yield return new WaitForSecondsRealtime(.06f);
            if (!requestedEnabled)
            {
                restartRoutine = null;
                yield break;
            }

            if (arSession != null)
            {
                arSession.enabled = false;
            }
            yield return null;
            if (arSession != null)
            {
                arSession.enabled = true;
            }
            if (cameraManager != null)
            {
                cameraManager.enabled = true;
            }
            provider.SetEnabled(true);
            Status = IsCameraSubsystemRunning ? "Enabled (Quest camera running)" : "Starting Quest camera";
            restartRoutine = null;
            StateChanged?.Invoke(State);
        }

        public void Toggle()
        {
            SetEnabled(State != PassthroughState.Enabled || !requestedEnabled);
        }

        private void OnDisable()
        {
            requestedEnabled = false;
            if (restartRoutine != null)
            {
                StopCoroutine(restartRoutine);
                restartRoutine = null;
            }
        }
    }

    public interface IPassthroughProvider
    {
        bool IsAvailable { get; }
        string UnavailableReason { get; }
        bool SetEnabled(bool enabled);
    }

    internal sealed class QuestPassthroughProvider : IPassthroughProvider
    {
        private readonly ARCameraManager cameraManager;

        public QuestPassthroughProvider(ARCameraManager cameraManager)
        {
            this.cameraManager = cameraManager;
        }

        public bool IsAvailable => cameraManager != null;
        public string UnavailableReason => "Quest AR camera manager is unavailable";

        public bool SetEnabled(bool enabled)
        {
            if (cameraManager == null) return false;
            cameraManager.enabled = enabled;
            return cameraManager.enabled == enabled;
        }
    }

    internal sealed class EditorPassthroughProvider : IPassthroughProvider
    {
        private readonly string reason;

        public EditorPassthroughProvider(string reason = null)
        {
            this.reason = reason;
        }

        public bool IsAvailable => false;
        public string UnavailableReason => reason ?? "Passthrough is available only on a Meta Quest build";

        public bool SetEnabled(bool enabled)
        {
            return false;
        }
    }
}
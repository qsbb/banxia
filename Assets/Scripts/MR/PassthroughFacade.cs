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

    public enum PassthroughLifecycleAction
    {
        None,
        Suspend,
        Restore
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
        private bool requestApplied;
        private bool applicationPaused;
        private bool applicationFocused = true;
        private bool suspendedForLifecycle;
        private bool providerEnableFailed;

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
            if (State != PassthroughState.Enabled || cameraManager == null ||
                suspendedForLifecycle || providerEnableFailed)
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
            applicationFocused = hasFocus;
            ApplyLifecycleState(hasFocus ? "focus restored" : "focus lost");
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            applicationPaused = pauseStatus;
            ApplyLifecycleState(pauseStatus ? "application paused" : "application resumed");
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
            ApplyEnabled(requestApplied ? requestedEnabled : requestOnStart, true, "provider changed");
        }

        public void SetEnabled(bool enabled)
        {
            ApplyEnabled(enabled, false, "user request");
        }

        private void ApplyEnabled(bool enabled, bool forceRestart, string reason)
        {
            EnsureCameraConfiguration();
            var sameRequest = requestApplied && requestedEnabled == enabled;
            requestedEnabled = enabled;
            requestApplied = true;

            if (sameRequest && !forceRestart && !suspendedForLifecycle &&
                ((enabled && (restartRoutine != null || State == PassthroughState.Enabled)) ||
                 (!enabled && State == PassthroughState.Disabled)))
            {
                return;
            }

            CancelRestart();

            if (enabled && (applicationPaused || !applicationFocused))
            {
                suspendedForLifecycle = true;
                SetState(PassthroughState.Enabled, "Waiting for application focus to restore passthrough");
                return;
            }

            suspendedForLifecycle = false;
            providerEnableFailed = false;
            if (enabled && arSession != null && !arSession.enabled)
            {
                arSession.enabled = true;
            }

            if (provider == null)
            {
                provider = new EditorPassthroughProvider();
            }

            if (!provider.IsAvailable)
            {
                SetState(PassthroughState.Unavailable, provider.UnavailableReason);
            }
            else if (!enabled)
            {
                var disabled = provider.SetEnabled(false);
                SetState(
                    PassthroughState.Disabled,
                    disabled ? "Disabled" : "Disabled (provider did not confirm shutdown)");
                if (!disabled)
                {
                    Debug.LogWarning($"[Passthrough] Provider shutdown was not confirmed ({reason}).", this);
                }
            }
            else
            {
                // Meta Quest can leave the passthrough stream in a stale mux state
                // when ARCameraManager is toggled in the same frame. Restart it over
                // two frames so the camera service receives a real stop/start pair.
                SetState(PassthroughState.Enabled, "Restarting Quest camera");
                restartRoutine = StartCoroutine(RestartCameraAfterToggle());
            }

            lastCameraRunning = null;
            nextStatusLogTime = 0f;
        }

        public static PassthroughLifecycleAction DecideLifecycleAction(
            bool requestedEnabled,
            bool applicationPaused,
            bool applicationFocused,
            bool suspendedForLifecycle)
        {
            var applicationInactive = applicationPaused || !applicationFocused;
            if (requestedEnabled && applicationInactive && !suspendedForLifecycle)
            {
                return PassthroughLifecycleAction.Suspend;
            }

            if (requestedEnabled && !applicationInactive && suspendedForLifecycle)
            {
                return PassthroughLifecycleAction.Restore;
            }

            return PassthroughLifecycleAction.None;
        }

        private void ApplyLifecycleState(string reason)
        {
            switch (DecideLifecycleAction(
                        requestedEnabled,
                        applicationPaused,
                        applicationFocused,
                        suspendedForLifecycle))
            {
                case PassthroughLifecycleAction.Suspend:
                    SuspendForLifecycle(reason);
                    break;
                case PassthroughLifecycleAction.Restore:
                    suspendedForLifecycle = false;
                    ApplyEnabled(true, true, reason);
                    break;
            }
        }

        private void SuspendForLifecycle(string reason)
        {
            suspendedForLifecycle = true;
            CancelRestart();

            var providerStopped = provider == null || !provider.IsAvailable || provider.SetEnabled(false);
            if (cameraManager != null)
            {
                cameraManager.enabled = false;
            }

            var status = providerStopped
                ? "Passthrough suspended while application is inactive"
                : "Passthrough suspend was not confirmed; restore will retry";
            SetState(PassthroughState.Enabled, status);
            if (!providerStopped)
            {
                Debug.LogWarning($"[Passthrough] Provider suspend was not confirmed ({reason}).", this);
            }

            Debug.Log($"[Passthrough] Lifecycle suspend ({reason}); requested=on.", this);
        }

        private void CancelRestart()
        {
            if (restartRoutine == null)
            {
                return;
            }

            StopCoroutine(restartRoutine);
            restartRoutine = null;
        }

        private void SetState(PassthroughState state, string status)
        {
            var changed = State != state || !string.Equals(Status, status, StringComparison.Ordinal);
            State = state;
            Status = status;
            if (changed)
            {
                StateChanged?.Invoke(State);
            }
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
            if (!requestedEnabled || applicationPaused || !applicationFocused)
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
            var enabled = provider.SetEnabled(true);
            providerEnableFailed = !enabled;
            SetState(
                PassthroughState.Enabled,
                enabled
                    ? (IsCameraSubsystemRunning ? "Enabled (Quest camera running)" : "Starting Quest camera")
                    : "Failed to enable passthrough provider");
            if (!enabled)
            {
                Debug.LogWarning("[Passthrough] Provider failed to enable after camera restart.", this);
            }
            restartRoutine = null;
        }

        public void Toggle()
        {
            SetEnabled(State != PassthroughState.Enabled || !requestedEnabled);
        }

        private void OnDisable()
        {
            CancelRestart();
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

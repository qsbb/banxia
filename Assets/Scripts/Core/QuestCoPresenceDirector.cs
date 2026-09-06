using System;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>
    /// Quest co-presence adapter. Common mode/call/placement state is kept here;
    /// passthrough remains the only Quest-specific presentation dependency.
    /// Flutter texture composition is deliberately not implied by this component.
    /// </summary>
    public sealed class QuestCoPresenceDirector : MonoBehaviour, ICoPresenceDirector
    {
        private const string PrefsKey = "banxia.phone.copresence.";
        private const float GroundHeight = 0f;
        private const float DefaultChromeTopRatio = 0.03f;
        private const float DefaultChromeBottomRatio = 0.88f;

        private Camera questCamera;
        private Transform avatarRoot;
        private PassthroughFacade passthrough;
        private CoPresenceMode mode = CoPresenceMode.VirtualScene;
        private VirtualEnvironment environment = VirtualEnvironment.NightStreet;
        private float callStartedAt = -1f;
        private float chromeTopPx = -1f;
        private float chromeBottomPx = -1f;
        private bool arPlaced;
        private CoPresenceFraming framing;

        public Camera MainCamera => questCamera;
        public bool ArActive => mode == CoPresenceMode.ArReality && passthrough != null &&
            passthrough.State == PassthroughState.Enabled;
        public CoPresenceMode CurrentMode => mode;
        public VirtualEnvironment CurrentEnvironment => environment;
        public bool VideoCallActive => mode == CoPresenceMode.VideoCall && callStartedAt >= 0f;
        public string CallDurationText => !VideoCallActive
            ? "00:00"
            : FormatDuration(Time.unscaledTime - callStartedAt);
        public bool ArCameraAvailable => passthrough != null &&
            passthrough.State != PassthroughState.Unavailable;
        public bool ArPlaced => arPlaced;
        public CoPresenceFraming CurrentFraming => framing;

        public event Action<CoPresenceMode> ModeChanged;
        public event Action<VirtualEnvironment> EnvironmentChanged;

        public void Initialize(Camera camera)
        {
            questCamera = camera;
            passthrough = FindObjectOfType<PassthroughFacade>();
            mode = (CoPresenceMode)Mathf.Clamp(
                PlayerPrefs.GetInt(PrefsKey + "mode", (int)CoPresenceMode.VirtualScene), 0, 2);
            environment = (VirtualEnvironment)Mathf.Clamp(
                PlayerPrefs.GetInt(PrefsKey + "env", (int)VirtualEnvironment.NightStreet), 0, 3);
            framing = default(CoPresenceFraming);
            arPlaced = false;
            QuestDebugMode.Log($"quest director init mode={mode} env={environment} " +
                $"camera={(questCamera != null)} passthrough={(passthrough != null)} ar={ArCameraAvailable}");
            if (mode == CoPresenceMode.ArReality && !ArCameraAvailable)
            {
                mode = CoPresenceMode.VirtualScene;
            }
        }

        public void SetAvatar(Transform avatar)
        {
            avatarRoot = avatar;
            QuestDebugMode.Log($"quest set-avatar bound={(avatar != null)} mode={mode} " +
                $"callActive={VideoCallActive}");
            if (VideoCallActive)
            {
                UpdateFraming();
            }
        }

        public void ApplyOnEnterScene()
        {
            arPlaced = false;
            QuestDebugMode.Log($"quest apply-on-enter-scene mode={mode} " +
                $"avatar={(avatarRoot != null)} camera={(questCamera != null)}");
            if (mode == CoPresenceMode.ArReality)
            {
                if (ArCameraAvailable)
                {
                    passthrough.SetEnabled(true);
                }
                else
                {
                    QuestDebugMode.LogGuard("quest.apply-on-enter-scene",
                        "AR requested but passthrough is unavailable; using VirtualScene");
                    mode = CoPresenceMode.VirtualScene;
                    PersistMode();
                }
            }

            if (mode == CoPresenceMode.VideoCall)
            {
                callStartedAt = Time.unscaledTime;
                UpdateFraming();
            }
            else
            {
                callStartedAt = -1f;
                framing = default(CoPresenceFraming);
            }
        }

        public void Suspend()
        {
            arPlaced = false;
            if (passthrough != null)
            {
                passthrough.SetEnabled(false);
            }
            callStartedAt = -1f;
            framing = default(CoPresenceFraming);
        }

        public bool SwitchMode(CoPresenceMode next)
        {
            if (!Enum.IsDefined(typeof(CoPresenceMode), next))
            {
                QuestDebugMode.LogGuard("quest.switch-mode", $"undefined mode {next}");
                return false;
            }
            if (next == mode)
            {
                QuestDebugMode.Log($"quest switch-mode no-op (already {next})");
                return true;
            }
            arPlaced = false;
            if (next == CoPresenceMode.ArReality && !ArCameraAvailable)
            {
                QuestDebugMode.LogGuard("quest.switch-mode", "AR requested but passthrough unavailable");
                return false;
            }
            QuestDebugMode.Log($"quest switch-mode {mode} -> {next}");

            if (passthrough != null)
            {
                passthrough.SetEnabled(next == CoPresenceMode.ArReality);
            }
            mode = next;
            callStartedAt = next == CoPresenceMode.VideoCall ? Time.unscaledTime : -1f;
            if (next == CoPresenceMode.VideoCall)
            {
                UpdateFraming();
            }
            else
            {
                framing = default(CoPresenceFraming);
            }
            PersistMode();
            ModeChanged?.Invoke(mode);
            return true;
        }

        public void SwitchEnvironment(VirtualEnvironment next)
        {
            if (!Enum.IsDefined(typeof(VirtualEnvironment), next) || next == environment)
            {
                return;
            }
            environment = next;
            PlayerPrefs.SetInt(PrefsKey + "env", (int)environment);
            PlayerPrefs.Save();
            EnvironmentChanged?.Invoke(environment);
        }

        public void SetChromeInsets(float top, float bottom)
        {
            var screenHeight = questCamera != null && questCamera.pixelHeight > 0
                ? questCamera.pixelHeight
                : Screen.height;
            if (!IsFinite(top) || !IsFinite(bottom) || screenHeight <= 1f ||
                top < 0f || bottom <= top || bottom > screenHeight)
            {
                QuestDebugMode.LogGuard("quest.chrome-insets",
                    $"invalid top={top} bottom={bottom} screenHeight={screenHeight}");
                return;
            }
            chromeTopPx = top;
            chromeBottomPx = bottom;
            if (VideoCallActive)
            {
                UpdateFraming();
            }
        }

        public bool PlaceAvatarAtScreenPoint(Vector2 screenPoint)
        {
            if (mode != CoPresenceMode.ArReality || questCamera == null || avatarRoot == null)
            {
                return false;
            }
            var ray = questCamera.ScreenPointToRay(screenPoint);
            var ground = new Plane(Vector3.up, new Vector3(0f, GroundHeight, 0f));
            if (!ground.Raycast(ray, out var distance))
            {
                return false;
            }
            var hit = ray.GetPoint(distance);
            avatarRoot.position = new Vector3(hit.x, GroundHeight, hit.z);
            arPlaced = true;
            return true;
        }

        private void UpdateFraming()
        {
            framing = default(CoPresenceFraming);
            if (questCamera == null || avatarRoot == null)
            {
                QuestDebugMode.LogGuard("quest.framing", $"camera={(questCamera != null)} avatar={(avatarRoot != null)}");
                return;
            }

            var bounds = RenderBoundsUtility.Compute(avatarRoot.gameObject);
            if (bounds.size.sqrMagnitude <= 1e-8f)
            {
                QuestDebugMode.LogGuard("quest.framing", "render bounds empty");
                return;
            }

            var screenHeight = questCamera.pixelHeight > 1 ? questCamera.pixelHeight : Screen.height;
            if (screenHeight <= 1)
            {
                QuestDebugMode.LogGuard("quest.framing", $"screenHeight={screenHeight}");
                return;
            }
            var top = IsFinite(chromeTopPx) ? chromeTopPx : screenHeight * DefaultChromeTopRatio;
            var bottom = IsFinite(chromeBottomPx) ? chromeBottomPx : screenHeight * DefaultChromeBottomRatio;
            if (bottom <= top)
            {
                top = screenHeight * DefaultChromeTopRatio;
                bottom = screenHeight * DefaultChromeBottomRatio;
            }

            var avatar = avatarRoot.GetComponent<AvatarController>();
            var head = avatar == null ? null : avatar.HeadBone;
            var headAnchor = head != null;
            var eyeY = headAnchor
                ? head.position.y + CallFramingSolver.EyeOffset
                : bounds.min.y + bounds.size.y * 0.83f;
            var headTopY = eyeY + CallFramingSolver.HeadTopAboveEye;
            var lowCutY = eyeY - CallFramingSolver.EyeToWaist;
            var footY = bounds.min.y;
            var solve = CallFramingSolver.SolveBust(new CallFramingSolver.Inputs
            {
                S = screenHeight,
                ThetaDeg = questCamera.fieldOfView,
                TopPx = top,
                BottomPx = bottom,
                EyeY = eyeY,
                HeadTopY = headTopY,
                FootY = footY,
                LowCutY = lowCutY,
            }, headAnchor ? CallFramingSolver.DistanceMax : 1.6f);

            var anchorX = headAnchor ? head.position.x : bounds.center.x;
            var anchorZ = headAnchor ? head.position.z : bounds.center.z;
            var eyeWorld = new Vector3(anchorX, eyeY, anchorZ);
            var headTopWorld = new Vector3(anchorX, headTopY, anchorZ);
            var lowCutWorld = new Vector3(anchorX, lowCutY, anchorZ);
            var footWorld = new Vector3(bounds.center.x, footY, bounds.center.z);
            framing = new CoPresenceFraming
            {
                Valid = true,
                HeadAnchor = headAnchor,
                Degraded = solve.Degraded || !headAnchor,
                ScreenHeight = screenHeight,
                TopPx = top,
                BottomPx = bottom,
                Distance = solve.Distance,
                CameraY = solve.CameraY,
                EyeY = eyeY,
                HeadTopY = headTopY,
                LowCutY = lowCutY,
                FootY = footY,
                EyeWorld = eyeWorld,
                HeadTopWorld = headTopWorld,
                LowCutWorld = lowCutWorld,
                FootWorld = footWorld,
            };
        }

        private void PersistMode()
        {
            PlayerPrefs.SetInt(PrefsKey + "mode", (int)mode);
            PlayerPrefs.Save();
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static string FormatDuration(float seconds)
        {
            var span = TimeSpan.FromSeconds(Mathf.Max(0f, seconds));
            return span.Hours > 0
                ? $"{span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
                : $"{span.Minutes:00}:{span.Seconds:00}";
        }
    }
}

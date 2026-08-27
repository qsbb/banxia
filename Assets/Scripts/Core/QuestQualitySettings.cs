using System;
using System.Collections;
using System.Collections.Generic;
using UMT;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features.Meta;

namespace QuestMmdPlayer
{
    public enum QuestQualityPreset
    {
        Performance = 0,
        Balanced = 1,
        Clear = 2
    }

    public enum MmdPhysicsPreset
    {
        Performance = 0,
        Balanced = 1,
        Fine = 2
    }

    [DisallowMultipleComponent]
    public sealed class QuestQualitySettings : MonoBehaviour
    {
        private const string PresetKey = "quest.quality.preset";
        private const string PhysicsPresetKey = "quest.physics.preset";
        private const float MinimumRenderScale = .7f;
        private const float MaximumRenderScale = 1.2f;
        private const float PreferredRefreshRate = 72f;

        [SerializeField] private QuestQualityPreset defaultPreset = QuestQualityPreset.Balanced;
        [SerializeField] private MmdPhysicsPreset defaultPhysicsPreset = MmdPhysicsPreset.Balanced;

        // 待机物理档：纯待机（无动作、无触碰、无接近手）时自动切换，
        // 低沉本打破 60Hz 固定步的 catch-up 恶性循环；动作/触碰/接近时自动恢复。
        [Header("待机物理档")]
        [SerializeField, Range(30, 120)] private int idlePhysicsFrequencyHz = 30;
        [SerializeField, Range(1, 4)] private int idlePhysicsMaximumSubsteps = 1;
        [SerializeField, Range(0, 2)] private int idlePhysicsReinforcement = 0;
        [SerializeField] private bool idlePhysicsFullHandContact = false;

        private AvatarController idlePhysicsAvatar;
        private AvatarTouchInteraction idlePhysicsTouch;
        private AvatarMmdPhysicsAdapter idlePhysicsHandPhysics;
        private bool idlePhysicsSourcesBound;
        private Coroutine refreshRateRequest;

        public event Action<QuestQualityPreset> QualityChanged;
        public QuestQualityPreset CurrentPreset { get; private set; }
        public MmdPhysicsPreset CurrentPhysicsPreset { get; private set; }
        public string Status { get; private set; } = "画质尚未应用";
        public float RenderScale { get; private set; } = 1f;
        public int AntiAliasing { get; private set; } = 4;
        public int PhysicsFrequencyHz { get; private set; } = 60;
        public int PhysicsMaximumSubsteps { get; private set; } = 2;
        public int PhysicsReinforcement { get; private set; } = 1;
        public bool FullHandContact { get; private set; } = true;
        public bool IsIdlePhysicsActive { get; private set; }
        public event Action<bool> IdlePhysicsActiveChanged;
        public string RefreshRateStatus { get; private set; } = "等待 XR 显示器";
        public int ApplicationTargetFrameRate { get; private set; }

        private void Awake()
        {
            // XR presents on its own cadence. Unity's quality-level VSync can
            // otherwise select a half-rate cadence on Quest even when the
            // runtime is running at 72Hz.
            ApplyFramePacing(PreferredRefreshRate);
            var saved = PlayerPrefs.GetInt(PresetKey, (int)defaultPreset);
            var savedPhysics = PlayerPrefs.GetInt(PhysicsPresetKey, (int)defaultPhysicsPreset);
            ApplyRenderPreset(ParsePreset(saved), false);
            ApplyPhysicsPreset(ParsePhysicsPreset(savedPhysics), false);
        }

        private void OnEnable()
        {
            ApplyFramePacing(PreferredRefreshRate);
            RestartRefreshRateRequest();
        }

        private void OnDisable()
        {
            if (refreshRateRequest != null)
            {
                StopCoroutine(refreshRateRequest);
                refreshRateRequest = null;
            }
        }

        private void OnDestroy()
        {
            UnbindIdlePhysicsSources();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                ApplyFramePacing(PreferredRefreshRate);
                RestartRefreshRateRequest();
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            if (!pauseStatus)
            {
                ApplyFramePacing(PreferredRefreshRate);
                RestartRefreshRateRequest();
            }
        }

        private void RestartRefreshRateRequest()
        {
            if (!isActiveAndEnabled)
            {
                return;
            }
            if (refreshRateRequest != null)
            {
                StopCoroutine(refreshRateRequest);
            }
            refreshRateRequest = StartCoroutine(RequestPreferredRefreshRate());
        }

        public void ApplyPreset(QuestQualityPreset preset)
        {
            ApplyRenderPreset(preset, true);
        }

        public void ApplyPhysicsPreset(MmdPhysicsPreset preset)
        {
            ApplyPhysicsPreset(preset, true);
        }

        internal void ApplyPhysicsPresetForQa(MmdPhysicsPreset preset)
        {
            ApplyPhysicsPreset(preset, false);
            ApplyPhysicsPolicyToLoadedModels(true, FullHandContact);
        }

        public void ResetToDefault()
        {
            ApplyPreset(defaultPreset);
            ApplyPhysicsPreset(defaultPhysicsPreset);
        }

        public static string GetDisplayName(QuestQualityPreset preset)
        {
            switch (preset)
            {
                case QuestQualityPreset.Performance:
                    return "性能";
                case QuestQualityPreset.Clear:
                    return "清晰";
                default:
                    return "平衡";
            }
        }

        public static string GetPhysicsDisplayName(MmdPhysicsPreset preset)
        {
            switch (preset)
            {
                case MmdPhysicsPreset.Performance:
                    return "性能";
                case MmdPhysicsPreset.Fine:
                    return "精细";
                default:
                    return "平衡";
            }
        }

        private void ApplyRenderPreset(QuestQualityPreset preset, bool persist)
        {
            CurrentPreset = preset;
            switch (preset)
            {
                case QuestQualityPreset.Performance:
                    RenderScale = .8f;
                    AntiAliasing = 2;
                    break;
                case QuestQualityPreset.Clear:
                    RenderScale = 1.15f;
                    AntiAliasing = 4;
                    break;
                default:
                    RenderScale = 1f;
                    AntiAliasing = 2;
                    break;
            }

            RenderScale = Mathf.Clamp(RenderScale, MinimumRenderScale, MaximumRenderScale);
            XRSettings.eyeTextureResolutionScale = RenderScale;
            XRSettings.renderViewportScale = 1f;
            QualitySettings.antiAliasing = AntiAliasing;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline)
            {
                pipeline.renderScale = 1f;
                pipeline.msaaSampleCount = AntiAliasing;
                pipeline.shadowDistance = preset == QuestQualityPreset.Performance
                    ? 12f
                    : preset == QuestQualityPreset.Clear ? 40f : 22f;
            }
            if (persist)
            {
                PlayerPrefs.SetInt(PresetKey, (int)preset);
                PlayerPrefs.Save();
            }

            UpdateStatus();
            QualityChanged?.Invoke(preset);
        }

        private void ApplyPhysicsPreset(MmdPhysicsPreset preset, bool persist)
        {
            CurrentPhysicsPreset = preset;
            GetPhysicsPolicy(
                preset,
                out var frequencyHz,
                out var maximumSubsteps,
                out var reinforcement,
                out var fullHandContact);
            PhysicsFrequencyHz = frequencyHz;
            PhysicsMaximumSubsteps = maximumSubsteps;
            PhysicsReinforcement = reinforcement;
            FullHandContact = fullHandContact;

            MMDPhysicsManager.ConfigureRuntimeQuality(
                PhysicsFrequencyHz,
                PhysicsMaximumSubsteps,
                PhysicsReinforcement);
            ApplyPhysicsPolicyToLoadedModels(persist, FullHandContact);
            if (persist)
            {
                PlayerPrefs.SetInt(PhysicsPresetKey, (int)preset);
                PlayerPrefs.Save();
            }
            UpdateStatus();
            QualityChanged?.Invoke(CurrentPreset);
        }

        public static void GetPhysicsPolicy(
            MmdPhysicsPreset preset,
            out int frequencyHz,
            out int maximumSubsteps,
            out int reinforcement,
            out bool fullHandContact)
        {
            switch (preset)
            {
                case MmdPhysicsPreset.Performance:
                    frequencyHz = 60;
                    maximumSubsteps = 2;
                    // Joint-heavy models spend most of their frame in the
                    // duplicate locked-translation constraints. The explicit
                    // performance profile removes those duplicates while the
                    // balanced default retains one reinforcement copy.
                    reinforcement = 0;
                    fullHandContact = false;
                    return;
                case MmdPhysicsPreset.Fine:
                    frequencyHz = 120;
                    maximumSubsteps = 4;
                    reinforcement = 2;
                    fullHandContact = true;
                    return;
                default:
                    // Keep the balanced profile at a bounded 60 Hz physics
                    // cadence. XR presentation remains explicitly requested
                    // at 72 Hz, so Bullet does not consume the render budget
                    // just to mirror the display refresh rate.
                    frequencyHz = 60;
                    maximumSubsteps = 2;
                    reinforcement = 1;
                    fullHandContact = true;
                    return;
            }
        }

        public void ApplyHandContactPolicy(AvatarMmdPhysicsAdapter adapter)
        {
            adapter?.SetHighFrequencyContact(FullHandContact);
        }

        /// <summary>
        /// Subscribes the idle-physics governor to the three motion/contact sources
        /// so it can swap between the low-cost idle profile and the user's preset
        /// without polling. Callers may invoke this repeatedly (e.g. after a model
        /// reload) because it always detaches the previous sources first.
        /// </summary>
        public void BindIdlePhysicsSources(
            AvatarController avatar,
            AvatarTouchInteraction touch,
            AvatarMmdPhysicsAdapter handPhysics)
        {
            UnbindIdlePhysicsSources();
            idlePhysicsAvatar = avatar;
            idlePhysicsTouch = touch;
            idlePhysicsHandPhysics = handPhysics;
            if (avatar != null)
            {
                avatar.ActionChanged += HandleAvatarActionChanged;
            }
            if (touch != null)
            {
                touch.TouchStateChanged += HandleTouchStateChanged;
            }
            if (handPhysics != null)
            {
                handPhysics.ActiveProbeChanged += HandleActiveProbeChanged;
            }
            idlePhysicsSourcesBound = true;
            EvaluateIdlePhysics();
        }

        public void UnbindIdlePhysicsSources()
        {
            if (!idlePhysicsSourcesBound)
            {
                return;
            }
            if (idlePhysicsAvatar != null)
            {
                idlePhysicsAvatar.ActionChanged -= HandleAvatarActionChanged;
            }
            if (idlePhysicsTouch != null)
            {
                idlePhysicsTouch.TouchStateChanged -= HandleTouchStateChanged;
            }
            if (idlePhysicsHandPhysics != null)
            {
                idlePhysicsHandPhysics.ActiveProbeChanged -= HandleActiveProbeChanged;
            }
            idlePhysicsAvatar = null;
            idlePhysicsTouch = null;
            idlePhysicsHandPhysics = null;
            idlePhysicsSourcesBound = false;
        }

        /// <summary>Applies the tunable low-cost idle profile to every loaded model.</summary>
        public void ApplyIdlePhysics()
        {
            if (IsIdlePhysicsActive)
            {
                return;
            }
            MMDPhysicsManager.ConfigureRuntimeQuality(
                Mathf.Clamp(idlePhysicsFrequencyHz, 30, 120),
                Mathf.Clamp(idlePhysicsMaximumSubsteps, 1, 4),
                Mathf.Clamp(idlePhysicsReinforcement, 0, 2));
            ApplyPhysicsPolicyToLoadedModels(true, idlePhysicsFullHandContact);
            SetIdlePhysicsActive(true);
        }

        /// <summary>Restores the user's saved preset profile (default balanced 60/2/1).</summary>
        public void RestorePresetPhysics()
        {
            if (!IsIdlePhysicsActive)
            {
                return;
            }
            MMDPhysicsManager.ConfigureRuntimeQuality(
                PhysicsFrequencyHz,
                PhysicsMaximumSubsteps,
                PhysicsReinforcement);
            ApplyPhysicsPolicyToLoadedModels(true, FullHandContact);
            SetIdlePhysicsActive(false);
        }

        private void HandleAvatarActionChanged(string action)
        {
            EvaluateIdlePhysics();
        }

        private void HandleTouchStateChanged(bool touched)
        {
            EvaluateIdlePhysics();
        }

        private void HandleActiveProbeChanged(bool hasActiveProbe)
        {
            EvaluateIdlePhysics();
        }

        private void EvaluateIdlePhysics()
        {
            var avatarAtRest = idlePhysicsAvatar == null ||
                AvatarMotionArbiter.Normalize(idlePhysicsAvatar.CurrentAction) == "idle";
            var notTouching = idlePhysicsTouch == null || !idlePhysicsTouch.IsTouched;
            var noNearHand = idlePhysicsHandPhysics == null ||
                idlePhysicsHandPhysics.ActiveProbeCount <= 0;
            if (avatarAtRest && notTouching && noNearHand)
            {
                ApplyIdlePhysics();
            }
            else
            {
                RestorePresetPhysics();
            }
        }

        private void SetIdlePhysicsActive(bool active)
        {
            if (IsIdlePhysicsActive == active)
            {
                return;
            }
            IsIdlePhysicsActive = active;
            UpdateStatus();
            IdlePhysicsActiveChanged?.Invoke(active);
        }

        private static void ApplyPhysicsPolicyToLoadedModels(bool rebuildPhysics, bool fullHandContact)
        {
            if (rebuildPhysics)
            {
                var managers = FindObjectsOfType<MMDPhysicsManager>(true);
                for (var index = 0; index < managers.Length; index++)
                {
                    managers[index]?.ApplyConfiguredRuntimeQuality();
                }
            }

            var adapters = FindObjectsOfType<AvatarMmdPhysicsAdapter>(true);
            for (var index = 0; index < adapters.Length; index++)
            {
                adapters[index]?.SetHighFrequencyContact(fullHandContact);
            }
        }

        private void UpdateStatus()
        {
            Status = GetDisplayName(CurrentPreset) + "画质 · 渲染比例 " + RenderScale.ToString("F2") +
                " · " + GetPhysicsDisplayName(CurrentPhysicsPreset) + "物理 " +
                PhysicsFrequencyHz + "Hz/" + PhysicsMaximumSubsteps + "步" +
                (FullHandContact ? " · 完整手部接触" : " · 低频手部接触") +
                (IsIdlePhysicsActive
                    ? " · 待机档 " + idlePhysicsFrequencyHz + "Hz/" +
                        idlePhysicsMaximumSubsteps + "步"
                    : string.Empty);
        }

        private IEnumerator RequestPreferredRefreshRate()
        {
            var displays = new List<XRDisplaySubsystem>();
            var requestAccepted = false;
            var lastReportedRefreshRate = 0f;
            // OpenXR can expose the display subsystem after the first scene has
            // already been enabled. Keep a short fast-retry window for normal
            // startup, then retry at a low cadence instead of silently leaving
            // the runtime at its default 60 Hz forever.
            var initialDeadline = Time.unscaledTime + 10f;
            var nextRequestAt = 0f;
            var nextPollAt = Time.unscaledTime;
            ApplyFramePacing(PreferredRefreshRate);
            while (true)
            {
                if (Time.unscaledTime < nextPollAt)
                {
                    yield return new WaitForSecondsRealtime(.5f);
                    continue;
                }

                displays.Clear();
                SubsystemManager.GetInstances(displays);
                var foundRunningDisplay = false;
                for (var index = 0; index < displays.Count; index++)
                {
                    var display = displays[index];
                    if (display == null || !display.running)
                    {
                        continue;
                    }
                    foundRunningDisplay = true;
                    if (!requestAccepted && Time.unscaledTime >= nextRequestAt)
                    {
                        requestAccepted = display.TryRequestDisplayRefreshRate(PreferredRefreshRate);
                        nextRequestAt = Time.unscaledTime + .5f;
                    }
                    if (display.TryGetDisplayRefreshRate(out var reported) &&
                        reported > 0f && !float.IsNaN(reported) && !float.IsInfinity(reported))
                    {
                        lastReportedRefreshRate = reported;
                        if (IsRequestedRefreshRateActive(reported, PreferredRefreshRate))
                        {
                            RefreshRateStatus = "已应用 " + reported.ToString("F0") +
                                "Hz · 目标 " + ApplicationTargetFrameRate + "FPS";
                            refreshRateRequest = null;
                            yield break;
                        }
                    }
                    // TryGetDisplayRefreshRate can keep returning the old 60 Hz
                    // value for several frames after a successful 72 Hz request.
                    // Never feed that transient value back into targetFrameRate.
                    RefreshRateStatus = requestAccepted
                        ? "已请求 " + PreferredRefreshRate.ToString("F0") + "Hz，等待运行时切换" +
                          (lastReportedRefreshRate > 0f
                              ? " · 当前 " + lastReportedRefreshRate.ToString("F0") + "Hz"
                              : string.Empty) +
                          " · 目标 " + ApplicationTargetFrameRate + "FPS"
                        : "运行时暂未接受 " + PreferredRefreshRate.ToString("F0") +
                          "Hz 请求 · 目标 " + ApplicationTargetFrameRate + "FPS";
                }

                if (Time.unscaledTime >= initialDeadline)
                {
                    RefreshRateStatus = lastReportedRefreshRate > 0f
                        ? "运行时未切换到 " + PreferredRefreshRate.ToString("F0") +
                          "Hz · 当前 " + lastReportedRefreshRate.ToString("F0") +
                          "Hz · 30秒后重试 · 目标 " + ApplicationTargetFrameRate + "FPS"
                        : foundRunningDisplay
                            ? "运行时未切换到 " + PreferredRefreshRate.ToString("F0") +
                              "Hz · 30秒后重试 · 目标 " + ApplicationTargetFrameRate + "FPS"
                            : "XR 显示器暂不可用 · 30秒后重试 · 目标 " + ApplicationTargetFrameRate + "FPS";
                    requestAccepted = false;
                    nextRequestAt = Time.unscaledTime + 30f;
                    nextPollAt = nextRequestAt;
                }
                else
                {
                    nextPollAt = Time.unscaledTime + .5f;
                }

                yield return new WaitForSecondsRealtime(.5f);
            }
        }

        private void ApplyFramePacing(float refreshRate)
        {
            var target = NormalizeRefreshRate(refreshRate);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = target;
            ApplicationTargetFrameRate = target;
        }

        public static int NormalizeRefreshRate(float refreshRate)
        {
            if (float.IsNaN(refreshRate) || float.IsInfinity(refreshRate) || refreshRate <= 0f)
            {
                return (int)PreferredRefreshRate;
            }
            return Mathf.Clamp(Mathf.RoundToInt(refreshRate), 30, 120);
        }

        public static bool IsRequestedRefreshRateActive(float reported, float requested)
        {
            return !float.IsNaN(reported) && !float.IsInfinity(reported) && reported > 0f &&
                !float.IsNaN(requested) && !float.IsInfinity(requested) && requested > 0f &&
                Mathf.Abs(reported - requested) <= .5f;
        }

        private static QuestQualityPreset ParsePreset(int value)
        {
            return Enum.IsDefined(typeof(QuestQualityPreset), value)
                ? (QuestQualityPreset)value
                : QuestQualityPreset.Balanced;
        }

        private static MmdPhysicsPreset ParsePhysicsPreset(int value)
        {
            return Enum.IsDefined(typeof(MmdPhysicsPreset), value)
                ? (MmdPhysicsPreset)value
                : MmdPhysicsPreset.Balanced;
        }
    }
}

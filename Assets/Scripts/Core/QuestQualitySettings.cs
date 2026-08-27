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

        private void Start()
        {
            // 保险：待机评估是事件驱动的；若模型在绑定之后加载、或启动序列中
            // 错过首个 ActionChanged 事件，启动后补一次评估，确保纯待机必定
            // 进入 30Hz/1 子步档。
            EvaluateIdlePhysics();
        }

        private void Update()
        {
            EvaluateIdleHysteresis();
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
                // 摘下再佩戴恢复后，立即按当前状态恢复待机档评估，
                // 避免恢复后直到下一次动作事件前一直跑满档物理。
                EvaluateIdlePhysics();
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

        /// <summary>性能监控源（待机档滞后验证用）。</summary>
        public void BindPerformanceMonitor(RuntimePerformanceMonitor monitor)
        {
            performanceMonitor = monitor;
        }

        /// <summary>
        /// 每帧计费驱动的滞后验证：待机档生效 N 帧后若主线程负载仍超预算，
        /// 说明 30Hz/1 子步不够，自动回退满档（止损，防止挡位本身成负载源）。
        /// 数据也回答了「不待机能否根治」——若 physics 段毫秒数在 30Hz 下
        /// 并未下降，则负载不在 Bullet 步进而需其他根修。
        /// </summary>
        private void EvaluateIdleHysteresis()
        {
            if (!IsIdlePhysicsActive || performanceMonitor == null)
            {
                return;
            }
            var billing = performanceMonitor.CaptureFrameBilling();
            idleFramesSinceActivate++;
            if (idleFramesSinceActivate < idleHysteresisFrames)
            {
                return;
            }
            // 超预算判定：帧 p95 仍超 72Hz 预算且总计费未明显下降
            var overBudget = billing.FrameP95Ms > idleFrameP95BudgetMs;
            if (overBudget && Time.unscaledTime >= nextIdleHysteresisLogAt)
            {
                nextIdleHysteresisLogAt = Time.unscaledTime + 5f;
                Debug.LogWarning(
                    $"[IdlePhysics] 待机档生效中但负载仍超预算：p95={billing.FrameP95Ms:F1}ms " +
                    $"fps={billing.CurrentFps:F1} 计费 total={billing.TotalMs:F2} " +
                    $"(solver={billing.SolverMs:F2} physics={billing.PhysicsMs:F2} " +
                    $"boneIk={billing.BoneIkMs:F2} sdef={billing.SdefMs:F2} " +
                    $"flush={billing.FlushMs:F2} hand={billing.HandContactMs:F2}) " +
                    $"hz={idlePhysicsFrequencyHz} sub={idlePhysicsMaximumSubsteps}",
                    this);
            }
        }

        private RuntimePerformanceMonitor performanceMonitor;
        private int idleFramesSinceActivate;
        private float nextIdleHysteresisLogAt;
        [Header("待机档滞后验证")]
        [SerializeField, Range(30, 300)] private int idleHysteresisFrames = 90;
        [SerializeField, Range(10f, 30f)] private float idleFrameP95BudgetMs = 13.9f;

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
            // 待机判定：IdlePose 播放的闲置动作（idle/sway 等）source=Idle，
            // 不能只认字面动作名 "idle"，否则 sway 一播就永远进不了待机档。
            var avatarAtRest = idlePhysicsAvatar == null ||
                idlePhysicsAvatar.CurrentActionSource == AvatarActionSource.Idle ||
                AvatarMotionArbiter.Normalize(idlePhysicsAvatar.CurrentAction) == "idle";
            var notTouching = idlePhysicsTouch == null || !idlePhysicsTouch.IsTouched;
            // 注意：探针（ActiveProbeCount）不参与待机判定——探针激活由当前
            // 档位决定（满档高频/待机档低频），参与判定会形成 flap：满档激活
            // 探针→判非待机→满档→探针又激活……每次 flap 还重建整个 Bullet
            // 世界。接近手不碰时保留待机档；真正触碰（0.5s 持续+3s 冷却）
            // 由 IsTouched 自动恢复满档。
            if (avatarAtRest && notTouching)
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
            if (active)
            {
                idleFramesSinceActivate = 0;
            }
            Debug.Log(
                "[IdlePhysics] 待机物理档" + (active
                    ? $"生效 frequency={idlePhysicsFrequencyHz}Hz substeps={idlePhysicsMaximumSubsteps} reinforcement={idlePhysicsReinforcement} fullHandContact={idlePhysicsFullHandContact}"
                    : "恢复，按当前画质预设"),
                this);
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

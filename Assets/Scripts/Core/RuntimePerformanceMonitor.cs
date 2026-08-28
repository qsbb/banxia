using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UMT;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.XR;
using ProviderXRStats = UnityEngine.XR.Provider.XRStats;

namespace QuestMmdPlayer
{
    public enum DeviceThermalState
    {
        Unavailable = -1,
        Normal = 0,
        Light = 1,
        Moderate = 2,
        Severe = 3,
        Critical = 4,
        Emergency = 5,
        Shutdown = 6,
        Unknown = 7
    }

    /// <summary>
    /// Low-overhead runtime telemetry for the in-headset performance panel.
    /// Frame data is sampled every frame; platform and model data are refreshed
    /// at a lower frequency so opening the panel does not distort the result.
    /// </summary>
    [DefaultExecutionOrder(11300)]
    [DisallowMultipleComponent]
    public sealed class RuntimePerformanceMonitor : MonoBehaviour
    {
        private const int FrameWindowCapacity = 240;
        // 10 seconds at the highest Quest refresh rate we support (144 Hz)
        // plus a small margin. Samples are value types and are overwritten in
        // place, so this does not allocate while the app is running.
        private const int DetailedWindowCapacity = 1536;
        private const float DetailedWindowSeconds = 10f;
        private const float SlowSampleIntervalSeconds = 1f;
        public const float AndroidSystemSampleIntervalSeconds = 30f;
        private const long BytesPerKilobyte = 1024L;
        private readonly float[] frameTimeWindow = new float[FrameWindowCapacity];
        private readonly float[] sortedFrameTimeWindow = new float[FrameWindowCapacity];
        private readonly FrameTiming[] frameTimings = new FrameTiming[1];
        private readonly PerformanceFrameSample[] detailedSamples =
            new PerformanceFrameSample[DetailedWindowCapacity];
        private readonly float[] detailedScratch = new float[DetailedWindowCapacity];
        private readonly List<XRDisplaySubsystem> xrDisplays = new List<XRDisplaySubsystem>(2);
        private readonly Queue<TimedFrameSample> activeFrameSamples = new Queue<TimedFrameSample>(2400);
        private readonly Queue<PhysicsDropSample> activePhysicsDrops = new Queue<PhysicsDropSample>(2400);
        private int frameTimeCount;
        private int frameTimeWriteIndex;
        private float nextSlowSampleAt;
        private float nextAndroidSystemSampleAt;
        private int currentModelInstanceId;
        private RuntimeMmdModelLoader currentModelLoader;
        private MMDPhysicsManager currentPhysicsManager;
        private MMDTransformManager currentTransformManager;
        private QuestTrackedHandVisualizer currentTrackedHands;
        private bool applicationFocused = true;
        private bool activeSamplingState;
        private float activeSessionStartedAt;
        private float physicsObservedTotalDroppedSeconds;
        private int physicsObservedDroppedFrameCount;
        private float compositorObservedDroppedFrames;
        private int physicsLastDroppedFrameCountDelta;
        private float detailedSamplePreviousCompositorDroppedFrames;
        private int detailedSampleWriteIndex;
        private int detailedSampleCount;
        private PerformanceWindowSummary detailedWindowSummary;
        private float nextDetailedWindowSummaryAt;
        private QuestMmdPlayerBootstrap bootstrap;
        private RuntimeDebugLog diagnostics;
        private bool physicsLifecycleInitialized;
        private bool previousPhysicsSuspended;
        private bool previousInitialPoseSeedPending;
        private Task<AndroidSystemMetricsSample> androidSystemMetricsTask;

        private readonly struct TimedFrameSample
        {
            internal readonly float time;
            internal readonly float milliseconds;

            internal TimedFrameSample(float time, float milliseconds)
            {
                this.time = time;
                this.milliseconds = milliseconds;
            }
        }

        private readonly struct PhysicsDropSample
        {
            internal readonly float time;
            internal readonly float seconds;
            internal readonly bool dropped;

            internal PhysicsDropSample(float time, float seconds, bool dropped)
            {
                this.time = time;
                this.seconds = seconds;
                this.dropped = dropped;
            }
        }

        private readonly struct AndroidSystemMetricsSample
        {
            internal readonly bool pssAvailable;
            internal readonly long pssBytes;
            internal readonly bool thermalAvailable;
            internal readonly DeviceThermalState thermalState;

            internal AndroidSystemMetricsSample(
                bool pssAvailable,
                long pssBytes,
                bool thermalAvailable,
                DeviceThermalState thermalState)
            {
                this.pssAvailable = pssAvailable;
                this.pssBytes = Math.Max(0L, pssBytes);
                this.thermalAvailable = thermalAvailable;
                this.thermalState = thermalState;
            }
        }

        /// <summary>
        /// One low-overhead frame sample retained for the recent diagnostics
        /// window. Action is a sanitized local label, never a turn or user id.
        /// </summary>
        public readonly struct PerformanceFrameSample
        {
            public readonly float Timestamp;
            public readonly int FrameIndex;
            public readonly float UnscaledDeltaMilliseconds;
            public readonly float MmdSamplingMilliseconds;
            public readonly float MmdSolverMilliseconds;
            public readonly float MmdBoneAndIkMilliseconds;
            public readonly float MmdPhysicsMilliseconds;
            public readonly float MmdFlushMilliseconds;
            public readonly float MmdSdefMilliseconds;
            public readonly float HandContactMilliseconds;
            public readonly int PhysicsSubsteps;
            public readonly float PhysicsDroppedSeconds;
            public readonly int PhysicsDroppedFrameCount;
            public readonly float CompositorDroppedFramesDelta;
            public readonly float XrCpuMilliseconds;
            public readonly float XrGpuMilliseconds;
            public readonly string Action;
            public readonly bool PhysicsSuspended;
            public readonly bool InitialPoseSeedPending;

            internal PerformanceFrameSample(
                float timestamp,
                int frameIndex,
                float unscaledDeltaMilliseconds,
                float mmdSamplingMilliseconds,
                float mmdSolverMilliseconds,
                float mmdBoneAndIkMilliseconds,
                float mmdPhysicsMilliseconds,
                float mmdFlushMilliseconds,
                float mmdSdefMilliseconds,
                float handContactMilliseconds,
                int physicsSubsteps,
                float physicsDroppedSeconds,
                int physicsDroppedFrameCount,
                float compositorDroppedFramesDelta,
                float xrCpuMilliseconds,
                float xrGpuMilliseconds,
                string action,
                bool physicsSuspended,
                bool initialPoseSeedPending)
            {
                Timestamp = timestamp;
                FrameIndex = frameIndex;
                UnscaledDeltaMilliseconds = unscaledDeltaMilliseconds;
                MmdSamplingMilliseconds = mmdSamplingMilliseconds;
                MmdSolverMilliseconds = mmdSolverMilliseconds;
                MmdBoneAndIkMilliseconds = mmdBoneAndIkMilliseconds;
                MmdPhysicsMilliseconds = mmdPhysicsMilliseconds;
                MmdFlushMilliseconds = mmdFlushMilliseconds;
                MmdSdefMilliseconds = mmdSdefMilliseconds;
                HandContactMilliseconds = handContactMilliseconds;
                PhysicsSubsteps = physicsSubsteps;
                PhysicsDroppedSeconds = physicsDroppedSeconds;
                PhysicsDroppedFrameCount = physicsDroppedFrameCount;
                CompositorDroppedFramesDelta = compositorDroppedFramesDelta;
                XrCpuMilliseconds = xrCpuMilliseconds;
                XrGpuMilliseconds = xrGpuMilliseconds;
                Action = action ?? string.Empty;
                PhysicsSuspended = physicsSuspended;
                InitialPoseSeedPending = initialPoseSeedPending;
            }
        }

        /// <summary>Read-only summary of the last approximately ten active seconds.</summary>
        public readonly struct PerformanceWindowSummary
        {
            public readonly int SampleCount;
            public readonly float WindowSeconds;
            public readonly float FrameP95Milliseconds;
            public readonly float FrameMaxMilliseconds;
            public readonly float MmdSolverP95Milliseconds;
            public readonly float MmdPhysicsP95Milliseconds;
            public readonly float MmdBoneAndIkP95Milliseconds;
            public readonly float MmdFlushP95Milliseconds;
            public readonly float MmdSdefP95Milliseconds;
            public readonly float HandContactP95Milliseconds;
            public readonly float PhysicsDroppedSeconds;
            public readonly int PhysicsDroppedFrameCount;
            public readonly float FirstPhysicsDroppedSeconds;
            public readonly int LongFrameCount;
            public readonly float FirstLongFrameOffsetMilliseconds;
            public readonly float CompositorDroppedFrames;
            public readonly float FirstCompositorDroppedFrames;
            public readonly float FirstPhysicsDropOffsetMilliseconds;
            public readonly float FirstCompositorDropOffsetMilliseconds;
            public readonly string CurrentAction;
            public readonly bool PhysicsSuspended;
            public readonly bool InitialPoseSeedPending;

            internal PerformanceWindowSummary(
                int sampleCount,
                float windowSeconds,
                float frameP95Milliseconds,
                float frameMaxMilliseconds,
                float mmdSolverP95Milliseconds,
                float mmdPhysicsP95Milliseconds,
                float mmdBoneAndIkP95Milliseconds,
                float mmdFlushP95Milliseconds,
                float mmdSdefP95Milliseconds,
                float handContactP95Milliseconds,
                float physicsDroppedSeconds,
                int physicsDroppedFrameCount,
                float firstPhysicsDroppedSeconds,
                int longFrameCount,
                float firstLongFrameOffsetMilliseconds,
                float compositorDroppedFrames,
                float firstCompositorDroppedFrames,
                float firstPhysicsDropOffsetMilliseconds,
                float firstCompositorDropOffsetMilliseconds,
                string currentAction,
                bool physicsSuspended,
                bool initialPoseSeedPending)
            {
                SampleCount = sampleCount;
                WindowSeconds = windowSeconds;
                FrameP95Milliseconds = frameP95Milliseconds;
                FrameMaxMilliseconds = frameMaxMilliseconds;
                MmdSolverP95Milliseconds = mmdSolverP95Milliseconds;
                MmdPhysicsP95Milliseconds = mmdPhysicsP95Milliseconds;
                MmdBoneAndIkP95Milliseconds = mmdBoneAndIkP95Milliseconds;
                MmdFlushP95Milliseconds = mmdFlushP95Milliseconds;
                MmdSdefP95Milliseconds = mmdSdefP95Milliseconds;
                HandContactP95Milliseconds = handContactP95Milliseconds;
                PhysicsDroppedSeconds = physicsDroppedSeconds;
                PhysicsDroppedFrameCount = physicsDroppedFrameCount;
                FirstPhysicsDroppedSeconds = firstPhysicsDroppedSeconds;
                LongFrameCount = longFrameCount;
                FirstLongFrameOffsetMilliseconds = firstLongFrameOffsetMilliseconds;
                CompositorDroppedFrames = compositorDroppedFrames;
                FirstCompositorDroppedFrames = firstCompositorDroppedFrames;
                FirstPhysicsDropOffsetMilliseconds = firstPhysicsDropOffsetMilliseconds;
                FirstCompositorDropOffsetMilliseconds = firstCompositorDropOffsetMilliseconds;
                CurrentAction = currentAction ?? string.Empty;
                PhysicsSuspended = physicsSuspended;
                InitialPoseSeedPending = initialPoseSeedPending;
            }

            public static PerformanceWindowSummary Empty => new PerformanceWindowSummary(
                0, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f,
                0f, 0, 0f, 0, -1f, 0f, 0f, -1f, -1f, string.Empty, false, false);
        }

        public PerformanceWindowSummary detailedWindow => detailedWindowSummary;

        /// <summary>单帧各段计费快照（给待机档滞后评估用，单位毫秒）。</summary>
        public readonly struct FrameBillingSnapshot
        {
            public FrameBillingSnapshot(
                float solverMs,
                float physicsMs,
                float boneIkMs,
                float sdefMs,
                float flushMs,
                float handContactMs,
                float totalMs,
                bool physicsActive,
                float frameP95Ms,
                float currentFps)
            {
                SolverMs = solverMs;
                PhysicsMs = physicsMs;
                BoneIkMs = boneIkMs;
                SdefMs = sdefMs;
                FlushMs = flushMs;
                HandContactMs = handContactMs;
                TotalMs = totalMs;
                PhysicsActive = physicsActive;
                FrameP95Ms = frameP95Ms;
                CurrentFps = currentFps;
            }

            public float SolverMs { get; }
            public float PhysicsMs { get; }
            public float BoneIkMs { get; }
            public float SdefMs { get; }
            public float FlushMs { get; }
            public float HandContactMs { get; }
            public float TotalMs { get; }
            public bool PhysicsActive { get; }
            public float FrameP95Ms { get; }
            public float CurrentFps { get; }
        }

        public FrameBillingSnapshot CaptureFrameBilling()
        {
            var solver = Mathf.Max(0f, mmdSolverMilliseconds);
            var physics = Mathf.Max(0f, mmdPhysicsMilliseconds);
            var boneIk = Mathf.Max(0f, mmdBoneAndIkMilliseconds);
            var sdef = Mathf.Max(0f, mmdSdefMilliseconds);
            var flush = Mathf.Max(0f, mmdFlushMilliseconds);
            var hand = Mathf.Max(0f, handContactMilliseconds);
            var total = solver + physics + boneIk + sdef + flush + hand;
            return new FrameBillingSnapshot(
                solver, physics, boneIk, sdef, flush, hand, total,
                physicsMetricsAvailable, Mathf.Max(0f, frameTimeP95Ms), Mathf.Max(0f, currentFps));
        }
        public int detailedWindowSampleCount => detailedSampleCount;
        public const float DetailedWindowDurationSeconds = DetailedWindowSeconds;

        public bool detailedSamplingEnabled { get; private set; }
        public bool headsetPresenceAvailable { get; private set; }
        public bool headsetWorn { get; private set; }
        public bool targetFpsAvailable { get; private set; }
        public float targetFps { get; private set; }
        public float currentFps { get; private set; }
        public int frameSampleCount { get; private set; }
        public float frameTimeP50Ms { get; private set; }
        public float frameTimeP95Ms { get; private set; }
        public float frameTimeMaxMs { get; private set; }
        public float fps5Seconds { get; private set; }
        public float fps30Seconds { get; private set; }
        public float activeSessionSeconds { get; private set; }
        public bool cpuFrameTimeAvailable { get; private set; }
        public float cpuFrameTimeMs { get; private set; }
        public bool gpuFrameTimeAvailable { get; private set; }
        public float gpuFrameTimeMs { get; private set; }
        public bool xrPerformanceMetricsAvailable { get; private set; }
        public float xrAppCpuTimeMs { get; private set; }
        public float xrAppGpuTimeMs { get; private set; }
        public float xrCpuUtilization { get; private set; }
        public float xrGpuUtilization { get; private set; }
        public bool compositorDroppedFramesAvailable { get; private set; }
        public float compositorDroppedFrames { get; private set; }
        public float compositorDroppedFramesSession { get; private set; }
        public long totalAllocatedMemoryBytes { get; private set; }
        public long totalReservedMemoryBytes { get; private set; }
        public long managedUsedMemoryBytes { get; private set; }
        public bool androidPssAvailable { get; private set; }
        public long androidPssBytes { get; private set; }
        public int gcGeneration0Collections { get; private set; }
        public int gcGeneration1Collections { get; private set; }
        public int gcGeneration2Collections { get; private set; }
        public bool thermalStatusAvailable { get; private set; }
        public DeviceThermalState thermalState { get; private set; } = DeviceThermalState.Unavailable;

        public bool modelLoaded { get; private set; }
        public int modelRendererCount { get; private set; }
        public int modelMaterialCount { get; private set; }
        public int modelTextureCount { get; private set; }
        public long modelEstimatedTextureBytes { get; private set; }
        public int modelVertexCount { get; private set; }
        public long modelTriangleCount { get; private set; }
        public int modelBlendShapeCount { get; private set; }
        public int modelBoneCount { get; private set; }
        public int modelRigidBodyCount { get; private set; }
        public int modelJointCount { get; private set; }

        public bool physicsMetricsAvailable { get; private set; }
        public int physicsFrequencyHz { get; private set; }
        public int physicsMaximumSubstepsPerFrame { get; private set; }
        public int physicsLastSubsteps { get; private set; }
        public float physicsLastDroppedSeconds { get; private set; }
        public float physicsTotalDroppedSeconds { get; private set; }
        public int physicsDroppedFrameCount { get; private set; }
        public int physicsPoseSourceFlipFrames { get; private set; }
        public float physicsSessionDroppedSeconds { get; private set; }
        public int physicsSessionDroppedFrameCount { get; private set; }
        public float physicsDroppedMillisecondsPerSecond5s { get; private set; }
        public float physicsDroppedMillisecondsPerSecond30s { get; private set; }
        public float physicsDroppedFramePercent5s { get; private set; }
        public float physicsDroppedFramePercent30s { get; private set; }
        public float mmdSamplingMilliseconds { get; private set; }
        public float mmdSolverMilliseconds { get; private set; }
        public float mmdBoneAndIkMilliseconds { get; private set; }
        public float mmdPhysicsMilliseconds { get; private set; }
        public float mmdFlushMilliseconds { get; private set; }
        public float mmdSdefMilliseconds { get; private set; }
        public float handContactMilliseconds { get; private set; }

        private void OnEnable()
        {
            diagnostics = GetComponent<RuntimeDebugLog>();
            nextSlowSampleAt = 0f;
            nextAndroidSystemSampleAt = 0f;
            currentModelInstanceId = int.MinValue;
            applicationFocused = true;
            CaptureHeadsetState();
            ResetActiveSessionMetrics();
        }

        private void Update()
        {
            PublishCompletedAndroidMetrics();
            // Presence is a cheap XR device query. Sampling it every frame
            // prevents one-second off-head gaps from entering the valid FPS or
            // physics-drop windows while the low-frequency metrics remain on
            // the slow sampling path below.
            CaptureHeadsetState();
            CaptureModelComplexityIfChanged();
            var activeSample = IsActivePerformanceSample();
            if (activeSample != activeSamplingState)
            {
                activeSamplingState = activeSample;
                ResetActiveSessionMetrics();
            }
            if (activeSample)
            {
                RecordActiveFrame(Time.unscaledDeltaTime * 1000f, Time.unscaledTime);
            }
            if (detailedSamplingEnabled)
            {
                FrameTimingManager.CaptureFrameTimings();
                CaptureFrameTiming();
            }

            ResolveSlowSamplingSchedule(
                Time.unscaledTime,
                detailedSamplingEnabled,
                ref nextSlowSampleAt,
                ref nextAndroidSystemSampleAt,
                out var captureSlowMetrics,
                out var captureAndroidSystemMetrics);
            if (!captureSlowMetrics)
            {
                return;
            }

            CaptureXrPerformanceMetrics();
            CaptureTargetFrameRate();
            if (detailedSamplingEnabled)
            {
                CaptureMemoryAndGc();
                if (captureAndroidSystemMetrics)
                {
                    // Debug.getPss walks process memory maps and the thermal
                    // query crosses Binder. Run both on an attached background
                    // JNI thread so the performance panel cannot reduce the
                    // FPS it is measuring, especially with joint-heavy avatars.
                    ScheduleAndroidMetricsCapture();
                }
            }
        }

        // MMDTransformManager, hand tracking, VMD and the conversation
        // presenter publish their frame metrics from LateUpdate. Sampling
        // here keeps those values correlated with the rendered frame instead
        // of reading the previous frame's values from Update.
        private void LateUpdate()
        {
            CapturePhysicsMetrics();
            CapturePhysicsLifecycle();
            if (IsActivePerformanceSample())
            {
                RecordDetailedPerformanceSample();
            }
        }

        public static void ResolveSlowSamplingSchedule(
            float now,
            bool detailedSampling,
            ref float nextSlowSample,
            ref float nextAndroidSystemSample,
            out bool captureSlowMetrics,
            out bool captureAndroidSystemMetrics)
        {
            captureSlowMetrics = now >= nextSlowSample;
            captureAndroidSystemMetrics = false;
            if (!captureSlowMetrics)
            {
                return;
            }

            nextSlowSample = now + SlowSampleIntervalSeconds;
            if (!detailedSampling || now < nextAndroidSystemSample)
            {
                return;
            }

            nextAndroidSystemSample = now + AndroidSystemSampleIntervalSeconds;
            captureAndroidSystemMetrics = true;
        }

        private void OnApplicationFocus(bool focused)
        {
            applicationFocused = focused;
            ApplyPhysicsSuspension();
            ResetActiveSessionMetrics();
        }

        private void OnApplicationPause(bool paused)
        {
            applicationFocused = !paused;
            ApplyPhysicsSuspension();
            ResetActiveSessionMetrics();
        }

        public void SetDetailedSamplingEnabled(bool enabled)
        {
            if (detailedSamplingEnabled == enabled)
            {
                return;
            }

            detailedSamplingEnabled = enabled;
            nextSlowSampleAt = 0f;
            if (enabled)
            {
                nextAndroidSystemSampleAt = 0f;
            }
        }

        internal bool IsQaSampleActive => IsActivePerformanceSample();

        internal void ResetQaSamplingWindow()
        {
            ResetActiveSessionMetrics();
        }

        public void RecordFrameDurationMilliseconds(float frameMilliseconds)
        {
            if (!IsFinitePositive(frameMilliseconds))
            {
                return;
            }

            // Treat lifecycle gaps as non-rendering time, not as a multi-second
            // frame. The headset-presence field separately explains off-head
            // throttling when the runtime still supplies slow frames.
            frameMilliseconds = Mathf.Min(frameMilliseconds, 1000f);
            frameTimeWindow[frameTimeWriteIndex] = frameMilliseconds;
            frameTimeWriteIndex = (frameTimeWriteIndex + 1) % frameTimeWindow.Length;
            frameTimeCount = Mathf.Min(frameTimeCount + 1, frameTimeWindow.Length);
            CalculateRollingFrameStatistics(
                out var average,
                out var p50,
                out var p95,
                out var maximum);
            frameSampleCount = frameTimeCount;
            currentFps = average <= 0f ? 0f : 1000f / average;
            frameTimeP50Ms = p50;
            frameTimeP95Ms = p95;
            frameTimeMaxMs = maximum;
        }

        private void RecordActiveFrame(float frameMilliseconds, float now)
        {
            RecordFrameDurationMilliseconds(frameMilliseconds);
            activeFrameSamples.Enqueue(new TimedFrameSample(now, Mathf.Min(frameMilliseconds, 1000f)));
            PruneFrameSamples(now - 30f);
            fps5Seconds = CalculateTimedFps(now - 5f);
            fps30Seconds = CalculateTimedFps(now - 30f);
            currentFps = fps5Seconds;
            activeSessionSeconds = Mathf.Max(0f, now - activeSessionStartedAt);
        }

        public static void CalculateFrameStatistics(
            float[] samples,
            int count,
            out float average,
            out float p50,
            out float p95,
            out float maximum)
        {
            average = 0f;
            p50 = 0f;
            p95 = 0f;
            maximum = 0f;
            if (samples == null || samples.Length == 0 || count <= 0)
            {
                return;
            }

            count = Mathf.Min(count, samples.Length);
            var valid = new List<float>(count);
            for (var index = 0; index < count; index++)
            {
                var value = samples[index];
                if (IsFinitePositive(value))
                {
                    valid.Add(value);
                    average += value;
                }
            }
            if (valid.Count == 0)
            {
                average = 0f;
                return;
            }

            valid.Sort();
            average /= valid.Count;
            p50 = Percentile(valid, .5f);
            p95 = Percentile(valid, .95f);
            maximum = valid[valid.Count - 1];
        }

        private void CalculateRollingFrameStatistics(
            out float average,
            out float p50,
            out float p95,
            out float maximum)
        {
            average = 0f;
            p50 = 0f;
            p95 = 0f;
            maximum = 0f;
            var validCount = 0;
            for (var index = 0; index < frameTimeCount; index++)
            {
                var value = frameTimeWindow[index];
                if (!IsFinitePositive(value))
                {
                    continue;
                }
                sortedFrameTimeWindow[validCount++] = value;
                average += value;
            }
            if (validCount == 0)
            {
                return;
            }

            Array.Sort(sortedFrameTimeWindow, 0, validCount);
            average /= validCount;
            p50 = Percentile(sortedFrameTimeWindow, validCount, .5f);
            p95 = Percentile(sortedFrameTimeWindow, validCount, .95f);
            maximum = sortedFrameTimeWindow[validCount - 1];
        }

        private bool IsActivePerformanceSample()
        {
            return applicationFocused && !IsModelLoading() &&
                (!headsetPresenceAvailable || headsetWorn);
        }

        private bool IsModelLoading()
        {
            ResolveModelLoader();
            return currentModelLoader != null && currentModelLoader.IsLoading;
        }

        private void ResolveModelLoader()
        {
            if (currentModelLoader != null)
            {
                return;
            }
            bootstrap ??= GetComponent<QuestMmdPlayerBootstrap>();
            currentModelLoader = bootstrap == null ? null : bootstrap.ModelLoader;
        }

        private void ResetActiveSessionMetrics()
        {
            Array.Clear(frameTimeWindow, 0, frameTimeWindow.Length);
            frameTimeCount = 0;
            frameTimeWriteIndex = 0;
            frameSampleCount = 0;
            currentFps = 0f;
            fps5Seconds = 0f;
            fps30Seconds = 0f;
            frameTimeP50Ms = 0f;
            frameTimeP95Ms = 0f;
            frameTimeMaxMs = 0f;
            activeFrameSamples.Clear();
            activePhysicsDrops.Clear();
            activeSessionStartedAt = Time.unscaledTime;
            activeSessionSeconds = 0f;
            physicsObservedTotalDroppedSeconds = currentPhysicsManager == null
                ? 0f
                : Mathf.Max(0f, currentPhysicsManager.totalDroppedSimulationSeconds);
            physicsObservedDroppedFrameCount = currentPhysicsManager == null
                ? 0
                : Mathf.Max(0, currentPhysicsManager.droppedSimulationFrameCount);
            compositorObservedDroppedFrames = Mathf.Max(0f, compositorDroppedFrames);
            physicsSessionDroppedSeconds = 0f;
            physicsSessionDroppedFrameCount = 0;
            physicsDroppedMillisecondsPerSecond5s = 0f;
            physicsDroppedMillisecondsPerSecond30s = 0f;
            physicsDroppedFramePercent5s = 0f;
            physicsDroppedFramePercent30s = 0f;
            compositorDroppedFramesSession = 0f;
            detailedSampleWriteIndex = 0;
            detailedSampleCount = 0;
            physicsLastDroppedFrameCountDelta = 0;
            detailedSamplePreviousCompositorDroppedFrames = Mathf.Max(0f, compositorDroppedFrames);
            detailedWindowSummary = PerformanceWindowSummary.Empty;
            nextDetailedWindowSummaryAt = Time.unscaledTime;
            physicsLifecycleInitialized = false;
        }

        private void CapturePhysicsLifecycle()
        {
            var physics = currentPhysicsManager;
            if (physics == null)
            {
                physicsLifecycleInitialized = false;
                return;
            }

            var suspended = physics.simulationSuspended;
            var initialPosePending = physics.initialPoseSeedPending;
            if (!physicsLifecycleInitialized)
            {
                physicsLifecycleInitialized = true;
                previousPhysicsSuspended = suspended;
                previousInitialPoseSeedPending = initialPosePending;
                diagnostics?.RecordStage(
                    "mmd_physics",
                    suspended ? "processing" : "ready",
                    suspended ? "simulation_suspended" : "simulation_active",
                    queueDepth: physics.rigidBodies == null ? 0 : physics.rigidBodies.Length,
                    eventCount: physics.joints == null ? 0 : physics.joints.Length);
                diagnostics?.RecordStage(
                    "mmd_physics",
                    initialPosePending ? "processing" : "completed",
                    initialPosePending ? "initial_pose_pending" : "initial_pose_seeded");
                return;
            }

            if (suspended != previousPhysicsSuspended)
            {
                previousPhysicsSuspended = suspended;
                diagnostics?.RecordStage(
                    "mmd_physics",
                    suspended ? "processing" : "ready",
                    suspended ? "simulation_suspended" : "simulation_resumed");
            }

            if (initialPosePending != previousInitialPoseSeedPending)
            {
                previousInitialPoseSeedPending = initialPosePending;
                diagnostics?.RecordStage(
                    "mmd_physics",
                    initialPosePending ? "processing" : "completed",
                    initialPosePending ? "initial_pose_pending" : "initial_pose_seeded");
            }
        }

        private void PruneFrameSamples(float minimumTime)
        {
            while (activeFrameSamples.Count > 0 && activeFrameSamples.Peek().time < minimumTime)
            {
                activeFrameSamples.Dequeue();
            }
        }

        private float CalculateTimedFps(float minimumTime)
        {
            var count = 0;
            var totalMilliseconds = 0f;
            foreach (var sample in activeFrameSamples)
            {
                if (sample.time < minimumTime || !IsFinitePositive(sample.milliseconds))
                {
                    continue;
                }
                count++;
                totalMilliseconds += sample.milliseconds;
            }
            return count == 0 || totalMilliseconds <= 0f
                ? 0f
                : count * 1000f / totalMilliseconds;
        }

        private void RecordDetailedPerformanceSample()
        {
            var now = Time.unscaledTime;
            var physics = currentPhysicsManager;
            var compositorDelta = ResolveCounterDelta(
                detailedSamplePreviousCompositorDroppedFrames,
                compositorDroppedFrames);
            detailedSamplePreviousCompositorDroppedFrames = Mathf.Max(0f, compositorDroppedFrames);
            var sample = new PerformanceFrameSample(
                now,
                Time.frameCount,
                Mathf.Max(0f, Time.unscaledDeltaTime * 1000f),
                Mathf.Max(0f, mmdSamplingMilliseconds),
                Mathf.Max(0f, mmdSolverMilliseconds),
                Mathf.Max(0f, mmdBoneAndIkMilliseconds),
                Mathf.Max(0f, mmdPhysicsMilliseconds),
                Mathf.Max(0f, mmdFlushMilliseconds),
                Mathf.Max(0f, mmdSdefMilliseconds),
                Mathf.Max(0f, handContactMilliseconds),
                Mathf.Max(0, physicsLastSubsteps),
                Mathf.Max(0f, physicsLastDroppedSeconds),
                Mathf.Max(0, physicsLastDroppedFrameCountDelta),
                Mathf.Max(0f, compositorDelta),
                xrPerformanceMetricsAvailable ? Mathf.Max(0f, xrAppCpuTimeMs) : 0f,
                xrPerformanceMetricsAvailable ? Mathf.Max(0f, xrAppGpuTimeMs) : 0f,
                ResolveCurrentActionLabel(),
                physics != null && physics.simulationSuspended,
                physics != null && physics.initialPoseSeedPending);
            detailedSamples[detailedSampleWriteIndex] = sample;
            detailedSampleWriteIndex = (detailedSampleWriteIndex + 1) % detailedSamples.Length;
            detailedSampleCount = Mathf.Min(detailedSampleCount + 1, detailedSamples.Length);
            if (now >= nextDetailedWindowSummaryAt)
            {
                UpdateDetailedWindowSummary(now);
                nextDetailedWindowSummaryAt = now + .5f;
            }
        }

        private string ResolveCurrentActionLabel()
        {
            bootstrap ??= GetComponent<QuestMmdPlayerBootstrap>();
            var owner = bootstrap;
            var vmd = owner == null ? null : owner.VmdActions;
            if (vmd != null && (vmd.IsLoading || vmd.IsPlaying || vmd.IsHoldingEndPose || vmd.IsBlendingOut) &&
                !string.IsNullOrEmpty(vmd.CurrentActionId))
            {
                return vmd.CurrentActionId;
            }
            var avatar = owner == null ? null : owner.Avatar;
            return avatar == null || string.IsNullOrEmpty(avatar.CurrentAction)
                ? "none"
                : avatar.CurrentAction;
        }

        private void UpdateDetailedWindowSummary(float now)
        {
            if (detailedSampleCount <= 0)
            {
                detailedWindowSummary = PerformanceWindowSummary.Empty;
                return;
            }

            var cutoff = now - DetailedWindowSeconds;
            var validCount = 0;
            var firstTimestamp = now;
            var lastTimestamp = now;
            var physicsDroppedSeconds = 0f;
            var physicsDroppedFrames = 0;
            var firstPhysicsDrop = 0f;
            var longFrameCount = 0;
            var firstLongFrameOffset = -1f;
            var compositorDropped = 0f;
            var firstCompositorDrop = 0f;
            var firstPhysicsDropOffset = -1f;
            var firstCompositorDropOffset = -1f;
            var currentAction = "none";
            var physicsSuspended = false;
            var initialPoseSeedPending = false;
            for (var offset = detailedSampleCount - 1; offset >= 0; offset--)
            {
                var index = (detailedSampleWriteIndex - 1 - offset + detailedSamples.Length) % detailedSamples.Length;
                var sample = detailedSamples[index];
                if (sample.Timestamp < cutoff)
                {
                    continue;
                }
                if (validCount == 0)
                {
                    firstTimestamp = sample.Timestamp;
                }
                lastTimestamp = sample.Timestamp;
                validCount++;
                physicsDroppedSeconds += sample.PhysicsDroppedSeconds;
                physicsDroppedFrames += sample.PhysicsDroppedFrameCount;
                if (firstPhysicsDropOffset < 0f &&
                    (sample.PhysicsDroppedSeconds > 0f || sample.PhysicsDroppedFrameCount > 0))
                {
                    firstPhysicsDrop = sample.PhysicsDroppedSeconds;
                    firstPhysicsDropOffset = Mathf.Max(0f, (sample.Timestamp - firstTimestamp) * 1000f);
                }
                if (sample.UnscaledDeltaMilliseconds >= 1000f / 30f)
                {
                    longFrameCount++;
                    if (firstLongFrameOffset < 0f)
                    {
                        firstLongFrameOffset = Mathf.Max(0f, (sample.Timestamp - firstTimestamp) * 1000f);
                    }
                }
                compositorDropped += sample.CompositorDroppedFramesDelta;
                if (firstCompositorDrop <= 0f && sample.CompositorDroppedFramesDelta > 0f)
                {
                    firstCompositorDrop = sample.CompositorDroppedFramesDelta;
                    firstCompositorDropOffset = Mathf.Max(0f, (sample.Timestamp - firstTimestamp) * 1000f);
                }
                currentAction = sample.Action;
                physicsSuspended = sample.PhysicsSuspended;
                initialPoseSeedPending = sample.InitialPoseSeedPending;
            }

            if (validCount <= 0)
            {
                detailedWindowSummary = PerformanceWindowSummary.Empty;
                return;
            }

            detailedWindowSummary = new PerformanceWindowSummary(
                validCount,
                Mathf.Max(0f, lastTimestamp - firstTimestamp),
                CalculateDetailedPercentile(cutoff, PerformanceMetric.Frame, .95f),
                CalculateDetailedMaximum(cutoff, PerformanceMetric.Frame),
                CalculateDetailedPercentile(cutoff, PerformanceMetric.MmdSolver, .95f),
                CalculateDetailedPercentile(cutoff, PerformanceMetric.MmdPhysics, .95f),
                CalculateDetailedPercentile(cutoff, PerformanceMetric.MmdBoneAndIk, .95f),
                CalculateDetailedPercentile(cutoff, PerformanceMetric.MmdFlush, .95f),
                CalculateDetailedPercentile(cutoff, PerformanceMetric.MmdSdef, .95f),
                CalculateDetailedPercentile(cutoff, PerformanceMetric.HandContact, .95f),
                physicsDroppedSeconds,
                physicsDroppedFrames,
                firstPhysicsDrop,
                longFrameCount,
                firstLongFrameOffset,
                compositorDropped,
                firstCompositorDrop,
                firstPhysicsDropOffset,
                firstCompositorDropOffset,
                currentAction,
                physicsSuspended,
                initialPoseSeedPending);
        }

        private enum PerformanceMetric
        {
            Frame,
            MmdSolver,
            MmdPhysics,
            MmdBoneAndIk,
            MmdFlush,
            MmdSdef,
            HandContact
        }

        private float CalculateDetailedPercentile(float cutoff, PerformanceMetric metric, float percentile)
        {
            var count = 0;
            for (var offset = 0; offset < detailedSampleCount; offset++)
            {
                var index = (detailedSampleWriteIndex - 1 - offset + detailedSamples.Length) % detailedSamples.Length;
                var sample = detailedSamples[index];
                if (sample.Timestamp < cutoff)
                {
                    continue;
                }
                var value = GetMetricValue(sample, metric);
                if (!IsFinitePositive(value))
                {
                    continue;
                }
                detailedScratch[count++] = value;
            }
            if (count == 0)
            {
                return 0f;
            }
            Array.Sort(detailedScratch, 0, count);
            return Percentile(detailedScratch, count, percentile);
        }

        private float CalculateDetailedMaximum(float cutoff, PerformanceMetric metric)
        {
            var maximum = 0f;
            for (var offset = 0; offset < detailedSampleCount; offset++)
            {
                var index = (detailedSampleWriteIndex - 1 - offset + detailedSamples.Length) % detailedSamples.Length;
                var sample = detailedSamples[index];
                if (sample.Timestamp < cutoff)
                {
                    continue;
                }
                maximum = Mathf.Max(maximum, GetMetricValue(sample, metric));
            }
            return maximum;
        }

        private static float GetMetricValue(PerformanceFrameSample sample, PerformanceMetric metric)
        {
            switch (metric)
            {
                case PerformanceMetric.MmdSolver: return sample.MmdSolverMilliseconds;
                case PerformanceMetric.MmdPhysics: return sample.MmdPhysicsMilliseconds;
                case PerformanceMetric.MmdBoneAndIk: return sample.MmdBoneAndIkMilliseconds;
                case PerformanceMetric.MmdFlush: return sample.MmdFlushMilliseconds;
                case PerformanceMetric.MmdSdef: return sample.MmdSdefMilliseconds;
                case PerformanceMetric.HandContact: return sample.HandContactMilliseconds;
                default: return sample.UnscaledDeltaMilliseconds;
            }
        }

        private void UpdateRecentPhysicsRates(float now)
        {
            while (activePhysicsDrops.Count > 0 && activePhysicsDrops.Peek().time < now - 30f)
            {
                activePhysicsDrops.Dequeue();
            }
            CalculatePhysicsWindow(now - 5f,
                out var dropped5, out var frames5, out var samples5);
            CalculatePhysicsWindow(now - 30f,
                out var dropped30, out var frames30, out var samples30);
            physicsDroppedMillisecondsPerSecond5s = dropped5 * 1000f / Mathf.Max(.001f, Mathf.Min(5f, activeSessionSeconds));
            physicsDroppedMillisecondsPerSecond30s = dropped30 * 1000f / Mathf.Max(.001f, Mathf.Min(30f, activeSessionSeconds));
            physicsDroppedFramePercent5s = samples5 == 0 ? 0f : frames5 * 100f / samples5;
            physicsDroppedFramePercent30s = samples30 == 0 ? 0f : frames30 * 100f / samples30;
        }

        private void CalculatePhysicsWindow(
            float minimumTime,
            out float droppedSeconds,
            out int droppedFrames,
            out int sampleCount)
        {
            droppedSeconds = 0f;
            droppedFrames = 0;
            sampleCount = 0;
            foreach (var sample in activePhysicsDrops)
            {
                if (sample.time < minimumTime)
                {
                    continue;
                }
                sampleCount++;
                droppedSeconds += sample.seconds;
                if (sample.dropped)
                {
                    droppedFrames++;
                }
            }
        }

        public static long EstimateRgbaTextureBytes(int width, int height)
        {
            if (width <= 0 || height <= 0)
            {
                return 0L;
            }
            var pixels = (long)width * height;
            return pixels > long.MaxValue / 4L ? long.MaxValue : pixels * 4L;
        }

        public static DeviceThermalState MapAndroidThermalStatus(int value)
        {
            return value >= 0 && value <= 6
                ? (DeviceThermalState)value
                : DeviceThermalState.Unknown;
        }

        private static float Percentile(List<float> sorted, float percentile)
        {
            if (sorted == null || sorted.Count == 0)
            {
                return 0f;
            }
            var position = Mathf.Clamp01(percentile) * (sorted.Count - 1);
            var lower = Mathf.FloorToInt(position);
            var upper = Mathf.CeilToInt(position);
            return Mathf.Lerp(sorted[lower], sorted[upper], position - lower);
        }

        private static float Percentile(float[] sorted, int count, float percentile)
        {
            if (sorted == null || count <= 0)
            {
                return 0f;
            }
            var position = Mathf.Clamp01(percentile) * (count - 1);
            var lower = Mathf.FloorToInt(position);
            var upper = Mathf.CeilToInt(position);
            return Mathf.Lerp(sorted[lower], sorted[upper], position - lower);
        }

        private static bool IsFinitePositive(float value)
        {
            return value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private void CaptureFrameTiming()
        {
            var count = FrameTimingManager.GetLatestTimings(1u, frameTimings);
            if (count == 0)
            {
                cpuFrameTimeAvailable = false;
                gpuFrameTimeAvailable = false;
                return;
            }

            var cpu = (float)frameTimings[0].cpuFrameTime;
            var gpu = (float)frameTimings[0].gpuFrameTime;
            cpuFrameTimeAvailable = IsFinitePositive(cpu);
            gpuFrameTimeAvailable = IsFinitePositive(gpu);
            cpuFrameTimeMs = cpuFrameTimeAvailable ? cpu : 0f;
            gpuFrameTimeMs = gpuFrameTimeAvailable ? gpu : 0f;
        }

        private void CaptureXrPerformanceMetrics()
        {
            xrPerformanceMetricsAvailable = false;
            compositorDroppedFramesAvailable = false;
            xrDisplays.Clear();
            SubsystemManager.GetInstances(xrDisplays);
            for (var index = 0; index < xrDisplays.Count; index++)
            {
                var display = xrDisplays[index];
                if (display == null || !display.running)
                {
                    continue;
                }
                var hasCpu = ProviderXRStats.TryGetStat(display, "perfmetrics.appcputime", out var cpu);
                var hasGpu = ProviderXRStats.TryGetStat(display, "perfmetrics.appgputime", out var gpu);
                var hasCpuUtil = ProviderXRStats.TryGetStat(display, "perfmetrics.cpuutilavg", out var cpuUtil);
                var hasGpuUtil = ProviderXRStats.TryGetStat(display, "perfmetrics.gpuutil", out var gpuUtil);
                var hasDrops = ProviderXRStats.TryGetStat(display, "appstats.compositordroppedframes", out var drops);
                xrPerformanceMetricsAvailable = hasCpu || hasGpu || hasCpuUtil || hasGpuUtil || hasDrops;
                xrAppCpuTimeMs = hasCpu && cpu >= 0f ? cpu : 0f;
                xrAppGpuTimeMs = hasGpu && gpu >= 0f ? gpu : 0f;
                xrCpuUtilization = hasCpuUtil && cpuUtil >= 0f ? cpuUtil : 0f;
                xrGpuUtilization = hasGpuUtil && gpuUtil >= 0f ? gpuUtil : 0f;
                if (hasDrops && drops >= 0f)
                {
                    compositorDroppedFramesAvailable = true;
                    var droppedDelta = ResolveCounterDelta(compositorObservedDroppedFrames, drops);
                    compositorObservedDroppedFrames = drops;
                    compositorDroppedFrames = drops;
                    if (IsActivePerformanceSample())
                    {
                        compositorDroppedFramesSession += droppedDelta;
                    }
                }
                return;
            }
        }

        private void CaptureHeadsetState()
        {
            var previousAvailable = headsetPresenceAvailable;
            var previousWorn = headsetWorn;
            var headset = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (headset.isValid &&
                headset.TryGetFeatureValue(CommonUsages.userPresence, out var present))
            {
                headsetPresenceAvailable = true;
                headsetWorn = present;
            }
            else if (!previousAvailable)
            {
                // Keep the explicit "presence unavailable" fallback. Once a
                // runtime has supplied presence, a transient failed feature
                // read must not split the worn session or resume physics.
                headsetPresenceAvailable = false;
                headsetWorn = false;
            }
            if (previousAvailable != headsetPresenceAvailable || previousWorn != headsetWorn)
            {
                ApplyPhysicsSuspension();
                ResetActiveSessionMetrics();
            }
        }

        private void ApplyPhysicsSuspension()
        {
            if (currentPhysicsManager == null)
            {
                return;
            }
            var shouldSuspend = !applicationFocused || (headsetPresenceAvailable && !headsetWorn);
            var wasSuspended = currentPhysicsManager.simulationSuspended;
            currentPhysicsManager.SetSimulationSuspended(shouldSuspend);
            if (wasSuspended && !shouldSuspend)
            {
                // Banxia reseed patch: after resuming, reseed physics from the current pose so stale kinetic
                // anchors do not yank hair/cloth toward an outdated pose (same contract as the VMD resume path).
                currentPhysicsManager.ReseedFromCurrentPose();
            }
        }

        private void CaptureTargetFrameRate()
        {
            targetFpsAvailable = false;
            targetFps = 0f;
            xrDisplays.Clear();
            SubsystemManager.GetInstances(xrDisplays);
            for (var index = 0; index < xrDisplays.Count; index++)
            {
                var display = xrDisplays[index];
                if (display != null && display.running &&
                    display.TryGetDisplayRefreshRate(out var refreshRate) &&
                    IsFinitePositive(refreshRate))
                {
                    targetFpsAvailable = true;
                    targetFps = refreshRate;
                    return;
                }
            }

            if (Application.targetFrameRate > 0)
            {
                targetFpsAvailable = true;
                targetFps = Application.targetFrameRate;
            }
        }

        private void CaptureMemoryAndGc()
        {
            totalAllocatedMemoryBytes = Math.Max(0L, Profiler.GetTotalAllocatedMemoryLong());
            totalReservedMemoryBytes = Math.Max(0L, Profiler.GetTotalReservedMemoryLong());
            managedUsedMemoryBytes = Math.Max(0L, Profiler.GetMonoUsedSizeLong());
            gcGeneration0Collections = Math.Max(0, GC.CollectionCount(0));
            gcGeneration1Collections = Math.Max(0, GC.CollectionCount(1));
            gcGeneration2Collections = Math.Max(0, GC.CollectionCount(2));
        }

        private void ScheduleAndroidMetricsCapture()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (ShouldStartAndroidSystemSample(
                    androidSystemMetricsTask != null,
                    androidSystemMetricsTask != null && androidSystemMetricsTask.IsCompleted))
            {
                androidSystemMetricsTask = Task.Run(QueryAndroidSystemMetrics);
            }
#else
            ApplyAndroidSystemMetrics(default);
#endif
        }

        public static bool ShouldStartAndroidSystemSample(bool taskExists, bool taskCompleted)
        {
            return !taskExists || taskCompleted;
        }

        private void PublishCompletedAndroidMetrics()
        {
            var task = androidSystemMetricsTask;
            if (task == null || !task.IsCompleted)
            {
                return;
            }

            androidSystemMetricsTask = null;
            ApplyAndroidSystemMetrics(task.Status == TaskStatus.RanToCompletion
                ? task.Result
                : default);
        }

        private void ApplyAndroidSystemMetrics(AndroidSystemMetricsSample sample)
        {
            androidPssAvailable = sample.pssAvailable;
            androidPssBytes = sample.pssAvailable ? sample.pssBytes : 0L;
            thermalStatusAvailable = sample.thermalAvailable;
            thermalState = sample.thermalAvailable
                ? sample.thermalState
                : DeviceThermalState.Unavailable;
        }

        private static AndroidSystemMetricsSample QueryAndroidSystemMetrics()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var pssAvailable = false;
            var pssBytes = 0L;
            var thermalAvailable = false;
            var mappedThermalState = DeviceThermalState.Unavailable;
            AndroidJNI.AttachCurrentThread();
            try
            {
                try
                {
                    using (var debug = new AndroidJavaClass("android.os.Debug"))
                    {
                        var pssKilobytes = debug.CallStatic<long>("getPss");
                        if (pssKilobytes >= 0L && pssKilobytes <= long.MaxValue / BytesPerKilobyte)
                        {
                            pssAvailable = true;
                            pssBytes = pssKilobytes * BytesPerKilobyte;
                        }
                    }
                }
                catch (Exception)
                {
                    pssAvailable = false;
                    pssBytes = 0L;
                }

                try
                {
                    using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                    using (var power = activity.Call<AndroidJavaObject>("getSystemService", "power"))
                    {
                        var status = power.Call<int>("getCurrentThermalStatus");
                        thermalAvailable = true;
                        mappedThermalState = MapAndroidThermalStatus(status);
                    }
                }
                catch (Exception)
                {
                    thermalAvailable = false;
                    mappedThermalState = DeviceThermalState.Unavailable;
                }
            }
            finally
            {
                AndroidJNI.DetachCurrentThread();
            }

            return new AndroidSystemMetricsSample(
                pssAvailable,
                pssBytes,
                thermalAvailable,
                mappedThermalState);
#else
            return default;
#endif
        }

        private void CaptureModelComplexityIfChanged()
        {
            ResolveModelLoader();
            var loader = currentModelLoader;
            var root = loader == null ? null : loader.CurrentModel;
            var instanceId = root == null ? 0 : root.GetInstanceID();
            if (instanceId == currentModelInstanceId)
            {
                return;
            }

            currentModelInstanceId = instanceId;
            ResetModelComplexity();
            currentPhysicsManager = null;
            currentTransformManager = null;
            ResetActiveSessionMetrics();
            if (root == null)
            {
                return;
            }

            modelLoaded = true;
            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            var textures = new HashSet<Texture>();
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            modelRendererCount = renderers.Length;
            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                if (renderer == null)
                {
                    continue;
                }
                var sharedMaterials = renderer.sharedMaterials;
                for (var materialIndex = 0; materialIndex < sharedMaterials.Length; materialIndex++)
                {
                    var material = sharedMaterials[materialIndex];
                    if (material == null || !materials.Add(material))
                    {
                        continue;
                    }
                    var textureNames = material.GetTexturePropertyNames();
                    for (var textureIndex = 0; textureIndex < textureNames.Length; textureIndex++)
                    {
                        var texture = material.GetTexture(textureNames[textureIndex]);
                        if (texture != null && textures.Add(texture))
                        {
                            modelEstimatedTextureBytes = SaturatingAdd(
                                modelEstimatedTextureBytes,
                                EstimateRgbaTextureBytes(texture.width, texture.height));
                        }
                    }
                }

                Mesh mesh = null;
                if (renderer is SkinnedMeshRenderer skinned)
                {
                    mesh = skinned.sharedMesh;
                }
                else
                {
                    var filter = renderer.GetComponent<MeshFilter>();
                    mesh = filter == null ? null : filter.sharedMesh;
                }
                if (mesh == null || !meshes.Add(mesh))
                {
                    continue;
                }
                modelVertexCount = SaturatingAdd(modelVertexCount, mesh.vertexCount);
                modelBlendShapeCount = SaturatingAdd(modelBlendShapeCount, mesh.blendShapeCount);
                for (var submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    modelTriangleCount = SaturatingAdd(modelTriangleCount, mesh.GetIndexCount(submesh) / 3L);
                }
            }
            modelMaterialCount = materials.Count;
            modelTextureCount = textures.Count;

            currentTransformManager = root.GetComponentInChildren<MMDTransformManager>(true);
            modelBoneCount = currentTransformManager == null || currentTransformManager.bones == null
                ? 0
                : currentTransformManager.bones.Length;
            var physics = root.GetComponentInChildren<MMDPhysicsManager>(true);
            currentPhysicsManager = physics;
            if (physics != null && physics.enableGroundCollision)
            {
                // Banxia ground-plane patch: the infinite y=0 ground never matches a real MR floor and
                // no dynamic body rests near it, so its contact pairs are pure solver cost. Disable once
                // per model; the flag also keeps future rebuilds ground-free (BuildGround reads it).
                physics.SetGroundCollisionEnabled(false);
                Debug.Log("[Perf] ground collision disabled (no dynamic body rests near y=0 plane)", this);
            }
            currentTrackedHands ??= GetComponent<QuestTrackedHandVisualizer>();
            ApplyPhysicsSuspension();
            physicsObservedTotalDroppedSeconds = physics == null
                ? 0f
                : Mathf.Max(0f, physics.totalDroppedSimulationSeconds);
            physicsObservedDroppedFrameCount = physics == null
                ? 0
                : Mathf.Max(0, physics.droppedSimulationFrameCount);
            modelRigidBodyCount = physics == null || physics.rigidBodies == null ? 0 : physics.rigidBodies.Length;
            modelJointCount = physics == null || physics.joints == null ? 0 : physics.joints.Length;
        }

        private void CapturePhysicsMetrics()
        {
            var physics = currentPhysicsManager;
            physicsMetricsAvailable = physics != null;
            if (!physicsMetricsAvailable)
            {
                physicsFrequencyHz = 0;
                physicsMaximumSubstepsPerFrame = 0;
                physicsLastSubsteps = 0;
                physicsLastDroppedSeconds = 0f;
                physicsTotalDroppedSeconds = 0f;
                physicsDroppedFrameCount = 0;
                physicsPoseSourceFlipFrames = 0;
                physicsSessionDroppedSeconds = 0f;
                physicsSessionDroppedFrameCount = 0;
                physicsDroppedMillisecondsPerSecond5s = 0f;
                physicsDroppedMillisecondsPerSecond30s = 0f;
                physicsDroppedFramePercent5s = 0f;
                physicsDroppedFramePercent30s = 0f;
                mmdSamplingMilliseconds = 0f;
                mmdSolverMilliseconds = 0f;
                mmdBoneAndIkMilliseconds = 0f;
                mmdPhysicsMilliseconds = 0f;
                mmdFlushMilliseconds = 0f;
                mmdSdefMilliseconds = 0f;
                handContactMilliseconds = 0f;
                physicsLastDroppedFrameCountDelta = 0;
                return;
            }

            physicsFrequencyHz = MMDPhysicsManager.simulationFrequencyHz;
            physicsMaximumSubstepsPerFrame = MMDPhysicsManager.maximumSubstepsPerFrame;
            physicsLastSubsteps = Mathf.Max(0, physics.lastSimulationSubstepCount);
            physicsLastDroppedSeconds = Mathf.Max(0f, physics.lastDroppedSimulationSeconds);
            physicsTotalDroppedSeconds = Mathf.Max(0f, physics.totalDroppedSimulationSeconds);
            physicsDroppedFrameCount = Mathf.Max(0, physics.droppedSimulationFrameCount);
            physicsPoseSourceFlipFrames = Mathf.Max(0, physics.totalPoseSourceFlipFrames);
            var droppedSecondsDelta = ResolvePhysicsDropDelta(
                physicsObservedTotalDroppedSeconds,
                physicsTotalDroppedSeconds);
            var droppedFrameDelta = Mathf.Max(
                0,
                physicsDroppedFrameCount - physicsObservedDroppedFrameCount);
            physicsLastDroppedFrameCountDelta = droppedFrameDelta;
            physicsObservedTotalDroppedSeconds = physicsTotalDroppedSeconds;
            physicsObservedDroppedFrameCount = physicsDroppedFrameCount;
            var transformManager = currentTransformManager;
            mmdSamplingMilliseconds = transformManager == null ? 0f : Mathf.Max(0f, transformManager.lastSamplingMilliseconds);
            mmdSolverMilliseconds = transformManager == null ? 0f : Mathf.Max(0f, transformManager.lastSolverMilliseconds);
            mmdBoneAndIkMilliseconds = transformManager == null ? 0f : Mathf.Max(0f, transformManager.lastBoneAndIkMilliseconds);
            mmdPhysicsMilliseconds = transformManager == null ? 0f : Mathf.Max(0f, transformManager.lastPhysicsMilliseconds);
            mmdFlushMilliseconds = transformManager == null ? 0f : Mathf.Max(0f, transformManager.lastFlushMilliseconds);
            mmdSdefMilliseconds = transformManager == null ? 0f : Mathf.Max(0f, transformManager.lastSdefMilliseconds);
            var trackedHands = currentTrackedHands;
            handContactMilliseconds = trackedHands == null
                ? 0f
                : Mathf.Max(0f, trackedHands.LastContactEvaluationMilliseconds);
            if (IsActivePerformanceSample())
            {
                physicsSessionDroppedSeconds += droppedSecondsDelta;
                physicsSessionDroppedFrameCount += droppedFrameDelta;
                activePhysicsDrops.Enqueue(new PhysicsDropSample(
                    Time.unscaledTime,
                    droppedSecondsDelta,
                    droppedFrameDelta > 0));
                UpdateRecentPhysicsRates(Time.unscaledTime);
            }
        }

        public static float ResolvePhysicsDropDelta(
            float previousTotalDroppedSeconds,
            float currentTotalDroppedSeconds)
        {
            if (!IsFinitePositive(currentTotalDroppedSeconds))
            {
                return 0f;
            }

            if (!IsFinitePositive(previousTotalDroppedSeconds) ||
                currentTotalDroppedSeconds < previousTotalDroppedSeconds)
            {
                return currentTotalDroppedSeconds;
            }

            return currentTotalDroppedSeconds - previousTotalDroppedSeconds;
        }

        private static float ResolveCounterDelta(float previous, float current)
        {
            if (!IsFinitePositive(current))
            {
                return 0f;
            }
            if (!IsFinitePositive(previous) || current < previous)
            {
                return current;
            }
            return current - previous;
        }

        private void ResetModelComplexity()
        {
            modelLoaded = false;
            modelRendererCount = 0;
            modelMaterialCount = 0;
            modelTextureCount = 0;
            modelEstimatedTextureBytes = 0L;
            modelVertexCount = 0;
            modelTriangleCount = 0L;
            modelBlendShapeCount = 0;
            modelBoneCount = 0;
            modelRigidBodyCount = 0;
            modelJointCount = 0;
            currentPhysicsManager = null;
            currentTransformManager = null;
        }

        private static int SaturatingAdd(int left, int right)
        {
            return right > 0 && left > int.MaxValue - right ? int.MaxValue : left + right;
        }

        private static long SaturatingAdd(long left, long right)
        {
            return right > 0L && left > long.MaxValue - right ? long.MaxValue : left + right;
        }
    }
}

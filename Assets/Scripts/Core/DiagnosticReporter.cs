using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace QuestMmdPlayer
{
    /// <summary>One client-side turn span of the diagnostics@1.0 contract.</summary>
    internal readonly struct ClientTurnSpan
    {
        public ClientTurnSpan(
            string component,
            string stage,
            string status,
            string code,
            int startOffsetMs,
            int endOffsetMs,
            int durationMs,
            int chunks)
        {
            Component = component ?? string.Empty;
            Stage = stage ?? string.Empty;
            Status = status ?? "completed";
            Code = code ?? string.Empty;
            StartOffsetMs = startOffsetMs;
            EndOffsetMs = endOffsetMs;
            DurationMs = durationMs;
            Chunks = chunks;
        }

        public string Component { get; }
        public string Stage { get; }
        public string Status { get; }
        public string Code { get; }
        public int StartOffsetMs { get; }
        public int EndOffsetMs { get; }
        public int DurationMs { get; }
        public int Chunks { get; }
    }

    /// <summary>One finished turn's client-side span bundle.</summary>
    internal readonly struct ClientTurnDiagnostics
    {
        public ClientTurnDiagnostics(string turnId, string traceId, ClientTurnSpan[] spans)
        {
            TurnId = turnId ?? string.Empty;
            TraceId = traceId ?? string.Empty;
            Spans = spans ?? Array.Empty<ClientTurnSpan>();
        }

        public string TurnId { get; }
        public string TraceId { get; }
        public ClientTurnSpan[] Spans { get; }
    }

    [Serializable]
    internal sealed class DiagnosticsSpanPayload
    {
        public string component = "";
        public string stage = "";
        public string status = "completed";
        public string code = "";
        public int start_offset_ms;
        public int end_offset_ms;
        public int duration_ms = -1;
        public int chunks;
    }

    [Serializable]
    internal sealed class DiagnosticsSpansReportPayload
    {
        public string type = "diagnostics.report";
        public string protocol_version = "1.0";
        public string kind = "spans";
        public string session_id = "";
        public string turn_id = "";
        public string trace_id = "";
        public long ts_ms;
        public DiagnosticsSpanPayload[] spans = new DiagnosticsSpanPayload[0];
    }

    [Serializable]
    internal sealed class DiagnosticsPerfReportPayload
    {
        public string type = "diagnostics.report";
        public string protocol_version = "1.0";
        public string kind = "perf";
        public string session_id = "";
        public string turn_id = "";
        public string trace_id = "";
        public long ts_ms;
        public float fps = -1f;
        public float frame_p50_ms = -1f;
        public float frame_p95_ms = -1f;
        public float frame_max_ms = -1f;
        public float compositor_dropped_session = -1f;
        public float physics_dropped_s = -1f;
        public int physics_dropped_frames = -1;
        public float xr_cpu_ms = -1f;
        public float xr_gpu_ms = -1f;
        public float cpu_util = -1f;
        public float gpu_util = -1f;
        public float mmd_solver_ms = -1f;
        public float mmd_physics_ms = -1f;
        public float mmd_bone_ik_ms = -1f;
        public float mmd_sdef_ms = -1f;
        public float mmd_flush_ms = -1f;
        public float hand_contact_ms = -1f;
        public long mem_alloc_bytes = -1;
        public long mem_pss_bytes = -1;
        public int gc0 = -1;
        public int gc1 = -1;
        public int gc2 = -1;
        public string thermal_state = "";
        public int model_renderer = -1;
        public int model_material = -1;
        public int model_texture = -1;
        public int model_vertex = -1;
        public int model_tri = -1;
        public int model_bone = -1;
        public int model_rigid = -1;
        public int model_joint = -1;
        public float target_fps = -1f;
        public float render_scale;
        public bool headset_worn;
        public string active_action = "";
        public int physics_hz;
        public int physics_substeps;
    }

    /// <summary>
    /// Quest→临 diagnostics@1.0 uploader (plan §Batch B). Performance snapshots
    /// are pushed at a low cadence only while detailed sampling (or QA mode) is
    /// active; turn span bundles are pushed once per turn boundary. All reports
    /// go through the bridge's low-priority bounded queue and are dropped, not
    /// retried, on failure — they must never disturb the audio/SSE path.
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(10500)]
    public sealed class DiagnosticReporter : MonoBehaviour
    {
        [SerializeField] private bool uploadEnabled = true;
        [SerializeField] private bool qaMode;
        [SerializeField, Range(1f, 30f)] private float perfIntervalSeconds = 2f;

        private RuntimePerformanceMonitor monitor;
        private QuestQualitySettings quality;
        private AstrBotBridge bridge;
        private float nextPerfAt;

        public bool UploadEnabled => uploadEnabled;

        public void Bind(
            RuntimePerformanceMonitor performanceMonitor,
            QuestQualitySettings qualitySettings,
            AstrBotBridge bridgeComponent)
        {
            monitor = performanceMonitor;
            quality = qualitySettings;
            bridge = bridgeComponent;
            nextPerfAt = 0f;
        }

        private void Update()
        {
            if (!uploadEnabled || monitor == null || bridge == null)
            {
                return;
            }
            if (!qaMode && !monitor.detailedSamplingEnabled)
            {
                return;
            }
            if (Time.unscaledTime < nextPerfAt)
            {
                return;
            }
            nextPerfAt = Time.unscaledTime + perfIntervalSeconds;
            Enqueue(JsonUtility.ToJson(BuildPerfPayload()));
        }

        internal void SubmitTurnDiagnostics(ClientTurnDiagnostics turn)
        {
            if (!uploadEnabled || bridge == null ||
                turn.Spans == null || turn.Spans.Length == 0)
            {
                return;
            }
            var spans = new DiagnosticsSpanPayload[turn.Spans.Length];
            for (var index = 0; index < turn.Spans.Length; index++)
            {
                var span = turn.Spans[index];
                spans[index] = new DiagnosticsSpanPayload
                {
                    component = BoundedToken(span.Component, 48),
                    stage = BoundedToken(span.Stage, 48),
                    status = BoundedToken(span.Status, 32),
                    code = BoundedToken(span.Code, 64),
                    start_offset_ms = ClampCount(span.StartOffsetMs, 3600000),
                    end_offset_ms = ClampCount(span.EndOffsetMs, 3600000),
                    duration_ms = ClampCount(span.DurationMs, 3600000),
                    chunks = ClampCount(span.Chunks, 1000000)
                };
            }
            var payload = new DiagnosticsSpansReportPayload
            {
                session_id = BoundedToken(bridge.SessionId, 64),
                turn_id = BoundedToken(turn.TurnId, 64),
                trace_id = BoundedToken(turn.TraceId, 64),
                ts_ms = NowMilliseconds(),
                spans = spans
            };
            Enqueue(JsonUtility.ToJson(payload));
        }

        private void Enqueue(string json)
        {
            if (string.IsNullOrEmpty(json) || bridge == null)
            {
                return;
            }
            // The bridge queue is bounded and drop-oldest; failures never retry.
            bridge.EnqueueDiagnosticsReport(json);
        }

        private DiagnosticsPerfReportPayload BuildPerfPayload()
        {
            var payload = new DiagnosticsPerfReportPayload
            {
                session_id = BoundedToken(bridge != null ? bridge.SessionId : null, 64),
                ts_ms = NowMilliseconds(),
                fps = ClampMetric(monitor.currentFps, 1000f),
                frame_p50_ms = ClampMetric(monitor.frameTimeP50Ms, 10000f),
                frame_p95_ms = ClampMetric(monitor.frameTimeP95Ms, 10000f),
                frame_max_ms = ClampMetric(monitor.frameTimeMaxMs, 10000f),
                compositor_dropped_session = monitor.compositorDroppedFramesAvailable
                    ? ClampMetric(monitor.compositorDroppedFramesSession, 1000000f)
                    : -1f,
                physics_dropped_s = ClampMetric(monitor.physicsSessionDroppedSeconds, 1000000f),
                physics_dropped_frames = ClampCount(
                    monitor.physicsSessionDroppedFrameCount, 10000000),
                xr_cpu_ms = monitor.xrPerformanceMetricsAvailable
                    ? ClampMetric(monitor.xrAppCpuTimeMs, 10000f)
                    : -1f,
                xr_gpu_ms = monitor.xrPerformanceMetricsAvailable
                    ? ClampMetric(monitor.xrAppGpuTimeMs, 10000f)
                    : -1f,
                cpu_util = monitor.xrPerformanceMetricsAvailable
                    ? ClampMetric(monitor.xrCpuUtilization, 1000f)
                    : -1f,
                gpu_util = monitor.xrPerformanceMetricsAvailable
                    ? ClampMetric(monitor.xrGpuUtilization, 1000f)
                    : -1f,
                mem_alloc_bytes = ClampBytes(monitor.totalAllocatedMemoryBytes),
                mem_pss_bytes = monitor.androidPssAvailable
                    ? ClampBytes(monitor.androidPssBytes)
                    : -1,
                gc0 = ClampCount(monitor.gcGeneration0Collections, 1000000),
                gc1 = ClampCount(monitor.gcGeneration1Collections, 1000000),
                gc2 = ClampCount(monitor.gcGeneration2Collections, 1000000),
                thermal_state = monitor.thermalStatusAvailable
                    ? BoundedToken(monitor.thermalState.ToString(), 32)
                    : string.Empty,
                target_fps = monitor.targetFpsAvailable
                    ? ClampMetric(monitor.targetFps, 1000f)
                    : -1f,
                render_scale = quality != null
                    ? Mathf.Clamp(quality.RenderScale, 0f, 4f)
                    : 0f,
                headset_worn = monitor.headsetPresenceAvailable && monitor.headsetWorn,
                active_action = BoundedToken(monitor.detailedWindow.CurrentAction, 48),
                physics_hz = monitor.physicsMetricsAvailable
                    ? Mathf.Clamp(monitor.physicsFrequencyHz, 0, 1000)
                    : 0,
                physics_substeps = monitor.physicsMetricsAvailable
                    ? Mathf.Clamp(monitor.physicsMaximumSubstepsPerFrame, 0, 16)
                    : 0
            };
            if (monitor.modelLoaded)
            {
                payload.model_renderer = ClampCount(monitor.modelRendererCount, 100000);
                payload.model_material = ClampCount(monitor.modelMaterialCount, 100000);
                payload.model_texture = ClampCount(monitor.modelTextureCount, 100000);
                payload.model_vertex = ClampCount(monitor.modelVertexCount, 10000000);
                payload.model_tri = ClampCount(
                    (int)Math.Min(monitor.modelTriangleCount, (long)int.MaxValue),
                    10000000);
                payload.model_bone = ClampCount(monitor.modelBoneCount, 1000000);
                payload.model_rigid = ClampCount(monitor.modelRigidBodyCount, 1000000);
                payload.model_joint = ClampCount(monitor.modelJointCount, 1000000);
            }
            if (monitor.physicsMetricsAvailable)
            {
                payload.mmd_solver_ms = ClampMetric(monitor.mmdSolverMilliseconds, 10000f);
                payload.mmd_physics_ms = ClampMetric(monitor.mmdPhysicsMilliseconds, 10000f);
                payload.mmd_bone_ik_ms = ClampMetric(monitor.mmdBoneAndIkMilliseconds, 10000f);
                payload.mmd_sdef_ms = ClampMetric(monitor.mmdSdefMilliseconds, 10000f);
                payload.mmd_flush_ms = ClampMetric(monitor.mmdFlushMilliseconds, 10000f);
                payload.hand_contact_ms = ClampMetric(monitor.handContactMilliseconds, 10000f);
            }
            return payload;
        }

        private static long NowMilliseconds()
        {
            return (long)(Time.unscaledTimeAsDouble * 1000.0);
        }

        private static float ClampMetric(float value, float maximum)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
            {
                return -1f;
            }
            return Mathf.Clamp(value, 0f, maximum);
        }

        private static int ClampCount(int value, int maximum)
        {
            return Mathf.Clamp(value, -1, maximum);
        }

        private static long ClampBytes(long value)
        {
            if (value < 0)
            {
                return -1;
            }
            return Math.Min(value, 1000000000000L);
        }

        internal static string BoundedToken(string value, int maximum)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            var candidate = value.Trim();
            if (candidate.Length > maximum)
            {
                candidate = candidate.Substring(0, maximum);
            }
            for (var index = 0; index < candidate.Length; index++)
            {
                var character = candidate[index];
                if (!char.IsLetterOrDigit(character) && character != '.' &&
                    character != '_' && character != ':' && character != '-')
                {
                    return string.Empty;
                }
            }
            return candidate;
        }
    }
}

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;

namespace QuestMmdPlayer
{
    internal sealed class OrderedDeliveryQueue<T>
    {
        private readonly Queue<T> pending = new Queue<T>();
        private bool workerActive;

        internal int Count => pending.Count;

        internal bool Enqueue(T item)
        {
            pending.Enqueue(item);
            if (workerActive)
            {
                return false;
            }

            workerActive = true;
            return true;
        }

        internal bool TryDequeue(out T item)
        {
            return pending.TryDequeue(out item);
        }

        internal void CompleteWorker()
        {
            workerActive = false;
        }

        internal void Reset()
        {
            pending.Clear();
            workerActive = false;
        }
    }

    /// <summary>
    /// AstrBot Embodiment Bridge protocol 1.0 transport. Secrets are loaded from a
    /// JSON file under Application.persistentDataPath and are never serialized
    /// into the Unity scene or APK.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AstrBotBridge : MonoBehaviour, IConversationTransport
    {
        internal const string DefaultConfigurationFileName = "embodiment_bridge.json";
        internal const string LegacyConfigurationFileName = "quest_avatar_bridge.json";

        [SerializeField] private bool autoConnect = true;
        [SerializeField] private string configurationFileName = DefaultConfigurationFileName;
        [SerializeField] private float reconnectDelaySeconds = 1.5f;
        [SerializeField] private int requestTimeoutSeconds = 15;
        // Keep SSE work below the Quest frame budget. Audio chunks remain
        // ordered, but are spread across frames so PCM conversion cannot stall
        // gaze, tracking, or rendering in one large burst.
        [SerializeField, Range(8, 64)] private int maxIncomingFramesPerUpdate = 24;
        [SerializeField, Range(1f, 8f)] private float maxSseDispatchMilliseconds = 3f;
        [SerializeField, Range(1f, 30f)] private float spatialContextUploadIntervalSeconds = 2f;

        private readonly ConcurrentQueue<SseEventFrame> incomingFrames = new ConcurrentQueue<SseEventFrame>();
        private AstrBotBridgeSettings settings;
        private Coroutine connectionRoutine;
        private UnityWebRequest activeSseRequest;
        private string sessionId = string.Empty;
        private long eventSequence;
        private bool sessionReady;
        private bool eventStreamReady;
        private bool healthReady;
        private string healthPipelineStatus = "unknown";
        private bool shuttingDown;
        private int receivedStreamData;
        private int receivedStreamHeaders;
        private readonly Queue<byte[]> outgoingAudioChunks = new Queue<byte[]>();
        private Coroutine audioUploadRoutine;
        private UnityWebRequest activeAudioRequest;
        private string audioUploadTurnId = string.Empty;
        private bool audioEndRequested;
        private int audioSequence;
        private int queuedInputAudioBytes;
        private const int AudioUploadBatchBytes = 16000;
        private const int MaxQueuedInputAudioBytes = 1048576;
        private const string StableSessionPreferenceKey = "banxia.astrbot.session_id.v1";
        private int uploadedInputAudioBytes;
        private int uploadedInputBatchCount;
        private int audioHttpRequestCount;
        private int audioHttpRequestTotalMs;
        private int audioHttpRequestMaxMs;
        private int audioQueuedPeakBytes;
        private float audioUploadStartedAt;
        private float audioEndRequestedAt = -1f;
        private float nextSessionStartAt;
        private RuntimeDebugLog diagnostics;
        private long sseConnectStartedAt;
        private long audioUploadDiagnosticStartedAt;
        private int receivedTurnEventCount;
        private int receivedReplyAudioChunks;
        private int receivedReplyAudioBytes;
        private bool receivedReplyText;
        private string receivedErrorCode = string.Empty;
        private string activeTurnId = string.Empty;
        private string currentTraceId = string.Empty;
        private int sseFramesReceived;
        private int sseFramesDispatched;
        private int sseQueueDepthPeak;
        private int sseQueueDelayMaxMs;
        private long sseGeneration;
        private float nextSsePressureLogAt;
        private const string SpatialRevisionPreferenceKey = "banxia.spatial.revision.v1";
        private RoomUnderstandingService spatialContextSource;
        private SpatialContextRequest pendingSpatialContext;
        private string pendingSpatialSignature = string.Empty;
        private string sentSpatialSignature = string.Empty;
        private Coroutine spatialContextUploadRoutine;
        private float nextSpatialContextUploadAt;
        private float lastSpatialContextUploadAt = float.NegativeInfinity;
        private long spatialRevision;
        private readonly HashSet<string> actionReceiptKeys = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<string> actionReceiptOrder = new Queue<string>();
        private readonly OrderedDeliveryQueue<ActionResultDelivery> actionResultDeliveries =
            new OrderedDeliveryQueue<ActionResultDelivery>();
        private Coroutine actionResultDeliveryRoutine;
        private const int MaxActionReceiptKeys = 512;

        private sealed class ActionResultDelivery
        {
            internal readonly string Json;
            internal readonly AvatarActionReceipt Receipt;

            internal ActionResultDelivery(string json, AvatarActionReceipt receipt)
            {
                Json = json;
                Receipt = receipt;
            }
        }

        public event Action<AvatarCommand> CommandReceived;
        public event Action<ConversationEvent> EventReceived;

        public bool IsConnected => sessionReady && eventStreamReady && activeSseRequest != null && !shuttingDown;
        public bool IsConfigured { get; private set; }
        public string ConfigurationPath => Path.Combine(Application.persistentDataPath, configurationFileName);
        public string ConfiguredBaseUrl => settings == null ? string.Empty : settings.base_url ?? string.Empty;
        public string Status { get; private set; } = "AstrBot configuration not loaded";
        public string BackendChainStatus { get; private set; } = "chain unknown";
        public int QueuedInputAudioBytes => queuedInputAudioBytes;
        public bool AudioUploadInProgress => !string.IsNullOrEmpty(audioUploadTurnId);
        public string AudioUploadDiagnosticStatus => AudioUploadInProgress
            ? $"audio queued={queuedInputAudioBytes} B uploaded={uploadedInputAudioBytes} B batches={uploadedInputBatchCount}"
            : "audio upload idle";

        private void Awake()
        {
            diagnostics = GetComponent<RuntimeDebugLog>() ?? gameObject.AddComponent<RuntimeDebugLog>();
            LoadConfiguration();
        }

        private void OnEnable()
        {
            shuttingDown = false;
            if (autoConnect && IsConfigured && connectionRoutine == null)
            {
                connectionRoutine = StartCoroutine(ConnectionLoop());
            }
        }

        private void Update()
        {
            var streamActivity = Interlocked.Exchange(ref receivedStreamHeaders, 0) != 0 |
                Interlocked.Exchange(ref receivedStreamData, 0) != 0;
            if (streamActivity && sessionReady && activeSseRequest != null &&
                IsSseHandshakeReady(activeSseRequest.responseCode))
            {
                MarkEventStreamReady();
            }

            var configuredBudget = Mathf.Clamp(maxIncomingFramesPerUpdate, 8, 64);
            var queueDepthAtFrameStart = incomingFrames.Count;
            var remainingFrameBudget = configuredBudget;
            var dispatchedThisFrame = 0;
            var dispatchStartedAt = Time.realtimeSinceStartup;
            var dispatchBudgetMs = Mathf.Clamp(maxSseDispatchMilliseconds, 1f, 8f);
            while (remainingFrameBudget-- > 0 &&
                (dispatchedThisFrame == 0 ||
                    (Time.realtimeSinceStartup - dispatchStartedAt) * 1000f < dispatchBudgetMs) &&
                incomingFrames.TryDequeue(out var frame))
            {
                dispatchedThisFrame++;
                if (!IsCurrentSseGeneration(frame.Generation, Interlocked.Read(ref sseGeneration)))
                {
                    continue;
                }
                var queueDelayMs = ElapsedMs(frame.ReceivedAtTicks);
                if (AstrBotProtocol.TryMapSseEvent(sessionId, frame.EventName, frame.Data, out var message, out var error))
                {
                    if (!ShouldDispatchTurn(message, activeTurnId))
                    {
                        continue;
                    }
                    message.TransportReceivedAtTicks = frame.ReceivedAtTicks;
                    message.TransportQueueDelayMs = queueDelayMs;
                    var messageTraceId = RuntimeDebugLog.TraceLabel(message.TurnId);
                    sseFramesDispatched++;
                    UpdateMaximum(ref sseQueueDelayMaxMs, Mathf.Max(0, queueDelayMs));
                    var firstAudioFrame = message.Type == ConversationEventType.AudioChunk && receivedReplyAudioChunks == 0;
                    if (message.Type != ConversationEventType.AudioChunk || firstAudioFrame || queueDelayMs >= 25)
                    {
                        RecordStage(
                            "sse_dispatch",
                            "completed",
                            "sse_queue",
                            elapsedMs: queueDelayMs,
                            eventCount: sseFramesDispatched,
                            traceId: messageTraceId,
                            queueDepth: incomingFrames.Count);
                    }
                    RecordIncomingEvent(message, messageTraceId);
                    if (message.Type == ConversationEventType.ReplyEnd)
                    {
                        RecordStage(
                            "sse_dispatch",
                            "completed",
                            "sse_queue_summary",
                            elapsedMs: Volatile.Read(ref sseQueueDelayMaxMs),
                            eventCount: sseFramesDispatched,
                            traceId: messageTraceId,
                            queueDepth: Volatile.Read(ref sseQueueDepthPeak));
                    }
                    message.TransportDispatchedAtTicks = DiagnosticTimestamp();
                    EventReceived?.Invoke(message);
                }
                else if (!error.Contains("stale session"))
                {
                    Debug.LogWarning("[AstrBotBridge] Ignored invalid SSE event: " + error);
                }
            }
            if (queueDepthAtFrameStart > configuredBudget ||
                (dispatchedThisFrame >= configuredBudget && incomingFrames.Count > 0))
            {
                UpdateMaximum(ref sseQueueDepthPeak, queueDepthAtFrameStart);
                if (Time.unscaledTime >= nextSsePressureLogAt)
                {
                    nextSsePressureLogAt = Time.unscaledTime + 1f;
                    var pressureTrace = string.IsNullOrEmpty(currentTraceId)
                        ? string.Empty
                        : currentTraceId;
                    RecordStage(
                        "sse_dispatch",
                        "limited",
                        "queue_pressure",
                        eventCount: dispatchedThisFrame,
                        traceId: pressureTrace,
                        queueDepth: incomingFrames.Count);
                    Debug.LogWarning(
                        $"[AstrBotBridge] SSE queue pressure: start={queueDepthAtFrameStart} " +
                        $"dispatched={dispatchedThisFrame} budget={configuredBudget} remaining={incomingFrames.Count} " +
                        $"trace={pressureTrace}",
                        this);
                }
            }
            TryStartSpatialContextUpload();
        }

        public void BindSpatialContext(RoomUnderstandingService source)
        {
            if (spatialContextSource != null)
            {
                spatialContextSource.SnapshotChanged -= QueueSpatialContextSnapshot;
            }
            spatialContextSource = source;
            if (spatialContextSource != null)
            {
                spatialContextSource.SnapshotChanged += QueueSpatialContextSnapshot;
                QueueSpatialContextSnapshot();
            }
        }

        private void QueueSpatialContextSnapshot()
        {
            if (spatialContextSource == null || !spatialContextSource.HasRoomData)
            {
                pendingSpatialContext = null;
                pendingSpatialSignature = string.Empty;
                return;
            }
            var request = CreateSpatialContextRequest(
                sessionId,
                0,
                spatialContextSource.SemanticSnapshot,
                spatialContextSource.Capabilities);
            var signature = request.ContentSignature();
            if (string.Equals(signature, pendingSpatialSignature, StringComparison.Ordinal) ||
                (pendingSpatialContext == null &&
                 string.Equals(signature, sentSpatialSignature, StringComparison.Ordinal)))
            {
                return;
            }
            pendingSpatialContext = request;
            pendingSpatialSignature = signature;
        }

        private void TryStartSpatialContextUpload()
        {
            if (pendingSpatialContext == null && spatialContextSource != null &&
                spatialContextSource.HasRoomData && sessionReady &&
                Time.unscaledTime - lastSpatialContextUploadAt >= 15f)
            {
                QueueSpatialContextHeartbeat();
            }
            if (!sessionReady || shuttingDown || pendingSpatialContext == null ||
                spatialContextUploadRoutine != null || Time.unscaledTime < nextSpatialContextUploadAt)
            {
                return;
            }
            var request = pendingSpatialContext;
            request.session_id = sessionId;
            request.revision = NextSpatialRevision();
            var signature = pendingSpatialSignature;
            spatialContextUploadRoutine = StartCoroutine(UploadSpatialContext(request, signature));
        }

        private IEnumerator UploadSpatialContext(SpatialContextRequest payload, string signature)
        {
            var startedAt = DiagnosticTimestamp();
            using (var request = CreateJsonRequest("spatial/context", JsonUtility.ToJson(payload)))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request))
                {
                    lastSpatialContextUploadAt = Time.unscaledTime;
                    sentSpatialSignature = signature;
                    if (string.Equals(signature, pendingSpatialSignature, StringComparison.Ordinal))
                    {
                        pendingSpatialContext = null;
                        pendingSpatialSignature = string.Empty;
                    }
                    RecordStage(
                        "spatial_context",
                        "completed",
                        "spatial_context_uploaded",
                        request.responseCode,
                        ElapsedMs(startedAt));
                }
                else
                {
                    RecordStage(
                        "spatial_context",
                        "failed",
                        ReadFailureCode(request, "spatial_context_upload_failed"),
                        request.responseCode,
                        ElapsedMs(startedAt));
                }
            }
            nextSpatialContextUploadAt = Time.unscaledTime +
                Mathf.Clamp(spatialContextUploadIntervalSeconds, 1f, 30f);
            spatialContextUploadRoutine = null;
        }

        private void QueueSpatialContextHeartbeat()
        {
            if (spatialContextSource == null || !spatialContextSource.HasRoomData)
            {
                return;
            }
            var request = CreateSpatialContextRequest(
                sessionId,
                0,
                spatialContextSource.SemanticSnapshot,
                spatialContextSource.Capabilities);
            pendingSpatialContext = request;
            pendingSpatialSignature = request.ContentSignature();
        }

        private long NextSpatialRevision()
        {
            if (spatialRevision <= 0)
            {
                long.TryParse(
                    PlayerPrefs.GetString(SpatialRevisionPreferenceKey, "0"),
                    out spatialRevision);
            }
            spatialRevision = Math.Max(
                spatialRevision + 1L,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            PlayerPrefs.SetString(SpatialRevisionPreferenceKey, spatialRevision.ToString());
            PlayerPrefs.Save();
            return spatialRevision;
        }

        public static SpatialContextRequest CreateSpatialContextRequest(
            string currentSessionId,
            long revision,
            RoomSemanticSnapshot snapshot,
            SpatialCapabilitySnapshot capabilities)
        {
            return new SpatialContextRequest
            {
                session_id = currentSessionId ?? string.Empty,
                revision = Math.Max(0L, revision),
                floor_count = ClampSpatialCount(snapshot.FloorCount),
                seat_count = ClampSpatialCount(snapshot.SeatCount),
                bed_count = ClampSpatialCount(snapshot.BedCount),
                table_count = ClampSpatialCount(snapshot.TableCount),
                wall_count = ClampSpatialCount(snapshot.WallCount),
                door_count = ClampSpatialCount(snapshot.DoorCount),
                window_count = ClampSpatialCount(snapshot.WindowCount),
                scene_capture_available = capabilities.SceneCaptureAvailable,
                occlusion_available = capabilities.Occlusion == SpatialCapabilityState.Available
            };
        }

        private static int ClampSpatialCount(int value)
        {
            return Math.Max(0, Math.Min(64, value));
        }

        public bool ReloadConfiguration()
        {
            Shutdown(false);
            LoadConfiguration();
            shuttingDown = false;
            if (isActiveAndEnabled && autoConnect && IsConfigured)
            {
                connectionRoutine = StartCoroutine(ConnectionLoop());
            }
            return IsConfigured;
        }

        public void SendUserInput(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }
            StartTurn("manual-" + DateTime.UtcNow.Ticks, text.Trim());
        }

        public void StartTurn(string turnId, string userText)
        {
            activeTurnId = string.Empty;
            CancelAudioUpload();
            ClearIncomingFrames();
            if (!CanSend(turnId, true, true))
            {
                return;
            }

            var request = new TurnStartRequest
            {
                session_id = sessionId,
                turn_id = turnId,
                text = string.IsNullOrEmpty(userText)
                    ? string.Empty
                    : userText.Substring(0, Math.Min(userText.Length, 8192))
            };
            currentTraceId = RuntimeDebugLog.TraceLabel(turnId);
            activeTurnId = turnId;
            ResetReceivedTurnCounters();
            RecordStage("eventbus", "processing");
            StartCoroutine(PostJson("turn/start", JsonUtility.ToJson(request), turnId, true));
        }

        public bool BeginAudioTurn(string turnId)
        {
            activeTurnId = string.Empty;
            if (!CanSend(turnId, true, true))
            {
                return false;
            }

            CancelAudioUpload();
            ClearIncomingFrames();
            audioUploadTurnId = turnId;
            currentTraceId = RuntimeDebugLog.TraceLabel(turnId);
            audioEndRequested = false;
            audioSequence = 0;
            queuedInputAudioBytes = 0;
            uploadedInputAudioBytes = 0;
            uploadedInputBatchCount = 0;
            audioHttpRequestCount = 0;
            audioHttpRequestTotalMs = 0;
            audioHttpRequestMaxMs = 0;
            audioQueuedPeakBytes = 0;
            audioUploadStartedAt = Time.unscaledTime;
            audioUploadDiagnosticStartedAt = DiagnosticTimestamp();
            audioEndRequestedAt = -1f;
            activeTurnId = turnId;
            audioUploadRoutine = StartCoroutine(UploadAudioTurn(turnId));
            SetStatus("Recording voice for AstrBot");
            ResetReceivedTurnCounters();
            RecordStage("audio_upload", "ready");
            return true;
        }

        public bool QueueAudioChunk(string turnId, byte[] pcm16)
        {
            if (!string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal) ||
                pcm16 == null || pcm16.Length == 0 || pcm16.Length > 16000 ||
                (pcm16.Length & 1) != 0 || audioEndRequested)
            {
                return false;
            }
            if (queuedInputAudioBytes + pcm16.Length > MaxQueuedInputAudioBytes)
            {
                ClearActiveTurnIfMatches(turnId);
                EventReceived?.Invoke(new ConversationEvent
                {
                    Type = ConversationEventType.Error,
                    TurnId = turnId,
                    ErrorCode = "audio_upload_backpressure",
                    Text = "Voice upload could not keep up with capture"
                });
                RecordStage(
                    "audio_upload",
                    "blocked",
                    "audio_upload_backpressure",
                    bytes: queuedInputAudioBytes);
                CancelAudioUpload();
                return false;
            }

            // QueueAudioChunk takes ownership of the freshly encoded buffer.
            // ConversationController never reuses or mutates pcm16, so copying
            // it here only creates another short-lived allocation on the Quest
            // main thread for every 80 ms capture chunk.
            outgoingAudioChunks.Enqueue(pcm16);
            queuedInputAudioBytes += pcm16.Length;
            audioQueuedPeakBytes = Mathf.Max(audioQueuedPeakBytes, queuedInputAudioBytes);
            return true;
        }

        public bool EndAudioTurn(string turnId)
        {
            if (!string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal) || audioEndRequested)
            {
                return false;
            }

            audioEndRequested = true;
            audioEndRequestedAt = Time.unscaledTime;
            SetStatus("Uploading voice to AstrBot");
            RecordStage(
                "audio_upload",
                "uploading",
                chunks: outgoingAudioChunks.Count,
                bytes: queuedInputAudioBytes);
            return true;
        }

        public void Interrupt(string turnId)
        {
            if (string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal))
            {
                CancelAudioUpload();
            }
            if (string.Equals(turnId, activeTurnId, StringComparison.Ordinal))
            {
                activeTurnId = string.Empty;
            }
            ClearIncomingFrames();
            if (!CanSend(turnId, false, false))
            {
                return;
            }
            var request = new InterruptRequest
            {
                session_id = sessionId,
                turn_id = turnId,
                reason = "unity_interrupt"
            };
            StartCoroutine(PostJson("interrupt", JsonUtility.ToJson(request), turnId, false));
            RecordStage("interrupt", "processing");
        }

        public string SendInteraction(
            string interactionName,
            string phase,
            float strength,
            int durationMs = 0,
            string hand = "none")
        {
            if (!CanSend(string.Empty, false) || !IsInteraction(interactionName) || !IsPhase(phase))
            {
                return string.Empty;
            }

            var request = new InteractionRequest
            {
                session_id = sessionId,
                event_id = "event-" + Interlocked.Increment(ref eventSequence).ToString("D10"),
                name = interactionName,
                phase = phase,
                strength = Mathf.Clamp01(strength),
                duration_ms = Mathf.Clamp(durationMs, 0, 600000),
                hand = IsHand(hand) ? hand : "none"
            };
            StartCoroutine(PostJson("interaction", JsonUtility.ToJson(request), string.Empty, false));
            return request.event_id;
        }

        public bool SendActionResult(AvatarActionReceipt receipt)
        {
            if (receipt == null || string.IsNullOrWhiteSpace(receipt.TurnId) ||
                string.IsNullOrWhiteSpace(receipt.ActionId) || string.IsNullOrWhiteSpace(receipt.ReceiptId) ||
                string.IsNullOrWhiteSpace(receipt.Action) || receipt.Phase == AvatarActionReceiptPhase.Planned ||
                !CanSend(string.Empty, false))
            {
                return false;
            }

            var key = receipt.ReceiptId;
            if (actionReceiptKeys.Contains(key))
            {
                return true;
            }

            actionReceiptKeys.Add(key);
            actionReceiptOrder.Enqueue(key);
            while (actionReceiptOrder.Count > MaxActionReceiptKeys)
            {
                actionReceiptKeys.Remove(actionReceiptOrder.Dequeue());
            }

            // One worker preserves accepted -> started -> terminal HTTP arrival
            // order. Receipts are observational: delivery failure never changes
            // the local animation or injects a conversation error.
            var delivery = new ActionResultDelivery(
                CreateActionResultJson(sessionId, receipt),
                receipt);
            if (actionResultDeliveries.Enqueue(delivery))
            {
                actionResultDeliveryRoutine = StartCoroutine(DrainActionResults());
            }
            RecordStage("avatar_action", "completed", "action_receipt_queued", traceId: RuntimeDebugLog.TraceLabel(receipt.TurnId));
            return true;
        }

        private IEnumerator DrainActionResults()
        {
            while (!shuttingDown && actionResultDeliveries.TryDequeue(out var delivery))
            {
                yield return PostActionResult(delivery.Json, delivery.Receipt);
            }

            actionResultDeliveries.CompleteWorker();
            actionResultDeliveryRoutine = null;
        }

        public static string CreateActionResultJson(string activeSessionId, AvatarActionReceipt receipt)
        {
            if (receipt == null) return string.Empty;
            return JsonUtility.ToJson(new ActionResultRequest
            {
                session_id = activeSessionId ?? string.Empty,
                turn_id = receipt.TurnId ?? string.Empty,
                action_id = receipt.ActionId ?? string.Empty,
                receipt_id = receipt.ReceiptId ?? string.Empty,
                action = receipt.Action ?? string.Empty,
                status = ActionPhaseName(receipt.Phase),
                reason_code = receipt.ReasonCode ?? string.Empty,
                duration_ms = Mathf.Clamp(receipt.DurationMs, 0, 600000)
            });
        }

        private IEnumerator PostActionResult(string json, AvatarActionReceipt receipt)
        {
            const int maximumAttempts = 3;
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                var startedAt = DiagnosticTimestamp();
                using (var request = CreateJsonRequest("action/result", json))
                {
                    yield return request.SendWebRequest();
                    if (Succeeded(request))
                    {
                        RecordStage(
                            "avatar_action",
                            "completed",
                            "action_receipt_delivered",
                            request.responseCode,
                            ElapsedMs(startedAt),
                            traceId: RuntimeDebugLog.TraceLabel(receipt.TurnId));
                        yield break;
                    }

                    var retryable = request.result == UnityWebRequest.Result.ConnectionError ||
                        request.responseCode == 408 || request.responseCode == 429 ||
                        request.responseCode >= 500;
                    if (!retryable || attempt >= maximumAttempts)
                    {
                        Debug.LogWarning(
                            "[AstrBotBridge] Action receipt delivery failed: status=" +
                            ActionPhaseName(receipt.Phase) + " attempt=" + attempt +
                            " response=" + request.responseCode,
                            this);
                        RecordStage(
                            "avatar_action",
                            "failed",
                            "action_receipt_delivery_failed",
                            request.responseCode,
                            ElapsedMs(startedAt),
                            traceId: RuntimeDebugLog.TraceLabel(receipt.TurnId));
                        yield break;
                    }
                }

                // Json is intentionally built once before this loop so all
                // retries reuse the exact same receipt_id and body.
                yield return new WaitForSecondsRealtime(.2f * attempt);
            }
        }

        public bool TryIngestCommandJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var envelope = JsonUtility.FromJson<AstrBotCommandEnvelope>(json);
                if (envelope == null || string.IsNullOrWhiteSpace(envelope.command))
                {
                    return false;
                }

                CommandReceived?.Invoke(new AvatarCommand
                {
                    name = envelope.command,
                    motionId = envelope.motionId,
                    emotion = envelope.emotion,
                    text = envelope.text,
                    value = envelope.value,
                    blendSeconds = envelope.blendSeconds
                });
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[AstrBotBridge] Invalid command JSON: " + exception.Message);
                return false;
            }
        }

        public void SimulateWave()
        {
            CommandReceived?.Invoke(new AvatarCommand { name = "play_motion", motionId = "wave" });
        }

        public void SimulateEmotion(string emotion)
        {
            CommandReceived?.Invoke(new AvatarCommand { name = "set_emotion", emotion = emotion });
        }

        private IEnumerator ConnectionLoop()
        {
            var delay = new WaitForSecondsRealtime(Mathf.Max(.25f, reconnectDelaySeconds));
            while (!shuttingDown && isActiveAndEnabled && IsConfigured)
            {
                if (!sessionReady)
                {
                    if (Time.unscaledTime < nextSessionStartAt)
                    {
                        yield return null;
                        continue;
                    }
                    yield return CheckHealth();
                    if (!sessionReady && healthReady)
                    {
                        yield return StartSession();
                    }
                }

                if (sessionReady)
                {
                    yield return RunEventStream();
                }

                if (!shuttingDown && isActiveAndEnabled)
                {
                    yield return delay;
                }
            }
            connectionRoutine = null;
        }

        private IEnumerator CheckHealth()
        {
            healthReady = false;
            SetStatus("Checking AstrBot health");
            var startedAt = DiagnosticTimestamp();
            RecordStage("health", "processing");
            using (var request = UnityWebRequest.Get(Endpoint("health")))
            {
                ConfigureHeaders(request, false);
                request.timeout = Mathf.Clamp(requestTimeoutSeconds, 2, 60);
                yield return request.SendWebRequest();
                if (Succeeded(request))
                {
                    var response = JsonUtility.FromJson<HealthResponse>(request.downloadHandler.text);
                    if (response != null && response.status == "ok" && response.data != null &&
                        response.data.protocol_version == AstrBotProtocol.Version && response.data.transport == "http+sse")
                    {
                        healthPipelineStatus = response.data.series_integrations == null ||
                            response.data.series_integrations.astrbot_message_pipeline == null
                            ? "unknown"
                            : response.data.series_integrations.astrbot_message_pipeline.available
                                ? "ready"
                                : "unavailable";
                        healthReady = true;
                        SetStatus("AstrBot health check ready");
                        RecordStage(
                            "health",
                            "ok",
                            httpStatus: request.responseCode,
                            elapsedMs: ElapsedMs(startedAt));
                        yield break;
                    }
                    SetStatus("AstrBot health response is incompatible");
                    RecordStage(
                        "health",
                        "failed",
                        "health_incompatible",
                        request.responseCode,
                        ElapsedMs(startedAt));
                }
                else
                {
                    SetStatus(HttpFailure("Health check", request));
                    RecordStage(
                        "health",
                        "failed",
                        ReadFailureCode(request, "health_failed"),
                        request.responseCode,
                        ElapsedMs(startedAt));
                }
            }
        }

        private IEnumerator StartSession()
        {
            sessionId = GetOrCreateStableSessionId();
            var payload = new SessionStartRequest
            {
                session_id = sessionId,
                client_id = settings.client_id,
                user_id = settings.user_id,
                bot_id = settings.bot_id,
                group_id = settings.group_id ?? string.Empty,
                relationship_profile_id = settings.relationship_profile_id ?? string.Empty,
                supported_actions = AstrBotProtocol.SupportedActions()
            };

            eventStreamReady = false;
            SetStatus("Starting AstrBot session");
            var startedAt = DiagnosticTimestamp();
            RecordStage("session", "processing");
            using (var request = CreateJsonRequest("session/start", JsonUtility.ToJson(payload)))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request))
                {
                    nextSessionStartAt = 0f;
                    sessionReady = true;
                    sentSpatialSignature = string.Empty;
                    QueueSpatialContextSnapshot();
                    BackendChainStatus = ResolveBackendChainStatus(
                        ParseSessionChainStatus(request.downloadHandler.text), healthPipelineStatus);
                    SetStatus("AstrBot session ready (" + BackendChainStatus + ")");
                    var authorizationCode = AuthorizationCode(BackendChainStatus);
                    RecordStage(
                        "session",
                        "ready",
                        httpStatus: request.responseCode,
                        elapsedMs: ElapsedMs(startedAt));
                    RecordStage(
                        "authorization",
                        string.IsNullOrEmpty(authorizationCode) ? "authorized" : "limited",
                        authorizationCode,
                        request.responseCode,
                        ElapsedMs(startedAt));
                    Debug.Log("[AstrBotBridge] Backend chain: " + BackendChainStatus, this);
                    Debug.Log("[AstrBotBridge] Session established.", this);
                }
                else if (CanRecoverExistingSession(request.responseCode, request.downloadHandler.text))
                {
                    // Android can terminate the process before session.close is sent.
                    // Reusing one persisted ID lets the next process reattach.
                    nextSessionStartAt = 0f;
                    sessionReady = true;
                    sentSpatialSignature = string.Empty;
                    QueueSpatialContextSnapshot();
                    SetStatus("Existing AstrBot session found; reconnecting SSE");
                    RecordStage(
                        "session",
                        "ready",
                        "existing_session",
                        request.responseCode,
                        ElapsedMs(startedAt));
                }
                else if (IsSessionCapacityConflict(request.responseCode, request.downloadHandler.text))
                {
                    sessionId = string.Empty;
                    nextSessionStartAt = Time.unscaledTime + 10f;
                    SetStatus("AstrBot session capacity is full; reload the bridge plugin");
                    RecordStage(
                        "session",
                        "blocked",
                        "session_capacity_full",
                        request.responseCode,
                        ElapsedMs(startedAt));
                }
                else
                {
                    sessionId = string.Empty;
                    SetStatus(HttpFailure("Session start", request));
                    RecordStage(
                        "session",
                        "failed",
                        ReadFailureCode(request, "session_start_failed"),
                        request.responseCode,
                        ElapsedMs(startedAt));
                }
            }
        }

        private IEnumerator RunEventStream()
        {
            var generation = Interlocked.Increment(ref sseGeneration);
            ClearIncomingFrames();
            var request = UnityWebRequest.Get(Endpoint("events/" + UnityWebRequest.EscapeURL(sessionId)));
            eventStreamReady = false;
            Interlocked.Exchange(ref receivedStreamHeaders, 0);
            Interlocked.Exchange(ref receivedStreamData, 0);
            var handler = new SseDownloadHandler(
                frame =>
                {
                    // A DownloadHandler callback can arrive after the request
                    // has been aborted. Tag every frame so a late callback can
                    // never enter the next SSE stream's turn.
                    incomingFrames.Enqueue(new SseEventFrame(
                        frame.EventName,
                        frame.Data,
                        frame.ReceivedAtTicks,
                        generation));
                    Interlocked.Increment(ref sseFramesReceived);
                    UpdateMaximum(ref sseQueueDepthPeak, incomingFrames.Count);
                    Interlocked.Exchange(ref receivedStreamData, 1);
                },
                () => Interlocked.Exchange(ref receivedStreamHeaders, 1));
            request.downloadHandler.Dispose();
            request.downloadHandler = handler;
            ConfigureHeaders(request, true);
            request.timeout = 0;
            activeSseRequest = request;
            SetStatus("Connecting AstrBot SSE");
            sseConnectStartedAt = DiagnosticTimestamp();
            RecordStage("sse", "processing");

            var operation = request.SendWebRequest();
            while (!operation.isDone && !shuttingDown)
            {
                if (!eventStreamReady && IsSseHandshakeReady(request.responseCode))
                {
                    MarkEventStreamReady();
                }
                yield return null;
            }
            if (shuttingDown && !operation.isDone)
            {
                request.Abort();
            }
            if (ReferenceEquals(activeSseRequest, request))
            {
                activeSseRequest = null;
            }
            eventStreamReady = false;
            InvalidateSseGeneration(generation);

            if (!shuttingDown)
            {
                if (request.responseCode == 404)
                {
                    EmitActiveTurnError("session_expired", "AstrBot session expired");
                    sessionReady = false;
                    sessionId = string.Empty;
                    SetStatus("AstrBot session expired; recreating");
                    RecordStage(
                        "sse",
                        "disconnected",
                        "session_expired",
                        request.responseCode,
                        ElapsedMs(sseConnectStartedAt));
                }
                else
                {
                    EmitActiveTurnError(
                        "sse_disconnected",
                        HttpFailure("SSE disconnected", request));
                    SetStatus(HttpFailure("SSE disconnected", request));
                    RecordStage(
                        "sse",
                        "disconnected",
                        ReadFailureCode(request, "sse_disconnected"),
                        request.responseCode,
                        ElapsedMs(sseConnectStartedAt),
                        eventCount: receivedTurnEventCount);
                }
            }
            request.Dispose();
        }

        private void EmitActiveTurnError(string code, string text)
        {
            if (string.IsNullOrEmpty(activeTurnId))
            {
                return;
            }

            var turnId = activeTurnId;
            if (string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal))
            {
                // The SSE request can finish while the audio upload coroutine
                // is still waiting on turn/start or audio/chunk. Stop that
                // producer before publishing the terminal error.
                CancelAudioUpload();
            }
            activeTurnId = string.Empty;
            var message = new ConversationEvent
            {
                Type = ConversationEventType.Error,
                TurnId = turnId,
                ErrorCode = code,
                Text = string.IsNullOrWhiteSpace(text) ? code : text
            };
            RecordIncomingEvent(message, RuntimeDebugLog.TraceLabel(turnId));
            EventReceived?.Invoke(message);
        }

        public static bool IsCurrentSseGeneration(long frameGeneration, long currentGeneration)
        {
            // Generation zero is retained for deterministic parser/unit tests
            // and for frames injected by the local mock harness.
            return frameGeneration == 0L || frameGeneration == currentGeneration;
        }

        public static bool ShouldDispatchTurn(ConversationEvent message, string currentTurnId)
        {
            if (message == null || string.IsNullOrEmpty(message.TurnId))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(currentTurnId) &&
                string.Equals(message.TurnId, currentTurnId, StringComparison.Ordinal))
            {
                return true;
            }

            // Interaction turns are a parallel channel and may arrive while
            // no conversation turn is active.
            return message.TurnId.StartsWith("i:", StringComparison.Ordinal);
        }

        private void InvalidateSseGeneration(long generation)
        {
            if (Interlocked.CompareExchange(ref sseGeneration, generation + 1L, generation) == generation)
            {
                ClearIncomingFrames();
            }
        }

        private void ClearIncomingFrames()
        {
            while (incomingFrames.TryDequeue(out _))
            {
            }
        }

        private void ClearActiveTurnIfMatches(string turnId)
        {
            if (string.Equals(turnId, activeTurnId, StringComparison.Ordinal))
            {
                activeTurnId = string.Empty;
            }
        }

        private IEnumerator UploadAudioTurn(string turnId)
        {
            var startSucceeded = false;
            var start = new TurnStartRequest
            {
                session_id = sessionId,
                turn_id = turnId,
                text = null
            };
            yield return PostAudioJson(
                "turn/start",
                JsonUtility.ToJson(start),
                turnId,
                result => startSucceeded = result,
                0,
                -1);
            if (!startSucceeded || !string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal))
            {
                ClearAudioUpload(turnId);
                yield break;
            }

            while (string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal))
            {
                if (outgoingAudioChunks.Count > 0)
                {
                    var pcm16 = DequeueAudioBatch(outgoingAudioChunks, AudioUploadBatchBytes);
                    queuedInputAudioBytes = Mathf.Max(0, queuedInputAudioBytes - pcm16.Length);
                    var chunk = new AudioChunkRequest
                    {
                        session_id = sessionId,
                        turn_id = turnId,
                        sequence = audioSequence,
                        data = Convert.ToBase64String(pcm16)
                    };
                    var chunkSucceeded = false;
                    yield return PostAudioJson(
                        "audio/chunk",
                        JsonUtility.ToJson(chunk),
                        turnId,
                        result => chunkSucceeded = result,
                        pcm16.Length,
                        audioSequence);
                    if (!chunkSucceeded)
                    {
                        ClearAudioUpload(turnId);
                        yield break;
                    }
                    audioSequence++;
                    uploadedInputAudioBytes += pcm16.Length;
                    uploadedInputBatchCount++;
                    continue;
                }

                if (audioEndRequested)
                {
                    var endSucceeded = false;
                    var end = new AudioEndRequest { session_id = sessionId, turn_id = turnId };
                    yield return PostAudioJson(
                        "audio/end",
                        JsonUtility.ToJson(end),
                        turnId,
                        result => endSucceeded = result,
                        0,
                        audioSequence);
                    if (endSucceeded)
                    {
                        EventReceived?.Invoke(new ConversationEvent
                        {
                            Type = ConversationEventType.Thinking,
                            TurnId = turnId,
                            IsSyntheticTransportEvent = true
                        });
                        SetStatus("AstrBot is processing voice");
                        var now = Time.unscaledTime;
                        Debug.Log($"[AstrBotBridge] Voice upload complete: {uploadedInputAudioBytes} B in " +
                            $"{uploadedInputBatchCount} batches, stream {Mathf.Max(0f, now - audioUploadStartedAt):F2}s, " +
                            $"final flush {(audioEndRequestedAt < 0f ? 0f : Mathf.Max(0f, now - audioEndRequestedAt)):F2}s, " +
                            $"http requests={audioHttpRequestCount} max_ms={audioHttpRequestMaxMs} " +
                            $"queue_peak={audioQueuedPeakBytes} B.");
                        diagnostics?.Record(
                            "AstrBotBridge",
                            "语音上传网络统计：请求 " + audioHttpRequestCount +
                            " 次，平均 " + (audioHttpRequestCount == 0 ? 0 : audioHttpRequestTotalMs / audioHttpRequestCount) +
                            "ms，最长 " + audioHttpRequestMaxMs + "ms，队列峰值 " + audioQueuedPeakBytes + "B");
                        RecordStage(
                            "audio_upload",
                            "completed",
                            elapsedMs: ElapsedMs(audioUploadDiagnosticStartedAt),
                            chunks: uploadedInputBatchCount,
                            bytes: uploadedInputAudioBytes);
                        RecordStage("stt", "processing");
                    }
                    ClearAudioUpload(turnId);
                    yield break;
                }

                yield return null;
            }
        }

        private IEnumerator PostAudioJson(
            string endpoint,
            string json,
            string turnId,
            Action<bool> completed,
            int payloadBytes,
            int sequence)
        {
            var startedAt = DiagnosticTimestamp();
            using (var request = CreateJsonRequest(endpoint, json))
            {
                activeAudioRequest = request;
                yield return request.SendWebRequest();
                if (ReferenceEquals(activeAudioRequest, request))
                {
                    activeAudioRequest = null;
                }
                var elapsedMs = ElapsedMs(startedAt);
                audioHttpRequestCount++;
                audioHttpRequestTotalMs += elapsedMs;
                audioHttpRequestMaxMs = Mathf.Max(audioHttpRequestMaxMs, elapsedMs);
                if (endpoint == "audio/end" || elapsedMs >= 500)
                {
                    diagnostics?.Record(
                        "AstrBotBridge",
                        "语音请求 " + endpoint + " 耗时 " + elapsedMs + "ms" +
                        (Succeeded(request) ? "（成功）" : "（失败）"));
                }
                var succeeded = Succeeded(request);
                if (ShouldRecordAudioRequestStage(
                    endpoint,
                    sequence,
                    succeeded,
                    elapsedMs))
                {
                    RecordStage(
                        "audio_upload",
                        succeeded ? "ok" : "failed",
                        AudioRequestCode(endpoint),
                        request.responseCode,
                        elapsedMs,
                        sequence >= 0 ? sequence + 1 : 0,
                        payloadBytes,
                        queueDepth: outgoingAudioChunks.Count);
                }
                if (!succeeded && string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal))
                {
                    ClearActiveTurnIfMatches(turnId);
                    EventReceived?.Invoke(new ConversationEvent
                    {
                        Type = ConversationEventType.Error,
                        TurnId = turnId,
                        ErrorCode = "audio_http_request_failed",
                        Text = HttpFailure(endpoint, request)
                    });
                    RecordStage(
                        "audio_upload",
                        "failed",
                        ReadFailureCode(request, "audio_http_request_failed"),
                        request.responseCode,
                        ElapsedMs(audioUploadDiagnosticStartedAt),
                        uploadedInputBatchCount,
                        uploadedInputAudioBytes);
                }
                completed?.Invoke(succeeded);
            }
        }

        public static byte[] DequeueAudioBatch(Queue<byte[]> chunks, int maximumBytes)
        {
            if (chunks == null || chunks.Count == 0)
            {
                return Array.Empty<byte>();
            }

            var limit = Mathf.Max(2, maximumBytes) & ~1;
            var total = 0;
            while (chunks.Count > 0)
            {
                var next = chunks.Peek();
                if (total > 0 && total + next.Length > limit)
                {
                    break;
                }
                total += next.Length;
                if (total >= limit)
                {
                    break;
                }
            }

            var merged = new byte[total];
            var offset = 0;
            while (offset < total && chunks.Count > 0)
            {
                var chunk = chunks.Dequeue();
                Buffer.BlockCopy(chunk, 0, merged, offset, chunk.Length);
                offset += chunk.Length;
            }
            return merged;
        }

        public static bool ShouldRecordAudioRequestStage(
            string endpoint,
            int sequence,
            bool succeeded,
            int elapsedMs)
        {
            return !string.Equals(endpoint, "audio/chunk", StringComparison.Ordinal) ||
                sequence == 0 ||
                !succeeded ||
                elapsedMs >= 250;
        }

        private void CancelAudioUpload()
        {
            if (audioUploadRoutine != null)
            {
                StopCoroutine(audioUploadRoutine);
                audioUploadRoutine = null;
            }
            if (activeAudioRequest != null)
            {
                activeAudioRequest.Abort();
                activeAudioRequest.Dispose();
                activeAudioRequest = null;
            }
            audioUploadTurnId = string.Empty;
            audioEndRequested = false;
            audioSequence = 0;
            queuedInputAudioBytes = 0;
            uploadedInputAudioBytes = 0;
            uploadedInputBatchCount = 0;
            audioHttpRequestCount = 0;
            audioHttpRequestTotalMs = 0;
            audioHttpRequestMaxMs = 0;
            audioQueuedPeakBytes = 0;
            audioEndRequestedAt = -1f;
            audioUploadDiagnosticStartedAt = 0L;
            outgoingAudioChunks.Clear();
        }

        private void ClearAudioUpload(string turnId)
        {
            if (!string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal))
            {
                return;
            }
            audioUploadRoutine = null;
            activeAudioRequest = null;
            audioUploadTurnId = string.Empty;
            audioEndRequested = false;
            audioSequence = 0;
            queuedInputAudioBytes = 0;
            uploadedInputAudioBytes = 0;
            uploadedInputBatchCount = 0;
            audioHttpRequestCount = 0;
            audioHttpRequestTotalMs = 0;
            audioHttpRequestMaxMs = 0;
            audioQueuedPeakBytes = 0;
            audioEndRequestedAt = -1f;
            audioUploadDiagnosticStartedAt = 0L;
            outgoingAudioChunks.Clear();
        }

        private IEnumerator PostJson(string endpoint, string json, string turnId, bool emitThinking)
        {
            var startedAt = DiagnosticTimestamp();
            using (var request = CreateJsonRequest(endpoint, json))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request))
                {
                    if (emitThinking)
                    {
                        EventReceived?.Invoke(new ConversationEvent
                        {
                            Type = ConversationEventType.Thinking,
                            TurnId = turnId,
                            IsSyntheticTransportEvent = true
                        });
                    }
                    if (endpoint == "turn/start")
                    {
                        RecordStage(
                            "eventbus",
                            "processing",
                            httpStatus: request.responseCode,
                            elapsedMs: ElapsedMs(startedAt));
                    }
                    else if (endpoint == "interrupt")
                    {
                        RecordStage(
                            "interrupt",
                            "completed",
                            httpStatus: request.responseCode,
                            elapsedMs: ElapsedMs(startedAt));
                    }
                }
                else if (!string.IsNullOrEmpty(turnId))
                {
                    ClearActiveTurnIfMatches(turnId);
                    EventReceived?.Invoke(new ConversationEvent
                    {
                        Type = ConversationEventType.Error,
                        TurnId = turnId,
                        ErrorCode = "http_request_failed",
                        Text = HttpFailure(endpoint, request)
                    });
                    RecordStage(
                        endpoint == "interrupt" ? "interrupt" : "eventbus",
                        "failed",
                        ReadFailureCode(request, "http_request_failed"),
                        request.responseCode,
                        ElapsedMs(startedAt));
                }
                else
                {
                    Debug.LogWarning("[AstrBotBridge] " + HttpFailure(endpoint, request));
                }
            }
        }

        private UnityWebRequest CreateJsonRequest(string endpoint, string json)
        {
            var request = new UnityWebRequest(Endpoint(endpoint), UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json ?? "{}")),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.Clamp(requestTimeoutSeconds, 2, 60)
            };
            ConfigureHeaders(request, false);
            request.SetRequestHeader("Content-Type", "application/json");
            return request;
        }

        private void ConfigureHeaders(UnityWebRequest request, bool eventStream)
        {
            request.SetRequestHeader("Authorization", "ApiKey " + settings.astrbot_api_key);
            request.SetRequestHeader("X-Embodiment-Bridge-Key", settings.bridge_api_key);
            request.SetRequestHeader("Accept", eventStream ? "text/event-stream" : "application/json");
        }

        private bool CanSend(string turnId, bool emitError, bool requireEventStream = false)
        {
            if (sessionReady && (!requireEventStream || eventStreamReady) &&
                isActiveAndEnabled && !shuttingDown)
            {
                return true;
            }
            if (emitError && !string.IsNullOrEmpty(turnId))
            {
                EventReceived?.Invoke(new ConversationEvent
                {
                    Type = ConversationEventType.Error,
                    TurnId = turnId,
                    ErrorCode = "bridge_disconnected",
                    Text = "AstrBot bridge is not connected"
                });
            }
            return false;
        }

        private void LoadConfiguration()
        {
            IsConfigured = false;
            settings = null;
            BackendChainStatus = "chain unknown";
            healthPipelineStatus = "unknown";
            healthReady = false;
            eventStreamReady = false;
            TryMigrateLegacyConfiguration();
            if (!File.Exists(ConfigurationPath))
            {
                SetStatus("AstrBot configuration missing");
                RecordStage("configuration", "blocked", "configuration_missing");
                return;
            }

            try
            {
                settings = JsonUtility.FromJson<AstrBotBridgeSettings>(File.ReadAllText(ConfigurationPath, Encoding.UTF8));
                if (settings != null &&
                    BackendPairingProtocol.TryUpgradeLegacyPluginBaseUrl(settings.base_url, out var upgradedBaseUrl))
                {
                    settings.base_url = upgradedBaseUrl;
                    if (!BackendPairingProtocol.TryWriteSettingsAtomically(
                        ConfigurationPath,
                        settings,
                        out var migrationReason,
                        settings.allow_insecure_http))
                    {
                        Debug.LogWarning("[AstrBotBridge] Legacy endpoint was upgraded in memory but could not be saved: " + migrationReason);
                    }
                    else
                    {
                        RecordStage("configuration", "ready", "legacy_endpoint_migrated");
                    }
                }
                if (!AstrBotProtocol.TryValidateSettings(settings, out var reason))
                {
                    SetStatus("AstrBot config invalid: " + reason);
                    RecordStage("configuration", "failed", "configuration_invalid");
                    return;
                }
                // This is the single transport-policy gate. Plain HTTP is accepted
                // only after local opt-in and only for a literal private-network IP.
                settings.base_url = AstrBotProtocol.NormalizeBaseUrl(settings.base_url);
                IsConfigured = true;
                SetStatus("AstrBot config loaded");
                RecordStage("configuration", "ready", "configuration_ready");
                Debug.Log("[AstrBotBridge] Configuration loaded from " + ConfigurationPath);
            }
            catch (Exception exception)
            {
                SetStatus("AstrBot config could not be read");
                RecordStage("configuration", "failed", "configuration_invalid");
                Debug.LogWarning("[AstrBotBridge] Configuration error: " + exception.Message);
            }
        }

        private void TryMigrateLegacyConfiguration()
        {
            if (!string.Equals(configurationFileName, DefaultConfigurationFileName, StringComparison.Ordinal))
            {
                return;
            }

            var legacyPath = Path.Combine(Application.persistentDataPath, LegacyConfigurationFileName);
            if (!BackendPairingProtocol.TryMigrateLegacyConfiguration(
                legacyPath,
                ConfigurationPath,
                out var migrated,
                out var reason))
            {
                Debug.LogWarning("[AstrBotBridge] Legacy configuration migration skipped: " + reason);
                RecordStage("configuration", "failed", "legacy_configuration_migration_failed");
                return;
            }
            if (migrated)
            {
                Debug.Log("[AstrBotBridge] Legacy configuration migrated to " + ConfigurationPath);
                RecordStage("configuration", "ready", "legacy_configuration_migrated");
            }
        }

        private void OnDisable()
        {
            Shutdown(true);
        }

        private void OnApplicationQuit()
        {
            Shutdown(true);
        }

        private void Shutdown(bool closeSession)
        {
            if (shuttingDown)
            {
                return;
            }
            shuttingDown = true;
            CancelAudioUpload();
            activeTurnId = string.Empty;
            Interlocked.Increment(ref sseGeneration);
            if (connectionRoutine != null)
            {
                StopCoroutine(connectionRoutine);
                connectionRoutine = null;
            }
            if (spatialContextUploadRoutine != null)
            {
                StopCoroutine(spatialContextUploadRoutine);
                spatialContextUploadRoutine = null;
            }
            if (actionResultDeliveryRoutine != null)
            {
                StopCoroutine(actionResultDeliveryRoutine);
                actionResultDeliveryRoutine = null;
            }
            if (activeSseRequest != null)
            {
                activeSseRequest.Abort();
                activeSseRequest.Dispose();
                activeSseRequest = null;
            }
            if (closeSession && sessionReady && settings != null)
            {
                FireAndForgetClose();
            }
            sessionReady = false;
            eventStreamReady = false;
            healthReady = false;
            BackendChainStatus = "chain unknown";
            healthPipelineStatus = "unknown";
            nextSessionStartAt = 0f;
            nextSpatialContextUploadAt = 0f;
            lastSpatialContextUploadAt = float.NegativeInfinity;
            sentSpatialSignature = string.Empty;
            sessionId = string.Empty;
            actionReceiptKeys.Clear();
            actionReceiptOrder.Clear();
            actionResultDeliveries.Reset();
            ClearIncomingFrames();
        }

        private void OnDestroy()
        {
            if (spatialContextSource != null)
            {
                spatialContextSource.SnapshotChanged -= QueueSpatialContextSnapshot;
            }
        }

        private void FireAndForgetClose()
        {
            var payload = new SessionCloseRequest { session_id = sessionId };
            var request = CreateJsonRequest("session/close", JsonUtility.ToJson(payload));
            var operation = request.SendWebRequest();
            operation.completed += _ => request.Dispose();
        }

        private string Endpoint(string relative)
        {
            return settings.base_url + "/" + relative.TrimStart('/');
        }

        private static bool Succeeded(UnityWebRequest request)
        {
            return request.result == UnityWebRequest.Result.Success && request.responseCode >= 200 && request.responseCode < 300;
        }

        public static bool IsSseHandshakeReady(long responseCode)
        {
            return responseCode >= 200 && responseCode < 300;
        }

        public static bool CanRecoverExistingSession(long responseCode, string responseBody)
        {
            return responseCode == 409 && TryReadBridgeError(responseBody, out var code, out var message) &&
                code == "session_conflict" &&
                message.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsSessionCapacityConflict(long responseCode, string responseBody)
        {
            return responseCode == 409 && TryReadBridgeError(responseBody, out var code, out var message) &&
                code == "session_conflict" &&
                message.IndexOf("limit reached", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsStableSessionId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 35 ||
                !value.StartsWith("q3-", StringComparison.Ordinal))
            {
                return false;
            }
            for (var index = 3; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') ||
                      (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryReadBridgeError(string responseBody, out string code, out string message)
        {
            code = string.Empty;
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return false;
            }
            try
            {
                var response = JsonUtility.FromJson<BridgeErrorResponse>(responseBody);
                code = response?.data?.code ?? string.Empty;
                message = response?.message ?? string.Empty;
                return !string.IsNullOrEmpty(code);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        public static string ParseSessionChainStatus(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return "chain unknown";
            }

            try
            {
                var response = JsonUtility.FromJson<SessionStartResponse>(responseBody);
                var context = response == null || response.data == null
                    ? null
                    : response.data.protected_context;
                if (context == null)
                {
                    return "chain unknown";
                }
                return context.authorized
                    ? "EventBus eligible"
                    : NormalizeProtectedContextReason(context.reason);
            }
            catch (ArgumentException)
            {
                return "chain unknown";
            }
        }

        public static string ResolveBackendChainStatus(string sessionStatus, string pipelineStatus)
        {
            if (!string.Equals(sessionStatus, "EventBus eligible", StringComparison.Ordinal))
            {
                return string.IsNullOrWhiteSpace(sessionStatus) ? "chain unknown" : sessionStatus;
            }
            if (string.Equals(pipelineStatus, "ready", StringComparison.Ordinal))
            {
                return "EventBus ready";
            }
            if (string.Equals(pipelineStatus, "unavailable", StringComparison.Ordinal))
            {
                return "direct provider fallback";
            }
            return "EventBus eligible";
        }

        private static string NormalizeProtectedContextReason(string reason)
        {
            switch (reason)
            {
                case "owner_not_configured":
                case "quest_identity_not_allowlisted":
                case "invalid_bot_id":
                case "invalid_user_id":
                case "missing_bot_id":
                case "missing_user_id":
                case "client_id_mismatch":
                case "invalid_client_id":
                case "missing_client_id":
                case "trusted_client_id_missing":
                case "missing_platform_id":
                case "trusted_platform_id_missing":
                case "trusted_platform_not_configured":
                case "trusted_platform_unavailable":
                case "authorization_denied":
                case "authorization_timeout":
                case "authorization_error":
                    return reason;
                default:
                    return "protected_context_denied";
            }
        }

        private static string GetOrCreateStableSessionId()
        {
            var existing = PlayerPrefs.GetString(StableSessionPreferenceKey, string.Empty);
            if (IsStableSessionId(existing))
            {
                return existing;
            }
            var created = "q3-" + Guid.NewGuid().ToString("N");
            PlayerPrefs.SetString(StableSessionPreferenceKey, created);
            PlayerPrefs.Save();
            return created;
        }

        private void ResetReceivedTurnCounters()
        {
            receivedTurnEventCount = 0;
            receivedReplyAudioChunks = 0;
            receivedReplyAudioBytes = 0;
            receivedReplyText = false;
            receivedErrorCode = string.Empty;
            Interlocked.Exchange(ref sseFramesReceived, 0);
            sseFramesDispatched = 0;
            Interlocked.Exchange(ref sseQueueDepthPeak, 0);
            Interlocked.Exchange(ref sseQueueDelayMaxMs, 0);
        }

        private static string AudioRequestCode(string endpoint)
        {
            switch (endpoint)
            {
                case "turn/start": return "turn_start_http";
                case "audio/chunk": return "audio_chunk_http";
                case "audio/end": return "audio_end_http";
                default: return "audio_http";
            }
        }

        private static void UpdateMaximum(ref int target, int value)
        {
            var current = Volatile.Read(ref target);
            while (value > current)
            {
                var observed = Interlocked.CompareExchange(ref target, value, current);
                if (observed == current) return;
                current = observed;
            }
        }

        private void RecordIncomingEvent(ConversationEvent message, string traceId)
        {
            if (message == null) return;
            receivedTurnEventCount++;
            switch (message.Type)
            {
                case ConversationEventType.AsrFinal:
                    RecordStage("stt", "completed", eventCount: receivedTurnEventCount, traceId: traceId);
                    break;
                case ConversationEventType.ReplyTextDelta:
                    if (!receivedReplyText)
                    {
                        receivedReplyText = true;
                        RecordStage("eventbus", "completed", eventCount: receivedTurnEventCount, traceId: traceId);
                        RecordStage("llm", "completed", eventCount: receivedTurnEventCount, traceId: traceId);
                    }
                    break;
                case ConversationEventType.AudioChunk:
                    receivedReplyAudioChunks++;
                    receivedReplyAudioBytes += message.Pcm16 == null ? 0 : message.Pcm16.Length * 2;
                    break;
                case ConversationEventType.Error:
                    var code = string.IsNullOrWhiteSpace(message.ErrorCode)
                        ? "bridge_error"
                        : message.ErrorCode;
                    receivedErrorCode = code;
                    RecordStage(
                        ErrorStage(code),
                        "failed",
                        code,
                        eventCount: receivedTurnEventCount,
                        traceId: traceId);
                    break;
                case ConversationEventType.ReplyEnd:
                    if (receivedReplyAudioChunks > 0)
                    {
                        RecordStage(
                            "tts",
                            "completed",
                            chunks: receivedReplyAudioChunks,
                            bytes: receivedReplyAudioBytes,
                            eventCount: receivedTurnEventCount,
                            traceId: traceId);
                    }
                    RecordStage(
                        "reply",
                        message.TextSent || message.AudioSent ? "completed" : "failed",
                        message.TextSent || message.AudioSent
                            ? string.Empty
                            : string.IsNullOrEmpty(receivedErrorCode) ? "empty_reply" : receivedErrorCode,
                        chunks: receivedReplyAudioChunks,
                        bytes: receivedReplyAudioBytes,
                        eventCount: receivedTurnEventCount,
                        traceId: traceId);
                    break;
            }
            if (message.Type == ConversationEventType.ReplyEnd ||
                message.Type == ConversationEventType.Error)
            {
                if (string.Equals(message.TurnId, activeTurnId, StringComparison.Ordinal))
                {
                    activeTurnId = string.Empty;
                }
            }
        }

        private static string ErrorStage(string code)
        {
            if (code.StartsWith("stt", StringComparison.Ordinal)) return "stt";
            if (code.StartsWith("tts", StringComparison.Ordinal)) return "tts";
            if (code.StartsWith("audio", StringComparison.Ordinal)) return "audio_upload";
            if (code.Contains("pipeline") || code.Contains("owner") || code.Contains("identity") ||
                code.Contains("platform")) return "eventbus";
            return "reply";
        }

        private static string ActionPhaseName(AvatarActionReceiptPhase phase)
        {
            switch (phase)
            {
                case AvatarActionReceiptPhase.Accepted: return "accepted";
                case AvatarActionReceiptPhase.Started: return "started";
                case AvatarActionReceiptPhase.Completed: return "completed";
                case AvatarActionReceiptPhase.Rejected: return "rejected";
                case AvatarActionReceiptPhase.Interrupted: return "interrupted";
                default: return string.Empty;
            }
        }

        private static string AuthorizationCode(string backendChainStatus)
        {
            switch (backendChainStatus)
            {
                case "EventBus ready":
                case "EventBus eligible":
                case "direct provider fallback":
                    return string.Empty;
                default:
                    return string.IsNullOrWhiteSpace(backendChainStatus) || backendChainStatus == "chain unknown"
                        ? "protected_context_denied"
                        : backendChainStatus;
            }
        }

        private static string ReadFailureCode(UnityWebRequest request, string fallback)
        {
            if (request != null && TryReadBridgeError(
                    request.downloadHandler == null ? string.Empty : request.downloadHandler.text,
                    out var bridgeCode,
                    out _))
            {
                return string.IsNullOrWhiteSpace(bridgeCode) ? fallback : bridgeCode;
            }
            return fallback;
        }

        private static long DiagnosticTimestamp()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static int ElapsedMs(long startedAt)
        {
            if (startedAt <= 0L) return -1;
            var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - startedAt;
            var milliseconds = elapsed * 1000d / System.Diagnostics.Stopwatch.Frequency;
            return Mathf.Clamp((int)Math.Round(milliseconds), 0, 3600000);
        }

        private void RecordStage(
            string stage,
            string status,
            string code = "",
            long httpStatus = 0,
            int elapsedMs = -1,
            int chunks = 0,
            int bytes = 0,
            int eventCount = 0,
            string traceId = "",
            int queueDepth = -1,
            int bufferedMs = -1)
        {
            diagnostics = diagnostics != null ? diagnostics : GetComponent<RuntimeDebugLog>();
            diagnostics?.RecordStage(
                stage,
                status,
                code,
                httpStatus,
                elapsedMs,
                chunks,
                bytes,
                eventCount,
                traceId: string.IsNullOrEmpty(traceId) ? currentTraceId : traceId,
                queueDepth: queueDepth,
                bufferedMs: bufferedMs);
        }

        private void MarkEventStreamReady()
        {
            if (eventStreamReady)
            {
                return;
            }
            eventStreamReady = true;
            SetStatus("AstrBot SSE connected");
            RecordStage(
                "sse",
                "connected",
                httpStatus: activeSseRequest == null ? 0 : activeSseRequest.responseCode,
                elapsedMs: ElapsedMs(sseConnectStartedAt));
        }

        private void SetStatus(string value)
        {
            var next = string.IsNullOrWhiteSpace(value) ? "AstrBot status unavailable" : value;
            if (string.Equals(Status, next, StringComparison.Ordinal))
            {
                return;
            }
            Status = next;
            Debug.Log("[AstrBotBridge] " + next, this);
        }

        private static string HttpFailure(string operation, UnityWebRequest request)
        {
            var detail = request == null ? "request unavailable" : request.error;
            var code = request == null ? 0 : request.responseCode;
            if (request != null && TryReadBridgeError(
                    request.downloadHandler == null ? string.Empty : request.downloadHandler.text,
                    out var bridgeCode,
                    out _))
            {
                return operation + " failed: " + bridgeCode;
            }
            return operation + " failed (HTTP " + code + "): " + detail;
        }

        private static bool IsInteraction(string value)
        {
            return value == "handshake" || value == "head_pat" || value == "cheek_pinch" ||
                value == "gaze" || value == "speaking";
        }

        private static bool IsPhase(string value)
        {
            return value == "start" || value == "update" || value == "end" || value == "cancel";
        }

        private static bool IsHand(string value)
        {
            return value == "left" || value == "right" || value == "both" || value == "none";
        }

        private sealed class SseDownloadHandler : DownloadHandlerScript
        {
            private readonly SseEventStreamParser parser = new SseEventStreamParser();

            private readonly Action contentStarted;

            public SseDownloadHandler(
                Action<SseEventFrame> receive,
                Action contentStarted) : base(new byte[8192])
            {
                parser.EventReceived += receive;
                this.contentStarted = contentStarted;
            }

            protected override void ReceiveContentLengthHeader(ulong contentLength)
            {
                contentStarted?.Invoke();
            }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                parser.Push(data, dataLength);
                return true;
            }
        }

        [Serializable]
        private sealed class HealthResponse
        {
            public string status;
            public HealthData data;
        }

        [Serializable]
        private sealed class HealthData
        {
            public string protocol_version;
            public string transport;
            public SeriesIntegrations series_integrations;
        }

        [Serializable]
        private sealed class SeriesIntegrations
        {
            public MessagePipelineStatus astrbot_message_pipeline;
        }

        [Serializable]
        private sealed class MessagePipelineStatus
        {
            public bool available;
        }

        [Serializable]
        private sealed class SessionStartResponse
        {
            public string status;
            public SessionStartData data;
        }

        [Serializable]
        private sealed class SessionStartData
        {
            public SessionProtectedContext protected_context;
        }

        [Serializable]
        private sealed class SessionProtectedContext
        {
            public bool authorized;
            public string reason;
        }

        [Serializable]
        private sealed class BridgeErrorResponse
        {
            public string message;
            public BridgeErrorData data;
        }

        [Serializable]
        private sealed class BridgeErrorData
        {
            public string code;
        }
    }
}

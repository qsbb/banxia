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
    /// <summary>
    /// Quest Avatar Bridge protocol 1.0 transport. Secrets are loaded from a
    /// JSON file under Application.persistentDataPath and are never serialized
    /// into the Unity scene or APK.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class AstrBotBridge : MonoBehaviour, IConversationTransport
    {
        [SerializeField] private bool autoConnect = true;
        [SerializeField] private string configurationFileName = "quest_avatar_bridge.json";
        [SerializeField] private float reconnectDelaySeconds = 1.5f;
        [SerializeField] private int requestTimeoutSeconds = 15;
        [SerializeField, Range(8, 256)] private int maxIncomingFramesPerUpdate = 64;

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
        private float audioUploadStartedAt;
        private float audioEndRequestedAt = -1f;
        private float nextSessionStartAt;

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

            var remainingFrameBudget = Mathf.Clamp(maxIncomingFramesPerUpdate, 8, 256);
            while (remainingFrameBudget-- > 0 && incomingFrames.TryDequeue(out var frame))
            {
                if (AstrBotProtocol.TryMapSseEvent(sessionId, frame.EventName, frame.Data, out var message, out var error))
                {
                    EventReceived?.Invoke(message);
                }
                else if (!error.Contains("stale session"))
                {
                    Debug.LogWarning("[AstrBotBridge] Ignored invalid SSE event: " + error);
                }
            }
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
            CancelAudioUpload();
            if (!CanSend(turnId, true))
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
            StartCoroutine(PostJson("turn/start", JsonUtility.ToJson(request), turnId, true));
        }

        public bool BeginAudioTurn(string turnId)
        {
            if (!CanSend(turnId, true))
            {
                return false;
            }

            CancelAudioUpload();
            audioUploadTurnId = turnId;
            audioEndRequested = false;
            audioSequence = 0;
            queuedInputAudioBytes = 0;
            uploadedInputAudioBytes = 0;
            uploadedInputBatchCount = 0;
            audioUploadStartedAt = Time.unscaledTime;
            audioEndRequestedAt = -1f;
            audioUploadRoutine = StartCoroutine(UploadAudioTurn(turnId));
            SetStatus("Recording voice for AstrBot");
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
                EventReceived?.Invoke(new ConversationEvent
                {
                    Type = ConversationEventType.Error,
                    TurnId = turnId,
                    ErrorCode = "audio_upload_backpressure",
                    Text = "Voice upload could not keep up with capture"
                });
                CancelAudioUpload();
                return false;
            }

            var copy = new byte[pcm16.Length];
            Buffer.BlockCopy(pcm16, 0, copy, 0, pcm16.Length);
            outgoingAudioChunks.Enqueue(copy);
            queuedInputAudioBytes += copy.Length;
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
            return true;
        }

        public void Interrupt(string turnId)
        {
            if (string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal))
            {
                CancelAudioUpload();
            }
            if (!CanSend(turnId, false))
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
                        yield break;
                    }
                    SetStatus("AstrBot health response is incompatible");
                }
                else
                {
                    SetStatus(HttpFailure("Health check", request));
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
                relationship_profile_id = settings.relationship_profile_id ?? string.Empty
            };

            eventStreamReady = false;
            SetStatus("Starting AstrBot session");
            using (var request = CreateJsonRequest("session/start", JsonUtility.ToJson(payload)))
            {
                yield return request.SendWebRequest();
                if (Succeeded(request))
                {
                    nextSessionStartAt = 0f;
                    sessionReady = true;
                    BackendChainStatus = ResolveBackendChainStatus(
                        ParseSessionChainStatus(request.downloadHandler.text), healthPipelineStatus);
                    SetStatus("AstrBot session ready (" + BackendChainStatus + ")");
                    Debug.Log("[AstrBotBridge] Backend chain: " + BackendChainStatus, this);
                    Debug.Log("[AstrBotBridge] Session established.", this);
                }
                else if (CanRecoverExistingSession(request.responseCode, request.downloadHandler.text))
                {
                    // Android can terminate the process before session.close is sent.
                    // Reusing one persisted ID lets the next process reattach.
                    nextSessionStartAt = 0f;
                    sessionReady = true;
                    SetStatus("Existing AstrBot session found; reconnecting SSE");
                }
                else if (IsSessionCapacityConflict(request.responseCode, request.downloadHandler.text))
                {
                    sessionId = string.Empty;
                    nextSessionStartAt = Time.unscaledTime + 10f;
                    SetStatus("AstrBot session capacity is full; reload the bridge plugin");
                }
                else
                {
                    sessionId = string.Empty;
                    SetStatus(HttpFailure("Session start", request));
                }
            }
        }

        private IEnumerator RunEventStream()
        {
            var request = UnityWebRequest.Get(Endpoint("events/" + UnityWebRequest.EscapeURL(sessionId)));
            eventStreamReady = false;
            Interlocked.Exchange(ref receivedStreamHeaders, 0);
            Interlocked.Exchange(ref receivedStreamData, 0);
            var handler = new SseDownloadHandler(
                frame =>
                {
                    incomingFrames.Enqueue(frame);
                    Interlocked.Exchange(ref receivedStreamData, 1);
                },
                () => Interlocked.Exchange(ref receivedStreamHeaders, 1));
            request.downloadHandler.Dispose();
            request.downloadHandler = handler;
            ConfigureHeaders(request, true);
            request.timeout = 0;
            activeSseRequest = request;
            SetStatus("Connecting AstrBot SSE");

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

            if (!shuttingDown)
            {
                if (request.responseCode == 404)
                {
                    sessionReady = false;
                    sessionId = string.Empty;
                    SetStatus("AstrBot session expired; recreating");
                }
                else
                {
                    SetStatus(HttpFailure("SSE disconnected", request));
                }
            }
            request.Dispose();
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
                result => startSucceeded = result);
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
                        result => chunkSucceeded = result);
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
                        result => endSucceeded = result);
                    if (endSucceeded)
                    {
                        EventReceived?.Invoke(new ConversationEvent
                        {
                            Type = ConversationEventType.Thinking,
                            TurnId = turnId
                        });
                        SetStatus("AstrBot is processing voice");
                        var now = Time.unscaledTime;
                        Debug.Log($"[AstrBotBridge] Voice upload complete: {uploadedInputAudioBytes} B in " +
                            $"{uploadedInputBatchCount} batches, stream {Mathf.Max(0f, now - audioUploadStartedAt):F2}s, " +
                            $"final flush {(audioEndRequestedAt < 0f ? 0f : Mathf.Max(0f, now - audioEndRequestedAt)):F2}s.");
                    }
                    ClearAudioUpload(turnId);
                    yield break;
                }

                yield return null;
            }
        }

        private IEnumerator PostAudioJson(string endpoint, string json, string turnId, Action<bool> completed)
        {
            using (var request = CreateJsonRequest(endpoint, json))
            {
                activeAudioRequest = request;
                yield return request.SendWebRequest();
                if (ReferenceEquals(activeAudioRequest, request))
                {
                    activeAudioRequest = null;
                }
                var succeeded = Succeeded(request);
                if (!succeeded && string.Equals(turnId, audioUploadTurnId, StringComparison.Ordinal))
                {
                    EventReceived?.Invoke(new ConversationEvent
                    {
                        Type = ConversationEventType.Error,
                        TurnId = turnId,
                        ErrorCode = "audio_http_request_failed",
                        Text = HttpFailure(endpoint, request)
                    });
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
            var selected = new List<byte[]>();
            var total = 0;
            while (chunks.Count > 0)
            {
                var next = chunks.Peek();
                if (selected.Count > 0 && total + next.Length > limit)
                {
                    break;
                }
                selected.Add(chunks.Dequeue());
                total += next.Length;
                if (total >= limit)
                {
                    break;
                }
            }

            var merged = new byte[total];
            var offset = 0;
            foreach (var chunk in selected)
            {
                Buffer.BlockCopy(chunk, 0, merged, offset, chunk.Length);
                offset += chunk.Length;
            }
            return merged;
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
            audioEndRequestedAt = -1f;
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
            audioEndRequestedAt = -1f;
            outgoingAudioChunks.Clear();
        }

        private IEnumerator PostJson(string endpoint, string json, string turnId, bool emitThinking)
        {
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
                            TurnId = turnId
                        });
                    }
                }
                else if (!string.IsNullOrEmpty(turnId))
                {
                    EventReceived?.Invoke(new ConversationEvent
                    {
                        Type = ConversationEventType.Error,
                        TurnId = turnId,
                        ErrorCode = "http_request_failed",
                        Text = HttpFailure(endpoint, request)
                    });
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
            request.SetRequestHeader("Authorization", "Bearer " + settings.astrbot_api_key);
            request.SetRequestHeader("X-Quest-Avatar-Key", settings.bridge_api_key);
            request.SetRequestHeader("Accept", eventStream ? "text/event-stream" : "application/json");
        }

        private bool CanSend(string turnId, bool emitError)
        {
            if (sessionReady && isActiveAndEnabled && !shuttingDown)
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
            if (!File.Exists(ConfigurationPath))
            {
                SetStatus("AstrBot configuration missing");
                return;
            }

            try
            {
                settings = JsonUtility.FromJson<AstrBotBridgeSettings>(File.ReadAllText(ConfigurationPath, Encoding.UTF8));
                if (!AstrBotProtocol.TryValidateSettings(settings, out var reason))
                {
                    SetStatus("AstrBot config invalid: " + reason);
                    return;
                }
                // This is the single transport-policy gate. Plain HTTP is accepted
                // only after local opt-in and only for a literal private-network IP.
                settings.base_url = AstrBotProtocol.NormalizeBaseUrl(settings.base_url);
                IsConfigured = true;
                SetStatus("AstrBot config loaded");
                Debug.Log("[AstrBotBridge] Configuration loaded from " + ConfigurationPath);
            }
            catch (Exception exception)
            {
                SetStatus("AstrBot config could not be read");
                Debug.LogWarning("[AstrBotBridge] Configuration error: " + exception.Message);
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
            if (connectionRoutine != null)
            {
                StopCoroutine(connectionRoutine);
                connectionRoutine = null;
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
            sessionId = string.Empty;
            while (incomingFrames.TryDequeue(out _)) { }
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

        private void MarkEventStreamReady()
        {
            if (eventStreamReady)
            {
                return;
            }
            eventStreamReady = true;
            SetStatus("AstrBot SSE connected");
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

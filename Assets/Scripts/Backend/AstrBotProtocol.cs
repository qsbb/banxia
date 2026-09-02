using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net;
using System.Text;
using UnityEngine;

namespace QuestMmdPlayer
{
    [Serializable]
    public sealed class AstrBotBridgeSettings
    {
        public string base_url;
        public string astrbot_api_key;
        public string bridge_api_key;
        public string client_id = "quest3-companion";
        public string user_id = "quest-user";
        public string bot_id = "default";
        public string group_id = "";
        public string relationship_profile_id = "";
        public bool allow_insecure_http;
        public int audio_upload_batch_bytes = AstrBotProtocol.DefaultAudioUploadBatchBytes;
    }

    public readonly struct SseEventFrame
    {
        public SseEventFrame(
            string eventName,
            string data,
            long receivedAtTicks = 0L,
            long generation = 0L)
        {
            EventName = eventName ?? string.Empty;
            Data = data ?? string.Empty;
            ReceivedAtTicks = receivedAtTicks;
            Generation = generation;
        }

        public string EventName { get; }
        public string Data { get; }
        /// <summary>Monotonic timestamp captured when the SSE frame is assembled.</summary>
        public long ReceivedAtTicks { get; }
        /// <summary>Transport generation that produced this frame.</summary>
        public long Generation { get; }
    }

    /// <summary>
    /// Incremental UTF-8 SSE parser. Network chunks may split either a UTF-8
    /// character or an SSE line, so parsing cannot be based on individual reads.
    /// </summary>
    public sealed class SseEventStreamParser
    {
        private readonly Decoder decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder line = new StringBuilder();
        private readonly StringBuilder data = new StringBuilder();
        // DownloadHandlerScript uses a bounded read buffer. Reuse the decode
        // buffer across callbacks so a streaming TTS response does not create
        // a short-lived char array for every network read.
        private char[] charBuffer = Array.Empty<char>();
        private string eventName = string.Empty;

        public event Action<SseEventFrame> EventReceived;

        public void Push(byte[] bytes, int count)
        {
            if (bytes == null || count <= 0)
            {
                return;
            }

            count = Math.Min(count, bytes.Length);
            var requiredChars = Encoding.UTF8.GetMaxCharCount(count);
            if (charBuffer.Length < requiredChars)
            {
                charBuffer = new char[requiredChars];
            }
            var charCount = decoder.GetChars(bytes, 0, count, charBuffer, 0, false);
            for (var index = 0; index < charCount; index++)
            {
                var value = charBuffer[index];
                if (value == '\n')
                {
                    ProcessLine();
                }
                else if (value != '\r')
                {
                    line.Append(value);
                }
            }
        }

        public void Reset()
        {
            decoder.Reset();
            line.Clear();
            data.Clear();
            eventName = string.Empty;
        }

        private void ProcessLine()
        {
            if (line.Length == 0)
            {
                Dispatch();
                return;
            }
            if (line[0] == ':')
            {
                line.Clear();
                return;
            }

            var separator = -1;
            for (var index = 0; index < line.Length; index++)
            {
                if (line[index] == ':')
                {
                    separator = index;
                    break;
                }
            }

            var valueStart = separator < 0 ? line.Length : separator + 1;
            if (valueStart < line.Length && line[valueStart] == ' ')
            {
                valueStart++;
            }
            if (IsField(line, separator, "event"))
            {
                eventName = valueStart < line.Length
                    ? line.ToString(valueStart, line.Length - valueStart)
                    : string.Empty;
            }
            else if (IsField(line, separator, "data"))
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                if (valueStart < line.Length)
                {
                    data.Append(line, valueStart, line.Length - valueStart);
                }
            }
            line.Clear();
        }

        private static bool IsField(StringBuilder value, int separator, string expected)
        {
            var fieldLength = separator < 0 ? value.Length : separator;
            if (fieldLength != expected.Length)
            {
                return false;
            }
            for (var index = 0; index < expected.Length; index++)
            {
                if (value[index] != expected[index])
                {
                    return false;
                }
            }
            return true;
        }

        private void Dispatch()
        {
            if (data.Length > 0)
            {
                EventReceived?.Invoke(new SseEventFrame(
                    string.IsNullOrEmpty(eventName) ? "message" : eventName,
                    data.ToString(),
                    System.Diagnostics.Stopwatch.GetTimestamp()));
            }
            eventName = string.Empty;
            data.Clear();
        }
    }

    public static class AstrBotProtocol
    {
        public const string Version = "1.0";
        // A 16 KiB PCM16 chunk is about 341 ms at 24 kHz mono. Keeping a
        // single SSE event bounded prevents one malformed or burst-sized TTS
        // event from monopolizing the Unity frame and allocating unbounded
        // temporary arrays.
        public const int MaxReplyAudioBytes = 16 * 1024;

        // reply.suggestions bounds: at most 3 quick replies, each trimmed to
        // 200 characters so a rogue backend cannot flood the chat UI.
        public const int MaxSuggestionCount = 3;
        public const int MaxSuggestionLength = 200;

        // Streaming STT upload batching. 16 kHz mono PCM16 is 32 bytes/ms, so
        // the default 3200 bytes is ~100 ms of audio. Smaller batches shorten
        // the client-side aggregation latency but increase the HTTP request
        // rate; keep the floor at ~40 ms (1280 bytes) and cap at ~500 ms.
        public const int DefaultAudioUploadBatchBytes = 3200;
        public const int MinAudioUploadBatchBytes = 1280;
        public const int MaxAudioUploadBatchBytes = 16000;
        private static readonly string[] ExecutableActions =
        {
            "wave", "bow", "dance", "dance_next", "raise_hand", "raise_leg", "turn_half",
            "crouch", "sit", "lie", "nod", "sway", "handshake", "head_pat",
            "cheek_pinch", "refuse", "step_back"
        };

        public static string[] SupportedActions()
        {
            return (string[])ExecutableActions.Clone();
        }

        public static bool TryValidateSettings(AstrBotBridgeSettings settings, out string reason)
        {
            reason = string.Empty;
            if (settings == null)
            {
                reason = "Configuration is missing";
                return false;
            }
            if (!Uri.TryCreate(settings.base_url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            {
                reason = "base_url must be an absolute HTTP or HTTPS URL";
                return false;
            }
            if (uri.Scheme == Uri.UriSchemeHttp &&
                (!settings.allow_insecure_http || !IsPrivateNetworkHost(uri.Host)))
            {
                reason = "Plain HTTP requires allow_insecure_http=true and a literal private-network IP";
                return false;
            }
            if (string.IsNullOrWhiteSpace(settings.astrbot_api_key))
            {
                reason = "astrbot_api_key is missing";
                return false;
            }
            if (string.IsNullOrEmpty(settings.bridge_api_key) || settings.bridge_api_key.Length < 32)
            {
                reason = "bridge_api_key must contain at least 32 characters";
                return false;
            }
            if (!IsIdentifier(settings.client_id))
            {
                reason = "client_id is not a valid protocol identifier";
                return false;
            }
            if (!IsScopeValue(settings.user_id) || !IsScopeValue(settings.bot_id))
            {
                reason = "user_id and bot_id are required and must be at most 128 characters";
                return false;
            }
            return true;
        }

        public static string NormalizeBaseUrl(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().TrimEnd('/');
        }

        public static bool IsPrivateNetworkHost(string host)
        {
            if (!IPAddress.TryParse(host, out var address))
            {
                // Host names are deliberately rejected to avoid DNS rebinding.
                return false;
            }

            if (IPAddress.IsLoopback(address))
            {
                return true;
            }

            var bytes = address.GetAddressBytes();
            if (bytes.Length == 4)
            {
                return bytes[0] == 10 ||
                       (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                       (bytes[0] == 192 && bytes[1] == 168) ||
                       (bytes[0] == 169 && bytes[1] == 254);
            }

            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        }

        public static bool TryMapSseEvent(
            string expectedSessionId,
            string eventName,
            string json,
            out ConversationEvent message,
            out string error)
        {
            return TryMapSseEventCore(
                expectedSessionId,
                eventName,
                json,
                false,
                out message,
                out error);
        }

        internal static bool TryMapSseEventPooled(
            string expectedSessionId,
            string eventName,
            string json,
            out ConversationEvent message,
            out string error)
        {
            return TryMapSseEventCore(
                expectedSessionId,
                eventName,
                json,
                true,
                out message,
                out error);
        }

        private static bool TryMapSseEventCore(
            string expectedSessionId,
            string eventName,
            string json,
            bool poolAudio,
            out ConversationEvent message,
            out string error)
        {
            message = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(eventName) || string.IsNullOrWhiteSpace(json))
            {
                error = "SSE event or data is empty";
                return false;
            }

            BridgeSsePayload payload;
            try
            {
                payload = JsonUtility.FromJson<BridgeSsePayload>(json);
            }
            catch (Exception exception)
            {
                error = "Invalid SSE JSON: " + exception.Message;
                return false;
            }

            if (payload == null || payload.protocol_version != Version)
            {
                error = "Unsupported protocol version";
                return false;
            }
            if (!string.Equals(payload.type, eventName, StringComparison.Ordinal))
            {
                error = "SSE event name does not match payload type";
                return false;
            }
            if (!string.IsNullOrEmpty(expectedSessionId) &&
                !string.Equals(payload.session_id, expectedSessionId, StringComparison.Ordinal))
            {
                error = "SSE event belongs to a stale session";
                return false;
            }

            switch (payload.type)
            {
                case "asr.partial":
                    message = Basic(payload, ConversationEventType.AsrPartial);
                    break;
                case "asr.final":
                    message = Basic(payload, ConversationEventType.AsrFinal);
                    break;
                case "reply.text.delta":
                    message = Basic(payload, ConversationEventType.ReplyTextDelta);
                    break;
                case "reply.audio.chunk":
                    if (!TryDecodeAudio(payload, poolAudio, out var samples, out var sampleCount, out error))
                    {
                        return false;
                    }
                    message = Basic(payload, ConversationEventType.AudioChunk);
                    message.Pcm16 = samples;
                    message.Pcm16Length = sampleCount;
                    message.Pcm16FromPool = poolAudio;
                    message.SpeechId = payload.speech_id;
                    message.AudioSequence = payload.sequence;
                    message.AudioFirst = payload.first;
                    message.AudioEnd = payload.end;
                    message.SampleRate = payload.sample_rate;
                    break;
                case "reply.speech.timeline":
                    if (!TryMapVisemeTimeline(payload.visemes, out var timeline, out error))
                    {
                        return false;
                    }
                    message = Basic(payload, ConversationEventType.SpeechTimeline);
                    message.VisemeTimeline = timeline;
                    break;
                case "avatar.intent":
                    message = Basic(payload, ConversationEventType.AvatarIntent);
                    message.ActionId = AvatarActionReceiptTracker.IsActionId(payload.action_id)
                        ? payload.action_id
                        : string.Empty;
                    message.InReplyToEventId = payload.in_reply_to_event_id;
                    message.Emotion = SanitizeEmotion(payload.emotion);
                    message.ActionMethod = SanitizeActionMethod(payload.method, payload.gesture);
                    message.Gesture = message.ActionMethod;
                    message.ActionParameters = SanitizeActionParameters(payload.parameters);
                    message.ActionTransition = SanitizeActionTransition(payload.transition);
                    message.ActionSource = SanitizeActionSource(payload.source);
                    message.LookAt = SanitizeLookAt(payload.look_at);
                    message.Intensity = Mathf.Clamp01(payload.intensity);
                    message.DurationMs = Mathf.Clamp(payload.duration_ms, 0, 30000);
                    message.ReasonCode = payload.reason_code ?? string.Empty;
                    break;
                case "reply.end":
                    message = Basic(payload, ConversationEventType.ReplyEnd);
                    message.SpeechId = payload.speech_id;
                    message.AudioSequenceEnd = payload.audio_sequence_end;
                    message.TextSent = payload.text_sent;
                    message.AudioSent = payload.audio_sent;
                    break;
                case "reply.suggestions":
                    message = Basic(payload, ConversationEventType.ReplySuggestions);
                    message.Suggestions = SanitizeSuggestions(payload.suggestions);
                    break;
                case "error":
                    message = Basic(payload, ConversationEventType.Error);
                    message.ErrorCode = payload.code ?? "bridge_error";
                    message.Text = string.IsNullOrWhiteSpace(payload.message)
                        ? message.ErrorCode
                        : message.ErrorCode + ": " + payload.message;
                    break;
                default:
                    error = "Unsupported SSE event type: " + payload.type;
                    return false;
            }

            return true;
        }

        public static string SanitizeEmotion(string value)
        {
            switch (value)
            {
                case "neutral":
                case "happy":
                case "shy":
                case "surprised":
                case "concerned":
                case "uncomfortable":
                    return value;
                default:
                    return "neutral";
            }
        }

        public static string SanitizeGesture(string value)
        {
            switch (value)
            {
                case "idle":
                case "talk":
                case "wave":
                case "bow":
                case "handshake":
                case "head_pat":
                case "cheek_pinch":
                case "refuse":
                case "step_back":
                case "dance":
                case "dance_next":
                case "nod":
                case "sway":
                case "raise_hand":
                case "raise_leg":
                case "turn_half":
                case "crouch":
                case "sit":
                case "lie":
                case "lie_down":
                    return value;
                default:
                    return "idle";
            }
        }

        public static string SanitizeActionMethod(string method, string legacyGesture)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                return SanitizeGesture(legacyGesture);
            }
            return SanitizeGesture(method.Trim().ToLowerInvariant());
        }

        public static AvatarActionParameters SanitizeActionParameters(AvatarActionParametersPayload value)
        {
            if (value == null)
            {
                return new AvatarActionParameters();
            }
            return new AvatarActionParameters
            {
                AngleDegrees = Mathf.Clamp(value.angle_degrees, -180f, 180f),
                Depth = value.depth <= 0f ? 0f : Mathf.Clamp(value.depth, .2f, 1f),
                HoldMs = Mathf.Clamp(value.hold_ms, 0, 5000),
                Style = SanitizeActionStyle(value.style)
            };
        }

        public static AvatarActionTransition SanitizeActionTransition(AvatarActionTransitionPayload value)
        {
            if (value == null)
            {
                return new AvatarActionTransition();
            }
            return new AvatarActionTransition
            {
                EnterMs = Mathf.Clamp(value.enter_ms, 0, 5000),
                ExitMs = Mathf.Clamp(value.exit_ms, 0, 5000),
                Easing = SanitizeActionEasing(value.easing)
            };
        }

        public static string SanitizeActionEasing(string value)
        {
            return string.Equals(value, "ease_in_out", StringComparison.Ordinal)
                ? "ease_in_out"
                : "smoothstep";
        }

        public static string SanitizeActionStyle(string value)
        {
            switch (string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant())
            {
                case "gentle":
                case "natural":
                case "energetic":
                    return value.Trim().ToLowerInvariant();
                default:
                    return "natural";
            }
        }

        public static string SanitizeActionSource(string value)
        {
            switch (string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant())
            {
                case "explicit_request":
                case "fast_provider":
                case "eventbus_tool":
                case "direct_model":
                case "interaction_policy":
                case "fallback":
                    return value.Trim().ToLowerInvariant();
                default:
                    return "backend";
            }
        }

        public static string SanitizeLookAt(string value)
        {
            switch (value)
            {
                case "user":
                case "hand":
                case "away":
                case "none":
                    return value;
                default:
                    return "none";
            }
        }

        private static ConversationEvent Basic(BridgeSsePayload payload, ConversationEventType type)
        {
            return new ConversationEvent
            {
                Type = type,
                TurnId = payload.turn_id ?? string.Empty,
                Text = payload.text ?? string.Empty,
                BackendTiming = ToBackendTiming(payload.server_timing)
            };
        }

        private static BackendTimingSnapshot ToBackendTiming(ServerTimingPayload payload)
        {
            if (payload == null ||
                (payload.schema_version != 1 && payload.contract != "server_timing@1.0"))
            {
                return null;
            }

            return new BackendTimingSnapshot
            {
                SchemaVersion = 1,
                SttMs = ClampServerDuration(payload.stt_ms),
                DecisionMs = ClampServerDuration(payload.decision_ms),
                TtsFirstChunkMs = ClampServerDuration(payload.tts_first_chunk_ms),
                TtsTotalMs = ClampServerDuration(payload.tts_total_ms),
                DecisionHooksMs = ClampServerDuration(payload.decision_hooks_ms),
                DecisionProviderMs = ClampServerDuration(payload.decision_provider_ms),
                EventLoopLagMs = ClampServerDuration(payload.event_loop_lag_ms),
                ServerTraceId = BoundedProtocolToken(payload.trace_id),
                TurnTotalMs = ClampServerDuration(payload.turn_total_ms),
                DecisionPath = payload.decision_path == "astrbot_event_bus" ||
                    payload.decision_path == "direct_provider"
                    ? payload.decision_path
                    : "unknown"
            };
        }

        /// <summary>Keeps only opaque, bounded tokens (ids) from the server.</summary>
        private static string BoundedProtocolToken(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
            {
                return string.Empty;
            }
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!char.IsLetterOrDigit(character) && character != '.' &&
                    character != '_' && character != ':' && character != '-')
                {
                    return string.Empty;
                }
            }
            return value;
        }

        /// <summary>
        /// reply.suggestions 清洗：丢掉空/超长项，最多保留 3 条，逐条 Trim、截断到
        /// 200 字符。返回空数组表示本次无可显示建议。
        /// </summary>
        private static string[] SanitizeSuggestions(string[] suggestions)
        {
            if (suggestions == null || suggestions.Length == 0)
            {
                return Array.Empty<string>();
            }
            var kept = new List<string>(capacity: 3);
            foreach (var raw in suggestions)
            {
                if (raw == null)
                {
                    continue;
                }
                var trimmed = raw.Trim();
                if (trimmed.Length == 0)
                {
                    continue;
                }
                if (trimmed.Length > MaxSuggestionLength)
                {
                    trimmed = trimmed.Substring(0, MaxSuggestionLength);
                }
                kept.Add(trimmed);
                if (kept.Count == MaxSuggestionCount)
                {
                    break;
                }
            }
            return kept.ToArray();
        }

        private static int ClampServerDuration(int value)
        {
            return value <= 0 ? -1 : Mathf.Clamp(value, 1, 3600000);
        }

        private static bool TryDecodeAudio(
            BridgeSsePayload payload,
            bool poolAudio,
            out short[] samples,
            out int sampleCount,
            out string error)
        {
            samples = null;
            sampleCount = 0;
            error = string.Empty;
            if (payload.format != "pcm16" || payload.sample_rate != 24000 || payload.channels != 1)
            {
                error = "Unsupported reply audio format";
                return false;
            }

            var encoded = payload.data ?? string.Empty;
            var maximumEncodedLength = ((MaxReplyAudioBytes + 2) / 3) * 4;
            if (encoded.Length > maximumEncodedLength)
            {
                error = "Reply audio chunk is too large";
                return false;
            }

            // Allow the decoder to finish the rounded Base64 boundary so an
            // oversized payload is reported as oversized, not malformed.
            var bytes = ArrayPool<byte>.Shared.Rent(MaxReplyAudioBytes + 2);
            try
            {
                if (!Convert.TryFromBase64String(encoded, bytes, out var byteCount))
                {
                    error = "Reply audio is not valid Base64";
                    return false;
                }
                if (byteCount > MaxReplyAudioBytes)
                {
                    error = "Reply audio chunk is too large";
                    return false;
                }
                if (byteCount == 0 || (byteCount & 1) != 0)
                {
                    error = byteCount == 0
                        ? "Reply audio must contain PCM16 bytes"
                        : "Reply audio must contain an even number of PCM16 bytes";
                    return false;
                }

                sampleCount = byteCount / 2;
                samples = poolAudio
                    ? ArrayPool<short>.Shared.Rent(sampleCount)
                    : new short[sampleCount];
                if (BitConverter.IsLittleEndian)
                {
                    Buffer.BlockCopy(bytes, 0, samples, 0, byteCount);
                }
                else
                {
                    for (var index = 0; index < sampleCount; index++)
                    {
                        var offset = index * 2;
                        samples[index] = unchecked((short)(bytes[offset] | (bytes[offset + 1] << 8)));
                    }
                }
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }
        }

        private static bool TryMapVisemeTimeline(
            VisemeCuePayload[] payload,
            out SpeechVisemeCue[] timeline,
            out string error)
        {
            timeline = null;
            error = string.Empty;
            if (payload == null || payload.Length == 0 || payload.Length > 256)
            {
                error = "Speech timeline must contain 1 to 256 explicit cues";
                return false;
            }

            var mapped = new SpeechVisemeCue[payload.Length];
            var previousStart = -1;
            for (var index = 0; index < payload.Length; index++)
            {
                var cue = payload[index];
                if (cue == null || !IsVisemeSymbol(cue.symbol) || cue.start_ms < 0 ||
                    cue.end_ms <= cue.start_ms || cue.end_ms > 600000 ||
                    cue.start_ms < previousStart)
                {
                    error = "Speech timeline contains an invalid or unsorted cue";
                    return false;
                }
                previousStart = cue.start_ms;
                mapped[index] = new SpeechVisemeCue
                {
                    Symbol = cue.symbol.Trim(),
                    StartMs = cue.start_ms,
                    EndMs = cue.end_ms,
                    Weight = Mathf.Clamp01(cue.weight <= 0f ? 1f : cue.weight)
                };
            }
            timeline = mapped;
            return true;
        }

        private static bool IsVisemeSymbol(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 16) return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!char.IsLetterOrDigit(character) && character != '_' && character != '-') return false;
            }
            return true;
        }

        private static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64 || !char.IsLetterOrDigit(value[0]))
            {
                return false;
            }
            for (var index = 1; index < value.Length; index++)
            {
                var character = value[index];
                if (!char.IsLetterOrDigit(character) && character != '.' && character != '_' &&
                    character != ':' && character != '-')
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsScopeValue(string value)
        {
            return !string.IsNullOrWhiteSpace(value) && value.Length <= 128;
        }

        [Serializable]
        private sealed class BridgeSsePayload
        {
            public string type;
            public string protocol_version;
            public string session_id;
            public string turn_id;
            public string in_reply_to_event_id;
            public string action_id;
            public string method;
            public AvatarActionParametersPayload parameters;
            public AvatarActionTransitionPayload transition;
            public string source;
            public string text;
            public string emotion;
            public string gesture;
            public string look_at;
            public float intensity;
            public int duration_ms;
            public string reason_code;
            public string format;
            public int sample_rate;
            public int channels;
            public string data;
            public string speech_id;
            public int sequence;
            public bool first;
            public bool end;
            public int audio_sequence_end;
            public string code;
            public string message;
            public bool text_sent;
            public bool audio_sent;
            public ServerTimingPayload server_timing;
            public VisemeCuePayload[] visemes;
            public string[] suggestions;
        }

        [Serializable]
        private sealed class VisemeCuePayload
        {
            public string symbol;
            public int start_ms;
            public int end_ms;
            public float weight = 1f;
        }

        [Serializable]
        private sealed class ServerTimingPayload
        {
            public string contract;
            public int schema_version;
            public int stt_ms;
            public int decision_ms;
            public string decision_path;
            public int tts_first_chunk_ms;
            public int tts_total_ms;
            public int decision_hooks_ms;
            public int decision_provider_ms;
            public int event_loop_lag_ms;
            public string trace_id;
            public int turn_total_ms;
        }
    }

    [Serializable]
    internal sealed class SessionStartRequest
    {
        public string type = "session.start";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
        public string client_id;
        public string user_id;
        public string bot_id;
        public string group_id;
        public string relationship_profile_id;
        public string[] supported_actions;
    }

    [Serializable]
    public sealed class AvatarActionParametersPayload
    {
        public float angle_degrees;
        public float depth;
        public int hold_ms;
        public string style;
    }

    [Serializable]
    public sealed class AvatarActionTransitionPayload
    {
        public int enter_ms;
        public int exit_ms;
        public string easing;
    }

    [Serializable]
    internal sealed class TurnStartRequest
    {
        public string type = "turn.start";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
        public string turn_id;
        public string text;
        public bool cancel_previous = true;
    }

    /// <summary>
    /// 摄像头单帧附件（turn/start 可选 image 字段）。独立于 TurnStartRequest
    /// 序列化：JsonUtility 无法表达"字段不存在"，而旧版 Bridge 的 StrictModel
    /// 会拒绝未知字段——因此 image 轮次经由 SerializeTurnStart 手工注入，
    /// 纯文本轮次的请求体与旧版完全一致。
    /// </summary>
    [Serializable]
    public sealed class TurnImageAttachment
    {
        public const string MimeJpeg = "image/jpeg";
        public string data_base64;
        public string purpose;
    }

    internal static class TurnStartJson
    {
        /// <summary>序列化 turn/start；attachment 为 null 时不注入 image 字段。</summary>
        public static string Serialize(TurnStartRequest request, TurnImageAttachment attachment)
        {
            var json = JsonUtility.ToJson(request);
            if (attachment == null || string.IsNullOrEmpty(attachment.data_base64))
            {
                return json;
            }
            var imageJson = "{\"mime\":\"" + TurnImageAttachment.MimeJpeg
                + "\",\"data_base64\":\"" + attachment.data_base64
                + "\",\"purpose\":\"" + EscapeJson(attachment.purpose ?? string.Empty) + "\"}";
            var closing = json.LastIndexOf('}');
            return closing >= 0
                ? json.Substring(0, closing) + ",\"image\":" + imageJson + "}"
                : json;
        }

        private static string EscapeJson(string value)
        {
            var builder = new System.Text.StringBuilder(value.Length + 8);
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < ' ')
                        {
                            builder.Append("\\u").Append(((int)character).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            return builder.ToString();
        }
    }

    [Serializable]
    internal sealed class AudioChunkRequest
    {
        public string type = "audio.chunk";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
        public string turn_id;
        public int sequence;
        public string format = "pcm16";
        public int sample_rate = 16000;
        public int channels = 1;
        public string data;
        // Optional protocol-1.1 streaming metadata.
        public int byte_offset;
        public int capture_elapsed_ms;
    }

    [Serializable]
    internal sealed class AudioEndRequest
    {
        public string type = "audio.end";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
        public string turn_id;
        // Optional protocol-1.1 end-of-audio completeness metadata.
        public int last_sequence;
        public int total_bytes;
    }

    [Serializable]
    internal sealed class PlaybackReceiptRequest
    {
        public string type = "playback.receipt";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
        public string turn_id;
        public string speech_id;
        public string event_name;
        public int played_ms;
        public int buffered_ms;
        public int underflow_count;
        public string reason_code;
    }

    [Serializable]
    internal sealed class InteractionRequest
    {
        public string type = "interaction";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
        public string event_id;
        public string name;
        public string phase;
        public float strength;
        public int duration_ms;
        public string hand;
    }

    [Serializable]
    public sealed class ActionResultRequest
    {
        public string type = "action.result";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
        public string turn_id;
        public string action_id;
        public string receipt_id;
        public string action;
        public string status;
        public string reason_code;
        public int duration_ms;
    }

    [Serializable]
    internal sealed class InterruptRequest
    {
        public string type = "interrupt";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
        public string turn_id;
        public string reason;
    }

    [Serializable]
    internal sealed class SessionCloseRequest
    {
        public string type = "session.close";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
    }

    /// <summary>
    /// Coarse, privacy-bounded room facts. This wire object intentionally has
    /// no free text, image, mesh, pose, dimensions, anchor IDs, or device IDs.
    /// </summary>
    [Serializable]
    public sealed class SpatialContextRequest
    {
        public string session_id;
        public int schema_version = 1;
        public long revision;
        public int floor_count;
        public int seat_count;
        public int bed_count;
        public int table_count;
        public int wall_count;
        public int door_count;
        public int window_count;
        public bool scene_capture_available;
        public bool occlusion_available;

        public string ContentSignature()
        {
            return string.Join(
                ":",
                schema_version,
                floor_count,
                seat_count,
                bed_count,
                table_count,
                wall_count,
                door_count,
                window_count,
                scene_capture_available ? 1 : 0,
                occlusion_available ? 1 : 0);
        }
    }
}

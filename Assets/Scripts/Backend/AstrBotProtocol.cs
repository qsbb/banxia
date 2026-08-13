using System;
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
    }

    public readonly struct SseEventFrame
    {
        public SseEventFrame(string eventName, string data, long receivedAtTicks = 0L)
        {
            EventName = eventName ?? string.Empty;
            Data = data ?? string.Empty;
            ReceivedAtTicks = receivedAtTicks;
        }

        public string EventName { get; }
        public string Data { get; }
        /// <summary>Monotonic timestamp captured when the SSE frame is assembled.</summary>
        public long ReceivedAtTicks { get; }
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
        private string eventName = string.Empty;

        public event Action<SseEventFrame> EventReceived;

        public void Push(byte[] bytes, int count)
        {
            if (bytes == null || count <= 0)
            {
                return;
            }

            count = Math.Min(count, bytes.Length);
            var chars = new char[Encoding.UTF8.GetMaxCharCount(count)];
            var charCount = decoder.GetChars(bytes, 0, count, chars, 0, false);
            for (var index = 0; index < charCount; index++)
            {
                var value = chars[index];
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
            var current = line.ToString();
            line.Clear();
            if (current.Length == 0)
            {
                Dispatch();
                return;
            }
            if (current[0] == ':')
            {
                return;
            }

            var separator = current.IndexOf(':');
            var field = separator < 0 ? current : current.Substring(0, separator);
            var value = separator < 0 ? string.Empty : current.Substring(separator + 1);
            if (value.StartsWith(" ", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }

            if (field == "event")
            {
                eventName = value;
            }
            else if (field == "data")
            {
                if (data.Length > 0)
                {
                    data.Append('\n');
                }
                data.Append(value);
            }
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
                    if (!TryDecodeAudio(payload, out var samples, out error))
                    {
                        return false;
                    }
                    message = Basic(payload, ConversationEventType.AudioChunk);
                    message.Pcm16 = samples;
                    message.SampleRate = payload.sample_rate;
                    break;
                case "avatar.intent":
                    message = Basic(payload, ConversationEventType.AvatarIntent);
                    message.InReplyToEventId = payload.in_reply_to_event_id;
                    message.Emotion = SanitizeEmotion(payload.emotion);
                    message.Gesture = SanitizeGesture(payload.gesture);
                    message.LookAt = SanitizeLookAt(payload.look_at);
                    message.Intensity = Mathf.Clamp01(payload.intensity);
                    message.DurationMs = Mathf.Clamp(payload.duration_ms, 0, 30000);
                    message.ReasonCode = payload.reason_code ?? string.Empty;
                    break;
                case "reply.end":
                    message = Basic(payload, ConversationEventType.ReplyEnd);
                    message.TextSent = payload.text_sent;
                    message.AudioSent = payload.audio_sent;
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
                case "turn_half":
                case "sit":
                case "lie":
                case "lie_down":
                    return value;
                default:
                    return "idle";
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
                TurnTotalMs = ClampServerDuration(payload.turn_total_ms),
                DecisionPath = payload.decision_path == "astrbot_event_bus" ||
                    payload.decision_path == "direct_provider"
                    ? payload.decision_path
                    : "unknown"
            };
        }

        private static int ClampServerDuration(int value)
        {
            return value <= 0 ? -1 : Mathf.Clamp(value, 1, 3600000);
        }

        private static bool TryDecodeAudio(BridgeSsePayload payload, out short[] samples, out string error)
        {
            samples = null;
            error = string.Empty;
            if (payload.format != "pcm16" || payload.sample_rate != 24000 || payload.channels != 1)
            {
                error = "Unsupported reply audio format";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(payload.data ?? string.Empty);
            }
            catch (FormatException)
            {
                error = "Reply audio is not valid Base64";
                return false;
            }
            if (bytes.Length == 0 || (bytes.Length & 1) != 0)
            {
                error = "Reply audio must contain an even number of PCM16 bytes";
                return false;
            }

            samples = new short[bytes.Length / 2];
            for (var index = 0; index < samples.Length; index++)
            {
                var offset = index * 2;
                samples[index] = unchecked((short)(bytes[offset] | (bytes[offset + 1] << 8)));
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
            public string code;
            public string message;
            public bool text_sent;
            public bool audio_sent;
            public ServerTimingPayload server_timing;
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
    }

    [Serializable]
    internal sealed class AudioEndRequest
    {
        public string type = "audio.end";
        public string protocol_version = AstrBotProtocol.Version;
        public string session_id;
        public string turn_id;
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

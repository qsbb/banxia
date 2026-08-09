using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace QuestMmdPlayer
{
    [DisallowMultipleComponent]
    public sealed class RuntimeDebugLog : MonoBehaviour
    {
        [SerializeField, Range(24, 160)] private int capacity = 96;

        private readonly Queue<string> entries = new Queue<string>();
        private readonly Queue<StageEntry> stageEntries = new Queue<StageEntry>();
        private string rootCauseStage = string.Empty;
        private string rootCauseCode = string.Empty;
        private static readonly string[] AllowedPrefixes =
        {
            "[QuestMmdPlayer]",
            "[Conversation]",
            "[VoiceInput]",
            "[AstrBotBridge]",
            "[BackendPairing]",
            "[HumanInteraction]",
            "[TouchInteraction]",
            "[AvatarTouch]",
            "[HandTracking]",
            "[IdlePose]",
            "[PcmStream]",
            "[AvatarPlacement]",
            "[VmdActionLibrary]",
            "[FileImport]",
            "[Passthrough]",
            "[RuntimeDebug]"
        };

        public bool DisplayEnabled { get; private set; }
        public int Count => entries.Count;
        public string CurrentRootCause => string.IsNullOrEmpty(rootCauseCode)
            ? "未发现明确的失败阶段"
            : StageLabel(rootCauseStage) + "：" + CodeLabel(rootCauseCode);

        private void OnEnable()
        {
            Application.logMessageReceived += HandleLog;
            Record("RuntimeDebug", "前端诊断已就绪");
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= HandleLog;
        }

        public void SetDisplayEnabled(bool enabled)
        {
            DisplayEnabled = enabled;
            Record("RuntimeDebug", enabled ? "菜单日志已开启" : "菜单日志已关闭");
        }

        public void ToggleDisplay()
        {
            SetDisplayEnabled(!DisplayEnabled);
        }

        public void Clear()
        {
            entries.Clear();
            stageEntries.Clear();
            rootCauseStage = string.Empty;
            rootCauseCode = string.Empty;
            Record("RuntimeDebug", "诊断记录已清空");
        }

        public void Record(string category, string message)
        {
            var safeCategory = string.IsNullOrWhiteSpace(category) ? "App" : category.Trim();
            Add($"{Time.unscaledTime,6:F1}s [{safeCategory}] {Sanitize(message)}");
        }

        public string GetRecentText(int maximumLines = 5)
        {
            var snapshot = entries.ToArray();
            var first = Mathf.Max(0, snapshot.Length - Mathf.Max(1, maximumLines));
            var builder = new StringBuilder();
            for (var index = first; index < snapshot.Length; index++)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(snapshot[index]);
            }
            return builder.ToString();
        }

        public void RecordStage(
            string stage,
            string status,
            string code = "",
            long httpStatus = 0,
            int elapsedMs = -1,
            int chunks = 0,
            int bytes = 0,
            int eventCount = 0,
            int sampleRate = 0,
            int channels = 0)
        {
            var safeStage = SafeToken(stage, "runtime");
            var safeStatus = SafeToken(status, "unknown");
            var safeCode = SafeToken(code, string.Empty);
            var entry = new StageEntry
            {
                Time = Time.unscaledTime,
                Stage = safeStage,
                Status = safeStatus,
                Code = safeCode,
                HttpStatus = Mathf.Clamp((int)httpStatus, 0, 999),
                ElapsedMs = Mathf.Clamp(elapsedMs, -1, 3600000),
                Chunks = Mathf.Clamp(chunks, 0, 1000000),
                Bytes = Mathf.Clamp(bytes, 0, 1000000000),
                EventCount = Mathf.Clamp(eventCount, 0, 1000000),
                SampleRate = Mathf.Clamp(sampleRate, 0, 384000),
                Channels = Mathf.Clamp(channels, 0, 32)
            };
            stageEntries.Enqueue(entry);
            while (stageEntries.Count > Mathf.Max(12, capacity))
            {
                stageEntries.Dequeue();
            }

            if (IsFailureStatus(safeStatus))
            {
                rootCauseStage = safeStage;
                rootCauseCode = string.IsNullOrEmpty(safeCode) ? safeStatus : safeCode;
            }
            else if (IsSuccessStatus(safeStatus) && rootCauseStage == safeStage)
            {
                rootCauseStage = string.Empty;
                rootCauseCode = string.Empty;
            }

            Debug.Log(
                $"[RuntimeDebug] stage={safeStage} status={safeStatus}" +
                (string.IsNullOrEmpty(safeCode) ? string.Empty : " code=" + safeCode) +
                (entry.HttpStatus > 0 ? " http=" + entry.HttpStatus : string.Empty) +
                (entry.ElapsedMs >= 0 ? " elapsed_ms=" + entry.ElapsedMs : string.Empty) +
                (entry.Chunks > 0 ? " chunks=" + entry.Chunks : string.Empty) +
                (entry.Bytes > 0 ? " bytes=" + entry.Bytes : string.Empty) +
                (entry.EventCount > 0 ? " events=" + entry.EventCount : string.Empty) +
                (entry.SampleRate > 0 ? " sample_rate=" + entry.SampleRate : string.Empty) +
                (entry.Channels > 0 ? " channels=" + entry.Channels : string.Empty),
                this);
        }

        public string GetRecentTimelineText(int maximumLines = 10)
        {
            var snapshot = stageEntries.ToArray();
            var first = Mathf.Max(0, snapshot.Length - Mathf.Max(1, maximumLines));
            var builder = new StringBuilder();
            for (var index = first; index < snapshot.Length; index++)
            {
                if (builder.Length > 0) builder.Append('\n');
                builder.Append(FormatStageEntry(snapshot[index]));
            }
            return builder.ToString();
        }

        private void HandleLog(string condition, string stackTrace, LogType type)
        {
            if (!ShouldCapture(condition, type))
            {
                return;
            }

            Add($"{Time.unscaledTime,6:F1}s {TypeLabel(type)} {Sanitize(condition)}");
        }

        private void Add(string entry)
        {
            entries.Enqueue(entry);
            while (entries.Count > Mathf.Max(12, capacity))
            {
                entries.Dequeue();
            }
        }

        private static bool ShouldCapture(string condition, LogType type)
        {
            if (string.IsNullOrWhiteSpace(condition))
            {
                return false;
            }
            for (var index = 0; index < AllowedPrefixes.Length; index++)
            {
                if (condition.StartsWith(AllowedPrefixes[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return type == LogType.Error || type == LogType.Exception;
        }

        private static string Sanitize(string value)
        {
            var result = string.IsNullOrWhiteSpace(value)
                ? "(empty)"
                : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            var lower = result.ToLowerInvariant();
            if (lower.Contains("authorization") || lower.Contains("api_key") ||
                lower.Contains("apikey") || lower.Contains("bridge_key") ||
                lower.Contains("bearer ") || lower.Contains("secret") ||
                lower.Contains("token="))
            {
                return "[敏感详情已隐藏]";
            }
            return result.Length <= 180 ? result : result.Substring(0, 177) + "...";
        }

        private static string FormatStageEntry(StageEntry entry)
        {
            var builder = new StringBuilder();
            builder.AppendFormat("{0,6:F1}s [{1}] {2}", entry.Time, StageLabel(entry.Stage), StatusLabel(entry.Status));
            if (!string.IsNullOrEmpty(entry.Code)) builder.Append(" · ").Append(CodeLabel(entry.Code));
            if (entry.HttpStatus > 0) builder.Append(" · HTTP ").Append(entry.HttpStatus);
            if (entry.ElapsedMs >= 0) builder.Append(" · ").Append(entry.ElapsedMs).Append("ms");
            if (entry.Chunks > 0) builder.Append(" · ").Append(entry.Chunks).Append("块");
            if (entry.Bytes > 0) builder.Append("/").Append(entry.Bytes).Append("B");
            if (entry.EventCount > 0) builder.Append(" · ").Append(entry.EventCount).Append("事件");
            if (entry.SampleRate > 0) builder.Append(" · ").Append(entry.SampleRate).Append("Hz");
            if (entry.Channels > 0) builder.Append("/").Append(entry.Channels).Append("声道");
            return builder.ToString();
        }

        private static string SafeToken(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            var candidate = value.Trim();
            if (candidate.Length > 64) return fallback;
            for (var index = 0; index < candidate.Length; index++)
            {
                var character = candidate[index];
                if (!char.IsLetterOrDigit(character) && character != '_' && character != '-' && character != '.')
                {
                    return fallback;
                }
            }
            return candidate;
        }

        private static bool IsFailureStatus(string value)
        {
            return value == "error" || value == "failed" || value == "blocked" ||
                value == "limited" || value == "unavailable" || value == "timeout";
        }

        private static bool IsSuccessStatus(string value)
        {
            return value == "ok" || value == "ready" || value == "connected" ||
                value == "authorized" || value == "completed";
        }

        public static string StageLabel(string value)
        {
            switch (value)
            {
                case "configuration": return "配置";
                case "health": return "健康检查";
                case "authorization": return "身份授权";
                case "session": return "会话";
                case "sse": return "实时事件";
                case "microphone": return "麦克风";
                case "audio_upload": return "音频上传";
                case "stt": return "语音识别";
                case "eventbus": return "AstrBot/EventBus";
                case "llm": return "模型生成";
                case "tts": return "语音合成";
                case "audio_playback": return "音频播放";
                case "reply": return "回复结束";
                case "interrupt": return "打断";
                default: return value;
            }
        }

        public static string StatusLabel(string value)
        {
            switch (value)
            {
                case "ok": return "正常";
                case "ready": return "就绪";
                case "connected": return "已连接";
                case "authorized": return "已授权";
                case "completed": return "完成";
                case "processing": return "处理中";
                case "uploading": return "上传中";
                case "limited": return "受限";
                case "blocked": return "已阻止";
                case "failed": return "失败";
                case "error": return "错误";
                case "timeout": return "超时";
                case "disconnected": return "已断开";
                case "cancelled": return "已取消";
                default: return value;
            }
        }

        public static string CodeLabel(string value)
        {
            switch (value)
            {
                case "owner_not_configured": return "“序”尚未为这组 Quest 原始身份配置主人";
                case "quest_identity_not_allowlisted": return "Quest 原始身份不在“序”的允许列表";
                case "local_identity_not_configured": return "“临”的本地 Quest 身份尚未配置完整";
                case "local_api_principal_mismatch": return "Quest 使用的 AstrBot API Key 与本地绑定不一致";
                case "local_quest_identity_mismatch": return "Quest 客户端、平台、Bot 或主人用户与本地绑定不一致";
                case "invalid_user_id": return "用户 ID 无效或仍是占位值";
                case "missing_user_id": return "用户 ID 缺失";
                case "invalid_bot_id": return "Bot ID 无效";
                case "missing_bot_id": return "Bot ID 缺失";
                case "client_id_mismatch": return "客户端 ID 与服务端不一致";
                case "trusted_platform_not_configured": return "尚未配置 AstrBot 消息平台";
                case "trusted_platform_unavailable": return "已配置的 AstrBot 平台不可用";
                case "astrbot_message_pipeline_unavailable": return "AstrBot 消息链路不可用";
                case "astrbot_pipeline_timeout": return "AstrBot 消息链处理超时";
                case "stt_empty": return "没有识别到有效语音";
                case "stt_unavailable": return "语音识别服务未配置";
                case "stt_failed": return "语音识别失败";
                case "tts_failed": return "语音合成失败";
                case "llm_failed": return "模型生成失败";
                case "audio_http_request_failed": return "音频上传请求失败";
                case "audio_upload_backpressure": return "音频上传速度跟不上录音";
                case "bridge_disconnected": return "Bridge 会话未连接";
                case "http_request_failed": return "后端请求失败";
                case "health_incompatible": return "后端协议或传输类型不兼容";
                case "health_failed": return "后端健康检查失败";
                case "session_start_failed": return "后端会话创建失败";
                case "session_capacity_full": return "后端会话容量已满";
                case "session_expired": return "后端会话已过期";
                case "sse_disconnected": return "实时事件连接已断开";
                case "microphone_permission_missing": return "没有麦克风权限";
                case "microphone_device_missing": return "未发现麦克风设备";
                case "microphone_start_failed": return "麦克风启动失败";
                case "voice_turn_rejected": return "后端拒绝开始语音轮次";
                case "voice_end_rejected": return "后端拒绝结束语音轮次";
                case "no_speech_detected": return "没有检测到说话";
                case "response_first_event_timeout": return "后端接收后没有返回首个事件";
                case "response_event_stall_timeout": return "后端事件流在回复结束前停滞";
                case "empty_reply": return "后端结束了空回复";
                case "empty_backend_reply": return "后端结束了空回复";
                case "configuration_missing": return "尚未绑定后端";
                case "configuration_invalid": return "后端绑定配置无效";
                default: return value;
            }
        }

        private static string TypeLabel(LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                    return "[ERR]";
                case LogType.Warning:
                    return "[WARN]";
                default:
                    return "[LOG]";
            }
        }

        private sealed class StageEntry
        {
            public float Time;
            public string Stage;
            public string Status;
            public string Code;
            public int HttpStatus;
            public int ElapsedMs;
            public int Chunks;
            public int Bytes;
            public int EventCount;
            public int SampleRate;
            public int Channels;
        }
    }
}

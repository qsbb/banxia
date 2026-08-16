using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace QuestMmdPlayer
{
    [DisallowMultipleComponent]
    public sealed class RuntimeDebugLog : MonoBehaviour
    {
        [SerializeField, Range(24, 240)] private int capacity = 160;

        private readonly Queue<string> entries = new Queue<string>();
        private readonly Queue<StageEntry> stageEntries = new Queue<StageEntry>();
        private static readonly byte[] TraceKey = Guid.NewGuid().ToByteArray();
        private string rootCauseStage = string.Empty;
        private string rootCauseCode = string.Empty;
        private static readonly string[] AllowedPrefixes =
        {
            "[Banxia]",
            "[Conversation]",
            "[VoiceInput]",
            "[AstrBotBridge]",
            "[BackendPairing]",
            "[HumanInteraction]",
            "[AvatarAction]",
            "[ConversationPresenter]",
            "[CompanionMenu]",
            "[TouchInteraction]",
            "[AvatarTouch]",
            "[HandTracking]",
            "[MmdPhysicsAdapter]",
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
            int channels = 0,
            string traceId = "",
            int queueDepth = -1,
            int bufferedMs = -1)
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
                Channels = Mathf.Clamp(channels, 0, 32),
                TraceId = SafeToken(traceId, string.Empty),
                QueueDepth = Mathf.Clamp(queueDepth, -1, 1000000),
                BufferedMs = Mathf.Clamp(bufferedMs, -1, 3600000)
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
                (string.IsNullOrEmpty(entry.TraceId) ? string.Empty : " trace=" + entry.TraceId) +
                (string.IsNullOrEmpty(safeCode) ? string.Empty : " code=" + safeCode) +
                (entry.HttpStatus > 0 ? " http=" + entry.HttpStatus : string.Empty) +
                (entry.ElapsedMs >= 0 ? " elapsed_ms=" + entry.ElapsedMs : string.Empty) +
                (entry.Chunks > 0 ? " chunks=" + entry.Chunks : string.Empty) +
                (entry.Bytes > 0 ? " bytes=" + entry.Bytes : string.Empty) +
                (entry.EventCount > 0 ? " events=" + entry.EventCount : string.Empty) +
                (entry.SampleRate > 0 ? " sample_rate=" + entry.SampleRate : string.Empty) +
                (entry.Channels > 0 ? " channels=" + entry.Channels : string.Empty) +
                (entry.QueueDepth >= 0 ? " queue_depth=" + entry.QueueDepth : string.Empty) +
                (entry.BufferedMs >= 0 ? " buffered_ms=" + entry.BufferedMs : string.Empty),
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
            if (!string.IsNullOrEmpty(entry.TraceId)) builder.Append(" · #").Append(entry.TraceId);
            if (!string.IsNullOrEmpty(entry.Code)) builder.Append(" · ").Append(CodeLabel(entry.Code));
            if (entry.HttpStatus > 0) builder.Append(" · HTTP ").Append(entry.HttpStatus);
            if (entry.ElapsedMs >= 0) builder.Append(" · ").Append(entry.ElapsedMs).Append("ms");
            if (entry.Chunks > 0) builder.Append(" · ").Append(entry.Chunks).Append("块");
            if (entry.Bytes > 0) builder.Append("/").Append(entry.Bytes).Append("B");
            if (entry.EventCount > 0) builder.Append(" · ").Append(entry.EventCount).Append("事件");
            if (entry.SampleRate > 0) builder.Append(" · ").Append(entry.SampleRate).Append("Hz");
            if (entry.Channels > 0) builder.Append("/").Append(entry.Channels).Append("声道");
            if (entry.QueueDepth >= 0) builder.Append(" · 队列").Append(entry.QueueDepth);
            if (entry.BufferedMs >= 0) builder.Append(" · 缓冲").Append(entry.BufferedMs).Append("ms");
            return builder.ToString();
        }

        /// <summary>Creates a process-local keyed label without exposing the raw turn id.</summary>
        public static string TraceLabel(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            using (var hmac = new HMACSHA256(TraceKey))
            {
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(value));
                return "t" + BitConverter.ToString(hash, 0, 4).Replace("-", string.Empty).ToLowerInvariant();
            }
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
                case "sse_dispatch": return "事件分发";
                case "microphone": return "麦克风";
                case "audio_encode": return "音频编码";
                case "audio_upload": return "音频上传";
                case "stt": return "语音识别";
                case "backend_stt": return "后端语音识别";
                case "backend_decision": return "后端决策链";
                case "backend_tts": return "后端语音合成";
                case "backend_total": return "后端总耗时";
                case "eventbus": return "AstrBot/EventBus";
                case "llm": return "模型生成";
                case "tts": return "语音合成";
                case "audio_playback": return "音频播放";
                case "audio_buffer": return "播放缓冲";
                case "reply": return "回复结束";
                case "avatar_action": return "角色动作";
                case "hand_contact": return "手部触碰";
                case "spatial_context": return "房间语义";
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
                case "action_state_changed": return "\u52a8\u4f5c\u72b6\u6001\u5df2\u5207\u6362";
                case "rest_target_found": return "\u5df2\u627e\u5230\u5339\u914d\u7684\u573a\u666f\u4f11\u606f\u76ee\u6807";
                case "rest_target_missing": return "\u6ca1\u6709\u627e\u5230\u53ef\u7528\u7684\u5ea7\u4f4d\u6216\u8eba\u5367\u9762";
                case "rest_target_capability_missing": return "\u6700\u8fd1\u7684\u573a\u666f\u76ee\u6807\u4e0d\u652f\u6301\u8be5\u52a8\u4f5c";
                case "rest_target_unavailable": return "\u573a\u666f\u4f11\u606f\u76ee\u6807\u4e0d\u53ef\u7528";
                case "rest_target_busy": return "\u89d2\u8272\u6b63\u5728\u5bf9\u9f50\u5176\u4ed6\u573a\u666f\u76ee\u6807";
                case "rest_alignment_started": return "\u5f00\u59cb\u5e73\u6ed1\u5bf9\u9f50\u573a\u666f\u76ee\u6807";
                case "rest_alignment_completed": return "\u573a\u666f\u76ee\u6807\u5bf9\u9f50\u5b8c\u6210";
                case "rest_return_started": return "\u5f00\u59cb\u5e73\u6ed1\u8fd4\u56de\u7ad9\u7acb\u59ff\u6001";
                case "rest_return_completed": return "\u5df2\u8fd4\u56de\u7ad9\u7acb\u59ff\u6001";
            }
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
                case "asr_final": return "录音结束到识别完成";
                case "asr_to_first_text": return "识别完成到首段回复";
                case "first_event": return "录音结束到首个事件";
                case "first_text": return "录音结束到首段回复";
                case "first_audio": return "录音结束到首段语音";
                case "text_to_first_audio": return "首段文字到首段语音";
                case "reply_end": return "录音结束到回复结束";
                case "sse_queue": return "SSE 到主线程排队";
                case "playback_start": return "首段语音到实际开播";
                case "playback_callback": return "播放请求到音频回调";
                case "server_timing": return "服务端耗时摘要";
                case "astrbot_event_bus": return "AstrBot/EventBus";
                case "direct_provider": return "直接模型";
                case "unknown": return "未知路径";
                case "main_thread_queue": return "主线程排队";
                case "pcm_chunk": return "PCM 分块";
                case "first_pcm_chunk": return "首个 PCM 分块";
                case "pcm_max_chunk": return "最慢 PCM 编码块";
                case "turn_start_http": return "turn/start 请求";
                case "audio_chunk_http": return "audio/chunk 请求";
                case "audio_end_http": return "audio/end 请求";
                case "sse_queue_summary": return "SSE 排队汇总";
                case "empty_reply": return "后端结束了空回复";
                case "empty_backend_reply": return "后端结束了空回复";
                case "configuration_missing": return "尚未绑定后端";
                case "configuration_invalid": return "后端绑定配置无效";
                case "vmd_request": return "开始准备导入动作";
                case "vmd_cache_hit": return "命中动作内存缓存";
                case "vmd_read_completed": return "动作文件读取完成";
                case "vmd_motion_converted": return "身体动作转换完成";
                case "vmd_facial_converted": return "脸部动作转换完成";
                case "vmd_bindings_ready": return "动作骨骼与表情绑定完成";
                case "vmd_playback_cached": return "缓存动作已开始播放";
                case "vmd_playback_prepared": return "动作转换完成并开始播放";
                case "vmd_end_pose_hold": return "动作结束姿势保持中";
                case "vmd_blend_out": return "正在平滑过渡回待机";
                case "vmd_idle_restored": return "已恢复自然待机";
                case "vmd_load_failed": return "导入动作加载失败";
                case "vmd_model_bound": return "动作库已绑定当前模型";
                case "vmd_model_unbound": return "动作库没有可用模型";
                case "backend_intent_accepted": return "后端动作意图已接受";
                case "action_arbitration_blocked": return "动作因当前状态被仲裁阻止";
                case "local_action_fallback": return "后端无动作时执行本地兜底";
                case "custom_dance_started": return "自定义舞蹈已开始播放";
                case "custom_dance_unavailable": return "没有可播放的自定义舞蹈";
                case "model_switch_started": return "开始切换角色模型";
                case "model_switch_completed": return "角色模型切换完成";
                case "model_switch_failed": return "角色模型切换失败";
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
            public string TraceId;
            public int QueueDepth;
            public int BufferedMs;
        }
    }
}

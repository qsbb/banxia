using System;
using System.Text;

namespace QuestMmdPlayer
{
    /// <summary>Compact, bounded diagnostics text for the in-device panel.</summary>
    public static class RuntimeDiagnosticsFormatter
    {
        private const int MaxLineLength = 42;

        public static string BuildPanelText(
            RuntimeDiagnosticsSnapshot snapshot,
            string rootCause,
            string timeline,
            int maximumTimelineLines = 8)
        {
            if (snapshot == null)
            {
                return "诊断不可用";
            }

            var builder = new StringBuilder(2048);
            AppendLine(builder, "状态：" + (string.IsNullOrWhiteSpace(rootCause) ? "未发现明确失败阶段" : rootCause));
            AppendBlock(builder, FormatLink(snapshot));
            AppendBlock(builder, FormatVoice(snapshot));
            AppendBlock(builder, FormatTiming(snapshot.Conversation));
            AppendBlock(builder, FormatInteraction(snapshot.Interaction));
            AppendBlock(builder, FormatRoom(snapshot.Room, snapshot.Placement));
            AppendBlock(builder, FormatMotion(snapshot.Motion, snapshot.ModelLoad));
            AppendLine(builder, "最近阶段：");
            AppendTimeline(builder, timeline, maximumTimelineLines);
            return builder.ToString().TrimEnd();
        }

        public static string FormatLink(RuntimeDiagnosticsSnapshot snapshot)
        {
            var backend = snapshot.Backend;
            var conversation = snapshot.Conversation;
            var audio = snapshot.Audio;
            return "链路：" + BackendName(backend.ChainStatus) +
                " · 连接" + YesNo(backend.Connected) +
                " · 等待" + YesNo(conversation.AwaitingBackendResponse) +
                " · 状态" + ConversationName(conversation.State) +
                " · 音频" + YesNo(audio.PlaybackStarted) +
                " · 欠载" + audio.UnderflowCount;
        }

        public static string FormatVoice(RuntimeDiagnosticsSnapshot snapshot)
        {
            var voice = snapshot.Voice;
            var conversation = snapshot.Conversation;
            return "语音：监听" + YesNo(voice.Monitoring) +
                " · 录音" + YesNo(voice.Recording) +
                " · 常开" + YesNo(voice.AlwaysListening) +
                " · 说话" + YesNo(voice.SpeechDetected) +
                " · 本轮" + voice.LastTurnCaptureSeconds.ToString("F1") + "s" +
                " · 回复" + conversation.ReplyTextCharacters + "字";
        }

        public static string FormatTiming(ConversationDiagnostics conversation)
        {
            if (conversation == null) return "耗时：不可用";
            return "耗时：首段" + Ms(conversation.FirstInputChunkMs) +
                " · 输入结束" + Ms(conversation.InputEndMs) +
                " · 首事件" + Ms(conversation.FirstEventMs) +
                "\n耗时：首文字" + Ms(conversation.FirstTextMs) +
                " · 首音频" + Ms(conversation.FirstAudioMs) +
                " · 回复结束" + Ms(conversation.ReplyEndMs) +
                " · 播放结束" + Ms(conversation.AudioDoneMs);
        }

        public static string FormatInteraction(InteractionDiagnostics interaction)
        {
            if (interaction == null) return "交互：不可用";
            var contact = interaction.ActiveContactCount > 0 ||
                interaction.LastContactRegion != AvatarContactRegion.None;
            return "手追：" + interaction.TrackedHandCount + "只" +
                " · 接触" + interaction.ActiveContactCount +
                " · " + (contact ? ContactPhaseName(interaction.LastContactPhase) : "无接触") +
                " " + RegionName(interaction.LastContactRegion) +
                " · 捏合" + YesNo(interaction.LastContactPinching) +
                "\n接触：穿透" + interaction.LastContactPenetrationDepth.ToString("F3") +
                "m · 碰撞体" + interaction.ModelCollisionVolumeCount +
                " · 语义" + YesNo(interaction.SemanticContact);
        }

        public static string FormatRoom(RoomDiagnostics room, PlacementDiagnostics placement)
        {
            if (room == null || placement == null) return "空间：不可用";
            return "空间：" + (room.HasRoomData ? "已扫描" : "未扫描") +
                " · 面" + room.SurfaceCount +
                " · 地面" + room.FloorCount +
                " · 座位" + room.SeatCount +
                " · 桌面" + room.TableCount +
                " · 墙" + room.WallCount +
                "\n能力：MRUK" + CapabilityName(room.Mruk) +
                " · 平面" + CapabilityName(room.PlaneTracking) +
                " · 遮挡" + CapabilityName(room.Occlusion) +
                " · 放置" + YesNo(placement.HasPlacement);
        }

        public static string FormatMotion(MotionDiagnostics motion, ModelLoadDiagnostics model)
        {
            if (motion == null || model == null) return "动作：不可用";
            var prepare = motion.LastActionPrepareMs < 0 ? "-" : motion.LastActionPrepareMs + "ms";
            var load = model.LastTotalMs < 0 ? "-" : model.LastTotalMs + "ms";
            return "动作：" + PlaybackName(motion.PlaybackPhase) +
                " · 待机" + IdleName(motion.IdlePreset) +
                " · 来源" + SourceName(motion.CurrentActionSource) +
                " · 动作" + motion.InstalledActionCount +
                "/已准备" + motion.PreparedActionCount +
                "\n缓存命中/未命中/淘汰" + motion.ActionCacheHits + "/" +
                motion.ActionCacheMisses + "/" + motion.ActionCacheEvictions +
                " · 准备" + prepare + " · 模型" + LoadPhaseName(model.Phase) + "/" + load;
        }

        public static string BackendName(BackendChainState value)
        {
            switch (value)
            {
                case BackendChainState.EventBusEligible: return "AstrBot可用";
                case BackendChainState.EventBusReady: return "AstrBot就绪";
                case BackendChainState.DirectProviderFallback: return "直连回退";
                case BackendChainState.IdentityNotBound: return "身份未绑定";
                case BackendChainState.PairingIdentityInvalid: return "身份无效";
                case BackendChainState.ClientMismatch: return "客户端不匹配";
                case BackendChainState.PlatformUnavailable: return "平台不可用";
                case BackendChainState.AuthorizationTimeout: return "授权超时";
                case BackendChainState.AuthorizationDenied: return "授权拒绝";
                case BackendChainState.Unavailable: return "不可用";
                default: return "未知";
            }
        }

        public static string ConversationName(ConversationState value)
        {
            switch (value)
            {
                case ConversationState.Listening: return "聆听";
                case ConversationState.Thinking: return "思考";
                case ConversationState.Speaking: return "回复";
                case ConversationState.Interrupted: return "已打断";
                case ConversationState.Error: return "错误";
                default: return "空闲";
            }
        }

        public static string ContactPhaseName(TrackedHandContactPhase value)
        {
            switch (value)
            {
                case TrackedHandContactPhase.Began: return "开始";
                case TrackedHandContactPhase.Updated: return "持续";
                case TrackedHandContactPhase.Ended: return "结束";
                default: return "未知";
            }
        }

        public static string RegionName(AvatarContactRegion value)
        {
            switch (value)
            {
                case AvatarContactRegion.Body: return "身体";
                case AvatarContactRegion.Head: return "头部";
                case AvatarContactRegion.Face: return "脸部";
                case AvatarContactRegion.Hand: return "手部";
                case AvatarContactRegion.Hair: return "头发";
                case AvatarContactRegion.Limb: return "四肢";
                default: return "无";
            }
        }

        public static string CapabilityName(SpatialCapabilityState value)
        {
            switch (value)
            {
                case SpatialCapabilityState.Available: return "可用";
                case SpatialCapabilityState.Fallback: return "回退";
                default: return "不可用";
            }
        }

        public static string PlaybackName(VmdPlaybackPhase value)
        {
            switch (value)
            {
                case VmdPlaybackPhase.Loading: return "加载";
                case VmdPlaybackPhase.Playing: return "播放";
                case VmdPlaybackPhase.HoldingEndPose: return "保持结束姿势";
                case VmdPlaybackPhase.BlendingOut: return "过渡回待机";
                case VmdPlaybackPhase.Failed: return "失败";
                default: return "空闲";
            }
        }

        public static string LoadPhaseName(RuntimeModelLoadPhase value)
        {
            switch (value)
            {
                case RuntimeModelLoadPhase.Reading: return "读取";
                case RuntimeModelLoadPhase.Building: return "构建";
                case RuntimeModelLoadPhase.Ready: return "就绪";
                case RuntimeModelLoadPhase.Cancelled: return "取消";
                case RuntimeModelLoadPhase.Failed: return "失败";
                default: return "空闲";
            }
        }

        public static string IdleName(AvatarIdlePreset value)
        {
            switch (value)
            {
                case AvatarIdlePreset.Casual: return "自然";
                case AvatarIdlePreset.Formal: return "端正";
                default: return "放松";
            }
        }

        public static string SourceName(AvatarActionSource value)
        {
            switch (value)
            {
                case AvatarActionSource.Imported: return "导入";
                case AvatarActionSource.Touch: return "触碰";
                case AvatarActionSource.Backend: return "后端";
                case AvatarActionSource.Manual: return "手动";
                case AvatarActionSource.System: return "系统";
                case AvatarActionSource.Idle: return "待机";
                default: return "未知";
            }
        }

        public static string YesNo(bool value) => value ? "是" : "否";

        private static void AppendLine(StringBuilder builder, string value)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(TrimLine(value));
        }

        private static void AppendBlock(StringBuilder builder, string value)
        {
            var lines = (value ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < lines.Length; index++) AppendLine(builder, lines[index]);
        }

        private static void AppendTimeline(StringBuilder builder, string timeline, int maximumLines)
        {
            if (string.IsNullOrWhiteSpace(timeline))
            {
                AppendLine(builder, "暂无记录");
                return;
            }
            var lines = timeline.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var first = Math.Max(0, lines.Length - Math.Max(1, maximumLines));
            for (var index = first; index < lines.Length; index++) AppendLine(builder, "- " + lines[index]);
        }

        private static string TrimLine(string value, int maximum = MaxLineLength)
        {
            value = value ?? string.Empty;
            maximum = Math.Max(4, maximum);
            return value.Length <= maximum ? value : value.Substring(0, maximum - 3) + "...";
        }

        private static string Ms(int value) => value < 0 ? "-" : value + "ms";
    }
}

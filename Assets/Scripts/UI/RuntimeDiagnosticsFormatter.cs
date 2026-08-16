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
            AppendBlock(builder, FormatPerformanceSummary(snapshot.Performance));
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
            var diskRead = motion.LastDiskCacheReadMs < 0 ? "-" : motion.LastDiskCacheReadMs + "ms";
            var diskRebuild = motion.LastDiskCacheRebuildMs < 0 ? "-" : motion.LastDiskCacheRebuildMs + "ms";
            return "动作：" + PlaybackName(motion.PlaybackPhase) +
                " · 待机" + IdleName(motion.IdlePreset) +
                " · 来源" + SourceName(motion.CurrentActionSource) +
                " · 动作" + motion.InstalledActionCount +
                "/已准备" + motion.PreparedActionCount +
                "\n缓存命中/未命中/淘汰" + motion.ActionCacheHits + "/" +
                motion.ActionCacheMisses + "/" + motion.ActionCacheEvictions +
                " · 准备" + prepare + " · 模型" + LoadPhaseName(model.Phase) + "/" + load +
                "\n磁盘缓存" + (motion.LastActionUsedDiskCache ? "命中" : "未命中") +
                " · 命中/未命中/无效" + motion.DiskActionCacheHits + "/" +
                motion.DiskActionCacheMisses + "/" + motion.DiskActionCacheInvalid +
                " · 读取" + diskRead + " · 重建" + diskRebuild +
                "\n口型：" + (motion.MouthPresenterAvailable ? "可用" : "不可用") +
                " · 变形" + motion.MatchedVisemeCount +
                " · RMS" + motion.AudibleRms.ToString("F4") +
                " · 平滑" + motion.SmoothedMouthAmount.ToString("F2") +
                " · 可见" + motion.VisibleMouthAmount.ToString("F2") +
                " · 时间线" + (motion.SpeechTimelineActive ? "开" : "关") +
                " " + motion.TimelinePositionMs.ToString("F0") + "ms/" +
                motion.TimelinePeak.ToString("F2");
        }

        public static string FormatPerformanceSummary(PerformanceDiagnostics performance)
        {
            if (performance == null || !performance.Available)
            {
                return "性能：不可用";
            }
            var fps = performance.FrameSampleCount <= 0
                ? "-"
                : performance.CurrentFps.ToString("F1");
            var target = performance.TargetFpsAvailable
                ? performance.TargetFps.ToString("F0")
                : "-";
            return "性能：" + HeadsetName(performance) +
                " · FPS " + fps + "/" + target +
                " · P95 " + OptionalMilliseconds(
                    performance.FrameSampleCount > 0,
                    performance.FrameTimeP95Ms);
        }

        public static string FormatPerformance(PerformanceDiagnostics performance)
        {
            if (performance == null || !performance.Available)
            {
                return "设备性能采集不可用";
            }

            var builder = new StringBuilder(1024);
            builder.Append("头显：").Append(HeadsetName(performance));
            if (performance.HeadsetPresenceAvailable && !performance.HeadsetWorn)
            {
                builder.Append("\n未佩戴时系统会节流，FPS 不代表佩戴表现");
            }
            builder.Append("\nFPS：")
                .Append(performance.FrameSampleCount > 0 ? performance.CurrentFps.ToString("F1") : "不可用")
                .Append(" / 目标 ")
                .Append(performance.TargetFpsAvailable ? performance.TargetFps.ToString("F0") : "不可用")
                .Append(" · 样本 ").Append(performance.FrameSampleCount);
            builder.Append("\n帧时：P50 ")
                .Append(OptionalMilliseconds(performance.FrameSampleCount > 0, performance.FrameTimeP50Ms))
                .Append(" · P95 ")
                .Append(OptionalMilliseconds(performance.FrameSampleCount > 0, performance.FrameTimeP95Ms))
                .Append(" · 最大 ")
                .Append(OptionalMilliseconds(performance.FrameSampleCount > 0, performance.FrameTimeMaxMs));
            builder.Append("\nCPU/GPU 帧时：")
                .Append(OptionalMilliseconds(performance.CpuFrameTimeAvailable, performance.CpuFrameTimeMs))
                .Append(" / ")
                .Append(OptionalMilliseconds(performance.GpuFrameTimeAvailable, performance.GpuFrameTimeMs));
            builder.Append("\n佩戴窗口：5秒 ").Append(performance.Fps5Seconds.ToString("F1"))
                .Append(" FPS · 30秒 ").Append(performance.Fps30Seconds.ToString("F1"))
                .Append(" FPS · ").Append(performance.ActiveSessionSeconds.ToString("F0")).Append('s');

            if (performance.XrPerformanceMetricsAvailable)
            {
                builder.Append("\nOpenXR App CPU/GPU：")
                    .Append(performance.XrAppCpuTimeMs.ToString("F2")).Append(" / ")
                    .Append(performance.XrAppGpuTimeMs.ToString("F2")).Append(" ms");
                builder.Append("\nOpenXR 利用率 CPU/GPU：")
                    .Append(performance.XrCpuUtilization.ToString("F0")).Append("% / ")
                    .Append(performance.XrGpuUtilization.ToString("F0")).Append("% · 合成器丢帧 ")
                    .Append(performance.CompositorDroppedFramesSession.ToString("F0"));
            }

            if (!performance.DetailedSamplingEnabled)
            {
                builder.Append("\n详细采集：已停止（打开本页后启用）");
                return builder.ToString();
            }

            builder.Append("\n内存：已分配 ").Append(Megabytes(performance.TotalAllocatedMemoryBytes))
                .Append(" · 保留 ").Append(Megabytes(performance.TotalReservedMemoryBytes))
                .Append(" · 托管 ").Append(Megabytes(performance.ManagedUsedMemoryBytes));
            builder.Append("\n进程 PSS：")
                .Append(performance.AndroidPssAvailable ? Megabytes(performance.AndroidPssBytes) : "不可用")
                .Append(" · GC 0/1/2：")
                .Append(performance.GcGeneration0Collections).Append('/')
                .Append(performance.GcGeneration1Collections).Append('/')
                .Append(performance.GcGeneration2Collections);
            builder.Append("\n热状态：")
                .Append(performance.ThermalStatusAvailable
                    ? ThermalName(performance.ThermalState)
                    : "不可用");

            if (performance.ModelLoaded)
            {
                builder.Append("\n模型：顶点 ").Append(performance.ModelVertexCount)
                    .Append(" · 三角形 ").Append(performance.ModelTriangleCount)
                    .Append(" · 渲染器 ").Append(performance.ModelRendererCount);
                builder.Append("\n模型：材质 ").Append(performance.ModelMaterialCount)
                    .Append(" · 纹理 ").Append(performance.ModelTextureCount)
                    .Append("（RGBA估算 ").Append(Megabytes(performance.ModelEstimatedTextureBytes)).Append(')');
                builder.Append("\n模型：骨骼 ").Append(performance.ModelBoneCount)
                    .Append(" · 变形 ").Append(performance.ModelBlendShapeCount)
                    .Append(" · 刚体/关节 ").Append(performance.ModelRigidBodyCount)
                    .Append('/').Append(performance.ModelJointCount);
            }
            else
            {
                builder.Append("\n模型复杂度：未加载模型");
            }

            if (performance.PhysicsMetricsAvailable)
            {
                builder.Append("\nMMD物理：").Append(performance.PhysicsFrequencyHz)
                    .Append("Hz · 本帧 ").Append(performance.PhysicsLastSubsteps)
                    .Append('/').Append(performance.PhysicsMaximumSubstepsPerFrame).Append(" 步");
                builder.Append("\n物理丢弃：本帧 ")
                    .Append((performance.PhysicsLastDroppedSeconds * 1000f).ToString("F1"))
                    .Append("ms · 本次佩戴 ")
                    .Append(performance.PhysicsSessionDroppedSeconds.ToString("F2"))
                    .Append("s/").Append(performance.PhysicsSessionDroppedFrameCount).Append("帧");
                builder.Append("\n最近丢弃：5秒 ")
                    .Append(performance.PhysicsDroppedMillisecondsPerSecond5s.ToString("F1"))
                    .Append("ms/s · ").Append(performance.PhysicsDroppedFramePercent5s.ToString("F1"))
                    .Append("%帧；30秒 ")
                    .Append(performance.PhysicsDroppedMillisecondsPerSecond30s.ToString("F1"))
                    .Append("ms/s · ").Append(performance.PhysicsDroppedFramePercent30s.ToString("F1")).Append("%帧");
                builder.Append("\nMMD采样/骨骼IK/Bullet/回写/SDEF：")
                    .Append(performance.MmdSamplingMilliseconds.ToString("F2")).Append('/')
                    .Append(performance.MmdBoneAndIkMilliseconds.ToString("F2")).Append('/')
                    .Append(performance.MmdPhysicsMilliseconds.ToString("F2")).Append('/')
                    .Append(performance.MmdFlushMilliseconds.ToString("F2")).Append('/')
                    .Append(performance.MmdSdefMilliseconds.ToString("F2")).Append("ms");
                builder.Append(" · 手部接触 ").Append(performance.HandContactMilliseconds.ToString("F2")).Append("ms");
                builder.Append(" · 描边提交 ")
                    .Append(AvatarOutlineController.LastRenderSubmissionMilliseconds.ToString("F2"))
                    .Append("ms/").Append(AvatarOutlineController.LastRenderedSubmeshCount).Append("子网格");
            }
            else
            {
                builder.Append("\nMMD物理：不可用");
            }
            return builder.ToString();
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

        public static string HeadsetName(PerformanceDiagnostics performance)
        {
            if (performance == null || !performance.HeadsetPresenceAvailable) return "佩戴状态不可用";
            return performance.HeadsetWorn ? "已佩戴" : "未佩戴（系统节流）";
        }

        public static string ThermalName(DeviceThermalState value)
        {
            switch (value)
            {
                case DeviceThermalState.Normal: return "正常";
                case DeviceThermalState.Light: return "轻度";
                case DeviceThermalState.Moderate: return "中度";
                case DeviceThermalState.Severe: return "严重";
                case DeviceThermalState.Critical: return "临界";
                case DeviceThermalState.Emergency: return "紧急";
                case DeviceThermalState.Shutdown: return "即将关机";
                case DeviceThermalState.Unknown: return "未知";
                default: return "不可用";
            }
        }

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

        private static string OptionalMilliseconds(bool available, float value)
        {
            return available ? value.ToString("F1") + "ms" : "不可用";
        }

        private static string Megabytes(long bytes)
        {
            return (Math.Max(0L, bytes) / (1024d * 1024d)).ToString("F1") + "MB";
        }
    }
}

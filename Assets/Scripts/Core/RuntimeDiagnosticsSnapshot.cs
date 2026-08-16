using System;
using UnityEngine;

namespace QuestMmdPlayer
{
    public enum RuntimeMenuLayer
    {
        Unavailable,
        Closed,
        Main,
        Actions,
        ActionList,
        Pairing,
        PairingKeyboard,
        Appearance,
        Models,
        ModelList,
        Quality,
        Performance,
        Voice,
        TextInput,
        Debug,
        Unknown
    }

    public enum BackendChainState
    {
        Unavailable,
        Unknown,
        EventBusEligible,
        EventBusReady,
        DirectProviderFallback,
        IdentityNotBound,
        PairingIdentityInvalid,
        ClientMismatch,
        PlatformUnavailable,
        AuthorizationTimeout,
        AuthorizationDenied
    }

    [Serializable]
    public sealed class RuntimeDiagnosticsSnapshot
    {
        public const string CurrentSchemaVersion = "1.1";

        public string SchemaVersion { get; }
        public float CapturedAtRealtimeSeconds { get; }
        public MenuDiagnostics Menu { get; }
        public InteractionDiagnostics Interaction { get; }
        public VoiceDiagnostics Voice { get; }
        public ConversationDiagnostics Conversation { get; }
        public BackendDiagnostics Backend { get; }
        public AudioDiagnostics Audio { get; }
        public PassthroughDiagnostics Passthrough { get; }
        public PlacementDiagnostics Placement { get; }
        public RoomDiagnostics Room { get; }
        public MotionDiagnostics Motion { get; }
        public ModelLoadDiagnostics ModelLoad { get; }
        public PerformanceDiagnostics Performance { get; }

        internal RuntimeDiagnosticsSnapshot(
            float capturedAtRealtimeSeconds,
            MenuDiagnostics menu,
            InteractionDiagnostics interaction,
            VoiceDiagnostics voice,
            ConversationDiagnostics conversation,
            BackendDiagnostics backend,
            AudioDiagnostics audio,
            PassthroughDiagnostics passthrough,
            PlacementDiagnostics placement,
            RoomDiagnostics room,
            MotionDiagnostics motion,
            ModelLoadDiagnostics modelLoad,
            PerformanceDiagnostics performance)
        {
            SchemaVersion = CurrentSchemaVersion;
            CapturedAtRealtimeSeconds = Mathf.Max(0f, capturedAtRealtimeSeconds);
            Menu = menu;
            Interaction = interaction;
            Voice = voice;
            Conversation = conversation;
            Backend = backend;
            Audio = audio;
            Passthrough = passthrough;
            Placement = placement;
            Room = room;
            Motion = motion;
            ModelLoad = modelLoad;
            Performance = performance;
        }
    }

    [Serializable]
    public sealed class MenuDiagnostics
    {
        public bool Available { get; }
        public bool Open { get; }
        public RuntimeMenuLayer ActiveLayer { get; }

        internal MenuDiagnostics(bool available, bool open, RuntimeMenuLayer activeLayer)
        {
            Available = available;
            Open = open;
            ActiveLayer = activeLayer;
        }
    }

    [Serializable]
    public sealed class InteractionDiagnostics
    {
        public bool HandVisualizerAvailable { get; }
        public int TrackedHandCount { get; }
        public int ActiveContactCount { get; }
        public TrackedHandContactPhase LastContactPhase { get; }
        public AvatarContactRegion LastContactRegion { get; }
        public bool LastContactPinching { get; }
        public float LastContactPenetrationDepth { get; }
        public float LastContactDurationSeconds { get; }
        public bool TouchAvailable { get; }
        public bool TouchInputEnabled { get; }
        public bool Touched { get; }
        public bool Grabbing { get; }
        public int ModelCollisionVolumeCount { get; }
        public bool SemanticContact { get; }
        public HumanInteractionKind CurrentInteraction { get; }
        public bool LocalReactionsEnabled { get; }
        public bool HeadBoneMatched { get; }
        public bool HandBonesMatched { get; }
        public int MatchedMorphCount { get; }

        internal InteractionDiagnostics(
            QuestTrackedHandVisualizer hands,
            AvatarTouchInteraction touch,
            AvatarHumanInteraction human)
        {
            HandVisualizerAvailable = hands != null;
            TrackedHandCount = hands == null ? 0 : Mathf.Clamp(hands.TrackedHandCount, 0, 2);
            ActiveContactCount = hands == null ? 0 : Mathf.Max(0, hands.ActiveContactCount);
            var hasContactFact = hands != null && (hands.ActiveContactCount > 0 ||
                hands.LatestContactFact.Region != AvatarContactRegion.None);
            LastContactPhase = hasContactFact
                ? hands.LatestContactFact.Phase
                : TrackedHandContactPhase.Ended;
            LastContactRegion = hasContactFact
                ? hands.LatestContactFact.Region
                : AvatarContactRegion.None;
            LastContactPinching = hasContactFact && hands.LatestContactFact.Pinching;
            LastContactPenetrationDepth = hasContactFact
                ? Mathf.Max(0f, hands.LatestContactFact.PenetrationDepth)
                : 0f;
            LastContactDurationSeconds = hasContactFact
                ? Mathf.Max(0f, hands.LatestContactFact.DurationSeconds)
                : 0f;
            TouchAvailable = touch != null;
            TouchInputEnabled = touch != null && touch.InputEnabled;
            Touched = touch != null && touch.IsTouched;
            Grabbing = touch != null && touch.IsGrabbing;
            ModelCollisionVolumeCount = touch == null ? 0 : Mathf.Max(0, touch.ModelCollisionVolumeCount);
            SemanticContact = human != null && human.HasSemanticContact;
            CurrentInteraction = human == null ? HumanInteractionKind.None : human.CurrentInteraction;
            LocalReactionsEnabled = human != null && human.LocalReactionsEnabled;
            HeadBoneMatched = human != null && human.HasHeadBone;
            HandBonesMatched = human != null && human.HasHandBones;
            MatchedMorphCount = human == null ? 0 : Mathf.Max(0, human.MatchedMorphCount);
        }
    }

    [Serializable]
    public sealed class VoiceDiagnostics
    {
        public bool Available { get; }
        public bool Monitoring { get; }
        public bool Recording { get; }
        public bool AlwaysListening { get; }
        public float InputLevel { get; }
        public float ActivationThreshold { get; }
        public float ActivationProgress { get; }
        public bool SpeechDetected { get; }
        public int LastTurnPcmBytes { get; }
        public int LastTurnChunkCount { get; }
        public float LastTurnCaptureSeconds { get; }

        internal VoiceDiagnostics(QuestMicrophoneInput voice)
        {
            Available = voice != null;
            Monitoring = voice != null && voice.IsMonitoring;
            Recording = voice != null && voice.IsRecording;
            AlwaysListening = voice != null && voice.AlwaysListening;
            InputLevel = voice == null ? 0f : Mathf.Max(0f, voice.InputLevel);
            ActivationThreshold = voice == null ? 0f : Mathf.Max(0f, voice.ActivationThreshold);
            ActivationProgress = voice == null ? 0f : Mathf.Clamp01(voice.ActivationProgress);
            SpeechDetected = voice != null && voice.SpeechDetected;
            LastTurnPcmBytes = voice == null ? 0 : Mathf.Max(0, voice.LastTurnPcmBytes);
            LastTurnChunkCount = voice == null ? 0 : Mathf.Max(0, voice.LastTurnChunkCount);
            LastTurnCaptureSeconds = voice == null ? 0f : Mathf.Max(0f, voice.LastTurnCaptureSeconds);
        }
    }

    [Serializable]
    public sealed class ConversationDiagnostics
    {
        public bool Available { get; }
        public ConversationState State { get; }
        public bool AwaitingBackendResponse { get; }
        public bool CanStartVoiceInput { get; }
        public bool RealBackendConnected { get; }
        public bool UsingMockTransport { get; }
        public float BufferedAudioSeconds { get; }
        public int FirstInputChunkMs { get; }
        public int InputEndMs { get; }
        public int FirstEventMs { get; }
        public int FirstTextMs { get; }
        public int FirstAudioMs { get; }
        public int ReplyEndMs { get; }
        public int AudioDoneMs { get; }
        public int ReplyAudioChunkCount { get; }
        public int TranscriptCharacters { get; }
        public int ReplyTextCharacters { get; }

        internal ConversationDiagnostics(ConversationController conversation)
        {
            Available = conversation != null;
            State = conversation == null ? ConversationState.Idle : conversation.State;
            AwaitingBackendResponse = conversation != null && conversation.AwaitingBackendResponse;
            CanStartVoiceInput = conversation != null && conversation.CanStartVoiceInput;
            RealBackendConnected = conversation != null && conversation.IsRealBackendConnected;
            UsingMockTransport = conversation != null && conversation.IsUsingMockTransport;
            BufferedAudioSeconds = conversation == null ? 0f : Mathf.Max(0f, conversation.BufferedAudioSeconds);

            var timing = RuntimeDiagnosticsBuilder.ParseConversationTiming(
                conversation == null ? null : conversation.TurnTimingStatus);
            FirstInputChunkMs = timing.FirstInputChunkMs;
            InputEndMs = timing.InputEndMs;
            FirstEventMs = timing.FirstEventMs;
            FirstTextMs = timing.FirstTextMs;
            FirstAudioMs = timing.FirstAudioMs;
            ReplyEndMs = timing.ReplyEndMs;
            AudioDoneMs = timing.AudioDoneMs;
            ReplyAudioChunkCount = timing.ReplyAudioChunkCount;
            TranscriptCharacters = conversation == null ? 0 : Mathf.Max(0, conversation.TranscriptCharacters);
            ReplyTextCharacters = conversation == null ? 0 : Mathf.Max(0, conversation.ReplyTextCharacters);
        }
    }

    [Serializable]
    public sealed class BackendDiagnostics
    {
        public bool Available { get; }
        public bool Configured { get; }
        public bool Connected { get; }
        public BackendChainState ChainStatus { get; }
        public int QueuedInputAudioBytes { get; }
        public bool AudioUploadInProgress { get; }

        internal BackendDiagnostics(AstrBotBridge backend)
        {
            Available = backend != null;
            Configured = backend != null && backend.IsConfigured;
            Connected = backend != null && backend.IsConnected;
            ChainStatus = ClassifyChainStatus(
                backend == null ? "unavailable" : backend.BackendChainStatus);
            QueuedInputAudioBytes = backend == null ? 0 : Mathf.Max(0, backend.QueuedInputAudioBytes);
            AudioUploadInProgress = backend != null && backend.AudioUploadInProgress;
        }

        private static BackendChainState ClassifyChainStatus(string value)
        {
            switch (value)
            {
                case "unavailable": return BackendChainState.Unavailable;
                case "EventBus eligible": return BackendChainState.EventBusEligible;
                case "EventBus ready": return BackendChainState.EventBusReady;
                case "direct provider fallback": return BackendChainState.DirectProviderFallback;
                case "owner_not_configured":
                case "quest_identity_not_allowlisted": return BackendChainState.IdentityNotBound;
                case "invalid_bot_id":
                case "invalid_user_id":
                case "missing_bot_id":
                case "missing_user_id": return BackendChainState.PairingIdentityInvalid;
                case "client_id_mismatch":
                case "invalid_client_id":
                case "missing_client_id":
                case "trusted_client_id_missing": return BackendChainState.ClientMismatch;
                case "missing_platform_id":
                case "trusted_platform_id_missing":
                case "trusted_platform_not_configured":
                case "trusted_platform_unavailable": return BackendChainState.PlatformUnavailable;
                case "authorization_timeout": return BackendChainState.AuthorizationTimeout;
                case "authorization_denied":
                case "authorization_error":
                case "protected_context_denied": return BackendChainState.AuthorizationDenied;
                default: return BackendChainState.Unknown;
            }
        }
    }

    [Serializable]
    public sealed class AudioDiagnostics
    {
        public bool Available { get; }
        public bool PlaybackStarted { get; }
        public bool StreamCompleted { get; }
        public bool Drained { get; }
        public float BufferedSeconds { get; }
        public int UnderflowCount { get; }

        internal AudioDiagnostics(Pcm16StreamAudioPlayer player)
        {
            Available = player != null;
            PlaybackStarted = player != null && player.PlaybackStarted;
            StreamCompleted = player != null && player.StreamCompleted;
            Drained = player != null && player.IsDrained;
            BufferedSeconds = player == null ? 0f : Mathf.Max(0f, player.BufferedSeconds);
            UnderflowCount = player == null ? 0 : Mathf.Max(0, player.UnderflowCount);
        }
    }

    [Serializable]
    public sealed class PassthroughDiagnostics
    {
        public bool Available { get; }
        public PassthroughState State { get; }
        public bool CameraSubsystemRunning { get; }

        internal PassthroughDiagnostics(PassthroughFacade passthrough)
        {
            Available = passthrough != null;
            State = passthrough == null ? PassthroughState.Unavailable : passthrough.State;
            CameraSubsystemRunning = passthrough != null && passthrough.IsCameraSubsystemRunning;
        }
    }

    [Serializable]
    public sealed class PlacementDiagnostics
    {
        public bool Available { get; }
        public bool HasPlacement { get; }
        public bool UsingFallback { get; }
        public bool HasSpatialAnchor { get; }
        public bool HasHeightCalibration { get; }
        public bool HasCalibratedFloor { get; }
        public bool HasSavedBookmark { get; }
        public bool HasPreparedSeatTarget { get; }
        public float EstimatedUserHeight { get; }
        public float CalibratedFloorHeight { get; }

        internal PlacementDiagnostics(AvatarPlacementService placement)
        {
            Available = placement != null;
            HasPlacement = placement != null && placement.HasPlacement;
            UsingFallback = placement != null && placement.IsUsingFallback;
            HasSpatialAnchor = placement != null && placement.HasSpatialAnchor;
            HasHeightCalibration = placement != null && placement.HasHeightCalibration;
            HasCalibratedFloor = placement != null && placement.HasCalibratedFloor;
            HasSavedBookmark = placement != null && placement.HasSavedPlacementBookmark;
            HasPreparedSeatTarget = placement != null && placement.HasPreparedSeatTarget;
            EstimatedUserHeight = placement == null ? 0f : Mathf.Max(0f, placement.EstimatedUserHeight);
            CalibratedFloorHeight = placement == null ? 0f : placement.CalibratedFloorHeight;
        }
    }

    [Serializable]
    public sealed class RoomDiagnostics
    {
        public bool Available { get; }
        public bool HasRoomData { get; }
        public int SurfaceCount { get; }
        public int PlacementCandidateCount { get; }
        public int FloorCount { get; }
        public int SeatCount { get; }
        public int TableCount { get; }
        public int WallCount { get; }
        public SpatialCapabilityState MetaOpenXr { get; }
        public SpatialCapabilityState Mruk { get; }
        public SpatialCapabilityState PlaneTracking { get; }
        public SpatialCapabilityState Occlusion { get; }
        public SpatialCapabilityState VirtualCollision { get; }
        public bool SceneCaptureRequested { get; }

        internal RoomDiagnostics(RoomUnderstandingService room)
        {
            Available = room != null;
            HasRoomData = room != null && room.HasRoomData;
            SurfaceCount = room == null || room.Surfaces == null ? 0 : room.Surfaces.Count;
            PlacementCandidateCount = room == null || room.PlacementCandidates == null
                ? 0
                : room.PlacementCandidates.Count;
            FloorCount = room == null ? 0 : Mathf.Max(0, room.FloorCount);
            SeatCount = room == null ? 0 : Mathf.Max(0, room.SeatCount);
            TableCount = room == null ? 0 : Mathf.Max(0, room.TableCount);
            WallCount = room == null ? 0 : Mathf.Max(0, room.WallCount);
            var capabilities = room == null ? default : room.Capabilities;
            MetaOpenXr = capabilities.MetaOpenXr;
            Mruk = capabilities.Mruk;
            PlaneTracking = capabilities.PlaneTracking;
            Occlusion = capabilities.Occlusion;
            VirtualCollision = capabilities.VirtualCollision;
            SceneCaptureRequested = room != null && room.IsSceneCaptureTrackingRequested;
        }
    }

    [Serializable]
    public sealed class MotionDiagnostics
    {
        public bool AvatarAvailable { get; }
        public bool AvatarActionPlaying { get; }
        public AvatarActionSource CurrentActionSource { get; }
        public bool IdlePoseBound { get; }
        public AvatarIdlePreset IdlePreset { get; }
        public bool VmdLibraryAvailable { get; }
        public bool VmdModelBound { get; }
        public int InstalledActionCount { get; }
        public int PreparedActionCount { get; }
        public bool VmdLoading { get; }
        public bool VmdPlaying { get; }
        public bool HoldingEndPose { get; }
        public bool BlendingOut { get; }
        public VmdPlaybackPhase PlaybackPhase { get; }
        public int ActionCacheHits { get; }
        public int ActionCacheMisses { get; }
        public int ActionCacheEvictions { get; }
        public int LastActionPrepareMs { get; }
        public bool FullBodyMotionBusy { get; }
        public bool ConversationPresentationActive { get; }
        public bool SemanticContactOwnsInteraction { get; }
        public bool MouthPresenterAvailable { get; }
        public int MatchedVisemeCount { get; }
        public bool SpeechTimelineActive { get; }
        public float AudibleRms { get; }
        public float SmoothedMouthAmount { get; }
        public float VisibleMouthAmount { get; }
        public float TimelinePositionMs { get; }
        public float TimelinePeak { get; }

        internal MotionDiagnostics(
            AvatarController avatar,
            AvatarNaturalIdlePose idle,
            VmdActionLibrary vmd,
            ConversationController conversation,
            AvatarHumanInteraction human,
            AvatarConversationPresenter presenter)
        {
            AvatarAvailable = avatar != null;
            AvatarActionPlaying = avatar != null && avatar.IsPlaying;
            CurrentActionSource = avatar == null ? AvatarActionSource.Unknown : avatar.CurrentActionSource;
            IdlePoseBound = idle != null && idle.IsBound;
            IdlePreset = idle == null ? AvatarIdlePreset.Relaxed : idle.Preset;
            VmdLibraryAvailable = vmd != null;
            VmdModelBound = vmd != null && vmd.BoundModel;
            InstalledActionCount = vmd == null || vmd.Actions == null ? 0 : vmd.Actions.Count;
            PreparedActionCount = vmd == null ? 0 : Mathf.Max(0, vmd.PreparedActionCount);
            VmdLoading = vmd != null && vmd.IsLoading;
            VmdPlaying = vmd != null && vmd.IsPlaying;
            HoldingEndPose = vmd != null && vmd.IsHoldingEndPose;
            BlendingOut = vmd != null && vmd.IsBlendingOut;
            PlaybackPhase = vmd == null ? VmdPlaybackPhase.Idle : vmd.PlaybackPhase;
            ActionCacheHits = vmd == null ? 0 : vmd.CacheHitCount;
            ActionCacheMisses = vmd == null ? 0 : vmd.CacheMissCount;
            ActionCacheEvictions = vmd == null ? 0 : vmd.CacheEvictionCount;
            LastActionPrepareMs = vmd == null ? -1 : vmd.LastPrepareMilliseconds;
            FullBodyMotionBusy = AvatarActionPlaying || VmdLoading || VmdPlaying || HoldingEndPose || BlendingOut;
            ConversationPresentationActive = conversation != null && conversation.State != ConversationState.Idle;
            SemanticContactOwnsInteraction = human != null && human.HasSemanticContact;
            MouthPresenterAvailable = presenter != null;
            MatchedVisemeCount = presenter == null ? 0 : Mathf.Max(0, presenter.MatchedVisemeCount);
            SpeechTimelineActive = presenter != null && presenter.SpeechTimelineActive;
            AudibleRms = presenter == null ? 0f : Mathf.Max(0f, presenter.LastAudibleRms);
            SmoothedMouthAmount = presenter == null ? 0f : Mathf.Clamp01(presenter.SmoothedMouthAmount);
            VisibleMouthAmount = presenter == null ? 0f : Mathf.Clamp01(presenter.LastVisibleMouthAmount);
            TimelinePositionMs = presenter == null ? 0f : Mathf.Max(0f, presenter.LastTimelinePositionMs);
            TimelinePeak = presenter == null ? 0f : Mathf.Clamp01(presenter.LastTimelinePeak);
        }
    }

    public sealed class ModelLoadDiagnostics
    {
        public bool Available { get; }
        public bool Loading { get; }
        public RuntimeModelLoadPhase Phase { get; }
        public int LastTotalMs { get; }
        public int LastReadMs { get; }
        public int LastBuildMs { get; }

        internal ModelLoadDiagnostics(RuntimeMmdModelLoader loader)
        {
            Available = loader != null;
            Loading = loader != null && loader.IsLoading;
            Phase = loader == null ? RuntimeModelLoadPhase.Idle : loader.LoadPhase;
            LastTotalMs = loader == null ? -1 : loader.LastLoadMilliseconds;
            LastReadMs = loader == null ? -1 : loader.LastReadMilliseconds;
            LastBuildMs = loader == null ? -1 : loader.LastBuildMilliseconds;
        }
    }

    [Serializable]
    public sealed class PerformanceDiagnostics
    {
        public bool Available { get; }
        public bool DetailedSamplingEnabled { get; }
        public bool HeadsetPresenceAvailable { get; }
        public bool HeadsetWorn { get; }
        public bool TargetFpsAvailable { get; }
        public float CurrentFps { get; }
        public float TargetFps { get; }
        public int FrameSampleCount { get; }
        public float FrameTimeP50Ms { get; }
        public float FrameTimeP95Ms { get; }
        public float FrameTimeMaxMs { get; }
        public float Fps5Seconds { get; }
        public float Fps30Seconds { get; }
        public float ActiveSessionSeconds { get; }
        public bool CpuFrameTimeAvailable { get; }
        public float CpuFrameTimeMs { get; }
        public bool GpuFrameTimeAvailable { get; }
        public float GpuFrameTimeMs { get; }
        public bool XrPerformanceMetricsAvailable { get; }
        public float XrAppCpuTimeMs { get; }
        public float XrAppGpuTimeMs { get; }
        public float XrCpuUtilization { get; }
        public float XrGpuUtilization { get; }
        public float CompositorDroppedFramesSession { get; }
        public long TotalAllocatedMemoryBytes { get; }
        public long TotalReservedMemoryBytes { get; }
        public long ManagedUsedMemoryBytes { get; }
        public bool AndroidPssAvailable { get; }
        public long AndroidPssBytes { get; }
        public int GcGeneration0Collections { get; }
        public int GcGeneration1Collections { get; }
        public int GcGeneration2Collections { get; }
        public bool ThermalStatusAvailable { get; }
        public DeviceThermalState ThermalState { get; }
        public bool ModelLoaded { get; }
        public int ModelRendererCount { get; }
        public int ModelMaterialCount { get; }
        public int ModelTextureCount { get; }
        public long ModelEstimatedTextureBytes { get; }
        public int ModelVertexCount { get; }
        public long ModelTriangleCount { get; }
        public int ModelBlendShapeCount { get; }
        public int ModelBoneCount { get; }
        public int ModelRigidBodyCount { get; }
        public int ModelJointCount { get; }
        public bool PhysicsMetricsAvailable { get; }
        public int PhysicsFrequencyHz { get; }
        public int PhysicsMaximumSubstepsPerFrame { get; }
        public int PhysicsLastSubsteps { get; }
        public float PhysicsLastDroppedSeconds { get; }
        public float PhysicsTotalDroppedSeconds { get; }
        public int PhysicsDroppedFrameCount { get; }
        public float PhysicsSessionDroppedSeconds { get; }
        public int PhysicsSessionDroppedFrameCount { get; }
        public float PhysicsDroppedMillisecondsPerSecond5s { get; }
        public float PhysicsDroppedMillisecondsPerSecond30s { get; }
        public float PhysicsDroppedFramePercent5s { get; }
        public float PhysicsDroppedFramePercent30s { get; }
        public float MmdSamplingMilliseconds { get; }
        public float MmdSolverMilliseconds { get; }
        public float MmdBoneAndIkMilliseconds { get; }
        public float MmdPhysicsMilliseconds { get; }
        public float MmdFlushMilliseconds { get; }
        public float MmdSdefMilliseconds { get; }
        public float HandContactMilliseconds { get; }

        internal PerformanceDiagnostics(RuntimePerformanceMonitor monitor)
        {
            Available = monitor != null;
            DetailedSamplingEnabled = monitor != null && monitor.detailedSamplingEnabled;
            HeadsetPresenceAvailable = monitor != null && monitor.headsetPresenceAvailable;
            HeadsetWorn = monitor != null && monitor.headsetWorn;
            TargetFpsAvailable = monitor != null && monitor.targetFpsAvailable;
            CurrentFps = monitor == null ? 0f : Mathf.Max(0f, monitor.currentFps);
            TargetFps = monitor == null ? 0f : Mathf.Max(0f, monitor.targetFps);
            FrameSampleCount = monitor == null ? 0 : Mathf.Max(0, monitor.frameSampleCount);
            FrameTimeP50Ms = monitor == null ? 0f : Mathf.Max(0f, monitor.frameTimeP50Ms);
            FrameTimeP95Ms = monitor == null ? 0f : Mathf.Max(0f, monitor.frameTimeP95Ms);
            FrameTimeMaxMs = monitor == null ? 0f : Mathf.Max(0f, monitor.frameTimeMaxMs);
            Fps5Seconds = monitor == null ? 0f : Mathf.Max(0f, monitor.fps5Seconds);
            Fps30Seconds = monitor == null ? 0f : Mathf.Max(0f, monitor.fps30Seconds);
            ActiveSessionSeconds = monitor == null ? 0f : Mathf.Max(0f, monitor.activeSessionSeconds);
            CpuFrameTimeAvailable = monitor != null && monitor.cpuFrameTimeAvailable;
            CpuFrameTimeMs = monitor == null ? 0f : Mathf.Max(0f, monitor.cpuFrameTimeMs);
            GpuFrameTimeAvailable = monitor != null && monitor.gpuFrameTimeAvailable;
            GpuFrameTimeMs = monitor == null ? 0f : Mathf.Max(0f, monitor.gpuFrameTimeMs);
            XrPerformanceMetricsAvailable = monitor != null && monitor.xrPerformanceMetricsAvailable;
            XrAppCpuTimeMs = monitor == null ? 0f : Mathf.Max(0f, monitor.xrAppCpuTimeMs);
            XrAppGpuTimeMs = monitor == null ? 0f : Mathf.Max(0f, monitor.xrAppGpuTimeMs);
            XrCpuUtilization = monitor == null ? 0f : Mathf.Max(0f, monitor.xrCpuUtilization);
            XrGpuUtilization = monitor == null ? 0f : Mathf.Max(0f, monitor.xrGpuUtilization);
            CompositorDroppedFramesSession = monitor == null
                ? 0f
                : Mathf.Max(0f, monitor.compositorDroppedFramesSession);
            TotalAllocatedMemoryBytes = monitor == null ? 0L : Math.Max(0L, monitor.totalAllocatedMemoryBytes);
            TotalReservedMemoryBytes = monitor == null ? 0L : Math.Max(0L, monitor.totalReservedMemoryBytes);
            ManagedUsedMemoryBytes = monitor == null ? 0L : Math.Max(0L, monitor.managedUsedMemoryBytes);
            AndroidPssAvailable = monitor != null && monitor.androidPssAvailable;
            AndroidPssBytes = monitor == null ? 0L : Math.Max(0L, monitor.androidPssBytes);
            GcGeneration0Collections = monitor == null ? 0 : Mathf.Max(0, monitor.gcGeneration0Collections);
            GcGeneration1Collections = monitor == null ? 0 : Mathf.Max(0, monitor.gcGeneration1Collections);
            GcGeneration2Collections = monitor == null ? 0 : Mathf.Max(0, monitor.gcGeneration2Collections);
            ThermalStatusAvailable = monitor != null && monitor.thermalStatusAvailable;
            ThermalState = monitor == null ? DeviceThermalState.Unavailable : monitor.thermalState;
            ModelLoaded = monitor != null && monitor.modelLoaded;
            ModelRendererCount = monitor == null ? 0 : Mathf.Max(0, monitor.modelRendererCount);
            ModelMaterialCount = monitor == null ? 0 : Mathf.Max(0, monitor.modelMaterialCount);
            ModelTextureCount = monitor == null ? 0 : Mathf.Max(0, monitor.modelTextureCount);
            ModelEstimatedTextureBytes = monitor == null ? 0L : Math.Max(0L, monitor.modelEstimatedTextureBytes);
            ModelVertexCount = monitor == null ? 0 : Mathf.Max(0, monitor.modelVertexCount);
            ModelTriangleCount = monitor == null ? 0L : Math.Max(0L, monitor.modelTriangleCount);
            ModelBlendShapeCount = monitor == null ? 0 : Mathf.Max(0, monitor.modelBlendShapeCount);
            ModelBoneCount = monitor == null ? 0 : Mathf.Max(0, monitor.modelBoneCount);
            ModelRigidBodyCount = monitor == null ? 0 : Mathf.Max(0, monitor.modelRigidBodyCount);
            ModelJointCount = monitor == null ? 0 : Mathf.Max(0, monitor.modelJointCount);
            PhysicsMetricsAvailable = monitor != null && monitor.physicsMetricsAvailable;
            PhysicsFrequencyHz = monitor == null ? 0 : Mathf.Max(0, monitor.physicsFrequencyHz);
            PhysicsMaximumSubstepsPerFrame = monitor == null
                ? 0
                : Mathf.Max(0, monitor.physicsMaximumSubstepsPerFrame);
            PhysicsLastSubsteps = monitor == null ? 0 : Mathf.Max(0, monitor.physicsLastSubsteps);
            PhysicsLastDroppedSeconds = monitor == null ? 0f : Mathf.Max(0f, monitor.physicsLastDroppedSeconds);
            PhysicsTotalDroppedSeconds = monitor == null ? 0f : Mathf.Max(0f, monitor.physicsTotalDroppedSeconds);
            PhysicsDroppedFrameCount = monitor == null ? 0 : Mathf.Max(0, monitor.physicsDroppedFrameCount);
            PhysicsSessionDroppedSeconds = monitor == null
                ? 0f
                : Mathf.Max(0f, monitor.physicsSessionDroppedSeconds);
            PhysicsSessionDroppedFrameCount = monitor == null
                ? 0
                : Mathf.Max(0, monitor.physicsSessionDroppedFrameCount);
            PhysicsDroppedMillisecondsPerSecond5s = monitor == null
                ? 0f
                : Mathf.Max(0f, monitor.physicsDroppedMillisecondsPerSecond5s);
            PhysicsDroppedMillisecondsPerSecond30s = monitor == null
                ? 0f
                : Mathf.Max(0f, monitor.physicsDroppedMillisecondsPerSecond30s);
            PhysicsDroppedFramePercent5s = monitor == null
                ? 0f
                : Mathf.Max(0f, monitor.physicsDroppedFramePercent5s);
            PhysicsDroppedFramePercent30s = monitor == null
                ? 0f
                : Mathf.Max(0f, monitor.physicsDroppedFramePercent30s);
            MmdSamplingMilliseconds = monitor == null ? 0f : Mathf.Max(0f, monitor.mmdSamplingMilliseconds);
            MmdSolverMilliseconds = monitor == null ? 0f : Mathf.Max(0f, monitor.mmdSolverMilliseconds);
            MmdBoneAndIkMilliseconds = monitor == null ? 0f : Mathf.Max(0f, monitor.mmdBoneAndIkMilliseconds);
            MmdPhysicsMilliseconds = monitor == null ? 0f : Mathf.Max(0f, monitor.mmdPhysicsMilliseconds);
            MmdFlushMilliseconds = monitor == null ? 0f : Mathf.Max(0f, monitor.mmdFlushMilliseconds);
            MmdSdefMilliseconds = monitor == null ? 0f : Mathf.Max(0f, monitor.mmdSdefMilliseconds);
            HandContactMilliseconds = monitor == null ? 0f : Mathf.Max(0f, monitor.handContactMilliseconds);
        }
    }

    public struct ConversationTimingDiagnostics
    {
        public int FirstInputChunkMs;
        public int InputEndMs;
        public int FirstEventMs;
        public int FirstTextMs;
        public int FirstAudioMs;
        public int ReplyEndMs;
        public int AudioDoneMs;
        public int ReplyAudioChunkCount;

        public static ConversationTimingDiagnostics Unavailable => new ConversationTimingDiagnostics
        {
            FirstInputChunkMs = -1,
            InputEndMs = -1,
            FirstEventMs = -1,
            FirstTextMs = -1,
            FirstAudioMs = -1,
            ReplyEndMs = -1,
            AudioDoneMs = -1,
            ReplyAudioChunkCount = 0
        };
    }

    public static class RuntimeDiagnosticsBuilder
    {
        public static RuntimeDiagnosticsSnapshot Capture(QuestMmdPlayerBootstrap owner)
        {
            var menu = owner == null ? null : owner.Menu;
            var conversation = owner == null ? null : owner.Conversation;
            var player = conversation == null ? null : conversation.GetComponent<Pcm16StreamAudioPlayer>();
            if (player == null && owner != null)
            {
                player = owner.GetComponent<Pcm16StreamAudioPlayer>();
            }

            return new RuntimeDiagnosticsSnapshot(
                Time.realtimeSinceStartup,
                new MenuDiagnostics(menu != null, menu != null && menu.IsOpen, DetectMenuLayer(menu)),
                new InteractionDiagnostics(
                    owner == null ? null : owner.TrackedHands,
                    owner == null ? null : owner.TouchInteraction,
                    owner == null ? null : owner.HumanInteraction),
                new VoiceDiagnostics(owner == null ? null : owner.VoiceInput),
                new ConversationDiagnostics(conversation),
                new BackendDiagnostics(owner == null ? null : owner.AstrBot),
                new AudioDiagnostics(player),
                new PassthroughDiagnostics(owner == null ? null : owner.Passthrough),
                new PlacementDiagnostics(owner == null ? null : owner.Placement),
                new RoomDiagnostics(owner == null ? null : owner.RoomUnderstanding),
                new MotionDiagnostics(
                    owner == null ? null : owner.Avatar,
                    owner == null ? null : owner.IdlePose,
                    owner == null ? null : owner.VmdActions,
                    conversation,
                    owner == null ? null : owner.HumanInteraction,
                    conversation == null ? null : conversation.GetComponent<AvatarConversationPresenter>()),
                new ModelLoadDiagnostics(owner == null ? null : owner.ModelLoader),
                new PerformanceDiagnostics(owner == null ? null : owner.Performance));
        }

        public static RuntimeMenuLayer DetectMenuLayer(CompanionWorldMenu menu)
        {
            if (menu == null)
            {
                return RuntimeMenuLayer.Unavailable;
            }
            return menu.ActiveLayer;
        }

        public static RuntimeMenuLayer DetectMenuLayer(Transform menuRoot, bool menuOpen)
        {
            if (!menuOpen)
            {
                return RuntimeMenuLayer.Closed;
            }
            if (menuRoot == null)
            {
                return RuntimeMenuLayer.Unknown;
            }
            if (IsActive(menuRoot, "Backend Pairing Layer/Pairing Server Keyboard"))
            {
                return RuntimeMenuLayer.PairingKeyboard;
            }
            if (IsActive(menuRoot, "Action Presets Layer/Added Actions List"))
            {
                return RuntimeMenuLayer.ActionList;
            }
            if (IsActive(menuRoot, "Model Library Layer/Installed Model List"))
            {
                return RuntimeMenuLayer.ModelList;
            }
            if (IsActive(menuRoot, "Device Performance Layer")) return RuntimeMenuLayer.Performance;
            if (IsActive(menuRoot, "Quality Layer")) return RuntimeMenuLayer.Quality;
            if (IsActive(menuRoot, "Voice Layer")) return RuntimeMenuLayer.Voice;
            if (IsActive(menuRoot, "Model Library Layer")) return RuntimeMenuLayer.Models;
            if (IsActive(menuRoot, "Appearance Layer")) return RuntimeMenuLayer.Appearance;
            if (IsActive(menuRoot, "Backend Pairing Layer")) return RuntimeMenuLayer.Pairing;
            if (IsActive(menuRoot, "Action Presets Layer")) return RuntimeMenuLayer.Actions;
            if (IsActive(menuRoot, "Main Menu Layer")) return RuntimeMenuLayer.Main;
            if (IsActive(menuRoot, "Debug Layer")) return RuntimeMenuLayer.Debug;
            return RuntimeMenuLayer.Unknown;
        }

        public static ConversationTimingDiagnostics ParseConversationTiming(string value)
        {
            var result = ConversationTimingDiagnostics.Unavailable;
            if (string.IsNullOrWhiteSpace(value))
            {
                return result;
            }

            var tokens = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < tokens.Length; index++)
            {
                var separator = tokens[index].IndexOf('=');
                if (separator <= 0 || separator >= tokens[index].Length - 1)
                {
                    continue;
                }
                var key = tokens[index].Substring(0, separator);
                var raw = tokens[index].Substring(separator + 1);
                if (raw.EndsWith("ms", StringComparison.Ordinal))
                {
                    raw = raw.Substring(0, raw.Length - 2);
                }
                if (!int.TryParse(raw, out var parsed) || parsed < 0)
                {
                    continue;
                }

                switch (key)
                {
                    case "firstChunk": result.FirstInputChunkMs = parsed; break;
                    case "inputEnd": result.InputEndMs = parsed; break;
                    case "firstEvent": result.FirstEventMs = parsed; break;
                    case "firstText": result.FirstTextMs = parsed; break;
                    case "firstAudio": result.FirstAudioMs = parsed; break;
                    case "replyEnd": result.ReplyEndMs = parsed; break;
                    case "audioDone": result.AudioDoneMs = parsed; break;
                    case "chunks": result.ReplyAudioChunkCount = parsed; break;
                }
            }
            return result;
        }

        private static bool IsActive(Transform root, string path)
        {
            var child = root.Find(path);
            return child != null && child.gameObject.activeInHierarchy;
        }
    }
}

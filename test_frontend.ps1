param(
    [switch]$Strict
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Pass([string]$message) {
    Write-Host "[PASS] $message" -ForegroundColor Green
}

function Fail([string]$message) {
    $failures.Add($message)
    Write-Host "[FAIL] $message" -ForegroundColor Red
}

function Check-File([string]$relativePath) {
    $path = Join-Path $projectRoot $relativePath
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Pass "file exists: $relativePath"
        return $true
    }

    Fail "missing file: $relativePath"
    return $false
}

Write-Host "Quest MMD Player automated checks" -ForegroundColor Cyan
Write-Host "Project: $projectRoot"

$requiredFiles = @(
    "Packages/manifest.json",
    "Packages/com.candidumgames.unitymmdtools/package.json",
    "ProjectSettings/ProjectVersion.txt",
    "Assets/Scripts/Core/AvatarCommand.cs",
    "Assets/Scripts/Core/AvatarController.cs",
    "Assets/Scripts/Core/AvatarTouchInteraction.cs",
    "Assets/Scripts/Core/AvatarMmdPhysicsAdapter.cs",
    "Packages/com.candidumgames.unitymmdtools/Runtime/MMDRuntime/MMDPhysicsManager.cs",
    "Assets/Scripts/Core/QuestMmdPlayerBootstrap.cs",
    "Assets/Scripts/Core/QuestQualitySettings.cs",
    "Assets/Scripts/Core/RuntimeDiagnosticsSnapshot.cs",
    "Assets/Scripts/MMD/RuntimeMmdModelLoader.cs",
    "Assets/Scripts/MMD/VmdActionLibrary.cs",
    "Assets/Scripts/MR/PassthroughFacade.cs",
    "Assets/Scripts/MR/RoomUnderstandingService.cs",
    "Assets/Scripts/MR/QuestFileImportService.cs",
    "Assets/Plugins/Android/AndroidManifest.xml",
    "Assets/Plugins/Android/BanxiaFilePicker.androidlib/AndroidManifest.xml",
    "Assets/Plugins/Android/BanxiaFilePicker.androidlib/src/main/java/com/lingxi/banxia/filepicker/BanxiaFilePicker.java",
    "Assets/Plugins/Android/BanxiaFilePicker.androidlib/src/main/java/com/lingxi/banxia/filepicker/BanxiaFilePickerActivity.java",
    "Assets/Scripts/Backend/AstrBotBridge.cs",
    "Assets/Scripts/Backend/BackendPairingController.cs",
    "Assets/Scripts/Backend/BackendPairingProtocol.cs",
    "Assets/Scripts/UI/CompanionWorldMenu.cs",
    "Assets/Scripts/Conversation/ConversationModels.cs",
    "Assets/Scripts/Conversation/ConversationStateMachine.cs",
    "Assets/Scripts/Conversation/MockConversationTransport.cs",
    "Assets/Scripts/Conversation/Pcm16StreamAudioPlayer.cs",
    "Assets/Scripts/Conversation/Pcm16CaptureUtility.cs",
    "Assets/Scripts/Conversation/QuestMicrophoneInput.cs",
    "Assets/Scripts/Conversation/AvatarConversationPresenter.cs",
    "Assets/Scripts/Conversation/ConversationActionIntent.cs",
    "Assets/Scripts/Conversation/ConversationController.cs",
    "Assets/Editor/QuestMmdPlayerBuild.cs",
    "Assets/Editor/QuestPrivateLanManifestPostprocessor.cs",
    "Assets/Editor/QuestMmdPlayerRuntimeSmokeTest.cs",
    "Assets/XR/Settings/OpenXRPackageSettings.asset",
    "Assets/Plugins/Android/arm64-v8a/libUMTNativePlugin.so",
    "Assets/Plugins/Android/arm64-v8a/libc++_shared.so",
    "Assets/Tests/Editor/AvatarCommandTests.cs",
    "Assets/Tests/Editor/AvatarHumanInteractionTests.cs",
    "Assets/Tests/Editor/MmdPhysicsAdapterTests.cs",
    "Assets/Tests/Editor/ConversationStateMachineTests.cs",
    "Assets/Tests/Editor/ExternalInteractionTurnTests.cs",
    "Assets/Tests/Editor/Pcm16CaptureUtilityTests.cs",
    "Assets/Tests/Editor/VoiceConversationControllerTests.cs",
    "Assets/Tests/Editor/ConversationActionIntentTests.cs",
    "Assets/Tests/Editor/BackendDrivenInteractionTests.cs",
    "Assets/Tests/Editor/BackendPairingTests.cs",
    "Assets/Tests/Editor/VmdActionLibraryTests.cs",
    "Assets/Tests/Editor/RoomUnderstandingServiceTests.cs",
    "Assets/Tests/Editor/RuntimeDiagnosticsSnapshotTests.cs",
    "Assets/Tests/Editor/QuestFileImportServiceTests.cs",
    "README.md",
    "TESTING.md",
    "QUICK_TEST.md",
    "HUMAN_INTERACTION_TESTING_CN.md",
    "CONVERSATION_TESTING_CN.md",
    "VOICE_INPUT_TESTING_CN.md",
    "DEVELOPMENT_ROADMAP_CN.md",
    "REFERENCE_AUDIT.md",
    "ASTRBOT_PLUGIN_DEVELOPMENT_PROMPT_CN.md"
)

foreach ($file in $requiredFiles) {
    [void](Check-File $file)
}

$localModelSample = Join-Path $projectRoot "Assets/StreamingAssets/MmdSamples/ForestBerry/ForestBerry.pmx"
if (Test-Path -LiteralPath $localModelSample -PathType Leaf) {
    Pass "optional local PMX smoke-test sample is available"
} else {
    Pass "optional local PMX sample is absent; fallback avatar remains available"
}

$manifestPath = Join-Path $projectRoot "Packages/manifest.json"
if (Test-Path -LiteralPath $manifestPath) {
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
        if ($null -eq $manifest.dependencies) {
            Fail "manifest.json has no dependencies object"
        } else {
            Pass "manifest.json is valid JSON"
            $expectedPackages = @(
                "com.unity.xr.meta-openxr",
                "com.unity.xr.openxr",
                "com.unity.xr.management",
                "com.unity.xr.hands",
                "com.unity.render-pipelines.universal",
                "com.unity.modules.animation",
                "com.unity.modules.imageconversion"
            )
            foreach ($package in $expectedPackages) {
                if ($manifest.dependencies.PSObject.Properties.Name -contains $package) {
                    Pass "package declared: $package"
                } else {
                    Fail "package missing: $package"
                }
            }
        }
    } catch {
        Fail "manifest.json cannot be parsed: $($_.Exception.Message)"
    }
}

$umtManifestPath = Join-Path $projectRoot "Packages/com.candidumgames.unitymmdtools/package.json"
if (Test-Path -LiteralPath $umtManifestPath) {
    try {
        $umtManifest = Get-Content -LiteralPath $umtManifestPath -Raw | ConvertFrom-Json
        if ($umtManifest.name -eq "com.candidumgames.unitymmdtools" -and $umtManifest.version -eq "0.5.0") {
            Pass "embedded UMT package metadata is valid"
        } else {
            Fail "embedded UMT package metadata is unexpected"
        }
    } catch {
        Fail "embedded UMT package.json cannot be parsed: $($_.Exception.Message)"
    }
}

$versionPath = Join-Path $projectRoot "ProjectSettings/ProjectVersion.txt"
if (Test-Path -LiteralPath $versionPath) {
    $version = Get-Content -LiteralPath $versionPath -Raw
    if ($version -match "(2022\.3\.62f3c1|6000\.0\.59f2)") {
        Pass "supported Unity project version is declared"
    } else {
        Fail "ProjectVersion.txt is not using a supported Unity baseline"
    }
}

$sourceChecks = @{
    "Assets/Scripts/MMD/RuntimeMmdModelLoader.cs" = @("LoadFromFileAsync", "PMXImporter.BuildUnityObjectsAsync", "streamingAssetsPath", "textureBaseDirectory", "PreserveOriginalNames")
    "Assets/Scripts/MMD/VmdActionLibrary.cs" = @("VmdActionFilePolicy", "SearchOption.TopDirectoryOnly", "VMDReader.ReadAsync", "VMDAnimationClipConverter.ConvertAsync", "DefaultExecutionOrder(11000)", "bakePhysicsToFK = true", "BeginPhysicsArbitration", "StopAndReturnToIdle")
    "Assets/Scripts/Core/QuestQualitySettings.cs" = @("XRSettings.eyeTextureResolutionScale", "XRSettings.renderViewportScale", "QuestQualityPreset.Clear", "UniversalRenderPipelineAsset", "PlayerPrefs.Save")
    "Assets/Editor/QuestMmdPlayerBuild.cs" = @("MetaQuestFeature", "forceRemoveInternetPermission", "ConfigureQuestInteractionProfiles", "OculusTouchControllerProfile", "MetaQuestTouchProControllerProfile", "HandTracking", "GraphicsDeviceType.Vulkan", "AndroidArchitecture.ARM64", "InsecureHttpOption.AlwaysAllowed", "PlayerSettings.productName = QuestMmdPlayerBootstrap.AndroidTaskLabel", "PlayerSettings.SetApplicationIdentifier", "PlayerSettings.bundleVersion = AndroidVersionName", "PlayerSettings.Android.bundleVersionCode = AndroidVersionCode", "com.lingxi.banxia", "0.1.5", "Builds/Banxia.apk")
    "Assets/Plugins/Android/AndroidManifest.xml" = @("com.unity3d.player.UnityPlayerActivity", "android.intent.action.MAIN", "android.intent.category.LAUNCHER")
    "Assets/Editor/QuestPrivateLanManifestPostprocessor.cs" = @("usesCleartextTraffic", "horizonos.permission.HAND_TRACKING", "EnsurePermission", "ConfigureFilePickerModule", "buildConfig = false")
    "Assets/Editor/QuestMmdPlayerRuntimeSmokeTest.cs" = @("PMXImporter.Import", "applyRenames = false", "Runtime PMX Smoke Test")
    "Assets/Editor/QuestMmdPlayerMenu.cs" = @("RuntimeMmdModelLoader", "bundled PMX sample")
    "Assets/Scripts/Backend/AstrBotBridge.cs" = @("TryIngestCommandJson", "JsonUtility.FromJson", "CommandReceived", "ReloadConfiguration", "ConfiguredBaseUrl", "ParseSessionChainStatus", "ResolveBackendChainStatus", "sse_dispatch", "AudioRequestCode", "UpdateMaximum")
    "Assets/Scripts/Backend/AstrBotProtocol.cs" = @("ReceivedAtTicks", "server_timing", "ToBackendTiming", "ClampServerDuration")
    "Assets/Scripts/Backend/BackendPairingProtocol.cs" = @("TryBuildExchangeEndpoint", "TryParseQrPayload", "TryWriteSettingsAtomically", "File.Replace", "https")
    "Assets/Scripts/Backend/BackendPairingController.cs" = @("IPairingCodeScanner", "PairWithCode", "PairWithQrPayload", "PairingServerEndpoint", "ReloadConfiguration")
    "Assets/Scripts/UI/CompanionWorldMenu.cs" = @("PAIR BACKEND", "SET HOST PORT", "AUTO COMPLETE PATH", "TouchScreenKeyboard", "RefreshExternalActions", "PlaySelectedExternalAction", "ImportFile", "导入文件", "RoomUnderstanding")
    "Assets/Scripts/MR/PassthroughFacade.cs" = @("IPassthroughProvider", "EditorPassthroughProvider", "StateChanged")
    "Assets/Scripts/MR/QuestFileImportService.cs" = @("OpenPicker", "OnAndroidFileImported", "ExtractArchiveSafely", "MaximumExpandedArchiveBytes", "RuntimeMmdModelLoader", "VmdActionFilePolicy")
    "Assets/Plugins/Android/BanxiaFilePicker.androidlib/src/main/java/com/lingxi/banxia/filepicker/BanxiaFilePicker.java" = @("Intent", "BanxiaFilePickerActivity.class", "startActivity")
    "Assets/Plugins/Android/BanxiaFilePicker.androidlib/src/main/java/com/lingxi/banxia/filepicker/BanxiaFilePickerActivity.java" = @("Intent.ACTION_OPEN_DOCUMENT", "Intent.EXTRA_ALLOW_MULTIPLE", "copyUris", "replaceAll", "Imports/Batches", "Class.forName", "UnitySendMessage")
    "Assets/Scripts/Core/AvatarController.cs" = @("Move", "Rotate", "Scale", "PlayAction", "TogglePlayback", "CaptureActionPose", "rightUpperArm", "ApplyWave", "ApplyBow")
    "Assets/Scripts/Core/AvatarTouchInteraction.cs" = @("InputDevices.GetDeviceAtXRNode", "TouchStateChanged", "ApplyDualGrab", "primaryButton", "triggerButton", "XRHandJointID.IndexTip", "SetSemanticInteractionLock")
    "Assets/Scripts/Core/AvatarMmdPhysicsAdapter.cs" = @("ConfigureExternalKinematicSpheres", "SetExternalKinematicSpherePose", "PhysicsProbeCount")
    "Packages/com.candidumgames.unitymmdtools/Runtime/MMDRuntime/MMDPhysicsManager.cs" = @("ConfigureExternalKinematicSpheres", "SetExternalKinematicSpherePose", "CreateExternalKinematicSphereData", "collisionGroupMask = -1")
    "Assets/Scripts/Core/QuestMmdPlayerBootstrap.cs" = @("AndroidTaskLabel", "ActivityManager$TaskDescription", "setTaskDescription", "AvatarTouchInteraction", "AvatarHumanInteraction", "ConversationController", "QuestMicrophoneInput", "RoomUnderstandingService", "QuestFileImportService", "FileImport.Initialize", "BindInteractions", "handshake", "head_pat", "cheek_pinch")
    "Assets/Scripts/Core/AvatarHumanInteraction.cs" = @("XRHandSubsystem", "XRHandJointID.Palm", "HumanInteractionKind.Handshake", "HumanInteractionKind.HeadPat", "HumanInteractionKind.CheekPinch", "SimulateInteraction", "SetLocalReactionsEnabled", "PlayReaction", "SetSemanticInteractionLock")
    "Assets/Tests/Editor/AvatarCommandTests.cs" = @("JsonCommandIsAcceptedByBridge", "InvalidJsonIsRejected")
    "Assets/Tests/Editor/AvatarHumanInteractionTests.cs" = @("BindFindsMmdBonesAndSimulationChangesState", "HumanInteractionKind.Handshake", "HumanInteractionKind.HeadPat", "HumanInteractionKind.CheekPinch")
    "Assets/Scripts/Conversation/ConversationModels.cs" = @("LookAt", "BeginAudioTurn", "QueueAudioChunk", "EndAudioTurn", "SendInteraction")
    "Assets/Scripts/Conversation/ConversationStateMachine.cs" = @("turnSequence", "acceptingEvents", "TryFinishAudio", "Interrupt")
    "Assets/Scripts/Conversation/MockConversationTransport.cs" = @("IConversationTransport", "BeginAudioTurn", "QueueAudioChunk", "EndAudioTurn", "AudioChunk", "SendInteraction", "AvatarIntent")
    "Assets/Scripts/Conversation/Pcm16StreamAudioPlayer.cs" = @("AudioClip.Create", "Enqueue", "StopAndClear", "BufferedSeconds", "OnAudioFilterRead", "PlaybackTelemetryReady", "ReportPlaybackTelemetry", "QueuedChunkCount")
    "Assets/Scripts/Conversation/Pcm16CaptureUtility.cs" = @("ResampleAndEncode", "FloatToPcm16", "FramesForDuration")
    "Assets/Scripts/Conversation/QuestMicrophoneInput.cs" = @("Permission.Microphone", "Microphone.Start", "primary2DAxisClick", "ResampleAndEncode", "Voice upload queue full")
    "Assets/Scripts/Conversation/ConversationController.cs" = @("BeginVoiceInput", "PushVoiceAudio", "EndVoiceInput", "StartMockConversation", "HandleTransportEvent", "SendInteraction", "Interrupt", "backend_total", "PlaybackTelemetry", "audio_buffer")
    "Assets/Scripts/Core/RuntimeDebugLog.cs" = @("RuntimeDebugLog", "RecordStage", "GetRecentTimelineText", "CurrentRootCause", "TraceLabel", "queueDepth", "bufferedMs")
    "Assets/Scripts/Conversation/ConversationActionIntent.cs" = @("TryDetect", "跳舞", "挥手", "鞠躬")
    "Assets/Scripts/Conversation/AvatarConversationPresenter.cs" = @("PlayReaction", "SetBlendShapeWeight", "LatestRms", "ApplyIntent", "lookAtMode", "ApplyGaze")
    "Assets/Tests/Editor/ConversationStateMachineTests.cs" = @("TurnMovesFromListeningThroughSpeakingToIdle", "StaleTurnAndInterruptedTurnCannotChangeState")
    "Assets/Tests/Editor/ExternalInteractionTurnTests.cs" = @("CompleteReplyIsAcceptedAndOlderInteractionTurnIsRejected")
    "Assets/Tests/Editor/Pcm16CaptureUtilityTests.cs" = @("FloatSamplesEncodeAsLittleEndianPcm16", "EightyMillisecondsAtFortyEightKhzBecomesValidSixteenKhzChunk")
    "Assets/Tests/Editor/VoiceConversationControllerTests.cs" = @("VoiceTurnUsesOneTurnIdAndForwardsPcmBeforeEnd", "DisconnectedTransportCannotStartVoiceTurn")
    "Assets/Tests/Editor/BackendDrivenInteractionTests.cs" = @("SensorEventAndBackendReactionAreSeparate", "SetLocalReactionsEnabled", "PlayReaction")
    "Assets/Tests/Editor/QuestFileImportServiceTests.cs" = @("SupportedExtensionsAreRestrictedToModelAndMotionFormats", "ImportedNamesAreBoundedAndCannotEscapeDirectory", "SanitizeImportedName")
}

foreach ($entry in $sourceChecks.GetEnumerator()) {
    $relativePath = $entry.Key
    $path = Join-Path $projectRoot $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        continue
    }

    $source = Get-Content -LiteralPath $path -Raw
    foreach ($token in $entry.Value) {
        if ($source.Contains($token)) {
            Pass "source contract present: $relativePath -> $token"
        } else {
            Fail "source contract missing: $relativePath -> $token"
        }
    }
}

$bridgePath = Join-Path $projectRoot "Assets/Scripts/Backend/AstrBotBridge.cs"
if (Test-Path -LiteralPath $bridgePath) {
    $bridgeSource = Get-Content -LiteralPath $bridgePath -Raw
    if ($bridgeSource -match "Quest builds require HTTPS") {
        Fail "AstrBotBridge reintroduces an unconditional Android HTTP rejection"
    } else {
        Pass "AstrBotBridge delegates private-LAN HTTP policy to AstrBotProtocol"
    }
    if ($bridgeSource.Contains('SetRequestHeader("Authorization", "ApiKey "')) {
        Pass "AstrBotBridge uses the AstrBot API-key authentication scheme"
    } else {
        Fail "AstrBotBridge does not use the AstrBot API-key authentication scheme"
    }
    if ($bridgeSource.Contains('SetRequestHeader("Authorization", "Bearer "')) {
        Fail "AstrBotBridge incorrectly sends an AstrBot API key as a Dashboard bearer token"
    } else {
        Pass "AstrBotBridge does not send the AstrBot API key as a Dashboard bearer token"
    }
}

$projectSettingsPath = Join-Path $projectRoot "ProjectSettings/ProjectSettings.asset"
if (Test-Path -LiteralPath $projectSettingsPath) {
    $projectSettingsSource = Get-Content -LiteralPath $projectSettingsPath -Raw
    if ($projectSettingsSource -match "(?m)^\s*insecureHttpOption:\s*2\s*$") {
        Pass "UnityWebRequest permits explicitly opted-in private-LAN HTTP"
    } else {
        Fail "UnityWebRequest private-LAN HTTP build setting is not enabled"
    }
}

$openXrPath = Join-Path $projectRoot "Assets/XR/Settings/OpenXRPackageSettings.asset"
if (Test-Path -LiteralPath $openXrPath) {
    $openXrText = Get-Content -LiteralPath $openXrPath -Raw
    if ($openXrText -match "m_enabled:\s*1\s*\r?\n\s*nameUi: Hand Tracking Subsystem") {
        Pass "OpenXR hand tracking feature is enabled for Android"
    } else {
        Fail "OpenXR hand tracking feature is not enabled for Android"
    }
}

$commandJson = '{"command":"play_motion","motionId":"wave"}'
try {
    $command = $commandJson | ConvertFrom-Json
    if ($command.command -eq "play_motion" -and $command.motionId -eq "wave") {
        Pass "AstrBot command sample is valid JSON"
    } else {
        Fail "AstrBot command sample fields are incorrect"
    }
} catch {
    Fail "AstrBot command sample is invalid JSON"
}

$temporaryFiles = Get-ChildItem -Path $projectRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in @("manifest2.json", ".patch_probe") }
if ($temporaryFiles.Count -eq 0) {
    Pass "no temporary probe files remain"
} else {
    foreach ($file in $temporaryFiles) {
        Fail "temporary file remains: $($file.FullName)"
    }
}

Write-Host ""
if ($failures.Count -eq 0) {
    Write-Host "Automated checks passed." -ForegroundColor Green
    Write-Host "Still requires a headset: APK install, Quest model display, Passthrough, hand/controller input, and performance." -ForegroundColor Yellow
    exit 0
}

Write-Host "Automated checks failed: $($failures.Count)" -ForegroundColor Red
if ($Strict) {
    exit 1
}

exit 1

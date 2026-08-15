param(
    [switch]$Strict
)

$ErrorActionPreference = "Stop"
$projectRoot = $PSScriptRoot
$failures = New-Object System.Collections.Generic.List[string]

function Pass([string]$message) { Write-Host "[PASS] $message" -ForegroundColor Green }
function Fail([string]$message) { $failures.Add($message); Write-Host "[FAIL] $message" -ForegroundColor Red }
function CheckFile([string]$relativePath) {
    if (Test-Path -LiteralPath (Join-Path $projectRoot $relativePath) -PathType Leaf) { Pass "file: $relativePath" }
    else { Fail "missing file: $relativePath" }
}
function CheckSource([string]$relativePath, [string[]]$tokens) {
    $path = Join-Path $projectRoot $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "missing source: $relativePath"; return }
    $source = Get-Content -LiteralPath $path -Raw
    foreach ($token in $tokens) {
        if ($source.Contains($token)) { Pass "source: $relativePath -> $token" }
        else { Fail "source contract missing: $relativePath -> $token" }
    }
}

Write-Host "Banxia automated checks" -ForegroundColor Cyan
Write-Host "Project: $projectRoot"

$requiredFiles = @(
    "Packages/manifest.json",
    "Packages/com.candidumgames.unitymmdtools/package.json",
    "Packages/com.candidumgames.unitymmdtools/Runtime/MMDRuntime/MMDPhysicsManager.cs",
    "Packages/com.candidumgames.unitymmdtools/Runtime/PMX/PMXMeshBuilder.cs",
    "Packages/com.candidumgames.unitymmdtools/Runtime/PMX/PMXImporter.cs",
    "Packages/com.candidumgames.unitymmdtools/Runtime/PMX/PMXTextureLoader.cs",
    "ProjectSettings/ProjectVersion.txt",
    "Assets/Scripts/Core/AvatarController.cs",
    "Assets/Scripts/Core/AvatarMotionArbiter.cs",
    "Assets/Scripts/Core/XRInteractionCompatibility.cs",
    "Assets/Scripts/Core/AvatarTouchInteraction.cs",
    "Assets/Scripts/Core/AvatarHumanInteraction.cs",
    "Assets/Scripts/Core/AvatarMmdPhysicsAdapter.cs",
    "Assets/Scripts/Core/QuestTrackedHandVisualizer.cs",
    "Assets/Scripts/Core/QuestMmdPlayerBootstrap.cs",
    "Assets/Scripts/Core/QuestQualitySettings.cs",
    "Assets/Scripts/Core/RuntimeDiagnosticsSnapshot.cs",
    "Assets/Scripts/Conversation/AvatarConversationPresenter.cs",
    "Assets/Scripts/Conversation/ConversationController.cs",
    "Assets/Scripts/Conversation/ConversationStateMachine.cs",
    "Assets/Scripts/Conversation/QuestMicrophoneInput.cs",
    "Assets/Scripts/Conversation/Pcm16StreamAudioPlayer.cs",
    "Assets/Scripts/MMD/RuntimeMmdModelLoader.cs",
    "Assets/Scripts/MMD/AvatarQaCapture.cs",
    "Assets/Scripts/MMD/VmdActionLibrary.cs",
    "Assets/Scripts/MR/RoomUnderstandingService.cs",
    "Assets/Scripts/MR/SpatialCapabilityAdapter.cs",
    "Assets/Scripts/MR/QuestFileImportService.cs",
    "Assets/Scripts/MR/PassthroughFacade.cs",
    "Assets/Scripts/UI/CompanionWorldMenu.cs",
    "Assets/Scripts/UI/RuntimeDiagnosticsFormatter.cs",
    "Assets/Scripts/Backend/AstrBotBridge.cs",
    "Assets/Scripts/Backend/BackendPairingProtocol.cs",
    "Assets/Scripts/Backend/BackendPairingController.cs",
    "Assets/Plugins/Android/BanxiaFilePicker.androidlib/AndroidManifest.xml",
    "Assets/Plugins/Android/BanxiaFilePicker.androidlib/src/main/java/com/lingxi/banxia/filepicker/BanxiaFilePicker.java",
    "Assets/Plugins/Android/BanxiaFilePicker.androidlib/src/main/java/com/lingxi/banxia/filepicker/BanxiaFilePickerActivity.java",
    "Assets/Plugins/Android/arm64-v8a/libUMTNativePlugin.so",
    "Assets/Plugins/Android/arm64-v8a/libc++_shared.so",
    "Assets/Editor/QuestMmdPlayerBuild.cs",
    "Assets/Editor/QuestPrivateLanManifestPostprocessor.cs",
    "Assets/Plugins/Android/AndroidManifest.xml",
    "Assets/XR/Settings/OpenXRPackageSettings.asset",
    "Assets/Tests/Editor/AvatarMotionArbiterTests.cs",
    "Assets/Tests/Editor/AvatarTouchInteractionTests.cs",
    "Assets/Tests/Editor/ExternalInteractionTurnTests.cs",
    "Assets/Tests/Editor/VmdActionLibraryTests.cs",
    "Assets/Tests/Editor/RuntimeDiagnosticsSnapshotTests.cs",
    "Assets/Tests/Editor/VoiceConversationControllerTests.cs",
    "Assets/Tests/Editor/BackendPairingTests.cs",
    "Assets/Tests/Editor/QuestFileImportServiceTests.cs",
    "README.md",
    "TESTING.md"
)
foreach ($file in $requiredFiles) { CheckFile $file }

$manifestPath = Join-Path $projectRoot "Packages/manifest.json"
try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    foreach ($package in @("com.unity.xr.meta-openxr", "com.unity.xr.openxr", "com.unity.xr.management", "com.unity.xr.hands", "com.unity.render-pipelines.universal", "com.unity.modules.animation", "com.unity.modules.imageconversion", "com.unity.modules.unitywebrequest", "com.unity.modules.unitywebrequesttexture")) {
        if ($manifest.dependencies.PSObject.Properties.Name -contains $package) { Pass "package: $package" }
        else { Fail "package missing: $package" }
    }
} catch { Fail "manifest JSON invalid: $($_.Exception.Message)" }

$projectVersion = Get-Content -LiteralPath (Join-Path $projectRoot "ProjectSettings/ProjectVersion.txt") -Raw
if ($projectVersion -match "(2022\.3\.62f3c1|6000\.0\.59f2)") { Pass "supported Unity baseline" }
else { Fail "unsupported Unity baseline" }

try {
    $umt = Get-Content -LiteralPath (Join-Path $projectRoot "Packages/com.candidumgames.unitymmdtools/package.json") -Raw | ConvertFrom-Json
    if ($umt.name -eq "com.candidumgames.unitymmdtools" -and $umt.version -eq "0.5.0") { Pass "embedded UMT metadata" }
    else { Fail "unexpected embedded UMT metadata" }
} catch { Fail "embedded UMT metadata invalid: $($_.Exception.Message)" }

$sourceChecks = @{
    "Packages/com.candidumgames.unitymmdtools/Runtime/PMX/PMXMeshBuilder.cs" = @("BuildAsync", "Allocator.Persistent", "YieldIfNeeded")
    "Packages/com.candidumgames.unitymmdtools/Runtime/PMX/PMXImporter.cs" = @("PMXMeshBuilder.BuildAsync", "Build Renderers")
    "Packages/com.candidumgames.unitymmdtools/Runtime/PMX/PMXTextureLoader.cs" = @("UnityWebRequestTexture.GetTexture", "Task.Run", "YieldIfNeeded")
    "Assets/Scripts/Core/AvatarMotionArbiter.cs" = @("AvatarActionSource", "AvatarMotionDecision", "imported_motion_busy", "lower_priority_than_current")
    "Assets/Scripts/Core/XRInteractionCompatibility.cs" = @("IAvatarPokeInteractor", "IAvatarPokeInteractable", "PokeInteractionLifecycle")
    "Assets/Scripts/Core/AvatarController.cs" = @("PlayActionFromSource", "CurrentActionSource", "CaptureActionPose")
    "Assets/Scripts/Core/AvatarTouchInteraction.cs" = @("TryGetContactSurface", "SetSemanticInteractionLock")
    "Assets/Scripts/Core/AvatarMmdPhysicsAdapter.cs" = @("ConfigureExternalKinematicSpheres", "SetExternalKinematicSpherePose")
    "Assets/Scripts/Core/QuestTrackedHandVisualizer.cs" = @("ShouldShowTrackedHand", "TrackedHandContactAggregator", "Physics.SyncTransforms", "ContactDiagnosticCode", "ShouldRecordContactDiagnostic", "contactDiagnosticHoverInterval")
    "Assets/Scripts/MMD/VmdActionLibrary.cs" = @("VMDReader.ReadAsync", "VMDAnimationClipConverter.ConvertAsync", "BeginPhysicsArbitration", "StopAndReturnToIdle")
    "Assets/Scripts/MMD/RuntimeMmdModelLoader.cs" = @("SelectedModelPreference", "RestoreLastModelAsync", "ParsedModelCacheCapacity", "TrimParsedModelCache", "ModelCache")
    "Assets/Scripts/Conversation/AvatarConversationPresenter.cs" = @("ApplyIntent", "PlayRecommendedDance", "PlayActionFromSource")
    "Assets/Scripts/Conversation/ConversationController.cs" = @("BeginVoiceInput", "PushVoiceAudio", "HandleTransportEvent", "backend_total")
    "Assets/Scripts/Backend/AstrBotBridge.cs" = @("TryIngestCommandJson", "CommandReceived", "sse_dispatch", "X-Embodiment-Bridge-Key", "BindSpatialContext", "UploadSpatialContext", "spatial/context", "SpatialRevisionPreferenceKey", "HasRoomData")
    "Assets/Scripts/Backend/AstrBotProtocol.cs" = @("SpatialContextRequest", "floor_count", "bed_count", "scene_capture_available", "ContentSignature")
    "Assets/Scripts/MR/SpatialCapabilityAdapter.cs" = @("HasOptionalMruk", "TryRequestSceneCapture", "SpatialCapabilitySnapshot")
    "Assets/Scripts/MR/RoomUnderstandingService.cs" = @("TryFindNearestSeat", "TryFindNearestRestingSurface", "CountsAsSeat", "BedCount", "BuildSemanticSnapshot")
    "Assets/Scripts/UI/CompanionWorldMenu.cs" = @("TouchScreenKeyboard", "RefreshExternalActions", "ImportFile", "StopCurrentAction", "CompanionMenuInputBlocker", "open_model_list", "load_first_model", "capture_first_model", "[ModelCatalog] UI page=")
    "Assets/Scripts/UI/RuntimeDiagnosticsFormatter.cs" = @("BuildPanelText", "FormatMotion", "SourceName", "AppendTimeline")
    "Assets/Scripts/Core/QuestQualitySettings.cs" = @("XRSettings.eyeTextureResolutionScale", "XRSettings.renderViewportScale", "QuestQualityPreset.Clear", "PlayerPrefs.Save")
    "Assets/Scripts/MR/QuestFileImportService.cs" = @("OpenPicker", "OnAndroidFileImported", "ExtractArchiveSafely", "MaximumExpandedArchiveBytes", "VmdActionFilePolicy")
    "Assets/Plugins/Android/BanxiaFilePicker.androidlib/src/main/java/com/lingxi/banxia/filepicker/BanxiaFilePickerActivity.java" = @("Intent.ACTION_OPEN_DOCUMENT", "Imports/Batches", "replaceAll", "UnitySendMessage")
    "Assets/Scripts/Backend/BackendPairingProtocol.cs" = @("astrbot_plugin_embodiment_bridge", "TryBuildExchangeEndpoint", "TryMigrateLegacyConfiguration", "File.Replace", "https")
    "Assets/Scripts/Backend/BackendPairingController.cs" = @("PairWithCode", "PairWithQrPayload", "PairingServerEndpoint", "ReloadConfiguration")
    "Assets/Editor/QuestMmdPlayerBuild.cs" = @("HandTracking", "GraphicsDeviceType.Vulkan", "AndroidArchitecture.ARM64", "InsecureHttpOption.AlwaysAllowed", "com.lingxi.banxia", "Builds/Banxia.apk", "ValidateNoBundledAvatarModels")
    "Assets/Editor/QuestPrivateLanManifestPostprocessor.cs" = @("usesCleartextTraffic", "horizonos.permission.HAND_TRACKING", "EnsurePermission", "ConfigureFilePickerModule")
}
foreach ($entry in $sourceChecks.GetEnumerator()) { CheckSource $entry.Key $entry.Value }

$bridgeSource = Get-Content -LiteralPath (Join-Path $projectRoot "Assets/Scripts/Backend/AstrBotBridge.cs") -Raw
if ($bridgeSource.Contains('SetRequestHeader("Authorization", "ApiKey "')) { Pass "AstrBot API-key authentication scheme" }
else { Fail "AstrBot API-key authentication scheme missing" }
if ($bridgeSource.Contains('SetRequestHeader("Authorization", "Bearer "')) { Fail "API key is sent as Dashboard bearer token" }
else { Pass "no Dashboard bearer-token misuse" }

$models = Get-ChildItem -LiteralPath (Join-Path $projectRoot "Assets") -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @(".pmx", ".pmd", ".vrm", ".glb", ".gltf") }
if ($models) { Fail "production Assets contains avatar model sources" } else { Pass "no bundled avatar models" }

$openXr = Get-Content -LiteralPath (Join-Path $projectRoot "Assets/XR/Settings/OpenXRPackageSettings.asset") -Raw
if ($openXr -match "forceRemoveInternetPermission:\s*0") { Pass "OpenXR keeps the private LAN permission by default" }
else { Pass "OpenXR default may remove INTERNET; final manifest postprocessor restores it" }
$manifestPostprocessor = Get-Content -LiteralPath (Join-Path $projectRoot "Assets/Editor/QuestPrivateLanManifestPostprocessor.cs") -Raw
if ($manifestPostprocessor.Contains('EnsurePermission(document, "android.permission.INTERNET")')) { Pass "final Android manifest restores INTERNET permission" }
else { Fail "final Android manifest does not restore INTERNET permission" }
if ($openXr -match "m_enabled:\s*1\s*\r?\n\s*nameUi: Hand Tracking Subsystem") { Pass "OpenXR hand tracking enabled" }
else { Fail "OpenXR hand tracking disabled" }

if (Get-ChildItem -LiteralPath $projectRoot -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "^(probe|tmp|test_probe)" -and $_.FullName -notmatch "\\Tests\\" }) {
    Fail "temporary probe files remain"
} else { Pass "no temporary probe files" }

if ($failures.Count -gt 0) {
    Write-Host "Automated checks failed: $($failures.Count)" -ForegroundColor Red
    if ($Strict) { exit 1 }
} else {
    Write-Host "Automated checks passed." -ForegroundColor Green
}

Write-Host "Headset-only checks remain: APK install, Quest rendering, passthrough, input, and performance."

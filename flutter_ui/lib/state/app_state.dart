import 'dart:async';

import 'package:flutter/widgets.dart';

import '../core/bridge/bridge_client.dart';
import '../core/bridge/bridge_protocol.dart';

/// Root shell top-level mode: menu (tab shell) or scene (full-screen overlay).
enum UiMode { menu, scene }

/// Global navigator handle so the system-back handler can pop pushed sub-pages
/// (e.g. 画质设置) without a BuildContext. Wired to MaterialApp in main.dart.
final GlobalKey<NavigatorState> appNavigatorKey = GlobalKey<NavigatorState>();

/// Bottom tab index (design §2.1).
enum AppTab { companion, actions, settings }

/// One entry in the 24-capped chat transcript. `at` 是 UI 侧派生的本地
/// 到达时刻（M6 时间头用），不走协议。
class ChatBubble {
  ChatBubble(this.fromUser, this.text) : at = DateTime.now();

  final bool fromUser;
  final String text;
  final DateTime at;
}

class ToastData {
  const ToastData(this.message);

  final String message;
}

/// Mutable domain slices. [AppState] is the single [ChangeNotifier]; the hot
/// cross-cutting values ([uiMode], [tab], [toast]) are [ValueNotifier]s so the
/// root shell can listen to them individually without rebuilding everything.
class EndpointTestResult {
  const EndpointTestResult({
    required this.ok,
    required this.httpCode,
    required this.elapsedMs,
    required this.errorKind,
  });

  final bool ok;
  final int httpCode;
  final int elapsedMs;
  final String errorKind;
}

class ConnectionState {
  String bridgeStatus = '未连接';
  String pairingStatus = '未连接';
  String pairingCode = '';
  String server = '';
  String serverDraft = '';
  String committedServer = '';
  bool serverDraftDirty = false;
  /// Allows plaintext only for literal private-LAN authorities.
  bool privateHttp = false;
  /// Explicit opt-in for public/remote plaintext HTTP. Kept separate from
  /// [privateHttp] so enabling LAN access never silently broadens exposure.
  bool remoteHttp = false;
  bool certificatePinConfigured = false;
  String certificatePinSummary = '';
  String certificatePinDraft = '';
  bool certificatePinDraftDirty = false;
  bool connected = false;
  // 有序候选入口列表（完整 URL，首项=最高优先级，通常为配对下发的绑定
  // 地址），由引擎经 pairing.status 下发真值，UI 不做乐观修改。
  List<String> endpoints = const <String>[];
  // 当前实际承载流量的入口完整 URL，未绑定时为空串。
  String activeEndpoint = '';
  final Map<String, String> endpointTestRequests = <String, String>{};
  final Map<String, EndpointTestResult> endpointTestResults =
      <String, EndpointTestResult>{};
}

class ConversationState {
  String state = 'idle';
  String transportStatus = '';
  String replyText = '';
  String lastError = '';
  bool monitoring = false;
  bool alwaysListening = false;
  bool recording = false;
  double voiceLevel = 0;
  final List<String> suggestedReplies = <String>[];
  final List<ChatBubble> bubbles = <ChatBubble>[];
}

class ModelLibraryState {
  List<ModelInfo> models = <ModelInfo>[];
  String? currentPath;
  String importStatus = '';
  bool loading = false;
}

class ModelLoadingState {
  String requestId = '';
  int generation = 0;
  String phase = 'idle';
  String state = 'idle';
  double fraction = 0;
  String line = '';
  String errorCode = '';
  bool pendingEntry = false;

  bool get active => pendingEntry || state == 'started' || state == 'progress';
  bool get failed => state == 'failed';
  bool get cancelled => state == 'cancelled';
  bool get gateVisible => pendingEntry || active || failed || cancelled || fraction >= 1;
}

class ActionLibraryState {
  List<VmdActionInfo> actions = <VmdActionInfo>[];
  String? playingId;
  String idlePreset = '待机';
  String expression = '默认';
  bool refreshing = false;
}

class QualityState {
  String renderPreset = 'balanced';
  String physicsPreset = 'balanced';
  int fps = 120;
  double volume = 1.0;
  String status = '';
}

class CoPresenceState {
  CoPresenceMode mode = CoPresenceMode.videoCall;
  VirtualEnvironment environment = VirtualEnvironment.nightStreet;
  bool videoCallActive = false;
  String callDuration = '00:00';
  double chromeTop = 0;
  double chromeBottom = 0;
  int chromeMeasureNonce = 0;
  bool arPlaced = false;
  bool arAvailable = false;

  /// M2 modal-sheet kind: 'modes' | 'environments' (null = closed).
  String? sheetKind;
  bool sheetOpen = false;
}

class SettingsState {
  bool hud = true;
  bool framingGrid = false;
  bool camera = false;
  bool debugMode = false;
  int targetFps = 120;
  double volume = 1.0;
}

class UpdateState {
  String phase = 'idle';
  double progress = 0;
  String version = '0.3.2.20260906';
  bool hasUpdate = false;
}

class DiagnosticsState {
  final List<String> logLines = <String>[];
  PerfSnapshot? perf;
  int poseSrcFlip = 0;
}

/// Aggregate app state: dispatches commands to the bridge, routes engine
/// events into the domain slices, and derives UI values (design §3).
class AppState extends ChangeNotifier {
  AppState(this.bridge) {
    _sub = bridge.events.listen(_handleEvent);
  }

  final BridgeClient bridge;
  late final StreamSubscription<BridgeEvent> _sub;

  final ValueNotifier<UiMode> uiMode = ValueNotifier<UiMode>(UiMode.menu);
  final ValueNotifier<AppTab> tab = ValueNotifier<AppTab>(AppTab.companion);
  final ValueNotifier<ToastData?> toast = ValueNotifier<ToastData?>(null);

  final ConnectionState connection = ConnectionState();
  final ConversationState conversation = ConversationState();
  final ModelLibraryState models = ModelLibraryState();
  final ModelLoadingState modelLoading = ModelLoadingState();
  final ActionLibraryState actions = ActionLibraryState();
  final QualityState quality = QualityState();
  final CoPresenceState copresence = CoPresenceState();
  final SettingsState settings = SettingsState();
  final UpdateState update = UpdateState();
  final DiagnosticsState diagnostics = DiagnosticsState();

  FramingSnapshot framing = FramingSnapshot.unavailable();

  Timer? _toastTimer;
  int _suggestionGeneration = 0;
  int _nextSceneLoadGeneration = 0;
  final Map<String, EndpointTestResult> _earlyEndpointTestResults =
      <String, EndpointTestResult>{};
  final Set<String> _ignoredEndpointTestRequestIds = <String>{};
  final Map<String, Timer> _endpointResultTimers = <String, Timer>{};
  bool _disposed = false;

  bool get inScene => uiMode.value == UiMode.scene;
  bool get sceneEntryPending => modelLoading.pendingEntry;
  bool get sceneInputBlocked => modelLoading.gateVisible;
  bool get connected => connection.connected;

  void _clearModelLoading() {
    modelLoading
      ..requestId = ''
      ..generation = 0
      ..phase = 'idle'
      ..state = 'idle'
      ..fraction = 0
      ..line = ''
      ..errorCode = ''
      ..pendingEntry = false;
  }

  void _applyModelLoadProgress(Map<String, dynamic>? payload) {
    final ModelLoadProgress progress = ModelLoadProgress.fromJson(payload);
    if (progress.requestId.isEmpty || progress.generation <= 0) return;
    // Only the request created by enterScene may own the scene gate. This also
    // prevents a late terminal event after Return from reopening the gate.
    if (modelLoading.requestId.isEmpty) return;
    if (modelLoading.generation > 0 &&
        progress.generation < modelLoading.generation) return;
    if (modelLoading.requestId.isNotEmpty &&
        progress.requestId != modelLoading.requestId) return;
    modelLoading
      ..requestId = progress.requestId
      ..generation = progress.generation
      ..phase = progress.phase
      ..state = progress.state
      ..fraction = progress.fraction
      ..line = progress.line
      ..errorCode = progress.errorCode;
    if (progress.state == 'failed' || progress.state == 'cancelled') {
      modelLoading.pendingEntry = false;
    }
  }

  // ── Toast ─────────────────────────────────────────────────────────────────
  void showToast(String message) {
    if (_disposed) return;
    toast.value = ToastData(message);
    _toastTimer?.cancel();
    _toastTimer = Timer(const Duration(seconds: 2), () {
      if (!_disposed && toast.value?.message == message) {
        toast.value = null;
      }
    });
  }

  // ── Bootstrap (demo / standalone) ─────────────────────────────────────────
  Future<void> bootstrap() async {
    await dispatch(Cmd.modelDiscover);
    await dispatch(Cmd.actionRefresh);
    await dispatch(Cmd.logRefresh);
  }

  // ── Command dispatch ──────────────────────────────────────────────────────
  /// Dispatches [cmd] to the bridge; returns `true` when the engine accepted
  /// it. Optimistic UI derived in [_preDispatch] is rolled back when the
  /// bridge rejects the command, so a failed `enterScene`/`returnToMenu`
  /// never leaves the shell showing a scene the engine did not enter (the
  /// engine owns state, design §6).
  Future<bool> dispatch(String cmd, [Map<String, dynamic>? payload]) async {
    if (_disposed) return false;
    final VoidCallback? rollback = _preDispatch(cmd, payload);
    final BridgeReply reply = await bridge.call(cmd, payload);
    if (_disposed) return false;
    if (!reply.ok) {
      rollback?.call();
    }
    _applyReply(cmd, reply);
    _notify();
    return reply.ok;
  }

  /// Applies command-derived optimistic UI and returns a restore callback
  /// that [dispatch] invokes when the bridge rejects the command.
  VoidCallback? _preDispatch(String cmd, Map<String, dynamic>? payload) {
    // Menu/Scene ownership is a Flutter-side derivation (design §2.1).
    switch (cmd) {
      case Cmd.copresenceEnterScene:
        if (modelLoading.gateVisible) return null;
        final int generation = ++_nextSceneLoadGeneration;
        final String requestId = 'scene-$generation';
        modelLoading
          ..requestId = requestId
          ..generation = generation
          ..phase = 'loading'
          ..state = 'started'
          ..fraction = 0
          ..line = '正在准备模型…'
          ..errorCode = ''
          ..pendingEntry = true;
        _notify();
        return () {
          if (modelLoading.generation != generation) return;
          _clearModelLoading();
        };
      case Cmd.copresenceCancelEnterScene:
        final String requestId = modelLoading.requestId;
        return () {
          if (requestId.isNotEmpty && modelLoading.requestId == requestId) {
            modelLoading
              ..state = 'cancelled'
              ..fraction = -1
              ..errorCode = 'cancelled'
              ..line = '已取消模型加载'
              ..pendingEntry = false;
          }
        };
      case Cmd.copresenceReturnToMenu:
        final UiMode previousMode = uiMode.value;
        closeSheet();
        uiMode.value = UiMode.menu;
        // The sheet is Flutter-owned UI; a rejected return keeps its cleared
        // state and only the mode is restored.
        return () => uiMode.value = previousMode;
      case Cmd.conversationSend:
      case Cmd.conversationSendWithCamera:
        final List<String> previousSuggestions =
            List<String>.from(conversation.suggestedReplies);
        final int generation = ++_suggestionGeneration;
        conversation.suggestedReplies.clear();
        _notify();
        return () {
          if (_suggestionGeneration != generation ||
              conversation.suggestedReplies.isNotEmpty) {
            return;
          }
          conversation.suggestedReplies
            ..clear()
            ..addAll(previousSuggestions);
          _notify();
        };
      case Cmd.pairingClearBinding:
        final String previousServer = connection.server;
        final String previousDraft = connection.serverDraft;
        final String previousCommitted = connection.committedServer;
        final bool previousDraftDirty = connection.serverDraftDirty;
        final bool previousConnected = connection.connected;
        final String previousPairingStatus = connection.pairingStatus;
        final String previousPairingCode = connection.pairingCode;
        final bool previousPrivateHttp = connection.privateHttp;
        final bool previousRemoteHttp = connection.remoteHttp;
        final bool previousPinConfigured = connection.certificatePinConfigured;
        final String previousPinSummary = connection.certificatePinSummary;
        final String previousPinDraft = connection.certificatePinDraft;
        final bool previousPinDraftDirty = connection.certificatePinDraftDirty;
        final List<String> previousEndpoints =
            List<String>.from(connection.endpoints);
        final String previousActiveEndpoint = connection.activeEndpoint;
        connection.server = '';
        connection.serverDraft = '';
        connection.committedServer = '';
        connection.serverDraftDirty = false;
        connection.connected = false;
        connection.pairingStatus = '未连接';
        connection.pairingCode = '';
        connection.certificatePinConfigured = false;
        connection.certificatePinSummary = '';
        connection.certificatePinDraft = '';
        connection.certificatePinDraftDirty = false;
        connection.endpoints = const <String>[];
        connection.activeEndpoint = '';
        _notify();
        return () {
          connection.server = previousServer;
          connection.serverDraft = previousDraft;
          connection.committedServer = previousCommitted;
          connection.serverDraftDirty = previousDraftDirty;
          connection.connected = previousConnected;
          connection.pairingStatus = previousPairingStatus;
          connection.pairingCode = previousPairingCode;
          connection.privateHttp = previousPrivateHttp;
          connection.remoteHttp = previousRemoteHttp;
          connection.certificatePinConfigured = previousPinConfigured;
          connection.certificatePinSummary = previousPinSummary;
          connection.certificatePinDraft = previousPinDraft;
          connection.certificatePinDraftDirty = previousPinDraftDirty;
          connection.endpoints = previousEndpoints;
          connection.activeEndpoint = previousActiveEndpoint;
          _notify();
        };
      default:
        return null;
    }
  }

  void _applyReply(String cmd, BridgeReply reply) {
    if (!reply.ok) {
      final message = reply.error?.trim();
      if (message != null && message.isNotEmpty) showToast(message);
      return;
    }
    final data = reply.data;
    switch (cmd) {
      case Cmd.modelDiscover:
        _applyModels(data?['models']);
        break;
      case Cmd.actionRefresh:
        _applyActions(data?['actions']);
        break;
      case Cmd.logRefresh:
        _applyLogLines(data?['lines']);
        break;
      case Cmd.updateCheck:
        _applyUpdateStatus(data?['status']);
        break;
      default:
        return;
    }
  }

  // ── Engine event routing ──────────────────────────────────────────────────
  void eventsForTest(BridgeEvent event) => _handleEvent(event);

  void _handleEvent(BridgeEvent event) {
    final p = event.payload;
    switch (event.name) {
      case Evt.connectionChanged:
        connection.connected = p?['connected'] == true;
        if (p?['bridgeStatus'] is String) {
          connection.bridgeStatus = p!['bridgeStatus'] as String;
        }
        break;
      case Evt.pairingEndpointTest:
        final String requestId = _str(p?['requestId']);
        if (requestId.isEmpty) break;
        String? endpoint;
        for (final MapEntry<String, String> entry
            in connection.endpointTestRequests.entries) {
          if (entry.value == requestId) {
            endpoint = entry.key;
            break;
          }
        }
        if (_ignoredEndpointTestRequestIds.remove(requestId)) break;
        final EndpointTestResult result = EndpointTestResult(
          ok: p?['ok'] == true,
          httpCode: _int(p?['httpCode']),
          elapsedMs: _int(p?['elapsedMs']),
          errorKind: _str(p?['errorKind']),
        );
        // A real host normally delivers the command reply before the event, but
        // retain an early result briefly so transport reordering is harmless.
        if (endpoint == null) {
          _earlyEndpointTestResults[requestId] = result;
          Timer(const Duration(seconds: 5), () {
            if (!_disposed) _earlyEndpointTestResults.remove(requestId);
          });
          if (_earlyEndpointTestResults.length > 16) {
            _earlyEndpointTestResults
                .remove(_earlyEndpointTestResults.keys.first);
          }
          break;
        }
        connection.endpointTestRequests.remove(endpoint);
        _storeEndpointTestResult(endpoint, result);
        break;
      case Evt.pairingStatus:
        if (p?['status'] is String) {
          connection.pairingStatus = p!['status'] as String;
        }
        if (p?['server'] is String) {
          final String server = p!['server'] as String;
          connection.server = server;
          // A late status frame must not change the rollback baseline or the
          // input field while the user is editing an uncommitted address.
          if (!connection.serverDraftDirty) {
            connection.committedServer = server;
            connection.serverDraft = server;
            connection.serverDraftDirty = false;
          }
        }
        if (p?['codeLen'] is num) {
          final int codeLen =
              (p!['codeLen'] as num).toInt().clamp(0, 6).toInt();
          if (codeLen == 0) connection.pairingCode = '';
        }
        if (p?['privateHttp'] is bool) {
          connection.privateHttp = p!['privateHttp'] as bool;
        }
        if (p?['remoteHttp'] is bool) {
          connection.remoteHttp = p!['remoteHttp'] as bool;
        }
        if (p?['certificatePinConfigured'] is bool) {
          connection.certificatePinConfigured =
              p!['certificatePinConfigured'] as bool;
        }
        if (p?['certificatePinSummary'] is String) {
          connection.certificatePinSummary =
              p!['certificatePinSummary'] as String;
        }
        // 入口优先级列表：引擎下发有序完整 URL（首项=最高优先级）。
        if (p?['endpoints'] is List) {
          connection.endpoints = (p!['endpoints'] as List)
              .whereType<String>()
              .toList(growable: false);
        }
        if (p?['activeEndpoint'] is String) {
          connection.activeEndpoint = p!['activeEndpoint'] as String;
        }
        break;
      case Evt.conversationState:
        if (p?['state'] is String) conversation.state = p!['state'] as String;
        if (p?['transportStatus'] is String) {
          conversation.transportStatus = p!['transportStatus'] as String;
        }
        if (p?['lastError'] is String) {
          conversation.lastError = p!['lastError'] as String;
        }
        break;
      case Evt.conversationTranscript:
        _pushBubble(true, p?['text']);
        break;
      case Evt.conversationReply:
        conversation.replyText = _str(p?['text']);
        _pushBubble(false, p?['text']);
        break;
      case Evt.conversationSuggestions:
        ++_suggestionGeneration;
        conversation.suggestedReplies
          ..clear()
          ..addAll((p?['suggestions'] is List
                  ? p!['suggestions'] as List
                  : const <dynamic>[])
              .map(_str)
              .where((String value) => value.trim().isNotEmpty)
              .take(3));
        break;
      case Evt.modelLoadProgress:
        _applyModelLoadProgress(p);
        break;
      case Evt.modelUpdated:
        _applyModels(p?['models']);
        if (p?['currentPath'] is String) {
          models.currentPath = p!['currentPath'] as String;
        }
        break;
      case Evt.modelImportStatus:
        models.importStatus = _str(p?['status']);
        // Structured model.loadProgress owns the loading gate and line display;
        // do not turn every progress point into a transient toast.
        if (_str(p?['requestId']).isEmpty && models.importStatus.isNotEmpty) {
          showToast(models.importStatus);
        }
        break;
      case Evt.actionUpdated:
        _applyActions(p?['actions']);
        break;
      case Evt.actionPlaybackChanged:
        actions.playingId = p?['playingId'] as String?;
        break;
      case Evt.qualityChanged:
        if (p?['renderPreset'] is String) {
          quality.renderPreset = p!['renderPreset'] as String;
        }
        if (p?['physicsPreset'] is String) {
          quality.physicsPreset = p!['physicsPreset'] as String;
        }
        if (p?['targetFps'] is num &&
            [30, 60, 120].contains((p!['targetFps'] as num).toInt())) {
          final int targetFps = (p['targetFps'] as num).toInt();
          quality.fps = targetFps;
          settings.targetFps = targetFps;
        }
        if (p?['volume'] is num) {
          final double volume = (p!['volume'] as num).toDouble();
          if (volume.isFinite && volume >= 0 && volume <= 1) {
            quality.volume = volume;
            settings.volume = volume;
          }
        }
        if (p?['debugMode'] is bool) {
          settings.debugMode = p!['debugMode'] as bool;
        }
        quality.status = _str(p?['status']);
        break;
      case Evt.copresenceMode:
        copresence.mode =
            CoPresenceMode.fromValue(p?['mode'] as String?) ?? copresence.mode;
        copresence.environment =
            VirtualEnvironment.fromValue(p?['environment'] as String?) ??
                copresence.environment;
        if (p?['videoCallActive'] is bool) {
          copresence.videoCallActive = p!['videoCallActive'] as bool;
        }
        if (p?['arAvailable'] is bool) {
          copresence.arAvailable = p!['arAvailable'] as bool;
        }
        if (p?['arPlaced'] is bool) {
          copresence.arPlaced = p!['arPlaced'] as bool;
        }
        // Engine-side scene truth is authoritative (QA can drive the engine
        // directly, bypassing Dart's dispatch path entirely).
        if (p?['inScene'] is bool) {
          final bool engineInScene = p!['inScene'] as bool;
          if (engineInScene) {
            uiMode.value = UiMode.scene;
            modelLoading.pendingEntry = false;
            if (modelLoading.state == 'completed' ||
                modelLoading.fraction >= 1) {
              _clearModelLoading();
            }
          } else {
            uiMode.value = UiMode.menu;
            _clearModelLoading();
          }
        }
        break;
      case Evt.copresenceCallTimer:
        copresence.callDuration = _str(p?['durationText']);
        break;
      case Evt.copresenceChromeInsetsNeeded:
        copresence.chromeMeasureNonce++;
        break;
      case Evt.copresencePlacementChanged:
        if (p?['arPlaced'] is bool) {
          copresence.arPlaced = p!['arPlaced'] as bool;
        }
        break;
      case Evt.framingAnchors:
        framing = FramingSnapshot.fromEvent(p);
        break;
      case Evt.voiceStatus:
        conversation.monitoring = p?['monitoring'] == true;
        conversation.alwaysListening = p?['alwaysListening'] == true;
        conversation.recording = p?['recording'] == true;
        conversation.voiceLevel = _num(p?['level']);
        break;
      case Evt.updateStatus:
        _applyUpdateStatus(p);
        break;
      case Evt.logUpdated:
        _applyLogLines(p?['lines']);
        break;
      case Evt.performanceSnapshot:
        diagnostics.perf = p == null ? null : PerfSnapshot.fromJson(p);
        break;
      case Evt.toast:
        showToast(_str(p?['message']));
        break;
      case Evt.settingsState:
        // Unity (PlayerPrefs) owns the persisted truth; the shell's switches
        // mirror it, including the values restored at engine startup.
        if (p?['hud'] is bool) settings.hud = p!['hud'] as bool;
        if (p?['framingGrid'] is bool) {
          settings.framingGrid = p!['framingGrid'] as bool;
        }
        if (p?['camera'] is bool) settings.camera = p!['camera'] as bool;
        if (p?['debugMode'] is bool)
          settings.debugMode = p!['debugMode'] as bool;
        break;
      case Evt.systemBack:
        handleSystemBack();
        break;
      default:
        break;
    }
    _notify();
  }

  // ── System back key (pushed by the Android host as `system.back`) ─────────
  /// Semantics: close the sheet → leave the scene → back to the home tab →
  /// let the system background the app. Without this the panel window swallows
  /// KEYCODE_BACK and the back key is a global no-op (2026-09 fix).
  void handleSystemBack() {
    if (copresence.sheetOpen) {
      closeSheet();
      return;
    }
    if (sceneEntryPending || modelLoading.gateVisible) {
      unawaited(cancelSceneLoad());
      return;
    }
    if (inScene) {
      unawaited(returnToMenu());
      return;
    }
    // Pushed sub-pages (画质设置等) pop before the tab fallback.
    final NavigatorState? nav = appNavigatorKey.currentState;
    if (nav != null && nav.canPop()) {
      nav.pop();
      return;
    }
    if (tab.value != AppTab.companion) {
      switchTab(AppTab.companion);
      return;
    }
    // SystemNavigator.pop no-ops in the panel-hosted engine (no activity
    // binding), so backgrounding goes through the Unity activity.
    unawaited(dispatch(Cmd.systemMinimize));
  }

  // ── Scene gesture passthrough (2026-09 fix) ───────────────────────────────
  /// The panel window eats every touch so Unity never sees one; gestures are
  /// recognized by the Flutter scene layer and forwarded in physical pixels
  /// (same convention as [arPlaceAt]).
  double get _devicePixelRatio {
    final views = WidgetsBinding.instance.platformDispatcher.views;
    return views.isEmpty ? 1.0 : views.first.devicePixelRatio;
  }

  void sceneOrbitBy(Offset logicalDelta) {
    if (sceneInputBlocked) return;
    unawaited(dispatch(Cmd.sceneOrbit, <String, dynamic>{
      'dx': logicalDelta.dx * _devicePixelRatio,
      'dy': logicalDelta.dy * _devicePixelRatio,
    }));
  }

  void sceneZoomBy(double scaleFactor) {
    if (sceneInputBlocked || !scaleFactor.isFinite || scaleFactor <= 0) return;
    unawaited(dispatch(Cmd.sceneZoom, <String, dynamic>{'scale': scaleFactor}));
  }

  void scenePanAvatarBy(Offset logicalDelta) {
    if (sceneInputBlocked) return;
    unawaited(dispatch(Cmd.scenePanAvatar, <String, dynamic>{
      'dx': logicalDelta.dx * _devicePixelRatio,
      'dy': logicalDelta.dy * _devicePixelRatio,
    }));
  }

  void _pushBubble(bool fromUser, dynamic text) {
    final value = _str(text);
    if (value.isEmpty) return;
    conversation.bubbles.add(ChatBubble(fromUser, value));
    // 24-entry cap (design §2.3).
    while (conversation.bubbles.length > 24) {
      conversation.bubbles.removeAt(0);
    }
  }

  void _applyModels(dynamic raw) {
    if (raw is! List) return;
    models.models = raw
        .whereType<Map>()
        .map((m) => ModelInfo.fromJson(Map<String, dynamic>.from(m)))
        .toList();
  }

  void _applyActions(dynamic raw) {
    if (raw is! List) return;
    actions.actions = raw
        .whereType<Map>()
        .map((m) => VmdActionInfo.fromJson(Map<String, dynamic>.from(m)))
        .toList();
  }

  void _applyLogLines(dynamic raw) {
    if (raw is! List) return;
    diagnostics.logLines
      ..clear()
      ..addAll(raw.map((e) => e.toString()));
  }

  void _applyUpdateStatus(dynamic raw) {
    if (raw is! Map) return;
    update.phase = _str(raw['phase']);
    update.progress = _num(raw['progress']);
    if (raw['version'] != null) update.version = _str(raw['version']);
    update.hasUpdate = raw['hasUpdate'] == true;
  }

  // ── High-level UI actions (M2/M3 semantics live here) ─────────────────────
  Future<void> enterScene([String? path]) async {
    final String requestId = modelLoading.requestId.isEmpty
        ? 'scene-${_nextSceneLoadGeneration + 1}'
        : modelLoading.requestId;
    await dispatch(Cmd.copresenceEnterScene, <String, dynamic>{
      if (path != null) 'path': path,
      'requestId': requestId,
      'generation': modelLoading.generation,
    });
  }

  Future<void> returnToMenu() async {
    if (sceneEntryPending) {
      await cancelSceneLoad();
      return;
    }
    await dispatch(Cmd.copresenceReturnToMenu);
  }

  Future<void> cancelSceneLoad() async {
    if (!modelLoading.gateVisible) return;
    await dispatch(Cmd.copresenceCancelEnterScene, <String, dynamic>{
      'requestId': modelLoading.requestId,
      'generation': modelLoading.generation,
    });
    await dispatch(Cmd.copresenceReturnToMenu);
  }

  Future<void> switchMode(CoPresenceMode mode) async {
    closeSheet();
    final bool ok = await dispatch(
        Cmd.copresenceSwitchMode, <String, dynamic>{'mode': mode.value});
    // Only announce success the engine confirmed; the bridge error toast on
    // failure already informed the user.
    if (ok) showToast('已切换：${mode.label}');
  }

  Future<void> switchEnvironment(VirtualEnvironment env) async {
    final bool ok = await dispatch(
        Cmd.copresenceSwitchEnvironment, <String, dynamic>{'env': env.value});
    if (ok) closeSheet();
  }

  Future<void> setTargetFps(int fps) async {
    if (![30, 60, 120].contains(fps)) return;
    final int previous = settings.targetFps;
    settings.targetFps = fps;
    _notify();
    if (!await dispatch(Cmd.settingsTargetFps, <String, dynamic>{'fps': fps})) {
      settings.targetFps = previous;
      _notify();
    }
  }

  Future<void> setVolume(double value) async {
    final double v = value.clamp(0.0, 1.0).toDouble();
    final double previous = settings.volume;
    settings.volume = v;
    _notify();
    if (!await dispatch(Cmd.settingsVolume, <String, dynamic>{'v': v})) {
      settings.volume = previous;
      _notify();
    }
  }

  Future<void> updateChromeInsets(double top, double bottom) async {
    final views = WidgetsBinding.instance.platformDispatcher.views;
    final ratio = views.isEmpty ? 1.0 : views.first.devicePixelRatio;
    if (!top.isFinite || !bottom.isFinite || top < 0 || bottom <= top) return;
    final physicalTop = top * ratio;
    final physicalBottom = bottom * ratio;
    if ((physicalTop - copresence.chromeTop).abs() < 0.5 &&
        (physicalBottom - copresence.chromeBottom).abs() < 0.5) {
      return;
    }
    copresence.chromeTop = physicalTop;
    copresence.chromeBottom = physicalBottom;
    _notify();
    await dispatch(Cmd.copresenceSetChromeInsets,
        <String, dynamic>{'top': physicalTop, 'bottom': physicalBottom});
  }

  /// AR 放置：把 Flutter 逻辑像素（top-origin）点按坐标换算为物理像素后经
  /// `copresence.arPlace{x,y}` 交给引擎，与 [updateChromeInsets] 同一坐标
  /// 约定（物理像素、原点在左上）。
  Future<void> arPlaceAt(Offset position) async {
    if (sceneInputBlocked) return;
    final views = WidgetsBinding.instance.platformDispatcher.views;
    final double ratio = views.isEmpty ? 1.0 : views.first.devicePixelRatio;
    await dispatch(Cmd.copresenceArPlace, <String, dynamic>{
      'x': position.dx * ratio,
      'y': position.dy * ratio,
    });
  }

  // ── M2 modal sheet ownership ──────────────────────────────────────────────
  void openSheet(String kind) {
    if (sceneInputBlocked) return;
    copresence.sheetKind = kind;
    copresence.sheetOpen = true;
    _notify();
  }

  void closeSheet() {
    copresence.sheetKind = null;
    copresence.sheetOpen = false;
    _notify();
  }

  void toggleSheet(String kind) {
    if (copresence.sheetOpen && copresence.sheetKind == kind) {
      closeSheet();
    } else {
      openSheet(kind);
    }
  }

  // ── Pairing (six-digit semantics, M3) ─────────────────────────────────────
  void appendPairingDigit(String digit) {
    if (connection.pairingCode.length >= 6 || digit.length != 1) return;
    final String previous = connection.pairingCode;
    connection.pairingCode += digit;
    _notify();
    unawaited(dispatch(Cmd.pairingDigit, <String, dynamic>{
      'op': 'append',
      'digit': digit,
    }).then((bool ok) {
      // The engine owns the 6-digit buffer; reject a mirrored digit the
      // bridge refused so the shell never shows a phantom pairing code.
      if (!ok) {
        connection.pairingCode = previous;
        _notify();
      }
    }));
  }

  void removePairingDigit() {
    if (connection.pairingCode.isEmpty) return;
    final String previous = connection.pairingCode;
    connection.pairingCode =
        connection.pairingCode.substring(0, connection.pairingCode.length - 1);
    _notify();
    unawaited(dispatch(Cmd.pairingDigit, <String, dynamic>{'op': 'remove'})
        .then((bool ok) {
      if (!ok) {
        connection.pairingCode = previous;
        _notify();
      }
    }));
  }

  void clearPairingCode() {
    final String previous = connection.pairingCode;
    connection.pairingCode = '';
    _notify();
    unawaited(dispatch(Cmd.pairingDigit, <String, dynamic>{'op': 'clear'})
        .then((bool ok) {
      if (!ok) {
        connection.pairingCode = previous;
        _notify();
      }
    }));
  }

  Future<void> submitPairing() async {
    if (connection.pairingCode.length != 6) {
      showToast('请输入完整的 6 位配对码');
      return;
    }
    final String server = connection.serverDraft.trim();
    if (server.isEmpty) {
      connection.serverDraft = connection.committedServer;
      connection.serverDraftDirty = false;
      _notify();
      showToast('请输入服务器地址');
      return;
    }
    if (!await commitPairingServer(connection.serverDraft)) {
      return;
    }
    // Progress toast first; the engine's own status/toast events and the
    // bridge error toast on failure replace it, so a failed pair never ends
    // with an optimistic "正在配对…" success signal.
    showToast('正在配对…');
    await dispatch(Cmd.pairingPair);
  }

  Future<bool> commitPairingServer(String value) async {
    final String previousServer = connection.committedServer;
    final String previousEngineServer = connection.server;
    final String server = value;
    if (server.trim() != server) {
      showToast('服务器地址前后不能包含空白');
      return false;
    }
    if (server.isEmpty) {
      connection.server = previousEngineServer;
      connection.serverDraft = previousServer;
      connection.serverDraftDirty = false;
      _notify();
      return false;
    }
    if (server == previousServer) {
      connection.server = server;
      connection.serverDraft = server;
      connection.serverDraftDirty = false;
      _notify();
      return true;
    }
    // Guard the rollback baseline from pairing.status frames emitted while the
    // bridge is applying the new endpoint.
    connection.serverDraft = server;
    connection.serverDraftDirty = true;
    _notify();
    if (!await dispatch(Cmd.pairingSetServer, <String, dynamic>{
      'server': server,
    })) {
      connection.server = previousEngineServer;
      connection.serverDraft = previousServer;
      connection.serverDraftDirty = false;
      _notify();
      return false;
    }
    connection.server = server;
    connection.serverDraft = server;
    connection.committedServer = server;
    connection.serverDraftDirty = false;
    _notify();
    return true;
  }

  void updatePairingServerDraft(String value) {
    connection.serverDraft = value;
    connection.serverDraftDirty = value != connection.committedServer;
    _notify();
  }

  void updateCertificatePinDraft(String value) {
    connection.certificatePinDraft = value;
    connection.certificatePinDraftDirty = true;
    _notify();
  }

  Future<bool> commitCertificatePin() async {
    final String value = connection.certificatePinDraft;
    if (value.isNotEmpty && !RegExp(r'^[0-9a-fA-F]{64}$').hasMatch(value)) {
      showToast('证书指纹必须是 64 位十六进制字符串');
      return false;
    }
    if (!await dispatch(Cmd.pairingSetCertificatePin,
        PairingCertificatePinPayload(value).toJson())) {
      return false;
    }
    connection.certificatePinDraft = '';
    connection.certificatePinDraftDirty = false;
    _notify();
    return true;
  }

  // ── 入口优先级列表（pairing.endpoint*）─────────────────────────────────
  // 三个操作都不做乐观更新：引擎接受后回放 pairing.status 真值，失败原因
  // （中文）由 dispatch 统一 toast。addEndpoint 返回是否被引擎接受，便于
  // 输入框仅在成功时清空。
  Future<bool> addEndpoint(String url) async {
    final String value = url.trim();
    if (value.isEmpty) {
      showToast('请输入入口地址');
      return false;
    }
    return dispatch(Cmd.pairingEndpointAdd, <String, dynamic>{'url': value});
  }

  Future<void> removeEndpoint(String url) async {
    final String endpoint = url.trim();
    if (endpoint.isEmpty) return;
    final String? requestId = connection.endpointTestRequests[endpoint];
    if (!await dispatch(
        Cmd.pairingEndpointRemove, <String, dynamic>{'url': endpoint})) {
      return;
    }
    if (requestId != null) {
      _ignoredEndpointTestRequestIds.add(requestId);
    }
    connection.endpointTestRequests.remove(endpoint);
    connection.endpointTestResults.remove(endpoint);
    _endpointResultTimers.remove(endpoint)?.cancel();
    _notify();
  }

  /// [offset] 仅允许 -1（上移）/ 1（下移），其余值直接丢弃。
  Future<void> moveEndpoint(String url, int offset) async {
    if (url.trim().isEmpty || (offset != -1 && offset != 1)) return;
    await dispatch(Cmd.pairingEndpointMove,
        <String, dynamic>{'url': url, 'offset': offset});
  }

  Future<bool> testEndpoint(String url) async {
    final String endpoint = url.trim();
    if (endpoint.isEmpty ||
        connection.endpointTestRequests.containsKey(endpoint)) {
      return false;
    }
    connection.endpointTestResults.remove(endpoint);
    _notify();
    final BridgeReply reply = await bridge
        .call(Cmd.pairingEndpointTest, <String, dynamic>{'url': endpoint});
    if (_disposed) return false;
    if (!reply.ok) {
      showToast(reply.error ?? '无法启动入口测试');
      _notify();
      return false;
    }
    final String requestId = _str(reply.data?['requestId']);
    if (requestId.isEmpty) {
      showToast('入口测试未返回请求编号');
      _notify();
      return false;
    }
    connection.endpointTestRequests[endpoint] = requestId;
    final EndpointTestResult? earlyResult =
        _earlyEndpointTestResults.remove(requestId);
    if (earlyResult != null) {
      connection.endpointTestRequests.remove(endpoint);
      _storeEndpointTestResult(endpoint, earlyResult);
    }
    _notify();
    return true;
  }

  bool isEndpointTesting(String url) =>
      connection.endpointTestRequests.containsKey(url);

  EndpointTestResult? endpointTestResult(String url) =>
      connection.endpointTestResults[url];

  void _storeEndpointTestResult(String endpoint, EndpointTestResult result) {
    _endpointResultTimers.remove(endpoint)?.cancel();
    connection.endpointTestResults[endpoint] = result;
    _endpointResultTimers[endpoint] = Timer(const Duration(seconds: 5), () {
      if (_disposed) return;
      connection.endpointTestResults.remove(endpoint);
      _endpointResultTimers.remove(endpoint);
      _notify();
    });
  }

  // ── Settings helpers ──────────────────────────────────────────────────────
  Future<void> toggleSetting(String key, bool value) async {
    if (key == 'debugMode') {
      if (await dispatch(
          Cmd.settingsToggle, <String, dynamic>{'key': key, 'value': value})) {
        settings.debugMode = value;
        _notify();
      }
      return;
    }
    switch (key) {
      case 'hud':
        settings.hud = value;
        break;
      case 'framingGrid':
        settings.framingGrid = value;
        break;
      case 'camera':
        settings.camera = value;
        break;
      default:
        break;
    }
    _notify();
    await dispatch(
        Cmd.settingsToggle, <String, dynamic>{'key': key, 'value': value});
  }

  void switchTab(AppTab next) {
    tab.value = next;
    _notify();
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _earlyEndpointTestResults.clear();
    _ignoredEndpointTestRequestIds.clear();
    for (final Timer timer in _endpointResultTimers.values) {
      timer.cancel();
    }
    _endpointResultTimers.clear();
    _toastTimer?.cancel();
    _sub.cancel();
    uiMode.dispose();
    tab.dispose();
    toast.dispose();
    super.dispose();
  }
}

String _str(dynamic v) => v == null ? '' : v.toString();

int _int(dynamic v) {
  if (v is num) return v.toInt();
  if (v is String) return int.tryParse(v) ?? 0;
  return 0;
}

double _num(dynamic v) {
  if (v is num) return v.toDouble();
  if (v is String) return double.tryParse(v) ?? 0;
  return 0;
}

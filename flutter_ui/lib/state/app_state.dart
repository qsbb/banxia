import 'dart:async';

import 'package:flutter/widgets.dart';

import '../core/bridge/bridge_client.dart';
import '../core/bridge/bridge_protocol.dart';

/// Root shell top-level mode: menu (tab shell) or scene (full-screen overlay).
enum UiMode { menu, scene }

/// Bottom tab index (design §2.1).
enum AppTab { companion, chat, actions, settings }

/// One entry in the 24-capped chat transcript.
class ChatBubble {
  const ChatBubble(this.fromUser, this.text);

  final bool fromUser;
  final String text;
}

class ToastData {
  const ToastData(this.message);

  final String message;
}

/// Mutable domain slices. [AppState] is the single [ChangeNotifier]; the hot
/// cross-cutting values ([uiMode], [tab], [toast]) are [ValueNotifier]s so the
/// root shell can listen to them individually without rebuilding everything.
class ConnectionState {
  String bridgeStatus = '未连接';
  String pairingStatus = '未连接';
  String pairingCode = '';
  String server = '';
  bool privateHttp = false;
  bool connected = false;
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
  final List<ChatBubble> bubbles = <ChatBubble>[];
}

class ModelLibraryState {
  List<ModelInfo> models = <ModelInfo>[];
  String? currentPath;
  String importStatus = '';
  bool loading = false;
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
  int fps = 60;
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
  int targetFps = 60;
  double volume = 1.0;
}

class UpdateState {
  String phase = 'idle';
  double progress = 0;
  String version = '0.3.2';
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
  final ActionLibraryState actions = ActionLibraryState();
  final QualityState quality = QualityState();
  final CoPresenceState copresence = CoPresenceState();
  final SettingsState settings = SettingsState();
  final UpdateState update = UpdateState();
  final DiagnosticsState diagnostics = DiagnosticsState();

  FramingSnapshot framing = FramingSnapshot.unavailable();

  Timer? _toastTimer;
  bool _disposed = false;

  bool get inScene => uiMode.value == UiMode.scene;
  bool get connected => connection.connected;

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
        final UiMode previousMode = uiMode.value;
        final bool previousActive = copresence.videoCallActive;
        uiMode.value = UiMode.scene;
        copresence.videoCallActive =
            copresence.mode == CoPresenceMode.videoCall;
        return () {
          uiMode.value = previousMode;
          copresence.videoCallActive = previousActive;
        };
      case Cmd.copresenceReturnToMenu:
        final UiMode previousMode = uiMode.value;
        closeSheet();
        uiMode.value = UiMode.menu;
        // The sheet is Flutter-owned UI; a rejected return keeps its cleared
        // state and only the mode is restored.
        return () => uiMode.value = previousMode;
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
      case Cmd.actionRefresh:
        _applyActions(data?['actions']);
      case Cmd.logRefresh:
        _applyLogLines(data?['lines']);
      case Cmd.updateCheck:
        _applyUpdateStatus(data?['status']);
      default:
        return;
    }
  }

  // ── Engine event routing ──────────────────────────────────────────────────
  void _handleEvent(BridgeEvent event) {
    final p = event.payload;
    switch (event.name) {
      case Evt.connectionChanged:
        connection.connected = p?['connected'] == true;
        if (p?['bridgeStatus'] is String) {
          connection.bridgeStatus = p!['bridgeStatus'] as String;
        }
      case Evt.pairingStatus:
        if (p?['status'] is String) {
          connection.pairingStatus = p!['status'] as String;
        }
        if (p?['privateHttp'] is bool) {
          connection.privateHttp = p!['privateHttp'] as bool;
        }
      case Evt.conversationState:
        if (p?['state'] is String) conversation.state = p!['state'] as String;
        if (p?['transportStatus'] is String) {
          conversation.transportStatus = p!['transportStatus'] as String;
        }
        if (p?['lastError'] is String) {
          conversation.lastError = p!['lastError'] as String;
        }
      case Evt.conversationTranscript:
        _pushBubble(true, p?['text']);
      case Evt.conversationReply:
        conversation.replyText = _str(p?['text']);
        _pushBubble(false, p?['text']);
      case Evt.modelUpdated:
        _applyModels(p?['models']);
        if (p?['currentPath'] is String) {
          models.currentPath = p!['currentPath'] as String;
        }
      case Evt.modelImportStatus:
        models.importStatus = _str(p?['status']);
        if (models.importStatus.isNotEmpty) showToast(models.importStatus);
      case Evt.actionUpdated:
        _applyActions(p?['actions']);
      case Evt.actionPlaybackChanged:
        actions.playingId = p?['playingId'] as String?;
      case Evt.qualityChanged:
        if (p?['renderPreset'] is String) {
          quality.renderPreset = p!['renderPreset'] as String;
        }
        if (p?['physicsPreset'] is String) {
          quality.physicsPreset = p!['physicsPreset'] as String;
        }
        quality.status = _str(p?['status']);
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
      case Evt.copresenceCallTimer:
        copresence.callDuration = _str(p?['durationText']);
      case Evt.copresenceChromeInsetsNeeded:
        copresence.chromeMeasureNonce++;
      case Evt.copresencePlacementChanged:
        if (p?['arPlaced'] is bool) {
          copresence.arPlaced = p!['arPlaced'] as bool;
        }
      case Evt.framingAnchors:
        framing = FramingSnapshot.fromEvent(p);
      case Evt.voiceStatus:
        conversation.monitoring = p?['monitoring'] == true;
        conversation.alwaysListening = p?['alwaysListening'] == true;
        conversation.recording = p?['recording'] == true;
        conversation.voiceLevel = _num(p?['level']);
      case Evt.updateStatus:
        _applyUpdateStatus(p);
      case Evt.logUpdated:
        _applyLogLines(p?['lines']);
      case Evt.performanceSnapshot:
        diagnostics.perf = p == null ? null : PerfSnapshot.fromJson(p);
      case Evt.toast:
        showToast(_str(p?['message']));
      default:
        break;
    }
    _notify();
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
    await dispatch(Cmd.copresenceEnterScene,
        <String, dynamic>{if (path != null) 'path': path});
  }

  Future<void> returnToMenu() async {
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
    final views = WidgetsBinding.instance.platformDispatcher.views;
    final double ratio = views.isEmpty ? 1.0 : views.first.devicePixelRatio;
    await dispatch(Cmd.copresenceArPlace, <String, dynamic>{
      'x': position.dx * ratio,
      'y': position.dy * ratio,
    });
  }

  // ── M2 modal sheet ownership ──────────────────────────────────────────────
  void openSheet(String kind) {
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
    // Progress toast first; the engine's own status/toast events and the
    // bridge error toast on failure replace it, so a failed pair never ends
    // with an optimistic "正在配对…" success signal.
    showToast('正在配对…');
    await dispatch(Cmd.pairingPair);
  }

  // ── Settings helpers ──────────────────────────────────────────────────────
  Future<void> toggleSetting(String key, bool value) async {
    switch (key) {
      case 'hud':
        settings.hud = value;
      case 'framingGrid':
        settings.framingGrid = value;
      case 'camera':
        settings.camera = value;
      default:
        break;
    }
    _notify();
    await dispatch(
        Cmd.settingsToggle, <String, dynamic>{'key': key, 'value': value});
  }

  void switchTab(AppTab next) {
    tab.value = next;
  }

  void _notify() {
    if (!_disposed) notifyListeners();
  }

  @override
  void dispose() {
    _disposed = true;
    _toastTimer?.cancel();
    _sub.cancel();
    uiMode.dispose();
    tab.dispose();
    toast.dispose();
    super.dispose();
  }
}

String _str(dynamic v) => v == null ? '' : v.toString();

double _num(dynamic v) {
  if (v is num) return v.toDouble();
  if (v is String) return double.tryParse(v) ?? 0;
  return 0;
}

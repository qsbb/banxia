import 'dart:convert';

/// Bridge protocol types and wire-name constants (design §6).
///
/// The Flutter shell never owns engine state: every UI mutation becomes a
/// command ([Cmd]), and the engine pushes truth back as events ([Evt]) plus
/// `reply` envelopes. This file keeps the wire schema and the JSON models the
/// shell consumes. The bridge only ever carries pure JSON — never widget trees.

/// Command names (Flutter → engine).
abstract final class Cmd {
  Cmd._();
  static const String modelDiscover = 'model.discover';
  static const String modelLoad = 'model.load';
  static const String modelDelete = 'model.delete';
  static const String modelImport = 'model.import';
  static const String actionRefresh = 'action.refresh';
  static const String actionPlay = 'action.play';
  static const String actionStop = 'action.stop';
  static const String actionDelete = 'action.delete';
  static const String idleCycle = 'idle.cycle';
  static const String expressionCycle = 'expression.cycle';
  static const String avatarCommand = 'avatar.command';
  static const String conversationSend = 'conversation.send';
  static const String conversationSendWithCamera =
      'conversation.sendWithCamera';
  static const String conversationInterrupt = 'conversation.interrupt';
  static const String voiceToggleListen = 'voice.toggleListen';
  static const String voiceToggleRecord = 'voice.toggleRecord';
  static const String voiceRestart = 'voice.restart';
  static const String voiceCancel = 'voice.cancel';
  static const String pairingSetServer = 'pairing.setServer';
  static const String pairingSetPrivateHttp = 'pairing.setPrivateHttp';
  static const String pairingDigit = 'pairing.digit';
  static const String pairingPair = 'pairing.pair';
  static const String pairingReconnect = 'pairing.reconnect';
  static const String pairingClearBinding = 'pairing.clearBinding';
  static const String qualityApplyPreset = 'quality.applyPreset';
  static const String qualityApplyPhysics = 'quality.applyPhysics';
  static const String qualityReset = 'quality.reset';
  static const String settingsTargetFps = 'settings.targetFps';
  static const String settingsVolume = 'settings.volume';
  static const String settingsToggle = 'settings.toggle';
  static const String copresenceEnterScene = 'copresence.enterScene';
  static const String copresenceReturnToMenu = 'copresence.returnToMenu';
  static const String copresenceSwitchMode = 'copresence.switchMode';
  static const String copresenceSwitchEnvironment =
      'copresence.switchEnvironment';
  static const String copresenceSetChromeInsets = 'copresence.setChromeInsets';
  static const String copresenceArPlace = 'copresence.arPlace';
  static const String sceneMoveMode = 'scene.moveMode';
  static const String sceneReframe = 'scene.reframe';
  static const String sceneHud = 'scene.hud';
  static const String updateCheck = 'update.check';
  static const String updateInstall = 'update.install';
  static const String logRefresh = 'log.refresh';
  static const String logClear = 'log.clear';
  static const String qaCommand = 'qa.command';
}

/// Event names (engine → Flutter).
abstract final class Evt {
  Evt._();
  static const String connectionChanged = 'connection.changed';
  static const String pairingStatus = 'pairing.status';
  static const String conversationState = 'conversation.state';
  static const String conversationTranscript = 'conversation.transcript';
  static const String conversationReply = 'conversation.reply';
  static const String modelUpdated = 'model.updated';
  static const String modelImportStatus = 'model.importStatus';
  static const String actionUpdated = 'action.updated';
  static const String actionPlaybackChanged = 'action.playbackChanged';
  static const String qualityChanged = 'quality.changed';
  static const String copresenceMode = 'copresence.mode';
  static const String copresenceCallTimer = 'copresence.callTimer';
  static const String copresenceChromeInsetsNeeded =
      'copresence.chromeInsetsNeeded';
  static const String copresencePlacementChanged =
      'copresence.placementChanged';
  static const String framingAnchors = 'framing.anchors';
  static const String voiceStatus = 'voice.status';
  static const String updateStatus = 'update.status';
  static const String logUpdated = 'log.updated';
  static const String performanceSnapshot = 'performance.snapshot';
  static const String toast = 'toast';
}

/// Envelope discriminator. Both directions share the same envelope (design §6).
enum BridgeMessageType { cmd, reply, event }

/// Co-presence mode wire values (design §6.1 `copresence.switchMode`).
enum CoPresenceMode {
  arReality('arReality', '同框现实'),
  virtualScene('virtualScene', '虚拟场景'),
  videoCall('videoCall', '视频通话');

  const CoPresenceMode(this.value, this.label);

  final String value;
  final String label;

  static CoPresenceMode? fromValue(String? v) {
    for (final m in values) {
      if (m.value == v) return m;
    }
    return null;
  }
}

/// Virtual environment wire values (design §6.1 `copresence.switchEnvironment`).
enum VirtualEnvironment {
  nightStreet('nightStreet', '夜街'),
  starrySky('starrySky', '星空'),
  bedroom('bedroom', '卧室'),
  seaside('seaside', '海边');

  const VirtualEnvironment(this.value, this.label);

  final String value;
  final String label;

  static VirtualEnvironment? fromValue(String? v) {
    for (final e in values) {
      if (e.value == v) return e;
    }
    return null;
  }
}

/// Bidirectional JSON-RPC-style envelope: `{v,id,type,name,payload,error}`.
class BridgeEnvelope {
  const BridgeEnvelope({
    required this.v,
    this.id,
    required this.type,
    required this.name,
    this.payload,
    this.error,
  });

  final int v;
  final int? id;
  final BridgeMessageType type;
  final String name;
  final Map<String, dynamic>? payload;
  final dynamic error;

  Map<String, dynamic> toJson() => <String, dynamic>{
        'v': v,
        if (id != null) 'id': id,
        'type': _typeName(type),
        'name': name,
        if (payload != null) 'payload': payload,
        if (error != null) 'error': error,
      };

  static BridgeEnvelope? tryParse(dynamic raw) {
    if (raw is! Map) return null;
    final map = raw;
    final typeName = map['type'];
    final name = map['name'];
    if (typeName is! String || name is! String) return null;
    final type = switch (typeName) {
      'cmd' => BridgeMessageType.cmd,
      'reply' => BridgeMessageType.reply,
      'event' => BridgeMessageType.event,
      _ => null,
    };
    if (type == null) return null;
    final idRaw = map['id'];
    final payloadRaw = map['payload'];
    return BridgeEnvelope(
      v: map['v'] is int ? map['v'] as int : 1,
      id: idRaw is int ? idRaw : null,
      type: type,
      name: name,
      payload: payloadRaw is Map ? Map<String, dynamic>.from(payloadRaw) : null,
      error: map['error'],
    );
  }

  static String _typeName(BridgeMessageType type) => switch (type) {
        BridgeMessageType.cmd => 'cmd',
        BridgeMessageType.reply => 'reply',
        BridgeMessageType.event => 'event',
      };
}

/// Engine reply to a command `id`: `{id, ok, data|error}`.
class BridgeReply {
  const BridgeReply({this.id, required this.ok, this.data, this.error});

  final int? id;
  final bool ok;
  final Map<String, dynamic>? data;
  final String? error;

  factory BridgeReply.ok(int? id, [Map<String, dynamic>? data]) =>
      BridgeReply(id: id, ok: true, data: data);

  factory BridgeReply.fail(int? id, String error) =>
      BridgeReply(id: id, ok: false, error: error);

  static BridgeReply? tryParse(dynamic raw) {
    if (raw is! Map) return null;
    final okRaw = raw['ok'];
    if (okRaw is! bool) return null;
    final idRaw = raw['id'];
    final dataRaw = raw['data'];
    final errRaw = raw['error'];
    return BridgeReply(
      id: idRaw is int ? idRaw : null,
      ok: okRaw,
      data: dataRaw is Map ? Map<String, dynamic>.from(dataRaw) : null,
      error: errRaw == null ? null : errRaw.toString(),
    );
  }
}

/// Engine-pushed event: `{name, payload}`.
class BridgeEvent {
  const BridgeEvent(this.name, [this.payload]);

  final String name;
  final Map<String, dynamic>? payload;
}

/// A model in the companion library (design §2.2).
class ModelInfo {
  const ModelInfo({
    required this.path,
    required this.displayName,
    this.size = '',
    this.inUse = false,
  });

  final String path;
  final String displayName;
  final String size;
  final bool inUse;

  factory ModelInfo.fromJson(Map<String, dynamic> json) => ModelInfo(
        path: _str(json['path']),
        displayName: _str(json['displayName'] ?? json['name']),
        size: _str(json['size']),
        inUse: json['inUse'] == true || json['current'] == true,
      );

  Map<String, dynamic> toJson() => <String, dynamic>{
        'path': path,
        'displayName': displayName,
        'size': size,
        'inUse': inUse,
      };
}

/// A VMD action in the action library (design §2.4).
class VmdActionInfo {
  const VmdActionInfo({
    required this.id,
    required this.name,
    this.duration = '',
    this.frames = 0,
    this.hasExpression = false,
  });

  final String id;
  final String name;
  final String duration;
  final int frames;
  final bool hasExpression;

  factory VmdActionInfo.fromJson(Map<String, dynamic> json) => VmdActionInfo(
        id: _str(json['id']),
        name: _str(json['name'] ?? json['displayName']),
        duration: json['duration'] != null
            ? _str(json['duration'])
            : _formatDuration(_num(json['durationSeconds'])),
        frames: json['frames'] != null
            ? _int(json['frames'])
            : _int(json['keyframeCount']),
        hasExpression:
            json['hasExpression'] == true || json['hasFacialTrack'] == true,
      );

  Map<String, dynamic> toJson() => <String, dynamic>{
        'id': id,
        'name': name,
        'duration': duration,
        'frames': frames,
        'hasExpression': hasExpression,
      };
}

/// A screen-projected anchor marker in normalized (0..1) coordinates.
class FramingAnchor {
  const FramingAnchor(this.x, this.y);

  final double x;
  final double y;

  factory FramingAnchor.fromJson(dynamic json) {
    if (json is Map) {
      return FramingAnchor(_num(json['x']), _num(json['y']));
    }
    return const FramingAnchor(0, 0);
  }
}

/// Framing snapshot (M1, INV-7). Mirrors the engine `FramingSnapshot`: chrome
/// band top/bottom (px), solved distance/camera height, anchor kind, and the
/// projected cross-hair anchors consumed by the QA overlay.
class FramingSnapshot {
  const FramingSnapshot({
    this.valid = false,
    this.screenWidthPx = 0,
    this.screenHeightPx = 0,
    this.topPx = 0,
    this.bottomPx = 0,
    this.distance = 0,
    this.cameraY = 0,
    this.headAnchor = false,
    this.degraded = false,
    this.anchors = const <String, FramingAnchor>{},
  });

  final bool valid;
  final double screenWidthPx;
  final double screenHeightPx;
  final double topPx;
  final double bottomPx;
  final double distance;
  final double cameraY;
  final bool headAnchor;
  final bool degraded;
  final Map<String, FramingAnchor> anchors;

  factory FramingSnapshot.unavailable() => const FramingSnapshot();

  factory FramingSnapshot.fromEvent(dynamic payload) {
    if (payload is! Map) return FramingSnapshot.unavailable();
    final anchors = <String, FramingAnchor>{};
    final rawAnchors = payload['anchors'];
    if (rawAnchors is Map) {
      rawAnchors.forEach((k, v) {
        anchors[k.toString()] = FramingAnchor.fromJson(v);
      });
    }
    return FramingSnapshot(
      valid: payload['valid'] == true,
      screenWidthPx: _num(payload['screenWidthPx']),
      screenHeightPx: _num(payload['screenHeightPx']),
      topPx: _num(payload['topPx']),
      bottomPx: _num(payload['bottomPx']),
      distance: _num(payload['distance'] ?? payload['d']),
      cameraY: _num(payload['cameraY'] ?? payload['h']),
      headAnchor:
          payload['headAnchor'] == true || payload['anchorKind'] == 'head',
      degraded: payload['degraded'] == true,
      anchors: anchors,
    );
  }
}

/// Performance telemetry snapshot (design §6.2 `performance.snapshot`).
class PerfSnapshot {
  const PerfSnapshot({
    this.fps5s = 0,
    this.fps30s = 0,
    this.frameP50Ms = 0,
    this.frameP95Ms = 0,
    this.physicsDropS = 0,
    this.poseSrcFlip = 0,
  });

  final double fps5s;
  final double fps30s;
  final double frameP50Ms;
  final double frameP95Ms;
  final double physicsDropS;
  final int poseSrcFlip;

  factory PerfSnapshot.fromJson(Map<String, dynamic> json) => PerfSnapshot(
        fps5s: _num(json['fps5s']),
        fps30s: _num(json['fps30s']),
        frameP50Ms: _num(json['frameP50Ms']),
        frameP95Ms: _num(json['frameP95Ms']),
        physicsDropS: _num(json['physicsDropS']),
        poseSrcFlip: _int(json['poseSrcFlip']),
      );
}

String _str(dynamic v) => v == null ? '' : v.toString();

String _formatDuration(double seconds) {
  if (seconds <= 0) return '';
  final int whole = seconds.round();
  return '${whole ~/ 60}:${(whole % 60).toString().padLeft(2, '0')}';
}

int _int(dynamic v) {
  if (v is int) return v;
  if (v is num) return v.toInt();
  if (v is String) return int.tryParse(v) ?? 0;
  return 0;
}

double _num(dynamic v) {
  if (v is num) return v.toDouble();
  if (v is String) return double.tryParse(v) ?? 0;
  return 0;
}

/// Convenience: encode an envelope to a JSON string (used by native senders).
String encodeEnvelope(BridgeEnvelope envelope) => jsonEncode(envelope.toJson());

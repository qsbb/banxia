import 'dart:async';

import 'package:flutter/services.dart';

import 'bridge_protocol.dart';

/// Transport-agnostic command surface (design §6).
///
/// The UI only ever talks to this interface: every mutation is a [call], every
/// engine push arrives on [events]. Screens never depend on the transport.
abstract class BridgeClient {
  Future<BridgeReply> call(String name, [Map<String, dynamic>? payload]);

  Stream<BridgeEvent> get events;

  void dispose();
}

/// Production transport: `MethodChannel('banxia.bridge')` carries the
/// request/reply envelope, `EventChannel('banxia.events')` carries engine-pushed
/// events. Missing plugin / platform errors degrade to a failed reply so the
/// shell stays usable when no native host is present.
class ChannelBridgeClient implements BridgeClient {
  static const MethodChannel _method = MethodChannel('banxia.bridge');
  static const EventChannel _events = EventChannel('banxia.events');

  final StreamController<BridgeEvent> _controller =
      StreamController<BridgeEvent>.broadcast();
  StreamSubscription<dynamic>? _sub;
  int _nextId = 1;

  ChannelBridgeClient() {
    _sub = _events.receiveBroadcastStream().listen(
          _onRaw,
          onError: (Object _) {},
        );
  }

  void _onRaw(dynamic raw) {
    final env = BridgeEnvelope.tryParse(raw);
    if (env == null || env.type != BridgeMessageType.event) {
      return;
    }
    _controller.add(BridgeEvent(env.name, env.payload));
  }

  @override
  Stream<BridgeEvent> get events => _controller.stream;

  @override
  Future<BridgeReply> call(String name, [Map<String, dynamic>? payload]) async {
    final id = _nextId++;
    final envelope = BridgeEnvelope(
      v: 1,
      id: id,
      type: BridgeMessageType.cmd,
      name: name,
      payload: payload,
    );
    try {
      final dynamic result =
          await _method.invokeMethod<dynamic>('call', envelope.toJson());
      return BridgeReply.tryParse(result) ??
          BridgeReply.fail(id, 'malformed reply');
    } on MissingPluginException {
      return BridgeReply.fail(id, 'bridge unavailable');
    } on PlatformException catch (e) {
      return BridgeReply.fail(id, e.message ?? 'bridge error');
    }
  }

  @override
  void dispose() {
    _sub?.cancel();
    _controller.close();
  }
}

/// In-memory engine stand-in used when the module runs without a Unity host
/// (and by tests). It mirrors the wire contract so every screen, the M2 modal
/// sheet and the M3 keypad are fully exercisable in a plain `flutter run`.
class LocalBridgeClient implements BridgeClient {
  final StreamController<BridgeEvent> _controller =
      StreamController<BridgeEvent>.broadcast();
  int _nextId = 1;

  final List<ModelInfo> _models = <ModelInfo>[
    const ModelInfo(
        path: 'kokona', displayName: 'Kokona', size: '12.4 MB', inUse: true),
    const ModelInfo(
        path: 'forestberry', displayName: 'Forest Berry', size: '18.1 MB'),
  ];

  final List<VmdActionInfo> _actions = <VmdActionInfo>[
    const VmdActionInfo(id: 'idle', name: '待机', duration: '—'),
    const VmdActionInfo(
        id: 'wave',
        name: '挥手',
        duration: '0:02',
        frames: 60,
        hasExpression: true),
    const VmdActionInfo(
        id: 'dance',
        name: '舞蹈',
        duration: '0:12',
        frames: 360,
        hasExpression: true),
  ];

  CoPresenceMode _mode = CoPresenceMode.videoCall;
  VirtualEnvironment _environment = VirtualEnvironment.nightStreet;
  bool _videoCallActive = false;
  bool _arPlaced = false;

  final List<String> _log = <String>[
    '[Banxia] shell ready',
    '[Banxia] bridge demo attached',
  ];

  @override
  Stream<BridgeEvent> get events => _controller.stream;

  void _emit(String name, [Map<String, dynamic>? payload]) {
    _controller.add(BridgeEvent(name, payload));
  }

  BridgeReply _ok(int id, [Map<String, dynamic>? data]) =>
      BridgeReply.ok(id, data);

  @override
  Future<BridgeReply> call(String name, [Map<String, dynamic>? payload]) async {
    final id = _nextId++;
    final p = payload ?? const <String, dynamic>{};
    switch (name) {
      case Cmd.modelDiscover:
        return _ok(id, <String, dynamic>{
          'models': _models.map((e) => e.toJson()).toList(),
        });
      case Cmd.modelLoad:
        _mode = CoPresenceMode.videoCall;
        _videoCallActive = true;
        _emit(Evt.copresenceMode, _modeEvent());
        _emit(Evt.framingAnchors, _demoFraming());
        _emit(Evt.toast, <String, dynamic>{'message': '已进入场景'});
        return _ok(id);
      case Cmd.modelDelete:
        _emit(Evt.modelUpdated, <String, dynamic>{
          'models': _models.map((e) => e.toJson()).toList(),
        });
        _emit(Evt.toast, <String, dynamic>{'message': '已删除模型'});
        return _ok(id);
      case Cmd.modelImport:
        _emit(Evt.modelImportStatus, <String, dynamic>{'status': '已打开系统文件选择器'});
        return _ok(id);
      case Cmd.actionRefresh:
        return _ok(id, <String, dynamic>{
          'actions': _actions.map((e) => e.toJson()).toList(),
        });
      case Cmd.actionPlay:
        _emit(
            Evt.actionPlaybackChanged, <String, dynamic>{'playingId': p['id']});
        return _ok(id);
      case Cmd.actionStop:
        _emit(Evt.actionPlaybackChanged, <String, dynamic>{'playingId': null});
        _emit(Evt.toast, <String, dynamic>{'message': '已回到待机'});
        return _ok(id);
      case Cmd.actionDelete:
        _emit(Evt.actionUpdated, <String, dynamic>{
          'actions': _actions.map((e) => e.toJson()).toList(),
        });
        return _ok(id);
      case Cmd.idleCycle:
        _emit(Evt.toast, <String, dynamic>{'message': '切换待机预设'});
        return _ok(id);
      case Cmd.expressionCycle:
        _emit(Evt.toast, <String, dynamic>{'message': '切换表情'});
        return _ok(id);
      case Cmd.avatarCommand:
        _emit(Evt.toast,
            <String, dynamic>{'message': 'avatar.command: ${p['name']}'});
        return _ok(id);
      case Cmd.conversationSend:
        _emit(Evt.conversationTranscript, <String, dynamic>{'text': p['text']});
        _emit(Evt.conversationReply, <String, dynamic>{
          'text': '（演示回复）收到：${p['text']}',
        });
        _emit(Evt.conversationState,
            <String, dynamic>{'state': 'replying', 'transportStatus': 'http'});
        return _ok(id);
      case Cmd.conversationInterrupt:
        _emit(Evt.conversationState,
            <String, dynamic>{'state': 'idle', 'transportStatus': 'http'});
        return _ok(id);
      case Cmd.voiceToggleListen:
      case Cmd.voiceToggleRecord:
      case Cmd.voiceRestart:
      case Cmd.voiceCancel:
        _emit(Evt.voiceStatus, <String, dynamic>{
          'monitoring': true,
          'alwaysListening': false,
          'recording': name == Cmd.voiceToggleRecord,
          'level': 0.3,
        });
        return _ok(id);
      case Cmd.pairingSetServer:
      case Cmd.pairingSetPrivateHttp:
      case Cmd.pairingDigit:
        return _ok(id);
      case Cmd.pairingPair:
        _emit(Evt.connectionChanged,
            <String, dynamic>{'connected': true, 'bridgeStatus': '已连接'});
        _emit(Evt.pairingStatus, <String, dynamic>{
          'status': '已连接',
          'privateHttp': p['privateHttp'] ?? false,
          'codeLen': 6,
        });
        _emit(Evt.toast, <String, dynamic>{'message': '配对成功'});
        return _ok(id);
      case Cmd.pairingReconnect:
        _emit(Evt.pairingStatus, <String, dynamic>{'status': '重连中…'});
        _emit(Evt.toast, <String, dynamic>{'message': '重新连接后端'});
        return _ok(id);
      case Cmd.pairingClearBinding:
        _emit(Evt.connectionChanged,
            <String, dynamic>{'connected': false, 'bridgeStatus': '未连接'});
        _emit(Evt.pairingStatus, <String, dynamic>{'status': '未连接'});
        _emit(Evt.toast, <String, dynamic>{'message': '已解除后端绑定'});
        return _ok(id);
      case Cmd.qualityApplyPreset:
        _emit(Evt.qualityChanged, <String, dynamic>{
          'renderPreset': p['preset'],
          'physicsPreset': null,
          'status': '画质已应用',
        });
        return _ok(id);
      case Cmd.qualityApplyPhysics:
        _emit(Evt.qualityChanged, <String, dynamic>{
          'renderPreset': null,
          'physicsPreset': p['preset'],
          'status': '物理已应用',
        });
        return _ok(id);
      case Cmd.qualityReset:
        _emit(Evt.qualityChanged, <String, dynamic>{
          'renderPreset': 'balanced',
          'physicsPreset': 'balanced',
          'status': '已恢复默认画质',
        });
        return _ok(id);
      case Cmd.settingsTargetFps:
      case Cmd.settingsVolume:
      case Cmd.settingsToggle:
        return _ok(id);
      case Cmd.copresenceEnterScene:
        _videoCallActive = _mode == CoPresenceMode.videoCall;
        _emit(Evt.copresenceMode, _modeEvent());
        _emit(Evt.framingAnchors, _demoFraming());
        return _ok(id);
      case Cmd.copresenceReturnToMenu:
        _videoCallActive = false;
        _arPlaced = false;
        _emit(Evt.copresenceMode, _modeEvent());
        _emit(Evt.copresencePlacementChanged,
            <String, dynamic>{'arPlaced': false});
        _emit(Evt.framingAnchors, <String, dynamic>{'valid': false});
        return _ok(id);
      case Cmd.copresenceSwitchMode:
        _mode = CoPresenceMode.fromValue(p['mode'] as String?) ?? _mode;
        _videoCallActive = _mode == CoPresenceMode.videoCall;
        _arPlaced = false;
        _emit(Evt.copresenceMode, _modeEvent());
        _emit(Evt.copresencePlacementChanged,
            <String, dynamic>{'arPlaced': false});
        if (_videoCallActive) _emit(Evt.framingAnchors, _demoFraming());
        return _ok(id);
      case Cmd.copresenceSwitchEnvironment:
        _environment =
            VirtualEnvironment.fromValue(p['env'] as String?) ?? _environment;
        _emit(Evt.copresenceMode, _modeEvent());
        return _ok(id);
      case Cmd.copresenceSetChromeInsets:
        return _ok(id);
      case Cmd.copresenceArPlace:
        final x = p['x'];
        final y = p['y'];
        if (x is! num ||
            y is! num ||
            !x.isFinite ||
            !y.isFinite ||
            x < 0 ||
            y < 0 ||
            x > 1440 ||
            y > 3200) {
          return BridgeReply.fail(id, '放置坐标无效');
        }
        _arPlaced = true;
        _emit(Evt.copresencePlacementChanged,
            <String, dynamic>{'arPlaced': true});
        _emit(Evt.copresenceMode, _modeEvent());
        return _ok(id);
      case Cmd.sceneMoveMode:
        _emit(Evt.toast, <String, dynamic>{'message': '移动模式已切换'});
        return _ok(id);
      case Cmd.sceneReframe:
        _emit(Evt.framingAnchors, _demoFraming());
        return _ok(id);
      case Cmd.sceneHud:
        _emit(Evt.toast, <String, dynamic>{'message': 'HUD 已切换'});
        return _ok(id);
      case Cmd.updateCheck:
        return _ok(id, <String, dynamic>{
          'status': <String, dynamic>{
            'phase': 'idle',
            'hasUpdate': false,
            'version': '0.3.2',
          },
        });
      case Cmd.updateInstall:
        return _ok(id);
      case Cmd.logRefresh:
        return _ok(id, <String, dynamic>{'lines': List<String>.from(_log)});
      case Cmd.logClear:
        _log.clear();
        _emit(Evt.logUpdated, <String, dynamic>{'lines': <String>[]});
        return _ok(id);
      case Cmd.qaCommand:
        _emit(Evt.toast, <String, dynamic>{'message': 'QA: ${p['name']}'});
        return _ok(id);
      default:
        return _ok(id);
    }
  }

  Map<String, dynamic> _modeEvent() => <String, dynamic>{
        'mode': _mode.value,
        'environment': _environment.value,
        'videoCallActive': _videoCallActive,
        'arAvailable': false,
        'arPlaced': _arPlaced,
      };

  Map<String, dynamic> _demoFraming() => <String, dynamic>{
        'valid': true,
        'screenWidthPx': 1440.0,
        'screenHeightPx': 3200.0,
        'topPx': 330.0,
        'bottomPx': 2640.0,
        'distance': 0.79,
        'cameraY': 1.50,
        'headAnchor': true,
        'degraded': false,
        'anchors': <String, dynamic>{
          'headTop': <String, dynamic>{'x': 0.50, 'y': 0.20},
          'eye': <String, dynamic>{'x': 0.50, 'y': 0.33},
          'waist': <String, dynamic>{'x': 0.50, 'y': 0.70},
          'feet': <String, dynamic>{'x': 0.50, 'y': 0.90},
        },
      };

  @override
  void dispose() {
    _controller.close();
  }
}

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
  bool _debugMode = false;

  ChannelBridgeClient() {
    _sub = _events.receiveBroadcastStream().listen(
      _onRaw,
      onError: (Object error, StackTrace stack) {
        if (_debugMode) _controller.addError(error, stack);
      },
    );
  }

  void _onRaw(dynamic raw) {
    final env = BridgeEnvelope.tryParse(raw);
    if (env == null || env.type != BridgeMessageType.event) {
      return;
    }
    if (env.name == Evt.qualityChanged && env.payload?['debugMode'] is bool) {
      _debugMode = env.payload!['debugMode'] as bool;
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
      final reply = BridgeReply.tryParse(result);
      if (reply == null && _debugMode)
        throw StateError('malformed bridge reply');
      if (reply?.ok == true &&
          name == Cmd.settingsToggle &&
          payload?['key'] == 'debugMode' &&
          payload?['value'] is bool) {
        _debugMode = payload!['value'] as bool;
      }
      return reply ?? BridgeReply.fail(id, 'malformed reply');
    } on MissingPluginException {
      if (_debugMode) rethrow;
      return BridgeReply.fail(id, 'bridge unavailable');
    } on PlatformException catch (e) {
      if (_debugMode) rethrow;
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
  bool _inScene = false;
  int _loadGeneration = 0;
  bool _privateHttp = false;
  bool _remoteHttp = false;
  String _renderPreset = 'balanced';
  String _physicsPreset = 'balanced';
  int _targetFps = 120;
  double _volume = 1.0;
  bool _debugMode = false;
  String _server = '';
  String _pairingCode = '';
  String _certificatePinSha256 = '';
  // 演示用候选入口列表（完整 URL，首项=最高优先级）与当前生效入口。
  final List<String> _endpoints = <String>[];
  String _activeEndpoint = '';

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

  Map<String, dynamic> _qualityPayload([String status = '']) =>
      <String, dynamic>{
        'renderPreset': _renderPreset,
        'physicsPreset': _physicsPreset,
        'status': status,
        'targetFps': _targetFps,
        'volume': _volume,
        'debugMode': _debugMode,
      };

  void _emitQuality([String status = '']) {
    _emit(Evt.qualityChanged, _qualityPayload(status));
  }

  Map<String, dynamic> _pairingPayload(
          {String? status, int? codeLen, bool includeEndpoints = false}) =>
      <String, dynamic>{
        'status': status ?? (_server.isEmpty ? '未连接' : '配对服务器已设置'),
        'server': _server,
        'privateHttp': _privateHttp,
        'remoteHttp': _remoteHttp,
        'codeLen': codeLen ?? _pairingCode.length,
        if (includeEndpoints) 'endpoints': List<String>.from(_endpoints),
        if (includeEndpoints) 'activeEndpoint': _activeEndpoint,
        'certificatePinConfigured': _certificatePinSha256.isNotEmpty,
        'certificatePinSummary': _certificatePinSha256.isEmpty
            ? ''
            : '${_certificatePinSha256.substring(0, 8)}…${_certificatePinSha256.substring(56)}',
      };

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
      case Cmd.conversationSendWithCamera:
        final String text = (p['text'] as String? ?? '').trim();
        _emit(Evt.conversationTranscript, <String, dynamic>{'text': text});
        _emit(Evt.conversationReply, <String, dynamic>{
          'text': name == Cmd.conversationSendWithCamera
              ? '（演示回复）已收到一帧${text.isEmpty ? '' : '：$text'}'
              : '（演示回复）收到：$text',
        });
        _emit(Evt.conversationSuggestions, <String, dynamic>{
          'suggestions': <String>['继续说说', '换个话题', '我知道了'],
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
        _server = (p['server'] as String? ?? '').trim();
        _emit(Evt.pairingStatus, _pairingPayload());
        return _ok(id);
      case Cmd.pairingSetCertificatePin:
        final String candidate = (p['sha256'] as String? ?? '').trim();
        if (candidate.isNotEmpty && !RegExp(r'^[0-9a-fA-F]{64}$').hasMatch(candidate)) {
          return BridgeReply.fail(id, '证书指纹必须是 64 位十六进制字符串');
        }
        _certificatePinSha256 = candidate.toLowerCase();
        _emit(Evt.pairingStatus, _pairingPayload(includeEndpoints: true));
        return _ok(id);
      case Cmd.pairingSetPrivateHttp:
        _privateHttp = p['enabled'] == true;
        _emit(Evt.pairingStatus, _pairingPayload());
        return _ok(id);
      case Cmd.pairingSetRemoteHttp:
        _remoteHttp = p['enabled'] == true;
        _emit(Evt.pairingStatus, _pairingPayload());
        return _ok(id);
      case Cmd.pairingDigit:
        final String op = p['op'] as String? ?? '';
        final String digit = p['digit'] as String? ?? '';
        if (op == 'append') {
          if (digit.length != 1 ||
              digit.codeUnitAt(0) < 48 ||
              digit.codeUnitAt(0) > 57 ||
              _pairingCode.length >= 6) {
            return BridgeReply.fail(id, '配对数字无效');
          }
          _pairingCode += digit;
        } else if (op == 'remove') {
          if (_pairingCode.isNotEmpty) {
            _pairingCode = _pairingCode.substring(0, _pairingCode.length - 1);
          }
        } else if (op == 'clear') {
          _pairingCode = '';
        } else {
          return BridgeReply.fail(id, '未知配对键盘操作');
        }
        _emit(Evt.pairingStatus, _pairingPayload());
        return _ok(id);
      case Cmd.pairingPair:
        _emit(Evt.connectionChanged,
            <String, dynamic>{'connected': true, 'bridgeStatus': '已连接'});
        // 演示：配对成功后把绑定地址作为首个入口（最高优先级）。
        if (_server.isNotEmpty && !_endpoints.contains(_server)) {
          _endpoints.insert(0, _server);
        }
        _activeEndpoint = _endpoints.isEmpty ? '' : _endpoints.first;
        _emit(
          Evt.pairingStatus,
          _pairingPayload(
            status: '已连接',
            codeLen: 0,
            includeEndpoints: true,
          ),
        );
        _pairingCode = '';
        _emit(Evt.toast, <String, dynamic>{'message': '配对成功'});
        return _ok(id);
      case Cmd.pairingReconnect:
        _emit(
          Evt.pairingStatus,
          _pairingPayload(status: '重连中…', codeLen: 0),
        );
        _emit(Evt.toast, <String, dynamic>{'message': '重新连接后端'});
        return _ok(id);
      case Cmd.pairingClearBinding:
        _emit(Evt.connectionChanged,
            <String, dynamic>{'connected': false, 'bridgeStatus': '未连接'});
        _server = '';
        _pairingCode = '';
        _certificatePinSha256 = '';
        _endpoints.clear();
        _activeEndpoint = '';
        _emit(
          Evt.pairingStatus,
          _pairingPayload(
            status: '未连接',
            codeLen: 0,
            includeEndpoints: true,
          ),
        );
        _emit(Evt.toast, <String, dynamic>{'message': '已解除后端绑定'});
        return _ok(id);
      case Cmd.pairingEndpointAdd:
        // 演示占位：只做基本去重，URL 规范化/校验由真实引擎负责。
        final String addUrl = (p['url'] as String? ?? '').trim();
        if (addUrl.isEmpty) {
          return BridgeReply.fail(id, '入口地址不能为空');
        }
        if (_endpoints.contains(addUrl)) {
          return BridgeReply.fail(id, '该入口已在列表中');
        }
        _endpoints.add(addUrl);
        _emitEndpointStatus();
        return _ok(id);
      case Cmd.pairingEndpointRemove:
        final String removeUrl = (p['url'] as String? ?? '').trim();
        if (!_endpoints.remove(removeUrl)) {
          return BridgeReply.fail(id, '入口不存在');
        }
        if (_activeEndpoint == removeUrl) {
          _activeEndpoint = _endpoints.isEmpty ? '' : _endpoints.first;
        }
        _emitEndpointStatus();
        return _ok(id);
      case Cmd.pairingEndpointMove:
        final String moveUrl = (p['url'] as String? ?? '').trim();
        final int offset = (p['offset'] as num?)?.toInt() ?? 0;
        final int index = _endpoints.indexOf(moveUrl);
        if (index < 0 || (offset != -1 && offset != 1)) {
          return BridgeReply.fail(id, '入口或偏移无效');
        }
        final int target = index + offset;
        if (target < 0 || target >= _endpoints.length) {
          return BridgeReply.fail(id, '已到边界，无法继续移动');
        }
        final String tmp = _endpoints[index];
        _endpoints[index] = _endpoints[target];
        _endpoints[target] = tmp;
        _emitEndpointStatus();
        return _ok(id);
      case Cmd.pairingEndpointTest:
        final String testUrl = (p['url'] as String? ?? '').trim();
        if (testUrl.isEmpty) {
          return BridgeReply.fail(id, '入口地址不能为空');
        }
        final String requestId = 'local-${_nextId++}';
        // Keep the local bridge asynchronous so tests exercise the same ack/event
        // contract as the Unity host.
        scheduleMicrotask(() {
          _emit(Evt.pairingEndpointTest, <String, dynamic>{
            'requestId': requestId,
            'ok': true,
            'httpCode': 200,
            'elapsedMs': 1,
            'errorKind': '',
          });
        });
        return _ok(id, <String, dynamic>{'requestId': requestId});
      case Cmd.qualityApplyPreset:
        _renderPreset = p['preset'] as String? ?? _renderPreset;
        _emitQuality('画质已应用');
        return _ok(id);
      case Cmd.qualityApplyPhysics:
        _physicsPreset = p['preset'] as String? ?? _physicsPreset;
        _emitQuality('物理已应用');
        return _ok(id);
      case Cmd.qualityReset:
        _renderPreset = 'balanced';
        _physicsPreset = 'balanced';
        _emitQuality('已恢复默认画质');
        return _ok(id);
      case Cmd.settingsTargetFps:
        final int? fps = (p['fps'] as num?)?.toInt();
        if (fps == null || ![30, 60, 120].contains(fps)) {
          return BridgeReply.fail(id, '目标帧率仅支持 30/60/120');
        }
        _targetFps = fps;
        _emitQuality('目标帧率已更新');
        return _ok(id);
      case Cmd.settingsVolume:
        final num? rawVolume = p['v'] as num?;
        if (rawVolume == null ||
            !rawVolume.isFinite ||
            rawVolume < 0 ||
            rawVolume > 1) {
          return BridgeReply.fail(id, '音量必须是 0 到 1 之间的数值');
        }
        _volume = rawVolume.toDouble();
        _emitQuality('音量已更新');
        return _ok(id);
      case Cmd.settingsToggle:
        if (p['key'] == 'debugMode' && p['value'] is bool) {
          _debugMode = p['value'] as bool;
          _emitQuality(_debugMode ? '调试模式已开启' : '调试模式已关闭');
        }
        return _ok(id);
      case Cmd.copresenceEnterScene:
        final String requestId = (p['requestId'] as String?) ?? 'local-scene-$id';
        final int generation = ++_loadGeneration;
        _emit(Evt.modelLoadProgress, <String, dynamic>{
          'requestId': requestId,
          'generation': generation,
          'phase': 'Reading',
          'state': 'started',
          'fraction': 0.0,
          'line': '正在准备模型…',
        });
        _emit(Evt.modelLoadProgress, <String, dynamic>{
          'requestId': requestId,
          'generation': generation,
          'phase': 'Ready',
          'state': 'completed',
          'fraction': 1.0,
          'line': '模型已就绪',
        });
        _videoCallActive = _mode == CoPresenceMode.videoCall;
        _inScene = true;
        _emit(Evt.copresenceMode, _modeEvent());
        _emit(Evt.framingAnchors, _demoFraming());
        return _ok(id);
      case Cmd.copresenceCancelEnterScene:
        _emit(Evt.modelLoadProgress, <String, dynamic>{
          'requestId': (p['requestId'] as String?) ?? '',
          'generation': (p['generation'] as num?)?.toInt() ?? 1,
          'phase': 'Cancelled',
          'state': 'cancelled',
          'fraction': -1.0,
          'line': '已取消模型加载',
          'errorCode': 'cancelled',
        });
        return _ok(id);
      case Cmd.copresenceReturnToMenu:
        _videoCallActive = false;
        _arPlaced = false;
        _inScene = false;
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
            'version': '0.3.2.20260906',
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

  /// 演示：入口增删/排序后回放 pairing.status（真实引擎由网络层下发真值）。
  void _emitEndpointStatus() {
    _emit(
      Evt.pairingStatus,
      _pairingPayload(
        status: _server.isEmpty ? '未连接' : '配对服务器已设置',
        codeLen: _pairingCode.length,
        includeEndpoints: true,
      ),
    );
  }

  Map<String, dynamic> _modeEvent() => <String, dynamic>{
        'mode': _mode.value,
        'environment': _environment.value,
        'videoCallActive': _videoCallActive,
        'arAvailable': false,
        'arPlaced': _arPlaced,
        'inScene': _inScene,
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

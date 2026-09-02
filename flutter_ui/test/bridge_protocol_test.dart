import 'package:flutter_test/flutter_test.dart';

import 'package:banxia_flutter_ui/core/bridge/bridge_protocol.dart';

void main() {
  group('BridgeEnvelope', () {
    test('cmd round-trips through json', () {
      final env = BridgeEnvelope(
        v: 1,
        id: 12,
        type: BridgeMessageType.cmd,
        name: 'model.load',
        payload: <String, dynamic>{'path': 'kokona'},
      );
      final parsed = BridgeEnvelope.tryParse(env.toJson());
      expect(parsed, isNotNull);
      expect(parsed!.type, BridgeMessageType.cmd);
      expect(parsed.id, 12);
      expect(parsed.name, 'model.load');
      expect(parsed.payload, <String, dynamic>{'path': 'kokona'});
    });

    test('reply round-trips', () {
      final env = BridgeEnvelope(
        v: 1,
        id: 7,
        type: BridgeMessageType.reply,
        name: 'model.discover',
        payload: <String, dynamic>{'ok': true},
      );
      final parsed = BridgeEnvelope.tryParse(env.toJson());
      expect(parsed!.type, BridgeMessageType.reply);
    });

    test('event round-trips', () {
      final env = BridgeEnvelope(
        v: 1,
        type: BridgeMessageType.event,
        name: 'toast',
        payload: <String, dynamic>{'message': '已切换'},
      );
      final parsed = BridgeEnvelope.tryParse(env.toJson());
      expect(parsed!.type, BridgeMessageType.event);
      expect(parsed.name, 'toast');
      expect(parsed.id, isNull);
    });

    test('rejects malformed input', () {
      expect(BridgeEnvelope.tryParse(null), isNull);
      expect(BridgeEnvelope.tryParse('nope'), isNull);
      expect(
          BridgeEnvelope.tryParse(
              <String, dynamic>{'type': 'wat', 'name': 'x'}),
          isNull);
    });
  });

  group('BridgeReply', () {
    test('parses ok + data', () {
      final reply = BridgeReply.tryParse(<String, dynamic>{
        'id': 3,
        'ok': true,
        'data': <String, dynamic>{'models': <dynamic>[]},
      });
      expect(reply, isNotNull);
      expect(reply!.ok, isTrue);
      expect(reply.id, 3);
      expect(reply.data, isNotNull);
    });

    test('parses error', () {
      final reply = BridgeReply.tryParse(<String, dynamic>{
        'id': 4,
        'ok': false,
        'error': 'bridge unavailable',
      });
      expect(reply!.ok, isFalse);
      expect(reply.error, 'bridge unavailable');
    });

    test('factory helpers', () {
      expect(BridgeReply.ok(1).ok, isTrue);
      expect(BridgeReply.fail(1, 'x').error, 'x');
    });
  });

  group('models', () {
    test('ModelInfo parse', () {
      final m = ModelInfo.fromJson(<String, dynamic>{
        'path': 'kokona',
        'displayName': 'Kokona',
        'size': '12.4 MB',
        'inUse': true,
      });
      expect(m.path, 'kokona');
      expect(m.displayName, 'Kokona');
      expect(m.size, '12.4 MB');
      expect(m.inUse, isTrue);
    });

    test('VmdActionInfo parse', () {
      final a = VmdActionInfo.fromJson(<String, dynamic>{
        'id': 'wave',
        'name': '挥手',
        'duration': '0:02',
        'frames': 60,
        'hasExpression': true,
      });
      expect(a.id, 'wave');
      expect(a.name, '挥手');
      expect(a.frames, 60);
      expect(a.hasExpression, isTrue);
    });

    test('FramingSnapshot parses anchors + d/h aliases', () {
      final snap = FramingSnapshot.fromEvent(<String, dynamic>{
        'valid': true,
        'screenWidthPx': 1440.0,
        'screenHeightPx': 3200.0,
        'topPx': 330.0,
        'bottomPx': 2640.0,
        'd': 0.79,
        'h': 1.50,
        'anchorKind': 'head',
        'degraded': false,
        'anchors': <String, dynamic>{
          'eye': <String, dynamic>{'x': 0.5, 'y': 0.33},
        },
      });
      expect(snap.valid, isTrue);
      expect(snap.screenWidthPx, 1440.0);
      expect(snap.screenHeightPx, 3200.0);
      expect(snap.topPx, 330.0);
      expect(snap.bottomPx, 2640.0);
      expect(snap.distance, closeTo(0.79, 0.001));
      expect(snap.cameraY, closeTo(1.50, 0.001));
      expect(snap.headAnchor, isTrue);
      expect(snap.anchors['eye']!.x, 0.5);
    });

    test('FramingSnapshot invalid fallback', () {
      final snap = FramingSnapshot.fromEvent(null);
      expect(snap.valid, isFalse);
      expect(FramingSnapshot.unavailable().valid, isFalse);
    });

    test('enum fromValue round-trips', () {
      expect(CoPresenceMode.fromValue('videoCall'), CoPresenceMode.videoCall);
      expect(VirtualEnvironment.fromValue('nightStreet'),
          VirtualEnvironment.nightStreet);
      expect(CoPresenceMode.fromValue('bogus'), isNull);
    });
  });

  test('cmd/event names are stable and non-empty', () {
    expect(Cmd.modelDiscover, 'model.discover');
    expect(Cmd.copresenceSwitchMode, 'copresence.switchMode');
    expect(Evt.framingAnchors, 'framing.anchors');
    expect(Evt.copresencePlacementChanged, 'copresence.placementChanged');
    expect(Evt.toast, 'toast');
  });
}

import 'dart:async';

import 'package:flutter_test/flutter_test.dart';

import 'package:banxia_flutter_ui/core/bridge/bridge_client.dart';
import 'package:banxia_flutter_ui/core/bridge/bridge_protocol.dart';
import 'package:banxia_flutter_ui/state/app_state.dart';

class _RejectingBridgeClient implements BridgeClient {
  final StreamController<BridgeEvent> _events =
      StreamController<BridgeEvent>.broadcast();

  @override
  Stream<BridgeEvent> get events => _events.stream;

  @override
  Future<BridgeReply> call(String name, [Map<String, dynamic>? payload]) async {
    if (name == Cmd.pairingSetServer || name == Cmd.pairingClearBinding) {
      return BridgeReply.fail(1, '配对操作失败');
    }
    return BridgeReply.ok(1);
  }

  @override
  void dispose() {
    _events.close();
  }
}

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

  test('switchTab updates the tab notifier and app notifier', () async {
    final bridge = LocalBridgeClient();
    final app = AppState(bridge);
    int notifications = 0;
    app.addListener(() => notifications++);

    app.switchTab(AppTab.chat);

    expect(app.tab.value, AppTab.chat);
    expect(notifications, 1);
    app.dispose();
    bridge.dispose();
  });

  test('local camera send preserves typed text and emits suggestions', () async {
    final bridge = LocalBridgeClient();
    final app = AppState(bridge);
    await bridge.call(Cmd.conversationSendWithCamera,
        <String, dynamic>{'text': '请描述这张照片'});
    await Future<void>.delayed(Duration.zero);
    expect(app.conversation.bubbles.first.text, '请描述这张照片');
    expect(app.conversation.suggestedReplies, hasLength(3));
    expect(app.conversation.suggestedReplies.first, '继续说说');
    app.dispose();
    bridge.dispose();
  });

  test('local pairing buffer mirrors append, removal, clear and server', () async {
    final bridge = LocalBridgeClient();
    final app = AppState(bridge);
    await bridge.call(Cmd.pairingSetServer,
        <String, dynamic>{'server': 'http://127.0.0.1:8080'});
    for (final digit in <String>['1', '2', '3', '4', '5', '6']) {
      app.appendPairingDigit(digit);
    }
    await Future<void>.delayed(Duration.zero);
    expect(app.connection.server, 'http://127.0.0.1:8080');
    expect(app.connection.serverDraft, 'http://127.0.0.1:8080');
    expect(app.connection.serverDraftDirty, isFalse);
    expect(app.connection.pairingCode, '123456');
    app.removePairingDigit();
    await Future<void>.delayed(Duration.zero);
    expect(app.connection.pairingCode, '12345');
    app.clearPairingCode();
    await Future<void>.delayed(Duration.zero);
    expect(app.connection.pairingCode, isEmpty);
    app.dispose();
    bridge.dispose();
  });

  test('pairing server draft rolls back when bridge rejects it', () async {
    final bridge = _RejectingBridgeClient();
    final app = AppState(bridge);
    app.connection.committedServer = 'https://old.example';
    app.connection.server = 'https://old.example';
    app.updatePairingServerDraft('https://new.example');

    final bool ok = await app.commitPairingServer(app.connection.serverDraft);

    expect(ok, isFalse);
    expect(app.connection.serverDraft, 'https://old.example');
    expect(app.connection.committedServer, 'https://old.example');
    app.dispose();
    bridge.dispose();
  });

  test('clear binding clears the committed server after bridge success', () async {
    final bridge = LocalBridgeClient();
    final app = AppState(bridge);
    await app.commitPairingServer('https://old.example');

    final bool ok = await app.dispatch(Cmd.pairingClearBinding);
    await Future<void>.delayed(Duration.zero);

    expect(ok, isTrue);
    expect(app.connection.server, isEmpty);
    expect(app.connection.serverDraft, isEmpty);
    expect(app.connection.committedServer, isEmpty);
    expect(app.connection.serverDraftDirty, isFalse);
    expect(app.connection.connected, isFalse);
    app.dispose();
    bridge.dispose();
  });

  test('clear binding restores server when bridge rejects it', () async {
    final bridge = _RejectingBridgeClient();
    final app = AppState(bridge);
    app.connection.server = 'https://old.example';
    app.connection.serverDraft = 'https://draft.example';
    app.connection.committedServer = 'https://old.example';
    app.connection.serverDraftDirty = true;

    final bool ok = await app.dispatch(Cmd.pairingClearBinding);

    expect(ok, isFalse);
    expect(app.connection.server, 'https://old.example');
    expect(app.connection.serverDraft, 'https://draft.example');
    expect(app.connection.committedServer, 'https://old.example');
    expect(app.connection.serverDraftDirty, isTrue);
    app.dispose();
    bridge.dispose();
  });

  test('pairing server commit trims and stores a successful address', () async {
    final bridge = LocalBridgeClient();
    final app = AppState(bridge);

    final bool ok = await app.commitPairingServer('  https://new.example  ');
    await Future<void>.delayed(Duration.zero);

    expect(ok, isTrue);
    expect(app.connection.server, 'https://new.example');
    expect(app.connection.serverDraft, 'https://new.example');
    expect(app.connection.committedServer, 'https://new.example');
    expect(app.connection.serverDraftDirty, isFalse);
    app.dispose();
    bridge.dispose();
  });

  test('pairing status events do not overwrite an active server draft', () async {
    final bridge = LocalBridgeClient();
    final app = AppState(bridge);
    await bridge.call(Cmd.pairingSetServer,
        <String, dynamic>{'server': 'https://old.example'});
    await Future<void>.delayed(Duration.zero);

    app.updatePairingServerDraft('https://draft.example');
    await bridge.call(Cmd.pairingSetServer,
        <String, dynamic>{'server': 'https://old.example'});
    await Future<void>.delayed(Duration.zero);

    expect(app.connection.server, 'https://old.example');
    expect(app.connection.committedServer, 'https://old.example');
    expect(app.connection.serverDraft, 'https://draft.example');
    expect(app.connection.serverDraftDirty, isTrue);
    app.dispose();
    bridge.dispose();
  });

  test('cmd/event names are stable and non-empty', () {
    expect(Cmd.modelDiscover, 'model.discover');
    expect(Cmd.copresenceSwitchMode, 'copresence.switchMode');
    expect(Evt.framingAnchors, 'framing.anchors');
    expect(Evt.conversationSuggestions, 'conversation.suggestions');
    expect(Evt.copresencePlacementChanged, 'copresence.placementChanged');
    expect(Evt.toast, 'toast');
  });
}

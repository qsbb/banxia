import 'dart:async';

import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:banxia_flutter_ui/core/bridge/bridge_client.dart';
import 'package:banxia_flutter_ui/core/bridge/bridge_protocol.dart';
import 'package:banxia_flutter_ui/screens/settings_screen.dart';
import 'package:banxia_flutter_ui/state/app_state.dart';

class _TestBridge implements BridgeClient {
  final controller = StreamController<BridgeEvent>.broadcast(sync: true);
  bool accept = true;
  Map<String, dynamic>? lastPayload;

  @override
  Stream<BridgeEvent> get events => controller.stream;

  @override
  Future<BridgeReply> call(String name, [Map<String, dynamic>? payload]) async {
    lastPayload = payload;
    return accept ? BridgeReply.ok(1) : BridgeReply.fail(1, 'rejected');
  }

  @override
  void dispose() => controller.close();
}

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();

  test('debug mode follows accepted commands and preserves rejected state', () async {
    final bridge = _TestBridge();
    final app = AppState(bridge);
    addTearDown(app.dispose);
    addTearDown(bridge.dispose);
    expect(app.settings.debugMode, isFalse);
    expect(app.settings.targetFps, 120);
    expect(app.quality.fps, 120);

    await app.toggleSetting('debugMode', true);
    expect(app.settings.debugMode, isTrue);
    expect(bridge.lastPayload, {'key': 'debugMode', 'value': true});
    bridge.accept = false;
    await app.toggleSetting('debugMode', false);
    expect(app.settings.debugMode, isTrue);
    bridge.accept = true;
    await app.toggleSetting('debugMode', false);
    expect(app.settings.debugMode, isFalse);
  });

  testWidgets('settings screen exposes one prominent debug toggle',
      (WidgetTester tester) async {
    final bridge = _TestBridge();
    final app = AppState(bridge);
    addTearDown(app.dispose);
    addTearDown(bridge.dispose);
    await tester.pumpWidget(MaterialApp(
      home: Scaffold(body: SettingsScreen(appState: app)),
    ));
    expect(find.text('调试模式（不拦截报错）'), findsOneWidget);
    expect(find.text('开启后异常会写入完整堆栈并重新抛出，仅建议改 bug 时使用。'),
        findsOneWidget);
    await tester.tap(find.byType(Switch).first);
    await tester.pumpAndSettle();
    expect(app.settings.debugMode, isTrue);
    expect(bridge.lastPayload, {'key': 'debugMode', 'value': true});
  });

  test('quality events hydrate shared FPS volume and debug settings', () {
    final bridge = _TestBridge();
    final app = AppState(bridge);
    addTearDown(app.dispose);
    addTearDown(bridge.dispose);
    bridge.controller.add(const BridgeEvent(Evt.qualityChanged,
        {'targetFps': 30, 'volume': 0.4, 'debugMode': true}));
    expect(app.settings.targetFps, 30);
    expect(app.quality.fps, 30);
    expect(app.settings.volume, 0.4);
    expect(app.quality.volume, 0.4);
    expect(app.settings.debugMode, isTrue);
    bridge.controller.add(const BridgeEvent(Evt.qualityChanged,
        {'targetFps': 72, 'volume': double.nan}));
    expect(app.settings.targetFps, 30);
    expect(app.settings.volume, 0.4);
    expect(app.settings.debugMode, isTrue);
  });

  test('local setting events preserve render and physics selections', () async {
    final bridge = LocalBridgeClient();
    final app = AppState(bridge);
    addTearDown(app.dispose);
    addTearDown(bridge.dispose);
    await app.dispatch(Cmd.qualityApplyPreset, {'preset': 'clear'});
    await app.dispatch(Cmd.qualityApplyPhysics, {'preset': 'fine'});
    await app.setTargetFps(30);
    await app.setVolume(0.4);
    await app.toggleSetting('debugMode', true);
    await Future<void>.delayed(Duration.zero);
    expect(app.quality.renderPreset, 'clear');
    expect(app.quality.physicsPreset, 'fine');
    expect(app.quality.fps, 30);
    expect(app.quality.volume, 0.4);
    expect(app.settings.debugMode, isTrue);
    await app.dispatch(Cmd.qualityReset);
    await Future<void>.delayed(Duration.zero);
    expect(app.quality.renderPreset, 'balanced');
    expect(app.quality.physicsPreset, 'balanced');
    expect(app.quality.fps, 30);
  });

  test('production bridge rethrows platform faults only in debug mode', () async {
    final messenger = TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger;
    const method = MethodChannel('banxia.bridge');
    const events = MethodChannel('banxia.events');
    messenger.setMockMethodCallHandler(events, (_) async => null);
    messenger.setMockMethodCallHandler(method, (call) async {
      final args = call.arguments as Map;
      if (args['name'] == Cmd.settingsToggle) return {'id': 1, 'ok': true};
      throw PlatformException(code: 'test', message: 'native failure');
    });
    final bridge = ChannelBridgeClient();
    addTearDown(() {
      bridge.dispose();
      messenger.setMockMethodCallHandler(method, null);
      messenger.setMockMethodCallHandler(events, null);
    });
    expect((await bridge.call(Cmd.modelDiscover)).ok, isFalse);
    await bridge.call(Cmd.settingsToggle, {'key': 'debugMode', 'value': true});
    await expectLater(bridge.call(Cmd.modelDiscover), throwsA(isA<PlatformException>()));
    await bridge.call(Cmd.settingsToggle, {'key': 'debugMode', 'value': false});
    expect((await bridge.call(Cmd.modelDiscover)).ok, isFalse);
  });
}

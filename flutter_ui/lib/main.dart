import 'dart:async';

import 'package:flutter/foundation.dart';
import 'package:flutter/material.dart';

import 'core/bridge/bridge_client.dart';
import 'screens/root_shell.dart';
import 'state/app_state.dart';

/// iOS 26 Liquid Glass (light) token set, ported 1:1 from
/// `Assets/UI/Resources/BanxiaTheme.uss`. Values follow the 390pt reference
/// canvas so the Flutter shell matches the existing phone shell pixel-for-pixel.
abstract final class BanxiaTokens {
  // Content surfaces.
  static const Color bg = Color(0xFFF2F2F7);
  static const Color bgElevated = Color(0xFFFFFFFF);
  static const Color bgCard = Color(0xFFFFFFFF);
  static const Color bgCardActive = Color(0xFFE5E5EA);
  static const Color separator = Color(0x4A3C3C43); // rgba(60,60,67,0.29)

  // Text.
  static const Color label = Color(0xFF000000);
  static const Color labelSecondary = Color(0x993C3C43); // rgba(60,60,67,0.6)
  static const Color labelTertiary = Color(0x4D3C3C43); // rgba(60,60,67,0.3)

  // System colors.
  static const Color tint = Color(0xFF007AFF);
  static const Color tintPressed = Color(0xFF0062CC);
  static const Color tintFill = Color(0xE6007AFF); // rgba(0,122,255,0.9)
  static const Color green = Color(0xFF34C759);
  static const Color red = Color(0xFFFF3B30);
  static const Color orange = Color(0xFFFF9500);

  // Liquid glass.
  static const Color glass = Color(0x1F787880); // rgba(120,120,128,0.12)
  static const Color glassPressed = Color(0x33787880); // 0.20
  static const Color glassChrome = Color(0xC7FFFFFF); // 0.78
  static const Color glassSelected = Color(0x0F000000);

  // Radii.
  static const double radiusCard = 20;
  static const double radiusGroup = 10;
  static const double radiusCapsule = 999;

  // M2 modal scrim (design §4 / .cp-backdrop).
  static const Color scrim = Color(0x66000000); // rgba(0,0,0,0.4)
}

/// Select the native transport automatically on Android. The explicit define
/// remains useful for host-side smoke tests; `false` forces the local demo.
const String _kChannelMode =
    String.fromEnvironment('BANXIA_CHANNEL', defaultValue: 'auto');

bool get _useChannelBridge {
  if (_kChannelMode == 'true') return true;
  if (_kChannelMode == 'false') return false;
  return !kIsWeb && defaultTargetPlatform == TargetPlatform.android;
}

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final BridgeClient bridge =
      _useChannelBridge ? ChannelBridgeClient() : LocalBridgeClient();
  final AppState appState = AppState(bridge);
  if (!_useChannelBridge) {
    unawaited(appState.bootstrap());
  }
  runApp(BanxiaApp(appState: appState));
}

class BanxiaApp extends StatelessWidget {
  const BanxiaApp({super.key, required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return MaterialApp(
      title: '伴夏',
      debugShowCheckedModeBanner: false,
      theme: ThemeData(
        useMaterial3: true,
        brightness: Brightness.light,
        scaffoldBackgroundColor: Colors.transparent,
        colorScheme: const ColorScheme.light(
          primary: BanxiaTokens.tint,
          secondary: BanxiaTokens.tint,
          surface: BanxiaTokens.bgCard,
          onPrimary: Colors.white,
          onSurface: BanxiaTokens.label,
        ),
        splashFactory: InkSparkle.splashFactory,
        fontFamily: 'Roboto',
      ),
      home: RootShell(appState: appState),
    );
  }
}

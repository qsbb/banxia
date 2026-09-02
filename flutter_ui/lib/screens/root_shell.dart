import 'package:flutter/material.dart';

import '../main.dart';
import '../scene/scene_overlay.dart';
import '../state/app_state.dart';
import 'actions_screen.dart';
import 'chat_screen.dart';
import 'companion_screen.dart';
import 'settings_screen.dart';

/// Root shell (design §2.1): Menu/Scene two-state shell with a 4-tab bottom
/// bar and a global toast overlay at the highest z-order. Scene mode replaces
/// the whole menu shell with the full-screen [SceneOverlay].
class RootShell extends StatelessWidget {
  const RootShell({super.key, required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: Listenable.merge(<Listenable>[
        appState,
        appState.uiMode,
        appState.tab,
        appState.toast,
      ]),
      builder: (BuildContext context, Widget? _) {
        return Scaffold(
          backgroundColor: Colors.transparent,
          body: Stack(
            fit: StackFit.expand,
            children: <Widget>[
              appState.inScene
                  ? SceneOverlay(appState: appState)
                  : _MenuShell(appState: appState),
              _ToastOverlay(appState: appState),
            ],
          ),
        );
      },
    );
  }
}

class _MenuShell extends StatelessWidget {
  const _MenuShell({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ColoredBox(
      color: BanxiaTokens.bg,
      child: Column(
        children: <Widget>[
          Expanded(
            child: IndexedStack(
              index: appState.tab.value.index,
              children: <Widget>[
                CompanionScreen(appState: appState),
                ChatScreen(appState: appState),
                ActionsScreen(appState: appState),
                SettingsScreen(appState: appState),
              ],
            ),
          ),
          _BottomNav(appState: appState),
        ],
      ),
    );
  }
}

/// iOS 26 floating glass capsule tab bar (4 items, tint selected).
class _BottomNav extends StatelessWidget {
  const _BottomNav({required this.appState});

  final AppState appState;

  static const List<(AppTab, IconData, String)> _tabs =
      <(AppTab, IconData, String)>[
    (AppTab.companion, Icons.person_outline, '首页'),
    (AppTab.chat, Icons.chat_bubble_outline, '对话'),
    (AppTab.actions, Icons.auto_awesome_outlined, '动作'),
    (AppTab.settings, Icons.settings_outlined, '设置'),
  ];

  @override
  Widget build(BuildContext context) {
    final AppTab current = appState.tab.value;
    return SafeArea(
      top: false,
      child: Container(
        height: 64,
        margin: const EdgeInsets.fromLTRB(20, 0, 20, 20),
        decoration: BoxDecoration(
          color: const Color(0xEBFFFFFF), // white 0.92 glass chrome
          borderRadius: BorderRadius.circular(32),
          border: Border.all(color: const Color(0x1F000000), width: 1),
        ),
        child: Row(
          children: <Widget>[
            for (final (tab, icon, label) in _tabs)
              Expanded(
                child: _TabItem(
                  icon: icon,
                  label: label,
                  selected: tab == current,
                  onTap: () => appState.switchTab(tab),
                ),
              ),
          ],
        ),
      ),
    );
  }
}

class _TabItem extends StatelessWidget {
  const _TabItem({
    required this.icon,
    required this.label,
    required this.selected,
    required this.onTap,
  });

  final IconData icon;
  final String label;
  final bool selected;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      behavior: HitTestBehavior.opaque,
      child: AnimatedContainer(
        duration: const Duration(milliseconds: 160),
        margin: const EdgeInsets.all(6),
        decoration: BoxDecoration(
          color: selected ? BanxiaTokens.glassSelected : Colors.transparent,
          borderRadius: BorderRadius.circular(24),
        ),
        child: Column(
          mainAxisAlignment: MainAxisAlignment.center,
          children: <Widget>[
            Icon(
              icon,
              size: 22,
              color: selected ? BanxiaTokens.tint : const Color(0x99000000),
            ),
            const SizedBox(height: 2),
            Text(
              label,
              style: TextStyle(
                fontSize: 11,
                fontWeight: selected ? FontWeight.bold : FontWeight.normal,
                color: selected ? BanxiaTokens.tint : const Color(0x99000000),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

/// Global toast, always on top (z-order above the scene sheet, per M2).
class _ToastOverlay extends StatelessWidget {
  const _ToastOverlay({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ValueListenableBuilder<ToastData?>(
      valueListenable: appState.toast,
      builder: (BuildContext context, ToastData? toast, Widget? _) {
        return IgnorePointer(
          child: AnimatedOpacity(
            opacity: toast == null ? 0 : 1,
            duration: const Duration(milliseconds: 160),
            child: Align(
              alignment: Alignment.bottomCenter,
              child: Container(
                margin: const EdgeInsets.only(bottom: 96, left: 40, right: 40),
                padding:
                    const EdgeInsets.symmetric(horizontal: 22, vertical: 12),
                decoration: BoxDecoration(
                  color: BanxiaTokens.glassChrome,
                  borderRadius: BorderRadius.circular(999),
                  border: Border.all(color: const Color(0x1F000000), width: 1),
                ),
                child: Text(
                  toast?.message ?? '',
                  textAlign: TextAlign.center,
                  style: const TextStyle(
                    fontSize: 15,
                    color: BanxiaTokens.label,
                  ),
                ),
              ),
            ),
          ),
        );
      },
    );
  }
}

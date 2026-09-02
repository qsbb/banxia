import 'package:flutter/material.dart';

import '../core/bridge/bridge_protocol.dart';
import '../main.dart';
import '../qa/framing_grid.dart';
import '../state/app_state.dart';

/// Scene-mode full-screen overlay (design §2.6).
///
/// Z-order (M2, INV-5): chrome < scrim < sheet < toast (toast lives in the
/// root shell above this overlay). Opening the modal hides the call controls
/// (not just dims them) and shows a 40% scrim; three close paths: scrim tap,
/// grabber drag ≥120px, and selecting a mode card.
class SceneOverlay extends StatelessWidget {
  const SceneOverlay({super.key, required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: appState,
      builder: (BuildContext context, Widget? _) {
        final CoPresenceState cp = appState.copresence;
        final bool videoCall =
            appState.inScene && cp.mode == CoPresenceMode.videoCall;
        final bool arReality = appState.inScene &&
            cp.mode == CoPresenceMode.arReality &&
            !cp.arPlaced;

        return Stack(
          fit: StackFit.expand,
          children: <Widget>[
            const _SceneBackdrop(),
            if (appState.settings.framingGrid)
              Positioned.fill(
                child: FramingGrid(
                  snapshot: appState.framing,
                  devicePixelRatio: MediaQuery.of(context).devicePixelRatio,
                ),
              ),
            // AR placement: transparent full-screen tap catcher that forwards
            // logical (top-origin) taps to the engine as physical pixels via
            // `copresence.arPlace{x,y}`. The hint above it is IgnorePointer
            // so taps pass through to this layer; the modal sits above and
            // wins while open.
            if (arReality)
              Positioned.fill(
                child: GestureDetector(
                  behavior: HitTestBehavior.translucent,
                  onTapUp: (TapUpDetails details) =>
                      appState.arPlaceAt(details.localPosition),
                  child: const SizedBox.expand(),
                ),
              ),
            if (videoCall)
              _VideoCallChrome(
                appState: appState,
                controlsHidden: cp.sheetOpen,
              )
            else if (arReality)
              const _ArPlaceHint()
            else
              _SceneToolbar(appState: appState),
            _CopresenceModal(appState: appState),
          ],
        );
      },
    );
  }
}

/// The Unity camera owns the scene pixels. Flutter contributes only transparent
/// overlay layers in scene mode so the 3D avatar remains visible beneath them.
class _SceneBackdrop extends StatelessWidget {
  const _SceneBackdrop();

  @override
  Widget build(BuildContext context) => const SizedBox.expand();
}

// ── Scene toolbar (non-video-call modes) ────────────────────────────────────
class _SceneToolbar extends StatelessWidget {
  const _SceneToolbar({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final CoPresenceState cp = appState.copresence;
    final String modeLabel =
        cp.mode == CoPresenceMode.virtualScene ? '环境' : '模式';
    return Positioned(
      left: 0,
      right: 0,
      bottom: 32,
      child: Row(
        mainAxisAlignment: MainAxisAlignment.center,
        children: <Widget>[
          _ToolPill(label: '主界面', onTap: appState.returnToMenu),
          _ToolPill(
              label: '移动', onTap: () => appState.dispatch(Cmd.sceneMoveMode)),
          _ToolPill(
              label: modeLabel, onTap: () => appState.toggleSheet('modes')),
          _ToolPill(
              label: '取景', onTap: () => appState.dispatch(Cmd.sceneReframe)),
          _ToolPill(label: 'HUD', onTap: () => appState.dispatch(Cmd.sceneHud)),
        ],
      ),
    );
  }
}

class _ToolPill extends StatelessWidget {
  const _ToolPill({required this.label, required this.onTap});

  final String label;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        margin: const EdgeInsets.symmetric(horizontal: 5),
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
        decoration: BoxDecoration(
          color: BanxiaTokens.glassChrome,
          borderRadius: BorderRadius.circular(999),
          border: Border.all(color: const Color(0x26000000), width: 1),
        ),
        child: Text(label,
            style: const TextStyle(fontSize: 15, color: BanxiaTokens.label)),
      ),
    );
  }
}

// ── AR place hint (does not intercept input) ────────────────────────────────
class _ArPlaceHint extends StatelessWidget {
  const _ArPlaceHint();

  @override
  Widget build(BuildContext context) {
    return IgnorePointer(
      child: Align(
        alignment: const Alignment(0, 0.45),
        child: Container(
          padding: const EdgeInsets.all(20),
          decoration: BoxDecoration(
            color: BanxiaTokens.glassChrome,
            borderRadius: BorderRadius.circular(16),
          ),
          child: const Column(
            mainAxisSize: MainAxisSize.min,
            children: <Widget>[
              Text('点按地面，把她放进来',
                  style: TextStyle(
                      fontWeight: FontWeight.bold,
                      fontSize: 17,
                      color: BanxiaTokens.label)),
              SizedBox(height: 6),
              Text('拖动移动 · 双指缩放 · 长按环绕',
                  style: TextStyle(
                      fontSize: 14, color: BanxiaTokens.labelSecondary)),
            ],
          ),
        ),
      ),
    );
  }
}

// ── Video call chrome (M1 inset measurement) ────────────────────────────────
class _VideoCallChrome extends StatefulWidget {
  const _VideoCallChrome({
    required this.appState,
    required this.controlsHidden,
  });

  final AppState appState;
  final bool controlsHidden;

  @override
  State<_VideoCallChrome> createState() => _VideoCallChromeState();
}

class _VideoCallChromeState extends State<_VideoCallChrome> {
  final GlobalKey _topKey = GlobalKey();
  final GlobalKey _controlsKey = GlobalKey();

  @override
  void didUpdateWidget(covariant _VideoCallChrome oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.controlsHidden != widget.controlsHidden) {
      WidgetsBinding.instance.addPostFrameCallback((_) => _measure());
    }
  }

  @override
  Widget build(BuildContext context) {
    WidgetsBinding.instance.addPostFrameCallback((_) => _measure());
    final CoPresenceState cp = widget.appState.copresence;
    return Positioned.fill(
      child: Column(
        mainAxisAlignment: MainAxisAlignment.spaceBetween,
        children: <Widget>[
          Padding(
            key: _topKey,
            padding: const EdgeInsets.fromLTRB(20, 20, 20, 0),
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 18, vertical: 12),
              decoration: BoxDecoration(
                color: BanxiaTokens.glassChrome,
                borderRadius: BorderRadius.circular(22),
                border: Border.all(color: const Color(0x26000000), width: 1),
              ),
              child: Row(
                children: <Widget>[
                  Container(
                    width: 10,
                    height: 10,
                    decoration: const BoxDecoration(
                      color: BanxiaTokens.green,
                      shape: BoxShape.circle,
                    ),
                  ),
                  const SizedBox(width: 8),
                  const Text('伴夏',
                      style: TextStyle(
                          fontWeight: FontWeight.bold,
                          fontSize: 17,
                          color: BanxiaTokens.label)),
                  const Spacer(),
                  Text(cp.callDuration,
                      style: const TextStyle(
                          fontWeight: FontWeight.bold,
                          fontSize: 17,
                          color: BanxiaTokens.labelSecondary)),
                ],
              ),
            ),
          ),
          if (!widget.controlsHidden)
            Padding(
              key: _controlsKey,
              padding: const EdgeInsets.only(bottom: 40),
              child: Row(
                mainAxisAlignment: MainAxisAlignment.center,
                children: <Widget>[
                  _CallButton(
                    label: '挂断',
                    color: BanxiaTokens.red,
                    onTap: widget.appState.returnToMenu,
                  ),
                  _CallButton(
                    label: '模式',
                    color: BanxiaTokens.glassChrome,
                    onTap: () => widget.appState.toggleSheet('modes'),
                  ),
                  _CallButton(
                    label: '去聊天',
                    color: BanxiaTokens.glassChrome,
                    onTap: widget.appState.returnToMenu,
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  void _measure() {
    if (!mounted || widget.controlsHidden) return;
    final RenderBox? topBox =
        _topKey.currentContext?.findRenderObject() as RenderBox?;
    final RenderBox? controlsBox =
        _controlsKey.currentContext?.findRenderObject() as RenderBox?;
    if (topBox == null || controlsBox == null) return;
    if (!topBox.hasSize || !controlsBox.hasSize) return;
    final Offset topTopLeft = topBox.localToGlobal(Offset.zero);
    final Offset controlsTopLeft = controlsBox.localToGlobal(Offset.zero);
    final double top = topTopLeft.dy + topBox.size.height;
    final double bottom = controlsTopLeft.dy;
    widget.appState.updateChromeInsets(top, bottom);
  }
}

class _CallButton extends StatelessWidget {
  const _CallButton({
    required this.label,
    required this.color,
    required this.onTap,
  });

  final String label;
  final Color color;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final bool light = color.computeLuminance() > 0.5;
    return GestureDetector(
      onTap: onTap,
      child: Container(
        margin: const EdgeInsets.symmetric(horizontal: 8),
        padding: const EdgeInsets.symmetric(horizontal: 22, vertical: 12),
        decoration: BoxDecoration(
          color: color,
          borderRadius: BorderRadius.circular(26),
          border: Border.all(color: const Color(0x14000000)),
        ),
        child: Text(
          label,
          style: TextStyle(
            fontSize: 15,
            fontWeight: FontWeight.bold,
            color: light ? BanxiaTokens.label : Colors.white,
          ),
        ),
      ),
    );
  }
}

// ── M2 modal (scrim + sheet, animated) ──────────────────────────────────────
class _CopresenceModal extends StatelessWidget {
  const _CopresenceModal({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final bool open = appState.copresence.sheetOpen;
    return Positioned.fill(
      child: IgnorePointer(
        ignoring: !open,
        child: Stack(
          children: <Widget>[
            AnimatedOpacity(
              opacity: open ? 1 : 0,
              duration: const Duration(milliseconds: 120),
              child: GestureDetector(
                onTap: appState.closeSheet,
                child: const ColoredBox(color: BanxiaTokens.scrim),
              ),
            ),
            Align(
              alignment: Alignment.bottomCenter,
              child: AnimatedSlide(
                offset: open ? Offset.zero : const Offset(0, 1),
                duration: const Duration(milliseconds: 160),
                curve: Curves.easeOut,
                child: _CopresenceSheet(appState: appState),
              ),
            ),
          ],
        ),
      ),
    );
  }
}

class _CopresenceSheet extends StatefulWidget {
  const _CopresenceSheet({required this.appState});

  final AppState appState;

  @override
  State<_CopresenceSheet> createState() => _CopresenceSheetState();
}

class _CopresenceSheetState extends State<_CopresenceSheet> {
  double _dragDown = 0;

  void _onDragUpdate(DragUpdateDetails details) {
    _dragDown += details.delta.dy;
    if (_dragDown >= 120) {
      _dragDown = 0;
      widget.appState.closeSheet();
    }
  }

  @override
  Widget build(BuildContext context) {
    final CoPresenceState cp = widget.appState.copresence;
    final bool environments = cp.sheetKind == 'environments';
    return Container(
      width: double.infinity,
      padding: const EdgeInsets.fromLTRB(16, 12, 16, 28),
      decoration: const BoxDecoration(
        color: BanxiaTokens.bgElevated,
        borderRadius: BorderRadius.vertical(top: Radius.circular(16)),
      ),
      child: SafeArea(
        top: false,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: <Widget>[
            Center(
              child: GestureDetector(
                onTap: widget.appState.closeSheet,
                onVerticalDragUpdate: _onDragUpdate,
                onVerticalDragEnd: (_) => _dragDown = 0,
                child: Container(
                  width: 120,
                  height: 6,
                  decoration: BoxDecoration(
                    color: BanxiaTokens.glass,
                    borderRadius: BorderRadius.circular(3),
                  ),
                ),
              ),
            ),
            const SizedBox(height: 12),
            Text(
              environments ? '虚拟环境' : '和她同框',
              style: const TextStyle(
                  fontSize: 20,
                  fontWeight: FontWeight.bold,
                  color: BanxiaTokens.label),
            ),
            const SizedBox(height: 12),
            if (environments)
              _EnvironmentChips(appState: widget.appState)
            else
              _ModeCards(appState: widget.appState),
          ],
        ),
      ),
    );
  }
}

class _ModeCards extends StatelessWidget {
  const _ModeCards({required this.appState});

  final AppState appState;

  static const List<(CoPresenceMode, String, String)> _modes =
      <(CoPresenceMode, String, String)>[
    (CoPresenceMode.arReality, 'AR · 相机取景', '点按地面，把她放进你的房间'),
    (CoPresenceMode.virtualScene, '伪 AR · 虚拟环境', '夜街 / 星空 / 卧室 / 海边'),
    (CoPresenceMode.videoCall, '半身 · 通话感', '胸像出镜 · 字幕 · 通话计时'),
  ];

  @override
  Widget build(BuildContext context) {
    final CoPresenceState cp = appState.copresence;
    return Column(
      children: <Widget>[
        for (final (CoPresenceMode mode, String tag, String desc) in _modes)
          _ModeCard(
            title: mode.label,
            tag: tag,
            desc: desc,
            current: cp.mode == mode,
            disabled: mode == CoPresenceMode.arReality && !cp.arAvailable,
            onTap: () => appState.switchMode(mode),
          ),
      ],
    );
  }
}

class _ModeCard extends StatelessWidget {
  const _ModeCard({
    required this.title,
    required this.tag,
    required this.desc,
    required this.current,
    required this.disabled,
    required this.onTap,
  });

  final String title;
  final String tag;
  final String desc;
  final bool current;
  final bool disabled;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return Opacity(
      opacity: disabled ? 0.45 : 1,
      child: GestureDetector(
        onTap: disabled ? null : onTap,
        child: Container(
          width: double.infinity,
          margin: const EdgeInsets.only(bottom: 10),
          padding: const EdgeInsets.all(14),
          decoration: BoxDecoration(
            color: BanxiaTokens.bgCard,
            borderRadius: BorderRadius.circular(12),
            border: Border.all(
              color: current ? BanxiaTokens.tint : const Color(0x14000000),
              width: current ? 2 : 1,
            ),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: <Widget>[
              Row(
                children: <Widget>[
                  Text(title,
                      style: const TextStyle(
                          fontWeight: FontWeight.bold,
                          fontSize: 17,
                          color: BanxiaTokens.label)),
                  const SizedBox(width: 8),
                  Text(tag,
                      style: const TextStyle(
                          fontSize: 12, color: BanxiaTokens.tint)),
                  const Spacer(),
                  if (current)
                    const Icon(Icons.check, color: BanxiaTokens.tint, size: 20),
                ],
              ),
              const SizedBox(height: 4),
              Text(desc,
                  style: const TextStyle(
                      fontSize: 14, color: BanxiaTokens.labelSecondary)),
            ],
          ),
        ),
      ),
    );
  }
}

class _EnvironmentChips extends StatelessWidget {
  const _EnvironmentChips({required this.appState});

  final AppState appState;

  @override
  Widget build(BuildContext context) {
    final CoPresenceState cp = appState.copresence;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: <Widget>[
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: <Widget>[
            for (final VirtualEnvironment env in VirtualEnvironment.values)
              _Chip(
                label: env.label,
                current: cp.environment == env,
                onTap: () => appState.switchEnvironment(env),
              ),
          ],
        ),
        const SizedBox(height: 10),
        const Text('环境光照自动匹配角色亮度 · 物理与画质跟随设置',
            style: TextStyle(fontSize: 12, color: BanxiaTokens.labelTertiary)),
        const SizedBox(height: 8),
        GestureDetector(
          onTap: () => appState.openSheet('modes'),
          child: const Padding(
            padding: EdgeInsets.symmetric(vertical: 10),
            child: Text('换种同框方式',
                style: TextStyle(
                    fontWeight: FontWeight.bold,
                    fontSize: 15,
                    color: BanxiaTokens.tint)),
          ),
        ),
      ],
    );
  }
}

class _Chip extends StatelessWidget {
  const _Chip({
    required this.label,
    required this.current,
    required this.onTap,
  });

  final String label;
  final bool current;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    return GestureDetector(
      onTap: onTap,
      child: Container(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
        decoration: BoxDecoration(
          color: current ? BanxiaTokens.glassSelected : BanxiaTokens.glass,
          borderRadius: BorderRadius.circular(999),
          border: Border.all(
            color: current ? const Color(0x73007AFF) : const Color(0x0F000000),
          ),
        ),
        child: Text(
          label,
          style: TextStyle(
            fontSize: 15,
            fontWeight: current ? FontWeight.bold : FontWeight.normal,
            color: current ? BanxiaTokens.tint : BanxiaTokens.labelSecondary,
          ),
        ),
      ),
    );
  }
}

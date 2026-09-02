import 'dart:async';

import 'package:flutter/material.dart';

import '../main.dart';

/// Derived keypad geometry (M3, INV-3). All values come from the measured
/// available height/width; zero magic numbers:
///
///   H = availableHeight / 16        (key height)
///   W = (availableWidth − 2·edge − 2·colGap) / 3
///   F = 0.42 · H                    (digit font size)
///   R = H / 2                       (exact capsule radius)
///   rowGap = 0.16 · H, colGap = 0.12 · W, edge = 24px
class PairingNumpadMetrics {
  PairingNumpadMetrics.compute({
    required double availableHeight,
    required double availableWidth,
  })  : availableHeight = availableHeight,
        availableWidth = availableWidth,
        edgeMargin = _edgeMargin,
        keyHeight = availableHeight / _heightDivisor,
        keyWidth =
            (availableWidth - 2 * _edgeMargin) / (3 + 2 * _columnGapRatio),
        radius = (availableHeight / _heightDivisor) / 2,
        fontSize = _fontRatio * (availableHeight / _heightDivisor),
        rowGap = _rowGapRatio * (availableHeight / _heightDivisor),
        columnGap = _columnGapRatio *
            ((availableWidth - 2 * _edgeMargin) / (3 + 2 * _columnGapRatio));

  static const double _edgeMargin = 24;
  static const double _heightDivisor = 16;
  static const double _columnGapRatio = 0.12;
  static const double _rowGapRatio = 0.16;
  static const double _fontRatio = 0.42;

  final double availableHeight;
  final double availableWidth;
  final double edgeMargin;
  final double keyHeight;
  final double keyWidth;
  final double radius;
  final double fontSize;
  final double rowGap;
  final double columnGap;
}

/// M3 pairing keypad: 3×4 iOS phone layout `1..9 / ⌫ 0 ✓`.
///
/// - ⌫ short press = backspace, long press (≥600 ms) = clear.
/// - ✓ submit (the six-digit validation + toast lives in [AppState]).
/// - Chinese semantic labels are exposed for accessibility.
class PairingNumpad extends StatelessWidget {
  const PairingNumpad({
    super.key,
    required this.availableHeight,
    required this.availableWidth,
    required this.onDigit,
    required this.onBackspace,
    required this.onClear,
    required this.onSubmit,
  });

  final double availableHeight;
  final double availableWidth;
  final ValueChanged<String> onDigit;
  final VoidCallback onBackspace;
  final VoidCallback onClear;
  final VoidCallback onSubmit;

  @override
  Widget build(BuildContext context) {
    final m = PairingNumpadMetrics.compute(
      availableHeight: availableHeight,
      availableWidth: availableWidth,
    );
    return Column(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        _row(m, <_KeySpec>[
          _KeySpec('1', () => onDigit('1'), semantic: '1'),
          _KeySpec('2', () => onDigit('2'), semantic: '2'),
          _KeySpec('3', () => onDigit('3'), semantic: '3'),
        ]),
        SizedBox(height: m.rowGap),
        _row(m, <_KeySpec>[
          _KeySpec('4', () => onDigit('4'), semantic: '4'),
          _KeySpec('5', () => onDigit('5'), semantic: '5'),
          _KeySpec('6', () => onDigit('6'), semantic: '6'),
        ]),
        SizedBox(height: m.rowGap),
        _row(m, <_KeySpec>[
          _KeySpec('7', () => onDigit('7'), semantic: '7'),
          _KeySpec('8', () => onDigit('8'), semantic: '8'),
          _KeySpec('9', () => onDigit('9'), semantic: '9'),
        ]),
        SizedBox(height: m.rowGap),
        _row(m, <_KeySpec>[
          _KeySpec(
            '⌫',
            onBackspace,
            semantic: '退格',
            onLongPress: onClear,
          ),
          _KeySpec('0', () => onDigit('0'), semantic: '0'),
          _KeySpec('✓', onSubmit, semantic: '确定'),
        ]),
      ],
    );
  }

  Widget _row(PairingNumpadMetrics m, List<_KeySpec> specs) {
    return Row(
      mainAxisSize: MainAxisSize.min,
      children: <Widget>[
        for (int i = 0; i < specs.length; i++) ...<Widget>[
          if (i > 0) SizedBox(width: m.columnGap),
          _NumpadKey(
            key: ValueKey<String>('numpad-key-${specs[i].label}'),
            width: m.keyWidth,
            height: m.keyHeight,
            radius: m.radius,
            fontSize: m.fontSize,
            spec: specs[i],
          ),
        ],
      ],
    );
  }
}

class _KeySpec {
  const _KeySpec(this.label, this.onTap, {this.semantic, this.onLongPress});

  final String label;
  final VoidCallback onTap;
  final String? semantic;
  final VoidCallback? onLongPress;
}

class _NumpadKey extends StatefulWidget {
  const _NumpadKey({
    super.key,
    required this.width,
    required this.height,
    required this.radius,
    required this.fontSize,
    required this.spec,
  });

  final double width;
  final double height;
  final double radius;
  final double fontSize;
  final _KeySpec spec;

  @override
  State<_NumpadKey> createState() => _NumpadKeyState();
}

class _NumpadKeyState extends State<_NumpadKey> {
  Timer? _longPressTimer;
  bool _longFired = false;
  bool _pressed = false;

  static const Duration _longPressThreshold = Duration(milliseconds: 600);

  void _onTapDown(TapDownDetails details) {
    _longFired = false;
    setState(() => _pressed = true);
    final onLong = widget.spec.onLongPress;
    if (onLong != null) {
      _longPressTimer = Timer(_longPressThreshold, () {
        _longFired = true;
        onLong();
      });
    }
  }

  void _onTapUp(TapUpDetails details) {
    _longPressTimer?.cancel();
    setState(() => _pressed = false);
    if (!_longFired) {
      widget.spec.onTap();
    }
  }

  void _onTapCancel() {
    _longPressTimer?.cancel();
    setState(() => _pressed = false);
  }

  @override
  void dispose() {
    _longPressTimer?.cancel();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final spec = widget.spec;
    return Semantics(
      container: true,
      excludeSemantics: true,
      label: spec.semantic ?? spec.label,
      button: true,
      child: GestureDetector(
        onTapDown: _onTapDown,
        onTapUp: _onTapUp,
        onTapCancel: _onTapCancel,
        child: AnimatedScale(
          scale: _pressed ? 0.97 : 1.0,
          duration: const Duration(milliseconds: 160),
          child: Container(
            width: widget.width,
            height: widget.height,
            decoration: BoxDecoration(
              color: _pressed ? BanxiaTokens.glassPressed : BanxiaTokens.glass,
              borderRadius: BorderRadius.circular(widget.radius),
              border: Border.all(color: const Color(0x26000000), width: 2),
            ),
            alignment: Alignment.center,
            child: Center(
              child: Text(
                spec.label,
                textAlign: TextAlign.center,
                style: TextStyle(
                  fontSize: widget.fontSize,
                  color: BanxiaTokens.label,
                  height: 1.0,
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

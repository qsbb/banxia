import 'dart:math' as math;

import 'package:flutter/material.dart';

import '../core/bridge/bridge_protocol.dart';

/// M1 QA framing overlay (INV-7 observability) — a faithful port of
/// `PhoneDiagnosticsHud.DrawFramingGrid`:
///   * red safety-band frame around the chrome-free viewport,
///   * green phone eye line (42% of the visible band) + 7/10 secondary line,
///   * cross-hair markers for headTop / eye / waist / feet,
///   * top-left readout `d=… h=… eye=…% anchor=head|bounds`.
///
/// It consumes the engine `framing.anchors` event (a [FramingSnapshot]) and is
/// a pure overlay: it never intercepts input.
class FramingGrid extends StatelessWidget {
  const FramingGrid({
    super.key,
    required this.snapshot,
    this.devicePixelRatio = 1.0,
  });

  final FramingSnapshot snapshot;
  final double devicePixelRatio;

  // Semantic constants kept 1:1 with CallFramingSolver / DrawFramingGrid.
  static const double eyeLineRatio = 0.42;
  static const double secondaryLineRatio = 0.70;
  static const double frameBandTop = 0.12;
  static const double frameBandBottom = 0.96;

  static const Color red = Color(0xE6FF3B30); // (1, 0.231, 0.188, 0.90)
  static const Color green = Color(0xF234C759); // (0.204, 0.780, 0.349, 0.95)
  static const Color greenSecondary = Color(0x8C34C759); // same, 0.55 alpha
  static const Color orange = Color(0xFFFFA61A); // (1, 0.65, 0.1)
  static const Color white = Color(0xFFFFFFFF);

  @override
  Widget build(BuildContext context) {
    return IgnorePointer(
      child: CustomPaint(
        painter: _FramingPainter(
          snapshot: snapshot,
          devicePixelRatio: devicePixelRatio,
        ),
        size: Size.infinite,
      ),
    );
  }
}

class _FramingPainter extends CustomPainter {
  _FramingPainter({
    required this.snapshot,
    required this.devicePixelRatio,
  });

  final FramingSnapshot snapshot;
  final double devicePixelRatio;

  @override
  void paint(Canvas canvas, Size size) {
    final w = size.width;
    final h = size.height;
    if (w <= 0 || h <= 0) return;

    final double physicalHeight = snapshot.screenHeightPx > 1
        ? snapshot.screenHeightPx
        : h * devicePixelRatio;
    final double logicalScale = physicalHeight > 1 ? h / physicalHeight : 1.0;
    final double top = snapshot.valid
        ? (snapshot.topPx * logicalScale).clamp(0.0, h).toDouble()
        : h * FramingGrid.frameBandTop;
    final double bottom = (snapshot.valid
            ? (snapshot.bottomPx * logicalScale).clamp(top, h).toDouble()
            : h * FramingGrid.frameBandBottom)
        .clamp(top, h);
    final double eyeLine = top + (bottom - top) * FramingGrid.eyeLineRatio;
    final double secondary =
        top + (bottom - top) * FramingGrid.secondaryLineRatio;
    final double margin = math.max(12.0, w * 0.025);
    final double lineWidth = math.max(2.0, h / 800);

    final Paint redPaint = Paint()
      ..color = FramingGrid.red
      ..strokeWidth = lineWidth
      ..style = PaintingStyle.stroke;
    final Paint greenPaint = Paint()
      ..color = FramingGrid.green
      ..strokeWidth = lineWidth
      ..style = PaintingStyle.stroke;
    final Paint greenSecondary = Paint()
      ..color = FramingGrid.greenSecondary
      ..strokeWidth = lineWidth
      ..style = PaintingStyle.stroke;

    // Red safety band.
    _line(canvas, Offset(margin, top), Offset(w - margin, top), redPaint);
    _line(canvas, Offset(margin, bottom), Offset(w - margin, bottom), redPaint);
    _line(canvas, Offset(margin, top), Offset(margin, bottom), redPaint);
    _line(
        canvas, Offset(w - margin, top), Offset(w - margin, bottom), redPaint);

    // Green 1/3 eye line and 7/10 secondary line.
    _line(canvas, Offset(margin, eyeLine), Offset(w - margin, eyeLine),
        greenPaint);
    _line(canvas, Offset(margin, secondary), Offset(w - margin, secondary),
        greenSecondary);

    // Readout (top-left, white backing box).
    final String readout = snapshot.valid
        ? 'frame d=${snapshot.distance.toStringAsFixed(2)} '
            'h=${snapshot.cameraY.toStringAsFixed(2)} '
            'eye=${(eyeLine / h * 100).toStringAsFixed(1)}% '
            'anchor=${snapshot.headAnchor ? 'head' : 'bounds'}'
            '${snapshot.degraded ? ' degraded' : ''}'
        : 'frame unavailable · waiting for camera/model';
    _drawReadout(canvas, readout, margin, h);

    if (!snapshot.valid) return;

    final List<(String, Color)> markers = <(String, Color)>[
      ('headTop', FramingGrid.red),
      ('eye', FramingGrid.green),
      ('waist', FramingGrid.orange),
      ('feet', FramingGrid.white),
    ];
    final double crossSize = math.max(8.0, h / 180);
    final double crossStroke = math.max(2.0, crossSize * 0.22);
    for (final (label, color) in markers) {
      final FramingAnchor? anchor = snapshot.anchors[label];
      if (anchor == null) continue;
      final Offset center = Offset(anchor.x * w, anchor.y * h);
      final Paint markerPaint = Paint()
        ..color = color
        ..strokeWidth = crossStroke
        ..style = PaintingStyle.stroke;
      _line(canvas, center - Offset(crossSize, 0),
          center + Offset(crossSize, 0), markerPaint);
      _line(canvas, center - Offset(0, crossSize),
          center + Offset(0, crossSize), markerPaint);
      _drawMarkerLabel(canvas, label, color, center, crossSize, w, h);
    }
  }

  void _drawReadout(Canvas canvas, String text, double margin, double h) {
    final double fontSize = math.max(14.0, h / 62);
    final TextPainter tp = TextPainter(
      text: TextSpan(
        text: text,
        style: TextStyle(
          color: Colors.black,
          fontSize: fontSize,
          fontFamily: 'monospace',
        ),
      ),
      textDirection: TextDirection.ltr,
    )..layout();
    final Rect box = Rect.fromLTWH(
      margin + 8,
      margin + 8,
      tp.width + 16,
      tp.height + 8,
    );
    canvas.drawRRect(
      RRect.fromRectAndRadius(box, const Radius.circular(4)),
      Paint()..color = const Color(0xE0FFFFFF),
    );
    tp.paint(canvas, Offset(margin + 16, margin + 12));
  }

  void _drawMarkerLabel(Canvas canvas, String label, Color color, Offset center,
      double crossSize, double w, double h) {
    final double fontSize = math.max(12.0, h / 62);
    final TextPainter tp = TextPainter(
      text: TextSpan(
        text: label,
        style: TextStyle(color: color, fontSize: fontSize),
      ),
      textDirection: TextDirection.ltr,
    )..layout();
    final double dx = (center.dx + crossSize + 4 + tp.width).clamp(0.0, w);
    final double dy = (center.dy - tp.height * 0.7).clamp(0.0, h - tp.height);
    tp.paint(canvas, Offset(dx - tp.width, dy));
  }

  void _line(Canvas canvas, Offset a, Offset b, Paint paint) {
    canvas.drawLine(a, b, paint);
  }

  @override
  bool shouldRepaint(covariant _FramingPainter oldDelegate) =>
      oldDelegate.snapshot != snapshot ||
      oldDelegate.devicePixelRatio != devicePixelRatio;
}

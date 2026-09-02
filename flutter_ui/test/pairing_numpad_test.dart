import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

import 'package:banxia_flutter_ui/core/bridge/bridge_client.dart';
import 'package:banxia_flutter_ui/scene/pairing_numpad.dart';
import 'package:banxia_flutter_ui/state/app_state.dart';

Widget _wrap(Widget child) {
  return MaterialApp(home: Scaffold(body: Center(child: child)));
}

void main() {
  group('PairingNumpadMetrics (M3 geometry)', () {
    test('H = availableHeight / 16 and R = H / 2', () {
      const double h = 800;
      const double w = 360;
      final m =
          PairingNumpadMetrics.compute(availableHeight: h, availableWidth: w);
      expect(m.keyHeight, closeTo(50.0, 0.0001));
      expect(m.radius, closeTo(25.0, 0.0001));
      expect(m.radius, closeTo(m.keyHeight / 2, 0.0001));
      expect(m.fontSize, closeTo(0.42 * 50.0, 0.0001));
    });

    test('W solves the fixed-point W = (aw − 2·edge − 2·colGap)/3', () {
      const double h = 800;
      const double w = 360;
      final m =
          PairingNumpadMetrics.compute(availableHeight: h, availableWidth: w);
      // W = (360 − 48) / (3 + 2·0.12)
      expect(m.keyWidth, closeTo(312 / 3.24, 0.0001));
      // columnGap = 0.12 · W
      expect(m.columnGap, closeTo(0.12 * m.keyWidth, 0.0001));
      // 3 keys + 2 gaps fill the inner width (availableWidth − 2·edge).
      final double rowWidth = 3 * m.keyWidth + 2 * m.columnGap;
      expect(rowWidth, closeTo(w - 2 * m.edgeMargin, 0.0001));
    });

    test('rowGap = 0.16 · H and edge margin is 24', () {
      final m = PairingNumpadMetrics.compute(
          availableHeight: 800, availableWidth: 360);
      expect(m.rowGap, closeTo(0.16 * 50.0, 0.0001));
      expect(m.edgeMargin, 24.0);
    });
  });

  group('PairingNumpad widget', () {
    testWidgets('renders a 3x4 grid of 12 keys', (WidgetTester tester) async {
      await tester.pumpWidget(_wrap(PairingNumpad(
        availableHeight: 800,
        availableWidth: 360,
        onDigit: (_) {},
        onBackspace: () {},
        onClear: () {},
        onSubmit: () {},
      )));

      for (final String label in const <String>[
        '1',
        '2',
        '3',
        '4',
        '5',
        '6',
        '7',
        '8',
        '9',
        '0',
        '⌫',
        '✓',
      ]) {
        expect(
            find.byKey(ValueKey<String>('numpad-key-$label')), findsOneWidget);
      }
      expect(find.byType(PairingNumpad), findsOneWidget);
    });

    testWidgets('keys have derived size and exact capsule radius',
        (WidgetTester tester) async {
      const double availableHeight = 800;
      const double availableWidth = 360;
      final m = PairingNumpadMetrics.compute(
          availableHeight: availableHeight, availableWidth: availableWidth);

      await tester.pumpWidget(_wrap(PairingNumpad(
        availableHeight: availableHeight,
        availableWidth: availableWidth,
        onDigit: (_) {},
        onBackspace: () {},
        onClear: () {},
        onSubmit: () {},
      )));

      final Finder containerFinder = find.descendant(
        of: find.byKey(const ValueKey<String>('numpad-key-1')),
        matching: find.byType(Container),
      );
      final Container container = tester.widget<Container>(containerFinder);
      final Size size = tester.getSize(containerFinder);

      expect(size.height, closeTo(m.keyHeight, 0.01));
      expect(size.width, closeTo(m.keyWidth, 0.01));

      final BoxDecoration decoration = container.decoration! as BoxDecoration;
      final BorderRadius radius = decoration.borderRadius! as BorderRadius;
      expect(radius.topLeft.x, closeTo(m.radius, 0.0001));
      expect(radius.topLeft.x, closeTo(size.height / 2, 0.01));
      // Center-aligned label (INV-4).
      expect(container.alignment, Alignment.center);
    });

    testWidgets('exposes Chinese semantics for utility keys and digits',
        (WidgetTester tester) async {
      final SemanticsHandle handle = tester.ensureSemantics();
      await tester.pumpWidget(_wrap(PairingNumpad(
        availableHeight: 800,
        availableWidth: 360,
        onDigit: (_) {},
        onBackspace: () {},
        onClear: () {},
        onSubmit: () {},
      )));

      expect(find.bySemanticsLabel('退格'), findsOneWidget);
      expect(find.bySemanticsLabel('确定'), findsOneWidget);
      for (final String digit in const <String>[
        '0',
        '1',
        '2',
        '3',
        '4',
        '5',
        '6',
        '7',
        '8',
        '9'
      ]) {
        expect(find.bySemanticsLabel(digit), findsOneWidget);
      }
      handle.dispose();
    });

    testWidgets('digit tap dispatches onDigit', (WidgetTester tester) async {
      String? tapped;
      await tester.pumpWidget(_wrap(PairingNumpad(
        availableHeight: 800,
        availableWidth: 360,
        onDigit: (String d) => tapped = d,
        onBackspace: () {},
        onClear: () {},
        onSubmit: () {},
      )));

      await tester.tap(find.byKey(const ValueKey<String>('numpad-key-5')));
      await tester.pump();
      expect(tapped, '5');
    });

    testWidgets('backspace short press vs long press (≥600ms)',
        (WidgetTester tester) async {
      int backspace = 0;
      int clear = 0;
      await tester.pumpWidget(_wrap(PairingNumpad(
        availableHeight: 800,
        availableWidth: 360,
        onDigit: (_) {},
        onBackspace: () => backspace++,
        onClear: () => clear++,
        onSubmit: () {},
      )));

      final Finder backspaceKey =
          find.byKey(const ValueKey<String>('numpad-key-⌫'));

      // Short press = backspace only.
      await tester.tap(backspaceKey);
      await tester.pump();
      expect(backspace, 1);
      expect(clear, 0);

      // Long press (≥600 ms) = clear only.
      final TestGesture gesture =
          await tester.startGesture(tester.getCenter(backspaceKey));
      await tester.pump(const Duration(milliseconds: 100));
      await tester.pump(const Duration(milliseconds: 650));
      await gesture.up();
      await tester.pump();
      expect(clear, 1);
      expect(backspace, 1);
    });

    testWidgets('submit key dispatches onSubmit', (WidgetTester tester) async {
      int submit = 0;
      await tester.pumpWidget(_wrap(PairingNumpad(
        availableHeight: 800,
        availableWidth: 360,
        onDigit: (_) {},
        onBackspace: () {},
        onClear: () {},
        onSubmit: () => submit++,
      )));

      await tester.tap(find.byKey(const ValueKey<String>('numpad-key-✓')));
      await tester.pump();
      expect(submit, 1);
    });
  });

  group('six-digit pairing semantics', () {
    test('AppState caps at 6 digits, backspace removes, clear empties', () {
      final LocalBridgeClient bridge = LocalBridgeClient();
      final AppState app = AppState(bridge);

      for (final String d in const <String>['1', '2', '3', '4', '5', '6']) {
        app.appendPairingDigit(d);
      }
      expect(app.connection.pairingCode, '123456');

      // 7th digit is ignored (six-digit semantics).
      app.appendPairingDigit('7');
      expect(app.connection.pairingCode, '123456');
      expect(app.connection.pairingCode.length, 6);

      app.removePairingDigit();
      expect(app.connection.pairingCode, '12345');

      app.clearPairingCode();
      expect(app.connection.pairingCode, '');

      app.dispose();
      bridge.dispose();
    });

    test('submit with fewer than 6 digits shows the existing toast', () async {
      final LocalBridgeClient bridge = LocalBridgeClient();
      final AppState app = AppState(bridge);
      app.appendPairingDigit('1');

      await app.submitPairing();
      expect(app.toast.value?.message, '请输入完整的 6 位配对码');

      app.dispose();
      bridge.dispose();
    });
  });
}

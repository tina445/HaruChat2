import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:haruchat_xcross_native_probe/main.dart';

void main() {
  Future<void> pumpProbe(WidgetTester tester, Size size) async {
    await tester.binding.setSurfaceSize(size);
    await tester.pumpWidget(const HaruChatNativeProbeApp());
    await tester.pump();
  }

  testWidgets('compact iPhone layout stacks model controls without overflow',
      (tester) async {
    await pumpProbe(tester, const Size(375, 812));

    expect(find.text('Choose GGUF'), findsOneWidget);
    expect(find.text('Load'), findsOneWidget);
    expect(find.text('Character test bench'), findsOneWidget);
    expect(find.text('Create & add'), findsOneWidget);
    final list = find
        .descendant(
          of: find.byType(ListView),
          matching: find.byType(Scrollable),
        )
        .first;
    await tester.scrollUntilVisible(find.text('Response'), 240, scrollable: list);
    expect(find.text('Response'), findsOneWidget);
    await tester.scrollUntilVisible(
      find.text('Structured native event log'),
      240,
      scrollable: list,
    );
    expect(find.text('Structured native event log'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('character creation dialog validates a bundle draft', (tester) async {
    await pumpProbe(tester, const Size(768, 1024));
    await tester.tap(find.text('Create & add'));
    await tester.pumpAndSettle();
    expect(find.text('Create test character'), findsOneWidget);
    expect(find.text('System instruction'), findsOneWidget);
    await tester.tap(find.text('Create & add').last);
    await tester.pump();
    expect(tester.takeException(), isNull);
  });

  testWidgets(
      'iPad portrait and landscape keep all diagnostic controls accessible',
      (tester) async {
    await pumpProbe(tester, const Size(768, 1024));
    expect(find.text('Generate'), findsOneWidget);
    expect(find.text('Structured native event log'), findsOneWidget);
    expect(tester.takeException(), isNull);

    await pumpProbe(tester, const Size(1194, 834));
    expect(find.text('Choose GGUF'), findsOneWidget);
    expect(find.text('Unload'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('short keyboard-like viewport becomes scroll safe',
      (tester) async {
    await pumpProbe(tester, const Size(375, 360));

    await tester.scrollUntilVisible(
      find.text('Response'),
      200,
      scrollable: find
          .descendant(
            of: find.byType(ListView),
            matching: find.byType(Scrollable),
          )
          .first,
    );
    expect(find.text('Response'), findsOneWidget);
    await tester.scrollUntilVisible(
      find.text('Structured native event log'),
      200,
      scrollable: find
          .descendant(
            of: find.byType(ListView),
            matching: find.byType(Scrollable),
          )
          .first,
    );
    expect(find.text('Structured native event log'), findsOneWidget);
    expect(find.byType(Scrollable), findsAtLeastNWidgets(1));
    expect(tester.takeException(), isNull);
  });
}

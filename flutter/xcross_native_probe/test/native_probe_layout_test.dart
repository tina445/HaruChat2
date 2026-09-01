import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:haruchat_xcross_native_probe/main.dart';

void main() {
  Future<void> pumpHarness(WidgetTester tester, Size size) async {
    await tester.binding.setSurfaceSize(size);
    await tester.pumpWidget(const HaruChatNativeProbeApp());
    await tester.pump();
  }

  testWidgets('landscape iPad projects the Unity control rail and chat surface',
      (tester) async {
    await pumpHarness(tester, const Size(1194, 834));
    expect(find.text('RUNTIME CONTROLS'), findsOneWidget);
    expect(find.text('GGUF 모델 가져오기'), findsOneWidget);
    expect(find.text('새 대화'), findsOneWidget);
    expect(find.textContaining('DIAGNOSTICS'), findsOneWidget);
    expect(find.byKey(const Key('composer')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('portrait iPad moves the rail into an accessible drawer',
      (tester) async {
    await pumpHarness(tester, const Size(768, 1024));
    expect(find.text('RUNTIME CONTROLS'), findsNothing);
    await tester.tap(find.byTooltip('Open navigation menu'));
    await tester.pumpAndSettle();
    expect(find.text('RUNTIME CONTROLS'), findsOneWidget);
    expect(find.text('응답 취소'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('chat surface starts with the Unity system guidance',
      (tester) async {
    await pumpHarness(tester, const Size(1194, 834));
    expect(find.text('모델을 가져온 뒤 캐릭터에게 말을 걸어 보세요.'), findsOneWidget);
    expect(find.text('메시지를 입력하세요'), findsOneWidget);
  });

  testWidgets('character editor exposes every fixed v1 bundle section',
      (tester) async {
    await pumpHarness(tester, const Size(1194, 834));
    await tester.tap(find.text('character 추가'));
    await tester.pumpAndSettle();
    expect(find.textContaining('BUNDLE MAP'), findsOneWidget);
    expect(find.text('personality.md'), findsWidgets);
    expect(find.text('style.md'), findsWidgets);
    expect(find.text('scenario.md'), findsWidgets);
    expect(find.text('lore/'), findsOneWidget);
    expect(find.text('examples.jsonl'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('iPhone width keeps the masthead and option rail overflow-safe',
      (tester) async {
    await pumpHarness(tester, const Size(320, 568));
    expect(find.text('HARU / P6'), findsOneWidget);
    expect(find.byKey(const Key('composer')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('keyboard inset lifts the composer instead of obscuring it',
      (tester) async {
    await pumpHarness(tester, const Size(768, 1024));
    addTearDown(tester.view.resetViewInsets);
    tester.view.viewInsets = const FakeViewPadding(bottom: 336);
    await tester.pump();
    await tester.pumpAndSettle();

    final padding = tester.widget<AnimatedPadding>(find.ancestor(
      of: find.byKey(const Key('composer')),
      matching: find.byType(AnimatedPadding),
    ));
    expect(
      (padding.padding as EdgeInsets).bottom,
      336 / tester.view.devicePixelRatio,
    );
    expect(find.byKey(const Key('composer')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}

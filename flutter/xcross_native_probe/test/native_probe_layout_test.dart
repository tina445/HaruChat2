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
    await tester.scrollUntilVisible(find.textContaining('DIAGNOSTICS'), 240,
        scrollable: find.byType(Scrollable).first);
    expect(find.textContaining('DIAGNOSTICS'), findsOneWidget);
    expect(find.byKey(const Key('memory-pulse')), findsOneWidget);
    expect(find.byKey(const Key('composer')), findsOneWidget);
    expect(find.byKey(const Key('memory-pulse')), findsOneWidget);
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

  testWidgets('iPad landscape keeps the rail at its native logical size',
      (tester) async {
    await pumpHarness(tester, const Size(1366, 1024));
    expect(find.text('RUNTIME CONTROLS'), findsOneWidget);
    expect(find.byTooltip('Open navigation menu'), findsNothing);
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

  testWidgets('memory atelier is reachable from the runtime rail',
      (tester) async {
    await pumpHarness(tester, const Size(1194, 834));
    await tester.tap(find.text('기억 노트 · 설정 (꺼짐)'));
    await tester.pumpAndSettle();

    expect(find.text('기억 아틀리에'), findsOneWidget);
    expect(find.byKey(const Key('memory-enable-toggle')), findsOneWidget);
    expect(find.textContaining('managed SQLite bridge 대기 중'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('memory settings show conservative context budget diagnostics',
      (tester) async {
    await pumpHarness(tester, const Size(1194, 834));
    await tester.tap(find.text('기억 노트 · 설정 (꺼짐)'));
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('memory-context-budget')), findsOneWidget);
    expect(find.byKey(const Key('context-window-slider')), findsOneWidget);
    expect(find.text('Context window: 8192 tokens'), findsOneWidget);
    expect(find.text('Temperature: 0.7'), findsOneWidget);
    expect(find.textContaining('Recommended context: 8192 tokens'),
        findsOneWidget);
    expect(
        find.textContaining('Memory reservation: 256 tokens'), findsOneWidget);
    expect(find.text('기본 8k'), findsOneWidget);
    expect(find.text('실험 96k'), findsOneWidget);
    expect(find.text('실험 상한 128k'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('memory notes support add edit and delete in the harness',
      (tester) async {
    await pumpHarness(tester, const Size(1194, 834));
    await tester.tap(find.text('기억 노트 · 설정 (꺼짐)'));
    await tester.pumpAndSettle();

    final add = find.text('추가').last;
    await tester.ensureVisible(add);
    await tester.pumpAndSettle();
    await tester.tap(add);
    await tester.pumpAndSettle();
    await tester.enterText(
        find.byKey(const Key('memory-note-text')), '하루는 민트 초콜릿을 좋아한다.');
    await tester.tap(find.byKey(const Key('memory-note-save')));
    await tester.pumpAndSettle();
    expect(find.text('하루는 민트 초콜릿을 좋아한다.'), findsOneWidget);

    await tester.tap(find.byTooltip('기억 노트 편집'));
    await tester.pumpAndSettle();
    await tester.enterText(
        find.byKey(const Key('memory-note-text')), '하루는 따뜻한 차를 좋아한다.');
    await tester.tap(find.byKey(const Key('memory-note-save')));
    await tester.pumpAndSettle();
    expect(find.text('하루는 따뜻한 차를 좋아한다.'), findsOneWidget);

    await tester.tap(find.byTooltip('기억 노트 삭제'));
    await tester.pumpAndSettle();
    expect(find.text('아직 기억 노트가 없습니다.'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('iPhone width keeps the masthead and option rail overflow-safe',
      (tester) async {
    await pumpHarness(tester, const Size(320, 568));
    expect(find.text('HARU / P6'), findsOneWidget);
    expect(find.byKey(const Key('composer')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('iPhone landscape uses the drawer when height is constrained',
      (tester) async {
    await pumpHarness(tester, const Size(874, 402));
    expect(find.text('RUNTIME CONTROLS'), findsNothing);
    await tester.tap(find.byTooltip('Open navigation menu'));
    await tester.pumpAndSettle();
    expect(find.text('RUNTIME CONTROLS'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets('keyboard resizes the chat and exposes a dismiss control',
      (tester) async {
    await pumpHarness(tester, const Size(768, 1024));
    addTearDown(tester.view.resetViewInsets);
    tester.view.viewInsets = const FakeViewPadding(bottom: 336);
    await tester.pump();
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('composer')), findsOneWidget);
    expect(find.byKey(const Key('dismiss-keyboard')), findsOneWidget);
    expect(find.byTooltip('키보드 내리기'), findsOneWidget);
    expect(tester.takeException(), isNull);
  });

  testWidgets(
      'short iPhone landscape prioritizes the composer above the keyboard',
      (tester) async {
    await pumpHarness(tester, const Size(568, 320));
    addTearDown(tester.view.resetViewInsets);
    tester.view.viewInsets = const FakeViewPadding(bottom: 216);
    await tester.pump();
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('composer')), findsOneWidget);
    expect(find.byKey(const Key('dismiss-keyboard')), findsOneWidget);
    expect(find.byKey(const Key('memory-pulse')), findsNothing);
    expect(tester.takeException(), isNull);
  });

  testWidgets('a blank surface tap dismisses a focused text field',
      (tester) async {
    await pumpHarness(tester, const Size(1194, 834));
    final modelPath = find.byType(TextField).first;
    final editable =
        find.descendant(of: modelPath, matching: find.byType(EditableText));
    await tester.tap(modelPath);
    await tester.pump();
    final input = tester.widget<EditableText>(editable);
    expect(input.focusNode.hasFocus, isTrue);

    await tester.tapAt(const Offset(760, 92));
    await tester.pump();
    expect(input.focusNode.hasFocus, isFalse);
    expect(tester.takeException(), isNull);
  });

  testWidgets('memory note editor remains scrollable above an iPhone keyboard',
      (tester) async {
    await pumpHarness(tester, const Size(320, 568));
    await tester.tap(find.byTooltip('Open navigation menu'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('기억 노트 · 설정 (꺼짐)'));
    await tester.pumpAndSettle();
    final add = find.text('추가').last;
    await tester.ensureVisible(add);
    await tester.pumpAndSettle();
    await tester.tap(add);
    await tester.pumpAndSettle();
    addTearDown(tester.view.resetViewInsets);
    tester.view.viewInsets = const FakeViewPadding(bottom: 336);
    await tester.pump();
    await tester.pumpAndSettle();

    expect(find.byKey(const Key('memory-note-text')), findsOneWidget);
    expect(find.byKey(const Key('memory-note-save')), findsOneWidget);
    expect(tester.takeException(), isNull);
  });
}

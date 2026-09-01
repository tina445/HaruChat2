# HaruChat P6 Flutter UI Test Host

`xcross_native_probe`는 Phase 3 native lifecycle probe이면서, Unity build가 불가능한 환경에서 P6 화면 구조를 검증하는 Flutter test host다. Unity scene의 control rail/drawer, model·character control, chat stream, cancel/reset/unload 및 diagnostics를 반영한다.

Flutter widget test와 native C ABI event projection을 검증할 뿐이다. Unity composition root, managed multi-turn conversation, iOS packaging, Metal runtime과 M4 iPad MVP Gate는 대체하지 않는다.

```bash
flutter test
```

iPad device 실행 절차와 xcross prerequisite는 [`docs/DEVELOPMENT.md`](../../docs/DEVELOPMENT.md)의 Phase 3/P6 섹션을 따른다.

Linux의 xcross 경로는 debug/JIT iPad probe만 지원한다. iPadOS 26에서는 이 앱을 홈 화면에서 단독 실행할 수 없으므로, fullscreen과 키보드 UX 검증은 AOT build가 필요하다.

유료 Apple Developer Program은 필요하지 않다. macOS/Xcode에서 무료 Personal Team으로 `ios/Runner.xcworkspace`의 Runner signing team을 설정한 뒤, checksum-verified `LlmCore.xcframework`를 stage하고 아래 명령으로 development-signed release IPA를 만든다.

```bash
bash scripts/prepare-xcross-native-probe.sh /absolute/path/LlmCore.xcframework.zip
bash scripts/build-flutter-ios-release.sh
```

xcross의 `xcross flutter build --ipa`는 device probe용 IPA일 뿐 release mode 검증이 아니다.

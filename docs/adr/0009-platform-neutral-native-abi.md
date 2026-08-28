# ADR-0009: Platform-neutral Native ABI와 Android Delivery 보류

## Context

MVP의 제품 target은 iPadOS이며 Linux CPU 개발·검증과 iOS XCFramework packaging이 먼저 필요하다. 그러나 native inference lifecycle은 Android에서도 재사용할 수 있는 성격이다. Android-specific API를 지금 ABI에 넣으면 iOS/Linux core가 JNI와 Unity Android packaging에 결합되고, 반대로 Android artifact까지 지금 요구하면 SDK/NDK, device, CI와 제품 범위가 불필요하게 커진다.

## Decision

`hc_llm_*`를 Linux, Apple, 장래 Android가 공유하는 C11 ABI로 유지한다.

- public header에는 opaque handle, fixed-width integer, versioned struct, UTF-8 byte pointer/length와 명시적 ownership만 둔다. JNI, Java/Kotlin, Objective-C/Swift, Unity와 platform loader type을 넣지 않는다.
- M1은 Linux shared library를 생산하고, Phase 2는 같은 ABI의 iOS XCFramework를 생산한다.
- CMake에는 Android arm64-v8a cross-compile을 위한 entry point와 artifact layout만 유지한다. Android SDK/NDK/JDK 설치, JNI/Java/Kotlin binding, Unity Android integration, Android device/CI artifact는 별도 승인 전 구현하지 않는다.
- `ILocalModelBackend` port와 DTO는 M1에 선언할 수 있으나, `LlamaCppBackend` P/Invoke와 `LocalModelAdapter` concrete implementation은 Phase 4에서 시작한다. 장래 Android binding도 같은 port의 infrastructure implementation으로 한정한다.

## Alternatives

- **Android 앱과 binding을 M1에 함께 구현:** platform 검증 범위와 toolchain 부담이 커지고 iPad MVP를 지연시킨다.
- **Android 전용 ABI/JNI를 지금 public header에 노출:** Core contract가 Android runtime에 결합되어 Apple/Linux consumer가 불필요한 dependency를 가진다.
- **Android 가능성을 전혀 보존하지 않음:** 향후 backend 재사용 시 public ABI 또는 상위 runtime을 다시 설계할 위험이 있다.

## Consequences

- Native test와 C consumer smoke는 platform-neutral header를 검증한다.
- Android support는 장래 NDK configure validation부터 시작하며, `.so` artifact 또는 device 실행 성공을 현재 milestone의 증거로 사용하지 않는다.
- Android-specific lifecycle, packaging 또는 permission 정책은 C ABI와 Character Runtime이 아닌 Android infrastructure/presentation layer에 둔다.
- iPadOS MVP scope와 Metal/device acceptance는 변경되지 않는다.

## Status

Accepted

## Date

2026-08-28

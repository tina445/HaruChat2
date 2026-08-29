# ADR-0010: xcross를 Phase 3 Flutter device probe host로만 사용

- Status: Accepted
- Date: 2026-08-29

## Context

Phase 2는 Codemagic에서 unsigned `LlmCore.xcframework`를 만들었지만, 로컬 Mac/Xcode가
없는 Linux 개발환경에서는 Personal Team 설치와 M4 iPad의 Metal runtime을 즉시 확인할 수
없다. xcross는 Linux/Windows에서 Flutter iOS debug app을 build, Apple ID/API key로 sign,
물리 iOS 17+ device에 install/run하는 경로와 SwiftPM iOS plugin을 제공한다고 문서화한다.

제품 MVP의 Presentation은 Unity이며, Phase 3의 목적은 UI framework 선택이 아니라 C ABI,
Metal backend와 native lifecycle의 device proof다.

## Decision

- `flutter/xcross_native_probe`를 Phase 3의 대체 **diagnostic host**로 추가한다.
- Flutter plugin은 SwiftPM binary target으로 P2 `LlmCore.xcframework`를 link하고
  `hc_llm_*` C ABI만 호출한다.
- Flutter UI는 GGUF import, load, generate, cancel/reset/unload, incremental response 및
  원본 `hc_llm_event` 구조 JSON log만 제공한다.
- 이는 debug/JIT probe 전용이다. Unity MVP UI, Live2D 계획, managed runtime 또는 release
  distribution 경로를 변경하지 않는다.
- Xcode `native/probe` host는 fallback으로 보존한다.
- Apple credential은 source/CI log에 넣지 않는다. xcross Apple ID flow에는 main iCloud
  계정 대신 development-only 계정을 사용한다.

## Alternatives

- **Xcode native probe만 사용:** Apple toolchain 정합성은 가장 높지만, local Mac이 없는
  사용자는 실제 device gate를 진행할 수 없다.
- **Flutter를 제품 UI로 전환:** Unity/Live2D MVP 방향과 Phase 6의 계획을 바꾸며, 이
  device proof가 해결하려는 native runtime 위험보다 큰 범위다.
- **unsigned artifact만 검증:** provisioning, iPad install, Metal runtime을 증명하지 못한다.

## Consequences

- Linux에서 P2 artifact를 실제 iPad에 설치해 Metal/device gate를 시도할 수 있다.
- xcross에는 Flutter, Swift, LLVM, Python/device tunnel tooling 및 legally obtained
  `Xcode.xip` SDK input이 필요하며, third-party provisioning implementation의 변화가 새
  운영 위험이다.
- 성공은 xcross 자체의 가능성 증명이 아니라, P2 artifact의 `llama.cpp-metal`, non-empty
  ordered stream, cancellation/reset/unload/reload가 M4 iPad에서 동작했다는 기록으로만
  인정한다.

# ADR-0005: Apple Target만 macOS CI에서 빌드

## Context

주 개발환경은 Linux이고 Apple SDK, Metal toolchain, iOS target과 XCFramework 생성에는 macOS/Xcode가 필요하다. Local Mac과 유료 Apple Developer Program 사용을 전제할 수 없고 CI 무료 quota도 제한적이다. Native core 검증을 Unity 전체 iOS build나 code signing에 묶으면 feedback이 느리고 실패 원인이 복잡해진다.

## Decision

일상 개발과 공통 test는 Linux에 두고 Apple 전용 compile/artifact 생성만 macOS CI에 위임한다.

- Codemagic Apple Silicon runner를 1순위, GitHub Actions macOS를 fallback으로 사용한다.
- CI는 Apple clang, iOS SDK, `GGML_METAL=ON`, device arm64 및 필요한 simulator slice를 검증한다.
- 초기 산출물은 public header와 library를 포함한 unsigned `LlmCore.xcframework`, checksum과 build manifest다.
- 최소 consumer link smoke까지만 native artifact pipeline의 필수 조건으로 하며 Unity Build Automation과 code signing에 종속시키지 않는다.
- Linux job은 managed/unit/native CPU/sanitizer test를 먼저 수행한다. macOS quota는 Apple-specific 변경과 release candidate에 집중한다.
- Xcode/SDK image와 llama.cpp commit을 기록하고 artifact provenance를 남긴다.
- Personal Team 실기기 설치는 CI artifact 성공과 별개인 Phase 0 feasibility Gate다. CI-only signing을 보장하지 않는다.

## Alternatives

- **모든 build를 macOS에서 수행:** Linux 우선 개발과 무료 quota에 불리하다.
- **local Mac 필수:** 현재 제약을 충족하지 못한다.
- **Unity Build Automation부터 사용:** native core compile/link 문제를 Unity pipeline과 결합해 초기 feedback을 악화시킨다.
- **Apple build를 MVP 말기에 시작:** Metal/toolchain/packaging 위험을 너무 늦게 발견한다.

## Consequences

- Linux에서 빠른 feedback을 유지하면서 Apple compile 위험을 초기에 검증한다.
- unsigned XCFramework 생성은 signed IPA, Personal Team provisioning, device 설치나 Metal runtime 성공을 증명하지 않는다.
- device MVP에는 별도의 signing 경로와 실제 M4 iPad 검증이 필요하다.
- 두 OS의 CMake option과 artifact layout이 drift하지 않도록 공통 target과 manifest test를 유지해야 한다.
- CI quota 또는 runner image 변경에 대비한 fallback과 pin update 절차가 필요하다.

## Status

Accepted

## Date

2026-08-27

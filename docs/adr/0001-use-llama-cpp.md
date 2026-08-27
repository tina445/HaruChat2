# ADR-0001: Local Inference Engine으로 llama.cpp 사용

## Context

HaruChat2는 M4 iPad에서 3~4B급 GGUF model을 Metal로 실행해야 하며 Linux에서 native core 대부분을 개발·검증해야 한다. GGUF loading, tokenization, sampling, context 관리와 Apple Metal backend를 직접 구현하는 것은 개인 프로젝트의 범위를 벗어난다. 동시에 Character/Agent 기능을 inference engine source에 섞으면 upstream update와 다른 backend 추가가 어려워진다.

## Decision

Local inference engine으로 `llama.cpp`를 사용한다.

- `native/third_party/llama.cpp`의 Git submodule로 관리하고 superproject에서 검증된 commit을 pin한다.
- 프로젝트 고유 코드는 별도 `native/llmcore` thin wrapper와 managed adapter에 둔다.
- upstream source는 원칙적으로 수정하지 않는다. 불가피한 patch는 별도 ADR, patch file, upstream 근거와 제거 조건을 요구한다.
- Linux에서는 CPU backend, Apple target에서는 Metal backend를 build option으로 선택한다.
- GGUF model path, quantization, context와 generation 설정은 runtime configuration이며 source에 고정하지 않는다.

## Alternatives

- **직접 inference runtime 구현:** 유지비와 안정성 위험이 지나치게 크므로 제외한다.
- **MLX/MLX Swift:** Apple 환경에는 적합하지만 Linux/Unity/C ABI 공통 경로와 GGUF 활용 요구에 불리하다.
- **Core ML 변환:** model별 변환 pipeline과 format 관리가 필요하고 교체 가능한 GGUF 목표에 맞지 않는다.
- **llama.cpp fork:** 초기 수정은 쉽지만 upstream 보안·성능 개선을 따라가기 어려워 제외한다.

## Consequences

- GGUF ecosystem과 Linux/Metal backend를 재사용한다.
- engine update는 submodule commit 변경으로 격리하고 wrapper/integration test로 검증할 수 있다.
- llama.cpp API churn을 wrapper가 흡수해야 하며 pin update마다 Linux와 Apple CI가 모두 필요하다.
- llama.cpp의 build time, binary size와 backend 제약을 수용한다.
- 다른 local engine을 추가할 때 `ILocalModelBackend` 구현은 필요하지만 Character/Agent Runtime은 변경하지 않는다.

## Status

Accepted

## Date

2026-08-27

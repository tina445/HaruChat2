# ADR-0003: Model Adapter와 Inference Backend 분리

## Context

Character Runtime은 local Qwen 외에도 다른 GGUF family와 향후 OpenAI-compatible provider를 사용할 수 있어야 한다. llama.cpp backend가 Qwen prompt, tool convention 또는 persona를 처리하면 model family, provider와 engine이 강결합된다. 반대로 모든 provider 차이를 Character/Agent에 노출하면 교체 비용이 전체 runtime으로 번진다.

## Decision

Model-facing contract와 local inference primitive를 두 경계로 나눈다.

- `IModelAdapter`는 normalized `ModelRequest`를 받고 token/reasoning/tool/usage/completed/error를 포함하는 `ModelEvent` stream, capability, cancellation과 usage semantics를 제공한다.
- `ILocalModelBackend`는 model/context lifecycle, metadata, tokenization, sampling, raw streaming, cancellation과 metrics만 제공한다.
- `LocalModelAdapter`는 `ModelProfile`과 `ILocalModelBackend`를 조합해 chat template, stop condition, tool convention과 request/event normalization을 담당한다.
- `LlamaCppBackend`는 C ABI를 소비하며 model family별 persona, prompt policy나 behavior를 포함하지 않는다.
- `ModelRouter` MVP는 사용자의 explicit selection만 해석한다. automatic routing/fallback은 별도 결정 전 구현하지 않는다.
- remote provider는 `IModelAdapter`를 직접 구현한다. Character, Memory와 Agent는 adapter interface만 참조한다.

## Alternatives

- **Provider별 runtime 분기:** 빠르게 시작할 수 있지만 Character/Agent가 provider 조건문으로 오염된다.
- **Qwen 전용 `LlamaCppBackend`:** 첫 model은 단순하지만 다른 GGUF에서 backend를 복제하게 된다.
- **Adapter와 backend를 하나의 interface로 통합:** remote HTTP와 native context primitive의 공통분모가 지나치게 크거나 새기 쉽다.
- **초기부터 자동 router:** 검증된 use case가 없어 YAGNI 원칙에 맞지 않는다.

## Consequences

- model/provider/backend를 독립적으로 교체하고 mock adapter로 상위 runtime을 Linux에서 테스트할 수 있다.
- Local adapter와 backend 사이에 mapping/lifecycle 코드가 추가된다.
- capability negotiation과 normalized error/event semantics를 명확히 유지해야 한다.
- 새 GGUF family는 우선 data profile로 지원하며 코드가 필요한 경우에만 specialized adapter를 추가한다.

## Status

Accepted

## Date

2026-08-27

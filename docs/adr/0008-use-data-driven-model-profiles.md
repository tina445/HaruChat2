# ADR-0008: Model 차이를 Data-driven Profile로 표현

## Context

Local runtime은 Qwen3.5 4B를 우선 사용하지만 다른 GGUF family, quantization과 chat/tool convention을 지원할 수 있어야 한다. Model마다 adapter class를 만들면 작은 설정 차이가 class 증가와 backend 분기로 이어진다. 반대로 GGUF metadata만 무조건 신뢰하면 불완전하거나 예상하지 못한 template에서 조용히 잘못된 prompt를 만들 수 있다.

## Decision

일반적인 local model 차이를 versioned, validated `ModelProfile` data로 표현하고 `LocalModelAdapter + ModelProfile + ILocalModelBackend` 조합을 기본으로 사용한다.

Profile의 최소 범위는 다음과 같다.

- stable profile ID, schema version, model family와 optional GGUF metadata matcher
- chat template policy: `GgufMetadata`, 알려진 `NamedTemplate` 또는 명시적 `Raw` test mode
- context limit/capability, generation default, stop condition과 tool-calling support/convention
- profile과 runtime이 허용하는 option의 type/range validation

설정 precedence는 높은 순서로 다음과 같다.

1. 사용자가 명시한 안전한 runtime override
2. 선택된 `ModelProfile`
3. profile policy가 허용한 GGUF metadata hint
4. adapter의 보수적인 default

명시적으로 선택한 profile ID가 있으면 metadata auto-match로 바꾸지 않는다. Profile이 없고 GGUF chat template metadata가 유효하면 generic profile로 제한적으로 실행할 수 있다. 안전한 request를 만들 수 없으면 family를 추측하지 않고 load 단계에서 actionable validation error를 반환한다.

Profile은 JSON data로 version 관리하며 executable code, secret, model path나 character persona를 포함하지 않는다. Data로 표현할 수 없는 protocol 차이가 test로 증명될 때만 specialized `IModelAdapter`를 추가한다. `LlamaCppBackend`는 profile을 해석하지 않고 metadata와 inference primitive만 제공한다.

## Alternatives

- **Model family마다 adapter class:** 특수 동작에는 필요할 수 있지만 단순 default/template 차이까지 class로 만들면 유지비가 커진다.
- **Backend에서 model family 분기:** inference engine과 model behavior가 강결합되어 금지한다.
- **GGUF metadata만 사용:** 편리하지만 metadata 부재/오류와 tool capability 정책을 충분히 통제하지 못한다.
- **하나의 universal hard-coded template:** 다른 model에서 correctness를 보장할 수 없다.

## Consequences

- quantization/file을 바꾸거나 유사 model을 추가할 때 대개 profile/config 변경만 필요하다.
- schema validation, migration과 profile conformance test가 필요하다.
- precedence와 provenance를 diagnostics에 노출해야 configuration 문제를 설명할 수 있다.
- 무분별한 profile field 확장을 막고 실제 model 차이에 근거해 schema를 변경해야 한다.
- 진짜 protocol 차이는 specialized adapter로 처리할 수 있어 data와 code의 경계를 유지한다.

## Status

Accepted

## Date

2026-08-27

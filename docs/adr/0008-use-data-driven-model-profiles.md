# ADR-0008: Model 차이를 Data-driven Profile로 표현

## Context

Local runtime은 Qwen3.5 4B를 우선 사용하지만 다른 GGUF family, quantization과 chat/tool convention을 지원할 수 있어야 한다. Model마다 adapter class를 만들면 작은 설정 차이가 class 증가와 backend 분기로 이어진다. GGUF의 `tokenizer.chat_template`은 해당 파일의 tokenizer와 함께 배포되는 prompt protocol이므로 정상적으로 제공되는 경우 우선 사용해야 한다. 다만 metadata만으로 tool/reasoning, stop sequence와 product policy까지 안전하게 결정할 수는 없다.

## Decision

일반적인 local model 차이를 versioned, validated `ModelProfile` data로 표현하고 `LocalModelAdapter + ModelProfile + ILocalModelBackend` 조합을 기본으로 사용한다.

Profile의 최소 범위는 다음과 같다.

- stable profile ID, schema version, model family와 optional GGUF metadata matcher
- declarative chat template: message/assistant fragments, role mapping과 fixed control token. 문자열 치환은 `{role}`과 `{content}`만 허용하며 executable template/Jinja는 실행하지 않는다.
- context limit/capability, generation default, stop condition과 tool-calling support/convention
- optional non-standard reasoning output marker policy (`openMarker`, `closeMarker`, `show`/`hide`/`separate`). Adapter는 profile 없이 `<think>…</think>`와 `<|im_end|>`를 정규화하며, 다른 delimiter 또는 제품별 표시 정책만 override data로 받는다.
- profile과 runtime이 허용하는 option의 type/range validation

설정 precedence는 높은 순서로 다음과 같다.

1. 사용자가 명시한 안전한 runtime override
2. 선택된 `ModelProfile`
3. profile policy가 허용한 GGUF metadata hint
4. adapter의 보수적인 default

profile ID를 지정하지 않으면 `LocalModelAdapter`는 native backend를 통해 로드된 GGUF의 `tokenizer.chat_template`을 먼저 적용한다. 이 경로는 Jinja를 managed code에서 실행하지 않고, pinned llama.cpp가 지원하는 template interpreter를 C ABI 뒤에서 사용한다. Template이 없거나 upstream이 지원하지 않는 파일에서만 정확히 하나인 metadata catalog match를 fallback policy로 적용한다. 명시 profile ID는 embedded template보다 우선한다. 안전한 request를 만들 수 없으면 family를 추측하지 않고 load 단계에서 actionable validation error를 반환한다.

구현의 `ModelProfileCatalog`는 이 precedence를 composition root에서 적용한다. 명시 profile ID를 먼저 resolve하고, 없을 때만 `architectureContains` matcher가 정확히 하나인 profile을 선택한다. matcher가 없거나 복수이면 추측하지 않고 configuration 오류로 끝낸다.

Profile은 JSON data로 version 관리하며 executable code, secret, model path나 character persona를 포함하지 않는다. 같은 chat protocol을 쓰는 모든 GGUF quantization은 하나의 profile을 공유한다. Common `<|im_end|>` control token과 `<think>…</think>` reasoning 출력은 adapter 기본 정규화 대상이므로 profile이 필요 없다. Data로 표현할 수 없는 protocol 차이가 test로 증명될 때만 specialized `IModelAdapter`를 추가한다. `LlamaCppBackend`는 profile을 해석하지 않고 metadata와 inference primitive만 제공한다.

## Alternatives

- **Model family마다 adapter class:** 특수 동작에는 필요할 수 있지만 단순 default/template 차이까지 class로 만들면 유지비가 커진다.
- **Backend에서 model family 분기:** inference engine과 model behavior가 강결합되어 금지한다.
- **GGUF metadata만 사용:** chat template에는 가장 적합하지만 metadata 부재/오류와 tool capability 정책을 충분히 통제하지 못한다. 따라서 embedded template 우선과 profile fallback을 함께 사용한다.
- **하나의 universal hard-coded template:** 다른 model에서 correctness를 보장할 수 없다.

## Consequences

- 정상 embedded template을 가진 새 GGUF/quantization은 model path/checksum 선택만 필요하다. template이 없거나 product 정책을 추가해야 할 때는 adapter code가 아니라 선언형 profile 하나를 추가하고 contract smoke로 검증한다.
- schema validation, migration과 profile conformance test가 필요하다.
- precedence와 provenance를 diagnostics에 노출해야 configuration 문제를 설명할 수 있다.
- 무분별한 profile field 확장을 막고 실제 model 차이에 근거해 schema를 변경해야 한다.
- 진짜 protocol 차이는 specialized adapter로 처리할 수 있어 data와 code의 경계를 유지한다.

## Status

Accepted

## Date

2026-08-27

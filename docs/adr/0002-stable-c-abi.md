# ADR-0002: Managed/Native 경계에 안정적인 C ABI 사용

## Context

Native runtime은 C++인 llama.cpp를 사용하지만 consumer는 Unity/C#이다. C++ class, exception, STL container와 compiler-specific ABI를 외부에 노출하면 Linux와 iOS compiler/runtime 차이, memory ownership과 Unity plugin lifetime에서 불안정해진다. Streaming과 cancellation도 ABI가 명확한 lifecycle을 제공해야 한다.

## Decision

`extern "C"` public API와 versioned C11-compatible header를 유일한 native public boundary로 사용한다.

- Model, context와 generation state는 opaque handle로 표현하고 생성한 API가 대응하는 destroy API를 제공한다.
- enum과 struct field는 fixed-width integer와 명시적 size/version field를 사용한다. ABI version이 맞지 않으면 load 직후 actionable error로 실패한다.
- C++ object, template, `std::string`, exception과 allocator를 ABI 밖으로 노출하지 않는다. 모든 exception은 wrapper 내부에서 error code로 변환한다.
- 입력 pointer는 호출 중에만 borrow한다. metadata와 diagnostic 문자열 조회는 caller-supplied buffer와 required-size query를 사용한다. Poll event payload는 job이 소유하며 같은 job의 다음 poll 또는 destroy 전까지만 유효하고 managed binding이 즉시 복사한다. Native가 할당한 메모리를 managed가 해제하는 규약은 만들지 않는다.
- 한 context의 generation lifecycle은 start, cancel, poll, reset 순서를 갖고 destroy는 worker 종료/join을 보장한다.
- 모든 symbol은 `hc_llm_*` prefix를 사용한다. API는 `hc_llm_get_abi_version`과 runtime build metadata를 노출하며 기능 추가는 backward-compatible append 또는 새 entry point로 수행한다.
- 구체적인 asynchronous delivery는 ADR-0007의 polling event queue를 따른다.

## Alternatives

- **C++/CLI:** iOS와 Unity AOT 환경에 적용할 수 없다.
- **Objective-C++/Swift 전용 API:** Apple에는 편하지만 Linux와 공통 artifact/API를 잃는다.
- **C++ ABI 직접 P/Invoke:** compiler, STL와 exception ABI가 안정적이지 않다.
- **Native가 callback으로 managed delegate 호출:** lifetime, GC/AOT trampoline과 thread exception 위험 때문에 기본 경계로 사용하지 않는다.

## Consequences

- Linux `.so`와 iOS XCFramework가 동일한 header contract를 사용한다.
- memory ownership, error와 lifecycle을 native integration test로 고정할 수 있다.
- ABI는 llama.cpp 또는 Qwen version에 종속되지 않는다.
- caller-buffer query, 짧은 job-owned event view와 handle validation으로 wrapper 코드가 늘어난다.
- breaking change에는 ABI major 증가, consumer compatibility check와 migration 기간이 필요하다.

## Status

Accepted

## Date

2026-08-27

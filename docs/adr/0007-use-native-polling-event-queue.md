# ADR-0007: Native Streaming에 Polling Event Queue 사용

## Context

llama.cpp generation은 worker thread에서 실행되어야 하며 token, metrics, completion과 error를 managed/Unity로 stream해야 한다. Native thread가 managed callback을 직접 호출하면 delegate pinning, IL2CPP/AOT trampoline, GC lifetime, exception crossing과 destroy race가 발생하기 쉽다. Unity main thread를 polling으로 block해서도 안 된다.

## Decision

Native/managed asynchronous 경계에 generation별 bounded polling event queue를 사용한다.

- `hc_llm_job_start`가 context-owned worker를 시작하고 즉시 반환한다. 한 context에는 active generation 하나만 허용한다.
- worker는 ordered `Token`, `Metrics`, `Completed`, `Cancelled`, `Error` event를 queue에 기록한다. Tool/Reasoning 의미 해석은 managed Model Adapter가 담당한다.
- managed backend의 background task가 non-blocking `hc_llm_job_poll`을 짧은 interval로 호출하고 `Channel`/`IAsyncEnumerable<ModelEvent>`로 변환한다. Unity main thread는 native poll을 수행하지 않는다.
- event header는 type, sequence, payload byte length와 terminal 여부를 가진다. Payload view는 job이 소유하며 같은 job의 다음 poll 또는 destroy 전까지만 유효하다. Managed backend는 poll 직후 즉시 복사하고 native pointer를 상위 계층에 노출하지 않는다.
- terminal event는 generation당 정확히 하나이고 이전 token보다 뒤에 온다. cancel은 queue가 가득 차도 관찰할 수 있는 별도 atomic signal이다.
- queue가 가득 차면 worker는 condition variable로 제한적으로 대기하며 token을 조용히 drop하지 않는다. consumer poll과 cancel/destroy가 worker를 깨운다.
- cancel은 idempotent하다. context reset/unload/destroy는 active generation cancel과 worker join 이후에만 상태를 해제한다.
- poll interval과 queue capacity는 합리적인 default를 제공하되 diagnostics로 queue depth와 consumer lag를 관찰한다.

## Alternatives

- **Native-to-managed callback:** latency는 낮지만 IL2CPP/AOT와 thread/lifetime 안전성이 나쁘다.
- **Unity main-thread polling:** 구현은 단순하지만 rendering frame과 token 소비를 결합하고 frame stall 위험이 있다.
- **Blocking read API:** background thread 하나를 계속 점유하고 cancellation/disposal 구현이 복잡해진다.
- **Unbounded queue:** producer는 단순하지만 느린 consumer에서 mobile memory가 무제한 증가할 수 있다.
- **Full response 반환:** streaming, TTFT와 cancellation 요구를 충족하지 못한다.

## Consequences

- ABI에서 managed function pointer를 보관하지 않아 GC/AOT와 destroy race를 크게 줄인다.
- 짧은 polling 지연과 background task가 추가되지만 token generation 시간에 비해 수용 가능하다.
- bounded queue의 backpressure로 memory 상한과 lossless ordering을 유지한다.
- cancellation, terminal-event exactly-once, destroy wakeup과 slow-consumer test가 필수다.
- Managed layer가 polling cadence와 main-thread dispatch를 책임진다.

## Status

Accepted

## Date

2026-08-27

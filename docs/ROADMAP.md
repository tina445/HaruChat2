# 개발 로드맵

## 1. 운영 원칙과 Milestone

각 Phase는 완료 조건을 충족한 뒤 종료한다. 서로 독립적인 managed/native 작업은 public contract와 파일 소유자를 먼저 정한 뒤 병렬화한다. 기능 수보다 작은 end-to-end vertical slice와 위험을 조기에 제거하는 Gate를 우선한다.

| Milestone | 포함 Phase | 결과 |
|---|---:|---|
| M0 Design & Feasibility | 0 | 승인된 설계 기준선, Linux toolchain 검증과 skeleton |
| M1 Linux Native Core | 1 | Linux에서 안정적인 C ABI와 local generation lifecycle 검증 |
| M2 Apple Native Proof | 2~3 | unsigned XCFramework와 M4 iPad native Metal probe |
| M3 Headless Character Runtime | 4~5 | Linux에서 managed adapter와 character streaming vertical slice |
| M4 Unity MVP | 6 | M4 iPad Unity 앱에서 local character streaming 대화 |
| M5 Runtime Expansion | 7~9 | Memory, Agent/Tool, Live2D 확장 |
| M6 Provider Expansion | 10 | OpenAI-compatible remote provider 선택 가능 |

Memory, Agent/Tool, Live2D와 remote provider는 M4 MVP의 필수 범위가 아니다.

## Phase 0 — 설계 기준선과 Feasibility Gate

**목표:** 구현에 들어가기 전에 요구사항, 경계, Linux toolchain과 재현 가능한 skeleton을 고정한다.

- **구현 범위:** .NET 10 solution/test host, `netstandard2.1` runtime contract project, CMake/Ninja skeleton, llama.cpp submodule pin, dependency lock, Linux one-command validation을 만든다. Arch Linux bootstrap은 이미 있는 toolchain을 재설치하지 않고 `ccache`와 `valgrind`를 포함한 필수 command를 검증하며, 설치가 필요하면 사용자가 실행할 `pacman --needed` 명령만 안내한다.
- **생성 파일/모듈:** `HaruChat.slnx`, `packages/`, `native/`, `scripts/`, `docs/`, build/test skeleton. Unity project와 CI workflow는 후속 Apple/Unity phase에서 추가한다.
- **의존성:** Linux 개발환경과 Git. JDK, Android SDK/NDK, Unity Android module, Unity license, Codemagic 계정, M4 iPad 및 Apple ID는 이 Phase의 의존성이 아니다.
- **테스트:** 문서 link/추적성, managed/native skeleton test, Arch toolchain verification과 one-command Linux validation을 수행한다.
- **완료 조건:** 문서와 ADR의 모순이 없고 Linux one-command validation이 정의·실행 가능하다. Apple signing feasibility는 완료 조건이 아니라 사용자 소유 Phase 3 진입 전 Gate로 기록된다.
- **예상 위험:** 로컬 Linux에 진단 도구가 없을 수 있다. bootstrap은 package manager를 변경하지 않으므로 사용자가 최소 패키지 설치를 승인·수행해야 한다.

## Phase 1 — Linux Native llama.cpp Wrapper

**목표:** llama.cpp를 수정하지 않고 Linux CPU에서 lifecycle과 streaming이 동작하는 thin C ABI wrapper를 만든다.

- **구현 범위:** official llama.cpp `v0.1.2` tag의 commit `1511ce3bc3f087376c8526b4ad07100bfabb277f`를 pinned submodule로 사용하고, opaque runtime/model/context/job handle, GGUF load/unload, context reset/reuse, async generation worker, bounded polling queue, cancellation, metadata/metrics, error 및 UTF-8 ownership을 구현한다. 한 context에는 활성 job 하나만 허용한다. M1에는 `ILocalModelBackend`와 DTO contract를 선언할 수 있으나 `LlamaCppBackend` P/Invoke와 `LocalModelAdapter` concrete implementation은 Phase 4까지 구현하지 않는다.
- **생성 파일/모듈:** `native/llmcore`, `native/third_party/llama.cpp`, public `hc_llm_*` C header, `libllmcore.so`, CMake target, native tests, 작은 C smoke harness.
- **의존성:** Phase 0의 문서 기준선과 toolchain, 검증된 llama.cpp commit, opt-in 테스트 GGUF.
- **테스트:** invalid argument/load, create/destroy, GGUF load, token streaming, event 순서, UTF-8 fragment, cancellation, reset, repeated generation, unload/reload, context당 중복 job, slow consumer와 sanitizer.
- **완료 조건:** Linux CPU에서 opt-in GGUF generation이 재현되고 cancel/reset/unload 뒤 leak, deadlock, use-after-free가 없다. C consumer가 public header를 compile/link하며 ABI version과 event payload lifetime을 검증한다. `HARUCHAT_TEST_MODEL_PATH`가 없으면 model-smoke만 skip하고 다른 lifecycle/sanitizer suite는 통과해야 한다.
- **예상 위험:** llama.cpp API churn, worker 종료 race, queue backpressure, CI용 GGUF 크기·라이선스. upstream pin과 wrapper-local 변환, optional model-smoke label로 격리한다. Android cross-compile은 CMake configure entry point만 준비하며 NDK/device/CI artifact 성공으로 주장하지 않는다.

## Phase 2 — macOS CI와 unsigned XCFramework

**목표:** managed/Unity 작업 전에 Apple compiler, Metal shader와 artifact packaging 위험을 제거한다.

- **구현 범위:** Codemagic Apple Silicon에서 device arm64와 필요한 simulator slice를 빌드하고 `LlmCore.xcframework`로 조립한다. `GGML_METAL=ON`, embedded Metal resource, public symbol과 최소 consumer link를 확인한다.
- **생성 파일/모듈:** Codemagic workflow, fallback GitHub Actions job, unsigned XCFramework zip, SHA-256, Xcode/SDK/commit/options build manifest와 native link smoke target.
- **의존성:** Phase 1 native target과 pinned submodule. Unity Build Automation과 app code signing에는 의존하지 않는다.
- **테스트:** Apple clang compile, Metal shader compile/link, expected device/simulator slices, exported `hc_llm_*`, clean checkout reproducibility와 consumer link.
- **완료 조건:** pin된 source에서 unsigned `LlmCore.xcframework`를 반복 생성하며 Unity native plugin이 소비할 header/library/resource layout을 갖춘다.
- **예상 위험:** runner image/SDK 변화, CMake Apple option, Metal resource 누락, 무료 CI quota. image를 고정하고 Apple-specific 변경에만 job을 사용한다.

## Phase 3 — 최소 M4 iPad Native Probe

**목표:** Unity와 managed runtime 전에 실제 device에서 C ABI와 Metal runtime을 검증한다.

- **구현 범위:** 최소 Objective-C++/Swift host 또는 Xcode sample에서 XCFramework를 load하고 runtime-configured GGUF로 load, generate, poll, cancel, reset, unload/reload를 실행한다. UI는 로그와 시작/취소 control 정도로 제한한다.
- **생성 파일/모듈:** disposable이 아닌 재현 가능한 native probe target, device checklist, model/profile/checksum을 포함한 결과 기록.
- **의존성:** 사용자 소유 Apple signing feasibility Gate의 통과, Phase 2 XCFramework, 실제 M4 iPad와 라이선스가 확인된 Qwen GGUF.
- **테스트:** Metal backend name/GPU offload 확인, non-empty ordered stream, cancel 후 재생성, context reset, 반복 generation, unload/reload, load time/TTFT/TPS/context/memory와 기본 pressure 관찰.
- **완료 조건:** M4 iPad에서 Metal이 실제 활성화된 상태로 native lifecycle 전체가 동작하고 crash나 명백한 지속 메모리 증가가 없다. compile 성공이나 simulator 결과로 대체하지 않는다.
- **예상 위험:** signing/provisioning, GGUF 반입, Metal runtime 미활성, memory/thermal pressure. 실패는 managed/Unity에서 우회하지 않고 native wrapper 또는 build 설정에서 해결한다.

## Phase 4 — Managed Model Abstraction Vertical Slice

**목표:** model/provider 지식과 inference engine을 분리하고 C ABI stream을 provider-neutral managed stream으로 변환한다.

- **구현 범위:** `IModelAdapter`, `IModelSession`, `ModelRequest`, `ModelEvent`, `ModelCapabilities`, `ModelConfig`, `ModelProfile`, `ModelRouter`, `ILocalModelBackend`, `LlamaCppBackend` P/Invoke와 `MockModelAdapter`를 구현한다.
- **생성 파일/모듈:** `com.haruchat.runtime`, `com.haruchat.llamacpp`, .NET 10 test host, profile schema/sample, native safe handle과 poll pump.
- **의존성:** Phase 1 C ABI. Phase 2~3의 발견 사항을 ABI/config default에 반영한다. Unity와 SQLite에는 의존하지 않는다.
- **테스트:** request normalization, profile precedence/validation, event mapping과 incremental UTF-8, cancellation propagation, explicit route selection, native error mapping, context busy/reset/reuse와 dispose race.
- **완료 조건:** 같은 `ModelRequest`를 mock/local adapter에 전달할 수 있고 상위 계층은 llama.cpp/Qwen concrete type을 참조하지 않는다. Linux에서 native stream을 `IAsyncEnumerable<ModelEvent>`로 소비한다.
- **예상 위험:** Unity와 .NET API surface 차이, P/Invoke lifetime, polling CPU 사용, profile 과설계. runtime source를 `netstandard2.1` 범위로 제한하고 contract suite를 공유한다.

## Phase 5 — Character Runtime와 Conversation Vertical Slice

**목표:** data-defined character를 model-independent request로 compile하고 Linux headless 대화를 완성한다.

- **구현 범위:** Character bundle v1 manifest/Markdown/examples loader, canonical path·schema·size 검증, `PromptCompiler`, in-memory `Conversation`, context budget와 `CharacterChatService`를 구현한다. 모델별 chat template은 adapter/profile에 둔다.
- **생성 파일/모듈:** character schema/sample, catalog/loader/compiler, conversation service, headless console vertical slice.
- **의존성:** Phase 4 adapter contract. Memory, Agent, Unity, Live2D는 필요하지 않다.
- **테스트:** valid/invalid bundle, traversal/symlink, UTF-8 Korean Markdown, duplicate ID, deterministic section order, context overflow, cancellation/error rollback, mock/local multi-turn stream.
- **완료 조건:** Linux에서 character 선택 → user input → mock 및 local adapter streaming → 성공 turn commit 흐름이 동작한다. 지원되는 다른 GGUF/profile로 교체해도 Character Runtime 코드는 바뀌지 않는다.
- **예상 위험:** prompt 중복·누출, 무제한 context, data를 executable config로 오해하는 문제. bundle은 data-only로 유지하고 budget/validation을 강제한다.

## Phase 6 — Unity/M4 iPad MVP

**목표:** 가장 작은 Unity UI에서 실제 M4 iPad local character chat을 완성한다.

- **구현 범위:** local UPM package 연결, XCFramework plugin import, composition root, model/character picker, load progress, 입력, incremental response, cancel/reset/unload, main-thread dispatcher와 diagnostics를 구현한다. MVP는 text-only다.
- **생성 파일/모듈:** `unity/HaruChat`, `com.haruchat.unity`, iPad app, device smoke checklist와 결과 기록.
- **의존성:** Phase 0 signing Gate, Phase 2 artifact, Phase 3 device proof, Phase 5 headless vertical slice, 실제 GGUF와 M4 iPad.
- **테스트:** managed 및 Unity EditMode/PlayMode, native plugin load, scene teardown, token batching과 main-thread responsiveness. Device에서 캐릭터 instruction, streaming, cancel, reset, repeated generation, unload/reload, memory pressure를 확인한다.
- **완료 조건 — MVP Gate:** 실제 M4 iPad의 Unity 앱에서 캐릭터와 runtime-configured Qwen GGUF를 선택하고 Metal로 load한 뒤 캐릭터 지침이 반영된 답변이 incremental streaming된다. 취소/새 대화/unload가 동작하고 Unity main thread를 block하지 않으며 오류가 복구 가능한 형태로 표시된다.
- **예상 위험:** Unity iOS plugin/resource packaging, signing, file picker/storage, frame stall, Live2D 없이도 큰 model의 memory/thermal 부담. simulator나 unsigned artifact만으로 MVP를 완료 처리하지 않는다.

## Phase 7 — SQLite Conversation Memory

**목표:** provider 독립적인 device-local conversation과 long-term memory를 추가한다.

- **구현 범위:** `IMemoryStore`, `IMemoryRetriever`, SQLite schema/forward migration, session summary, long-term memory, FTS5와 keyword/recency/importance ranking, retention/delete를 구현한다.
- **생성 파일/모듈:** `com.haruchat.memory.sqlite`, migration, retriever, memory settings와 privacy control.
- **의존성:** Phase 5 Character Runtime. MVP Gate와 독립적인 post-MVP milestone이다.
- **테스트:** empty/upgrade DB, CRUD/transaction, Korean FTS, deterministic ranking, character/session isolation, locked/disk-full/corruption, cancellation과 deletion.
- **완료 조건:** restart 후 conversation/memory가 복구되고 budget에 맞는 retrieval 결과가 prompt input으로 전달된다. provider 교체는 schema나 retrieval을 변경하지 않는다.
- **예상 위험:** iOS FTS5 linkage, DB growth, 민감 데이터, concurrent access. single-writer transaction과 실제 Apple link/device smoke로 검증한다.

## Phase 8 — Agent와 허용목록 Tool

**목표:** 모델이 등록되고 권한이 확인된 tool만 구조화 호출하도록 한다.

- **구현 범위:** bounded `AgentRuntime`, `ToolRegistry`, `ITool`, JSON schema validation, deny-by-default permission, timeout/cancellation, result 재주입을 구현한다. `time`, `random`, read-only `lore.search`부터 시작한다.
- **생성 파일/모듈:** agent/tool contracts, mock tools, permission policy, audit-safe result와 사용자 승인 hook.
- **의존성:** Phase 4 ModelEvent/capability와 Phase 5 request loop. Memory tool은 Phase 7 이후다.
- **테스트:** unknown/duplicate tool, malformed argument, denial, timeout/cancel, max iteration, oversized result, tool 지원/미지원 adapter.
- **완료 조건:** 임의 코드 실행 없이 allowlisted tool의 성공·거부·실패가 일관되게 model과 UI에 전달되고 무한 loop가 불가능하다.
- **예상 위험:** prompt injection, destructive tool, provider별 convention과 loop 비용. permission, hard limit와 result budget을 적용한다.

## Phase 9 — Live2D Presentation

**목표:** Core 변경 없이 중립적인 character state를 Live2D 표현으로 연결한다.

- **구현 범위:** `CharacterState/CharacterAction`을 Unity main thread에서 expression, motion, gaze, mouth/speaking으로 mapping한다. TTS/lip sync는 extension point로 둔다.
- **생성 파일/모듈:** `UnityCharacterController`, `Live2DCharacterAdapter`, mapping asset/config와 fallback presenter.
- **의존성:** Phase 6 Unity flow, Cubism SDK/asset license. expression tool은 Phase 8 이후 선택적으로 연결한다.
- **테스트:** mapping/EditMode, missing asset fallback, rapid transition coalescing, scene/model teardown와 device frame-time.
- **완료 조건:** Live2D가 없거나 mapping이 누락되어도 채팅이 동작하고, 있을 때 state가 main thread에서 안전하게 표현된다.
- **예상 위험:** SDK/asset license와 Unity compatibility, render와 LLM의 동시 memory/thermal 부담. Presentation degradation을 inference failure와 분리한다.

## Phase 10 — OpenAI-Compatible Provider

**목표:** Character/Agent 코드를 바꾸지 않고 선택 가능한 remote model을 추가한다.

- **구현 범위:** Base URL/API key/model ID/timeout/generation config, SSE streaming, capability, usage, cancellation, normalized error와 `RemotePrivacyPolicy`를 구현한다. 자동 fallback/routing은 제외한다.
- **생성 파일/모듈:** remote adapter package, secure configuration provider, privacy filter, mock HTTP conformance suite와 explicit model registration.
- **의존성:** Phase 4 adapter/router와 Phase 5 Character Runtime. Tool support는 Phase 8 계약을 재사용한다.
- **테스트:** local mock server로 SSE chunking, HTTP error, timeout, cancel, malformed payload, usage/tool event와 privacy exclusion. 실제 API key test는 opt-in이다.
- **완료 조건:** 사용자가 local/remote를 직접 선택할 수 있고 같은 Character/Agent pipeline이 동작한다. opt-in 전에는 device-local 데이터가 전송되지 않는다.
- **예상 위험:** endpoint dialect 차이, secret leakage, network failure와 비용. provider quirk를 adapter 내부에 제한하고 기본 로그를 redaction한다.

## 2. 명시적으로 보류하는 항목

다음은 구체적인 요구와 검증 사례가 생기기 전까지 구현하지 않는다.

- automatic/task-based routing과 fallback orchestration
- vector database/embedding retrieval
- multi-agent와 plugin marketplace
- cloud sync/remote memory
- scripting language와 임의 코드 실행
- App Store/TestFlight release pipeline
- image input/vision projector와 multimodal UI
- TTS/STT와 고급 emotion classifier

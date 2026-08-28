# HaruChat2 요구사항

## 1. 문서 목적

이 문서는 HaruChat2의 제품 범위와 검증 가능한 기능 요구사항(FR), 비기능 요구사항(NFR)을 정의한다. 구현 구조와 경계는 [ARCHITECTURE.md](ARCHITECTURE.md), 개발 및 빌드 절차는 [DEVELOPMENT.md](DEVELOPMENT.md), 단계별 구현 순서와 완료 기준은 [ROADMAP.md](ROADMAP.md), 주요 결정의 근거는 [ADR 목록](adr/README.md)을 따른다.

요구사항의 우선순위는 다음과 같다.

| 우선순위 | 의미 |
|---|---|
| P0 | MVP 완료에 필수이며 첫 M4 iPad vertical slice 전에 충족해야 한다. |
| P1 | MVP 직후의 핵심 확장이다. MVP 구조가 구현을 막아서는 안 되지만 최초 실행에는 필수가 아니다. |
| P2 | 장기 확장 후보이다. 구체 구현보다 현재 경계를 훼손하지 않는 것이 중요하다. |

## 2. 프로젝트 목적과 대상

HaruChat2는 iOS/iPadOS에서 동작하는 로컬 LLM 기반 AI 캐릭터 챗 애플리케이션이자, 향후 Memory, Agent/Tool, 외부 모델 API, Live2D, 음성 및 상호작용 기능을 추가할 수 있는 AI Character Runtime이다.

첫 제품 목표는 M4 iPad의 Unity 애플리케이션에서 사용자가 캐릭터와 로컬 Qwen GGUF 모델을 선택하고, 캐릭터 지침이 적용된 응답을 llama.cpp와 Metal로 생성하여 스트리밍으로 확인하는 것이다. 기존 3~4B급 모델의 성능 자체를 다시 입증하기보다 native wrapper, lifecycle, Metal, streaming, cancellation, context 관리 및 Unity 경계의 정확성을 검증한다.

주 대상은 다음과 같다.

- 개인 사용자 및 개발자 본인
- 주 실행 환경: Apple M4 기반 iPad, iPadOS, Metal
- 주 개발 환경: Linux와 Unity Editor
- Apple 전용 빌드 환경: 무료 사용량을 우선한 macOS CI

## 3. 핵심 제약과 원칙

- 로컬 LLM 구현과 Character/Agent Runtime을 분리한다.
- 모델별 규칙과 inference engine을 분리한다.
- Unity는 Presentation 계층으로 제한하며 Core Runtime은 Unity 타입을 참조하지 않는다.
- llama.cpp는 upstream dependency로 격리하고 프로젝트 고유 기능을 내부에 구현하지 않는다.
- 모델 파일, quantization 및 generation 설정을 런타임에 교체할 수 있어야 한다.
- Linux에서 가능한 빌드와 테스트는 Linux에서 수행하고 Apple target 빌드만 macOS CI에 위임한다.
- 외부 API가 없어도 핵심 캐릭터 채팅이 동작해야 한다.
- 개인 프로젝트 규모에 맞춰 constructor injection과 작은 factory를 우선하며 과도한 프레임워크를 도입하지 않는다.

## 4. Assumptions

| ID | 가정 |
|---|---|
| ASM-001 | 초기 저장소는 사실상 greenfield이며 기존 사용자 데이터나 하위 호환 migration이 없다. |
| ASM-002 | native inference 계층은 C/C++와 CMake, managed Core와 Unity 연동은 C#을 사용한다. 구체 버전은 [DEVELOPMENT.md](DEVELOPMENT.md)에서 고정한다. |
| ASM-003 | 기본 검증 모델은 Qwen3.5 4B 계열 GGUF와 모바일에 적합한 quantization이지만 특정 파일명이나 quantization을 코드에 고정하지 않는다. |
| ASM-004 | GGUF 모델은 저장소와 앱 패키지에 기본 포함하지 않고 사용자가 접근 가능한 device-local 경로에서 로드한다. 모델 반입 UX는 MVP에서 최소화할 수 있다. |
| ASM-005 | MVP는 한 번에 하나의 local model instance와 하나의 활성 generation을 사용한다. 다중 모델 동시 추론은 범위 밖이다. |
| ASM-006 | MVP 대화는 한 캐릭터의 foreground session을 대상으로 하며 session 내 대화 이력만 유지한다. 앱 재실행 후 복구되는 장기 저장은 Memory 단계로 미룬다. |
| ASM-007 | MVP `ModelRouter`는 사용자가 선택한 등록 모델로 명시적으로 전달하며 자동 fallback이나 task-based routing을 하지 않는다. |
| ASM-008 | Character Bundle은 device-local 파일이며 versioned manifest와 Markdown instruction 파일로 구성한다. 정확한 schema와 validation 규칙은 아키텍처 문서에서 정의한다. |
| ASM-009 | `ModelEvent` 계약은 향후 ToolCall과 Usage를 확장할 수 있지만 MVP는 Token, Completed, Error 및 cancellation terminal 상태를 필수로 한다. |
| ASM-010 | Memory, Agent, Live2D 및 remote provider는 MVP에서 구현하지 않되 교체 가능한 interface와 중립적인 domain type을 수용할 계층 경계를 보존한다. |
| ASM-011 | MVP 진단은 기능 검증에 필요한 상태와 측정값을 제공한다. 고정된 TPS, TTFT, 메모리 및 발열 합격 수치는 실제 M4 iPad baseline 측정 후 정한다. |
| ASM-012 | 무료 Apple ID/Personal Team과 7일 provisioning 만료를 허용하는 설치 경로를 우선 검토한다. 다만 macOS CI만으로 Personal Team 실기기 설치가 가능하다고 보장하지 않으며, Apple signing feasibility는 사용자 소유의 **Phase 3 진입 전 Gate**로 보류한다. Gate를 통과하지 못하면 일시적인 Xcode Mac 접근 또는 유료 Apple Developer Program을 device milestone의 대안으로 선택한다. App Store와 TestFlight 출시는 필요하지 않다. |
| ASM-013 | Codemagic Apple Silicon을 Apple 빌드의 우선 후보로 사용하고, 가용성이나 무료 한도 문제가 있으면 GitHub Actions macOS로 대체할 수 있다. |
| ASM-014 | Linux backend 통합 테스트는 CPU를 사용하고, Metal 활성화는 macOS CI의 빌드 검증과 실제 M4 iPad에서 최종 확인한다. |
| ASM-015 | 모델, llama.cpp, Unity, Live2D 및 기타 third-party 자산의 라이선스는 재배포 전에 별도 확인한다. |
| ASM-016 | Qwen 계열이 multimodal capability를 제공하더라도 MVP는 text-only character chat으로 한정한다. image input, vision projector와 multimodal UI는 별도 요구사항이 승인되기 전까지 구현하지 않는다. |
| ASM-017 | Android는 MVP delivery target이 아니다. native C ABI와 CMake target은 장래 Android arm64-v8a shared library를 허용하도록 platform-neutral하게 유지하지만, Android SDK/NDK, Java/Kotlin, Unity Android module, device/CI artifact는 별도 승인 전 도입하지 않는다. |

## 5. 범위

### 5.1 MVP

- 교체 가능한 local model 설정과 사용자의 명시적 모델 선택
- llama.cpp 기반 GGUF load/unload/reload 및 metadata 조회
- context 생성, 재사용, reset 및 소유권 관리
- Metal을 사용하는 M4 iPad local inference
- 순서가 보장된 streaming response와 generation cancellation
- Character Bundle 선택, 로드 및 validation
- 캐릭터 instruction, scenario, lore 및 현재 대화를 조합하는 Prompt Compiler
- foreground conversation session과 대화 이력
- `IModelAdapter`, `ILocalModelBackend`, data-driven `ModelProfile`, 수동 `ModelRouter`
- 구조화된 오류, backend 상태 및 runtime diagnostics
- Unity UI에서 캐릭터/모델 선택, 메시지 입력, 스트리밍 출력 및 취소
- Linux unit/integration test, macOS CI의 iOS XCFramework 빌드, M4 iPad device validation

### 5.2 Post-MVP

- SQLite/FTS5 기반 session summary 및 long-term Memory
- Agent loop, Tool Registry, permission check와 allowlisted Tool 실행
- Live2D Cubism 표현과 중립적인 `CharacterState` 연결
- OpenAI-compatible remote provider 및 provider별 adapter
- fallback과 task-based automatic routing
- TTS, STT, emotion, lip sync, motion, 게임 및 상호작용 기능
- 원격 전송 범위를 통제하는 `RemotePrivacyPolicy`와 플랫폼 secure storage

### 5.3 Out of Scope

- multi-agent orchestration
- distributed inference
- cloud synchronization과 remote memory sync
- MVP의 vector database 및 embedding retrieval
- plugin marketplace와 임의 scripting language
- 복잡한 dependency injection framework
- 다중 local model 동시 추론
- App Store 배포와 TestFlight 출시 pipeline
- 첫 milestone의 상세 성능 경쟁이나 모델 품질 비교
- image input, vision projector와 multimodal interaction
- llama.cpp 내부에 HaruChat2 전용 기능 또는 모델별 prompt 규칙 구현
- Android 앱, Android SDK/NDK 설치와 Android device/CI artifact 검증

## 6. 기능 요구사항

| ID | 기능명 | 설명 | 우선순위 | MVP | Subsystem | Acceptance Criteria |
|---|---|---|---|---|---|---|
| FR-001 | 런타임 모델 설정 | 모델 경로, profile, context 및 generation 설정을 소스 수정 없이 제공한다. | P0 | 예 | Model Abstraction | 서로 다른 유효 GGUF 경로와 generation 설정으로 앱을 재빌드하지 않고 로드 요청을 만들 수 있고, 특정 GGUF 파일명이 코드에 존재하지 않는다. |
| FR-002 | 수동 모델 선택과 라우팅 | 등록된 모델 중 사용자가 선택한 모델을 `ModelRouter`가 해당 adapter로 전달한다. | P0 | 예 | Model Router | 두 개 이상의 mock/local model 등록 시 선택한 ID의 adapter만 호출되며, 등록되지 않은 ID에는 구조화된 오류를 반환한다. MVP에서 자동 fallback은 발생하지 않는다. |
| FR-003 | GGUF 모델 load/unload/reload | local backend가 유효한 GGUF를 로드하고 자원을 해제하며 같은 또는 다른 모델을 다시 로드한다. | P0 | 예 | Native Runtime / Backend | Linux fixture와 실제 iPad 모델에서 load→unload→reload가 성공한다. 유효하지 않은 경로 또는 GGUF에는 process crash 없이 오류가 반환되고 부분 할당 자원이 정리된다. |
| FR-004 | 모델 metadata와 capability 조회 | backend가 GGUF metadata와 사용 가능한 runtime capability를 모델 독립적인 형태로 노출한다. | P0 | 예 | Backend / Model Adapter | 로드한 fixture에서 model/tokenizer/context 관련 metadata를 조회할 수 있고 adapter가 streaming, cancellation 및 tool support 여부를 `ModelCapabilities`로 보고한다. |
| FR-005 | Context lifecycle | 모델과 별도의 context를 생성하고 대화 중 재사용하며 reset 후 새 대화를 시작하고 destroy할 수 있다. | P0 | 예 | Native Runtime / Conversation | 동일 context에서 연속 두 turn을 생성할 수 있고 reset 후 이전 대화가 prompt state에 남지 않는다. context destroy 이후 요청은 안전한 invalid-handle 오류를 반환한다. |
| FR-006 | Local inference | Qwen 계열 GGUF의 prompt를 llama.cpp로 처리하여 local response를 생성한다. | P0 | 예 | Local Model Adapter / Backend | Linux CPU fixture에서 generation smoke test가 통과하고 M4 iPad에서는 진단 정보로 Metal backend 활성화를 확인한 상태에서 비어 있지 않은 응답을 생성한다. |
| FR-007 | 스트리밍 생성 | 생성 결과를 순서가 보장된 `ModelEvent` stream으로 전달한다. | P0 | 예 | Model Adapter / Native Interop | 생성된 token/text fragment가 발생 순서대로 전달되고 정상 요청마다 terminal `Completed`가 정확히 한 번 발생한다. fragment를 이어 붙인 결과에는 누락이나 중복이 없다. |
| FR-008 | Generation cancellation | 진행 중인 generation을 사용자 또는 `CancellationToken` 요청으로 중단한다. | P0 | 예 | Backend / Native Interop / Unity | 스트리밍 중 취소하면 추가 Token 이벤트 없이 cancellation terminal 상태로 종료하며, 이후 같은 모델을 reset하거나 새 context로 다시 생성하고 unload할 수 있다. |
| FR-009 | 생성 동기화와 상태 보호 | load, unload, reset, generate가 context/model lifecycle을 위반하지 않도록 직렬화한다. | P0 | 예 | Backend | 활성 generation 중 같은 context의 reset/unload는 busy 오류로 거부된다. generation을 취소하고 terminal 상태를 받은 뒤에는 reset/unload가 성공하며 use-after-free, deadlock 또는 process crash가 발생하지 않는다. |
| FR-010 | Character Bundle 선택과 로드 | 사용자가 캐릭터를 선택하면 manifest, Markdown instruction, 선택적 lore와 examples를 로드하고 검증한다. | P0 | 예 | Character Runtime | 최소 유효 bundle이 로드되고 누락된 필수 파일, 지원하지 않는 schema version, 잘못된 manifest는 파일 경로와 원인을 포함한 validation 오류로 보고된다. |
| FR-011 | Prompt compilation | 캐릭터 persona, system instruction, speaking style, scenario, 관련 lore, session history와 현재 입력을 모델 독립적인 `ModelRequest`로 조합한다. | P0 | 예 | Prompt Compiler | 고정 fixture에 대해 결정적인 `ModelRequest` snapshot test가 통과하고, Character Runtime 출력에는 Qwen/llama.cpp/OpenAI 전용 template 문자열이 포함되지 않는다. |
| FR-012 | Model Profile 적용 | chat template policy, context limit, stop condition, capability 및 generation default를 data-driven profile로 제공한다. | P0 | 예 | Model Adapter | profile 교체만으로 generation default와 stop 조건을 바꿀 수 있으며 `LlamaCppBackend` 또는 Character Runtime 코드 수정이 필요하지 않다. 잘못된 profile은 validation 오류가 된다. |
| FR-013 | Session conversation | 사용자/assistant turn을 순서대로 보관하고 다음 prompt에 전달하며 사용자가 새 대화를 시작할 수 있다. | P0 | 예 | Conversation / Character Runtime | 두 번째 turn의 compiled request에 첫 turn이 올바른 role과 순서로 포함되고, 새 대화/reset 후에는 포함되지 않는다. 실패·취소된 불완전 응답은 completed assistant turn으로 저장되지 않는다. |
| FR-014 | Model Adapter 표준 계약 | 상위 runtime이 provider와 backend를 알지 않고 normalized request, stream event, capability, cancellation 및 usage를 다룬다. | P0 | 예 | Model Abstraction | Character/Conversation unit test가 `MockModelAdapter`만으로 동작하며 Core 코드에서 llama.cpp, Qwen 또는 특정 HTTP endpoint concrete type을 참조하지 않는다. |
| FR-015 | Local backend 표준 계약 | `LocalModelAdapter`가 C ABI 세부사항 대신 `ILocalModelBackend`를 통해 lifecycle, tokenize/generate, cancel 및 diagnostics를 사용한다. | P0 | 예 | Inference Backend | mock backend로 adapter contract test가 통과하고 native 구현 교체 시 Character, Conversation 및 Prompt Compiler를 수정하지 않는다. |
| FR-016 | 구조화된 오류 처리 | 설정, 파일, lifecycle, inference, cancellation 및 interop 오류를 code, message, category와 복구 가능 여부로 전달한다. | P0 | 예 | Cross-cutting / Diagnostics | invalid model, invalid character, invalid handle 및 generation failure가 process crash나 원시 pointer 노출 없이 구분되는 오류로 UI까지 전달된다. 오류 후 가능한 경우 reset/retry/unload가 동작한다. |
| FR-017 | Runtime diagnostics | model/backend 상태, Metal 활성화 여부, model load time, TTFT, prompt/generation throughput 및 context usage를 가능한 범위에서 조회한다. | P0 | 예 | Backend / Diagnostics | Linux와 iPad smoke test가 최소 backend 종류, load 상태, context usage 및 timing을 기록한다. 지원하지 않는 metric은 0 등 오해 가능한 값 대신 unavailable로 표시된다. |
| FR-018 | Unity 비동기 연동 | Unity가 model load와 generation을 main thread 밖에서 실행하고 main thread에서 안전하게 UI를 갱신한다. | P0 | 예 | Unity Presentation | generation 중 UI 입력과 frame update가 계속 동작하고, stream fragment와 terminal/error 상태가 Unity UI에 표시되며 scene 종료 시 worker와 native handle이 정리된다. |
| FR-019 | MVP 캐릭터 채팅 화면 | 사용자가 캐릭터와 모델을 선택하고 메시지를 보내며 응답을 스트리밍으로 보고 취소하거나 새 대화를 시작한다. | P0 | 예 | Unity Presentation / Application | M4 iPad에서 캐릭터 선택→Qwen GGUF load→메시지 입력→캐릭터 지침이 반영된 스트리밍 응답의 end-to-end 시나리오가 성공한다. 취소와 새 대화가 동일 실행에서 성공한다. |
| FR-020 | Native artifact 제공 | 동일한 platform-neutral C ABI core를 Linux shared library와 iOS XCFramework로 제공한다. Android arm64-v8a `.so`는 장래 target으로만 준비한다. | P0 | 예 | Native Build / Interop | Linux test executable이 `libllmcore.so`에 link되고, macOS CI가 device arm64를 포함한 `LlmCore.xcframework`를 생성하여 최소 consumer link test를 통과한다. Android artifact는 MVP acceptance가 아니다. |
| FR-021 | SQLite Memory | session summary와 long-term memory를 SQLite에 저장하고 FTS5, keyword relevance, recency, importance로 검색한다. | P1 | 아니요 | Memory | 재실행 후 memory가 유지되고 fixture 검색의 filter/order가 결정적으로 검증된다. Character/Model Adapter는 SQLite concrete type을 참조하지 않는다. |
| FR-022 | Memory retrieval 확장 | `IMemoryStore`와 retrieval 경계를 통해 향후 embedding 검색을 추가한다. | P1 | 아니요 | Memory | keyword retriever를 mock/대체 구현으로 교체해도 Character Runtime과 저장 schema 소비자가 변경되지 않는다. |
| FR-023 | Agent와 Tool 실행 | ToolCall을 Agent Runtime이 수신해 등록 여부와 permission을 확인하고 실행 결과를 모델 대화에 반환한다. | P1 | 아니요 | Agent Runtime | 등록된 test tool만 schema validation 후 실행되고 미등록/거부 tool은 실행되지 않으며 구조화된 ToolResult가 생성된다. 모델이 임의 코드를 직접 실행할 경로가 없다. |
| FR-024 | 기본 Tool 세트 | memory, lore, time, random 및 character action 관련 allowlisted tool을 필요에 따라 제공한다. | P1 | 아니요 | Agent / Tool Registry | 각 tool은 독립 unit test와 permission metadata를 가지며 잘못된 argument가 side effect 없이 거부된다. |
| FR-025 | Live2D 표현 연결 | 중립적인 `CharacterState`와 `CharacterAction`을 Unity Adapter가 Live2D expression, gaze, mouth 및 motion으로 변환한다. | P1 | 아니요 | Unity / Live2D | Core를 Live2D 없이 테스트할 수 있고 mock state fixture가 Unity/Live2D 계층에서 기대 expression 또는 motion으로 매핑된다. |
| FR-026 | OpenAI-compatible provider | Base URL, API key, model ID, timeout, capability와 generation parameter를 런타임 설정으로 받는 remote adapter를 제공한다. | P1 | 아니요 | Remote Model Adapter | mock HTTP server를 대상으로 streaming, cancellation, 오류 및 capability contract test가 통과하고 Character/Agent 코드는 수정되지 않는다. |
| FR-027 | 확장 ModelRouter | 향후 local/remote fallback 및 task-based routing policy를 추가할 수 있다. | P2 | 아니요 | Model Router | policy fixture에 따라 선택 결과를 검증할 수 있고 adapter 구현과 Character Runtime을 수정하지 않고 새 policy를 등록할 수 있다. |
| FR-028 | Remote privacy 통제 | 외부 API 사용 시 전송 conversation 범위, long-term memory/tool result 제외, masking 및 opt-in을 정책으로 통제한다. | P1 | 아니요 | Privacy / Remote Adapter | opt-in 전에는 remote 요청이 전송되지 않고 policy fixture에서 제외된 memory/tool field가 HTTP payload에 존재하지 않는다. API key는 캐릭터 파일이나 저장소에 기록되지 않는다. |

## 7. 비기능 요구사항

| ID | 범주 | 요구 | 검증 방법 |
|---|---|---|---|
| NFR-001 | Performance | model load와 inference는 Unity main thread를 block하지 않아야 한다. | Unity Profiler와 instrumentation으로 blocking native call이 main thread에서 실행되지 않으며 generation 중 UI/frame update가 지속되는지 확인한다. |
| NFR-002 | Streaming | stream event는 생성 순서를 보존하고 소비 속도가 느려도 무제한 메모리 증가를 만들지 않아야 한다. | sequence ID를 사용한 integration test와 느린 consumer stress test로 순서, bounded queue/backpressure 정책 및 terminal event를 검증한다. |
| NFR-003 | Memory | model/context/event buffer의 ownership이 명확하고 reset/unload/reload 반복 시 누수나 use-after-free가 없어야 한다. | sanitizer가 가능한 Linux 테스트와 M4 iPad에서 최소 10회의 load→generate→reset→unload smoke loop를 실행해 crash와 지속 증가를 확인한다. |
| NFR-004 | Reliability | 정상 완료, 취소, 오류의 terminal 상태는 요청마다 정확히 한 번 발생하고 이후 자원 정리가 가능해야 한다. | contract test에서 모든 종료 경로의 terminal event 수와 후속 reset/unload 성공을 검사한다. |
| NFR-005 | Thread Safety | model/context 소유권과 허용되는 동시 작업을 문서화하고 lifecycle 경쟁으로 deadlock이나 data race가 발생하지 않아야 한다. | ThreadSanitizer가 가능한 native stress test와 generate/cancel/reset/unload 경쟁 test를 수행한다. |
| NFR-006 | Portability | Core Runtime은 Linux와 iOS arm64에서 빌드되고 Unity 없이도 실행·테스트할 수 있어야 한다. C ABI/CMake는 장래 Android arm64-v8a cross-compile을 허용하되 Android delivery를 요구하지 않는다. | clean Linux CI에서 native/managed test를 실행하고 macOS CI에서 iOS XCFramework 및 non-Unity consumer link test를 수행한다. Android NDK가 준비된 뒤 configure validation을 별도 기록한다. |
| NFR-007 | Extensibility | Character/Agent 상위 로직은 특정 모델, provider, endpoint 또는 inference engine에 강결합하지 않아야 한다. | dependency test와 mock adapter/backend 교체 test로 concrete provider 참조가 경계를 넘지 않는지 확인한다. |
| NFR-008 | Dependency Direction | Unity, Live2D, SQLite, HTTP, P/Invoke와 llama.cpp는 Core domain의 interface 뒤에 위치해야 한다. | project reference와 namespace dependency를 CI에서 검사하고 Core assembly가 UnityEngine/Cubism/native concrete assembly를 참조하지 않는지 확인한다. |
| NFR-009 | Maintainability | llama.cpp는 pin된 upstream dependency로 관리하고 HaruChat2 기능을 upstream source에 직접 구현하지 않아야 한다. | dependency commit을 기록하고 update 절차를 문서화한다. upstream tree diff가 있다면 별도 patch와 ADR 없이는 CI를 실패시킨다. |
| NFR-010 | ABI 안정성 | native 경계에는 C ABI, opaque handle, 고정 폭 정수, 명시적 UTF-8 문자열과 메모리 ownership만 노출하며 C++/STL 타입을 노출하지 않는다. | C header review, C consumer compile test, lifecycle/error/문자열 ownership test와 symbol inspection을 수행한다. |
| NFR-011 | Testability | 실제 LLM 없이 Character, Prompt, Conversation, Router 및 Agent를 결정적으로 테스트할 수 있어야 한다. | Linux에서 `MockModelAdapter`와 fixture만 사용한 unit test suite를 네트워크 및 Unity 없이 실행한다. |
| NFR-012 | Linux-first | Apple SDK가 필요하지 않은 빌드·unit·backend CPU 테스트는 Linux에서 수행할 수 있어야 한다. | Linux clean CI가 native CTest, managed unit test 및 mock integration test를 모두 통과하는지 확인한다. |
| NFR-013 | Apple Build Isolation | macOS CI는 Apple clang, Metal, iOS arm64, XCFramework 및 consumer link 검증에만 필수여야 한다. | Linux workflow가 macOS artifact 없이 core test를 완료하고 macOS workflow의 책임이 Apple target 단계로 제한되는지 pipeline review로 확인한다. |
| NFR-014 | Build Reproducibility | toolchain과 third-party dependency 버전을 고정하고 clean checkout에서 같은 명령으로 artifact를 생성할 수 있어야 한다. | 문서화된 bootstrap/build 명령을 clean Linux 및 macOS CI에서 실행하고 llama.cpp commit과 artifact metadata를 기록한다. |
| NFR-015 | Dependency Isolation | 대형 모델 파일과 생성 artifact를 Git에 커밋하지 않고, 테스트 fixture는 작고 출처와 라이선스가 명확해야 한다. | Git history/size check, `.gitignore` 검증, CI fixture manifest 및 license review를 수행한다. |
| NFR-016 | Security | API key, signing credential 및 민감 설정을 소스나 Character Bundle에 평문 저장하지 않는다. | secret scanning과 설정 파일 review를 수행하고 post-MVP remote adapter에서 platform secure storage mock/adapter test를 수행한다. |
| NFR-017 | Privacy | local mode에서는 캐릭터, conversation, memory 및 prompt가 device 밖으로 전송되지 않아야 한다. | network-disabled end-to-end test와 local adapter 경로의 outbound HTTP 호출 감시로 외부 전송이 없음을 확인한다. |
| NFR-018 | Offline Capability | 설치·provisioning과 모델 반입이 끝난 뒤 핵심 MVP 채팅은 인터넷 없이 동작해야 한다. | M4 iPad를 offline 상태로 전환한 후 model load와 end-to-end 캐릭터 대화를 실행한다. |
| NFR-019 | Observability | 오류와 diagnostics는 비밀, 전체 prompt 또는 민감한 memory를 기본 로그에 노출하지 않고 문제 재현에 필요한 상태를 제공해야 한다. | redaction unit test와 오류 로그 review로 model ID, state, error code는 남고 API key와 전체 사용자 대화는 남지 않는지 검사한다. |
| NFR-020 | Mobile Constraints | context, batch, GPU offload와 generation 설정을 조절할 수 있고 memory pressure 또는 thermal 상황에서 안전하게 취소·해제할 수 있어야 한다. | 설정 변경 test, iPad memory warning/장시간 smoke test 및 취소 후 unload 검증을 수행한다. 수치 합격선은 baseline 측정 후 ROADMAP에 추가한다. |
| NFR-021 | Error Recovery | 잘못된 모델/캐릭터 입력이나 취소 가능한 inference 실패가 앱 전체 crash로 이어지지 않아야 한다. | invalid fixture와 fault injection test 후 UI 오류 표시, context reset 또는 model reload가 가능한지 확인한다. |
| NFR-022 | Simplicity | MVP는 단순한 constructor injection/factory를 사용하고 post-MVP 기능의 선제 구현이나 복잡한 DI/plugin framework를 도입하지 않는다. | architecture 및 dependency review에서 현재 milestone에 사용되지 않는 framework, service, schema가 추가되지 않았는지 확인한다. |
| NFR-023 | Licensing | 사용 모델, llama.cpp, Unity plugin, Live2D 및 테스트 자산의 라이선스와 재배포 조건을 추적해야 한다. | dependency/license inventory를 릴리스 전에 검토하고 출처나 조건이 불명확한 자산은 artifact에서 제외한다. |

## 8. MVP 통합 Acceptance Criteria

MVP는 다음 조건을 모두 만족할 때 완료된 것으로 본다.

1. Linux clean environment에서 native core, managed Core 및 mock/unit test가 성공한다.
2. Linux native integration test에서 GGUF load, metadata, context 생성, streaming generation, cancellation, reset, repeated generation 및 unload/reload가 성공한다.
3. macOS CI가 Metal을 포함한 iOS arm64 `LlmCore.xcframework`를 만들고 최소 consumer link test를 통과한다.
4. M4 iPad에서 Unity 앱을 실행해 캐릭터와 런타임 GGUF를 선택하고 Metal 활성화를 진단 정보로 확인한다.
5. 사용자 메시지에 캐릭터 instruction이 적용된 응답이 순서대로 스트리밍되며 Unity UI가 inference 동안 응답성을 유지한다.
6. 진행 중 생성을 취소한 뒤 새 대화를 시작하거나 context를 reset하고 다시 생성할 수 있다.
7. 동일 실행에서 model unload/reload 후 다시 생성할 수 있고 crash, deadlock 또는 명백한 지속 메모리 증가가 없다.
8. 인터넷 연결과 외부 LLM API 없이 위 캐릭터 채팅 흐름이 동작한다.
9. Core Runtime의 Linux 테스트는 Unity, Live2D, HTTP provider 및 Apple SDK를 요구하지 않는다.
10. Qwen을 다른 호환 GGUF/profile로 바꿀 때 Character Runtime 코드를 수정하지 않는다.

## 9. 요구사항 변경 원칙

- 계층 경계, public interface, native ABI, 저장 형식 또는 build pipeline을 바꾸는 요구사항은 관련 [ADR](adr/README.md)을 추가하거나 갱신한다.
- MVP와 post-MVP 경계를 바꿀 때는 이 문서와 [ROADMAP.md](ROADMAP.md)의 milestone 및 완료 조건을 함께 갱신한다.
- 아직 측정되지 않은 performance, memory, thermal 수치를 임의의 필수 기준으로 확정하지 않는다. 실제 M4 iPad baseline과 측정 절차를 먼저 기록한다.

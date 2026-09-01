# HaruChat2 작업 지침

이 파일은 저장소 전체에 적용되는 공용 작업 규칙이다. 하위 디렉터리에 더 구체적인 `AGENTS.md`가 생기면 해당 범위에서는 하위 지침을 함께 적용한다.

## 1. 프로젝트 목표와 현재 단계

HaruChat2는 iOS/iPadOS에서 실행되는 로컬 LLM 기반 AI Character Runtime이다. 최종 MVP는 M4 iPad의 Unity 애플리케이션에서 사용자가 캐릭터와 GGUF 모델을 선택하고, llama.cpp의 Metal backend로 캐릭터 지침이 적용된 응답을 streaming으로 받는 것이다.

현재는 설계 우선 단계다. 승인된 [요구사항](docs/REQUIREMENTS.md), [아키텍처](docs/ARCHITECTURE.md), [개발 환경](docs/DEVELOPMENT.md), [로드맵](docs/ROADMAP.md), [ADR](docs/adr/README.md)을 구현의 기준으로 삼는다. 문서에 없는 큰 기능이나 구조를 선제 구현하지 않는다.

## 2. 아키텍처 불변 규칙

다음 규칙은 명시적인 ADR 변경 없이 위반해서는 안 된다.

1. 로컬 LLM 구현과 Character/Agent Runtime을 분리한다.
2. 모델별 규칙과 inference engine을 분리한다.
3. Unity는 Presentation과 composition root로 제한한다. Core에는 `GameObject`, `MonoBehaviour`, `Animator`, `CubismModel` 등 Unity/Live2D 타입을 넣지 않는다.
4. llama.cpp는 pinned upstream dependency로 유지하고 프로젝트 기능을 upstream 내부에 구현하지 않는다.
5. `LlamaCppBackend`에는 Qwen persona, chat convention, tool convention, generation 정책을 넣지 않는다. 모델별 차이는 우선 `ModelProfile`과 `LocalModelAdapter`에서 처리한다.
6. Character Runtime은 provider 중립적인 `ModelRequest`를 생성하며 llama.cpp, Qwen, OpenAI endpoint를 직접 알지 않는다.
7. Memory와 Agent는 특정 모델/provider/backend에 의존하지 않는다.
8. 외부 구현인 llama.cpp, SQLite, HTTP, Unity, Live2D는 interface 뒤에 둔다.
9. Native/managed 경계는 `hc_llm_*` C ABI만 사용한다. C++ 객체, STL 타입, 예외를 경계 밖으로 노출하지 않는다.
10. inference와 model load/unload는 Unity main thread를 block하지 않는다.
11. 모델 파일, API key, signing credential, runtime database를 저장소에 커밋하지 않는다.
12. Linux에서 가능한 빌드와 테스트는 Linux에서 수행하고 Apple 전용 compile/link만 macOS CI에 맡긴다.

의존성 방향은 다음과 같다.

```text
Unity Presentation
        ↓
Application / Character Runtime
        ↓
Domain + provider-neutral contracts
        ↓
Model Adapter / Memory / Tool ports
        ↓
llama.cpp · SQLite · HTTP adapters
        ↓
C ABI → llama.cpp → Metal / CPU
```

## 3. 범위와 단순성

- 첫 vertical slice를 항상 우선한다: Linux native lifecycle → Apple artifact → managed adapter → Character Runtime → Unity/M4 iPad streaming.
- MVP에 필요하지 않은 multi-agent, automatic routing, vector DB, cloud sync, plugin marketplace, scripting language, 복잡한 DI framework는 구현하지 않는다.
- constructor injection과 작은 factory를 우선하고, 추상화는 교체 가능성이 실제로 요구된 경계에만 둔다.
- Memory, Agent, Live2D, remote provider는 로드맵의 해당 Phase 전에는 확장 지점만 유지한다.
- 성능 수치는 추정으로 확정하지 않는다. 측정 가능한 metrics를 먼저 만들고 M4 iPad baseline으로 예산을 결정한다.

## 4. Canonical 문서

정보를 여러 파일에 복제하지 말고 아래 문서를 원본으로 사용한다.

- 기능·비기능 요구와 acceptance criteria: `docs/REQUIREMENTS.md`
- 계층, 인터페이스, 데이터 흐름, threading, ABI: `docs/ARCHITECTURE.md`
- toolchain, 명령, CI, signing, secrets: `docs/DEVELOPMENT.md`
- 순서, milestone, gate, 완료 조건: `docs/ROADMAP.md`
- 중요한 결정의 이유와 대안: `docs/adr/`

설계가 바뀌면 영향을 받는 canonical 문서와 ADR을 같은 변경에서 갱신한다. 코드와 문서가 충돌하면 임의로 한쪽을 따르지 말고 승인된 최신 ADR과 요구사항을 확인한다.

## 5. 작업 절차

1. `git status --short`와 관련 파일을 확인해 사용자 변경을 보존한다.
2. `rg`와 `rg --files`로 범위를 좁힌 뒤 필요한 파일 구간만 읽는다.
3. 요구사항 ID와 Roadmap Phase를 확인하고 가장 작은 vertical change를 정의한다.
4. 독립적인 작업은 파일 소유권을 분리해 병렬화한다.
5. 구현 후 변경 범위에 가까운 테스트부터 실행한다.
6. 통합 단계에서 dependency 방향, 문서 추적성, 전체 관련 테스트를 한 번 검증한다.
7. Unity 작업은 설치된 `unity` CLI를 우선 사용해 editor/version 상태를 확인하고, 가능한 경우 EditMode/PlayMode 테스트와 batch build를 실행한다. 직접 editor 실행은 CLI로 표현할 수 없는 작업에만 사용하며, 정확한 명령과 결과는 `docs/DEVELOPMENT.md`에 따른다. UGUI 화면의 hierarchy, layout, style과 control reference는 Scene/Prefab에 직렬화해 Editor에서 배치한다. `RuntimeInitializeOnLoadMethod` 또는 `new GameObject`로 제품 UI를 조립하지 않으며, runtime script는 scene-wired control의 상태·행동만 담당한다.
8. 완료 보고는 변경, 결정, 검증, 위험만 간결하게 남긴다.

## 6. 토큰 효율 규칙

- 저장소 공통 탐색은 리드가 한 번 수행하고, 서브 에이전트에게 확인된 사실과 필요한 파일만 전달한다.
- 전체 파일을 습관적으로 읽지 않는다. 검색 결과와 관련 구간부터 확인한다.
- 이미 확인된 저장소 상태, 요구사항, 결정을 반복 설명하지 않는다.
- 긴 파일, 로그, 빌드 출력 전문을 대화에 붙이지 않는다. 실패 원인 주변과 요약만 보고한다.
- 동일 내용을 여러 문서에 복사하지 않고 canonical 문서로 링크한다.
- 좁은 테스트를 각 작업에서 실행하고, 비용이 큰 전체 build/test는 통합 단계에서 한 번 수행한다.
- 변화 없는 상태를 반복 polling하거나 같은 진행 상황을 재서술하지 않는다.
- 대규모 재작성보다 의도를 충족하는 작은 diff를 우선하되 문서와 계약의 일관성은 유지한다.
- 현재 milestone 밖의 조사나 구현으로 컨텍스트를 확장하지 않는다.

## 7. 서브 에이전트 병렬화

서로 독립적이고 파일 소유권을 분리할 수 있는 작업이 둘 이상이면 서브 에이전트 병렬화를 반드시 검토하고, 충돌 사유가 없으면 사용한다.

- 리드 포함 동시 작업 슬롯은 4개로 보고 자식 에이전트는 최대 3명까지 사용한다.
- 리드는 위임 전에 목표, 입력 자료, 허용 파일, 금지 파일, 산출물, 검증 방법, 보고 형식을 지정한다.
- 동일 파일을 둘 이상의 에이전트가 동시에 수정하게 하지 않는다.
- 공통 interface와 cross-cutting 결정은 리드가 먼저 확정한다.
- 서브 에이전트가 담당 밖 파일 변경이 필요하다고 판단하면 직접 수정하지 않고 리드에게 알린다.
- 작은 단일 작업, 강한 순차 의존, 같은 파일 중심 작업, 공통 설계가 잠기지 않은 작업은 위임하지 않는다.
- 자식의 추가 재위임은 리드가 명시적으로 허용한 경우에만 한다.
- 리드만 최종 통합, 중복 제거, 문서 간 정합성, 전체 테스트 결과를 확정한다.
- 완료 보고에서 파일 전문을 재전송하지 않는다.

서브 에이전트와 최종 보고 형식은 다음과 같다.

```text
Changed:
Decisions:
Validated:
Risks/Blockers:
```

## 8. 구현과 테스트 원칙

- Managed Core는 `.NET Standard 2.1` 호환성을 유지하고 Unity-free Linux test host에서 검증한다.
- native build는 CMake와 Ninja를 기본으로 하고 CTest로 lifecycle과 streaming을 검증한다.
- Character/Agent 테스트에서는 실제 모델 대신 `MockModelAdapter`를 우선 사용한다.
- 실제 GGUF integration test는 출처, license, checksum이 고정된 작은 fixture를 사용한다.
- Native event byte 조각은 incremental UTF-8 decoder로 처리한다. 토큰 단위가 문자 경계와 일치한다고 가정하지 않는다.
- context당 active generation은 하나만 허용하고 cancellation 뒤에는 재사용 가능 상태를 명시적으로 검증한다.
- Unity API 호출은 main-thread dispatcher를 통해 수행하고 worker thread에서는 neutral DTO만 다룬다.
- iOS artifact compile 성공을 Metal runtime 활성화로 간주하지 않는다. 실제 M4 iPad metrics로 확인한다.
- secrets는 환경변수 또는 플랫폼 secure storage abstraction을 통해 주입하고 로그에 출력하지 않는다.

## 9. Git과 파일 관리

- 사용자 변경과 무관한 파일을 수정하거나 되돌리지 않는다.
- GGUF, runtime DB, build output, provisioning profile, API key를 커밋하지 않는다.
- `AGENTS.md`, 설계 문서, 관리형 project/solution 파일은 커밋한다.
- llama.cpp update는 pinned revision 변경과 호환성 검증을 별도 변경으로 수행한다.
- 생성 artifact 대신 재현 가능한 script, manifest, checksum을 커밋한다.
- destructive Git 명령을 사용하지 않는다.

## 10. 완료 정의

변경은 다음 조건을 모두 만족해야 완료다.

- 관련 FR/NFR 또는 Roadmap milestone과 연결된다.
- 계층 의존성과 MVP 범위를 위반하지 않는다.
- 관련 Linux 테스트가 통과한다.
- Apple/Unity/device 전용 검증을 실행하지 못한 경우 정확한 후속 gate로 기록한다.
- 변경된 public contract, build 절차, 결정이 canonical 문서와 ADR에 반영된다.
- 남은 위험과 검증하지 못한 항목을 숨기지 않고 짧게 보고한다.

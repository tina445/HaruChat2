# ADR-0006: Unity를 Presentation 계층으로 제한

## Context

Unity와 향후 Live2D는 iPad UI와 character 표현에 적합하지만 `MonoBehaviour`, `GameObject`, `Animator`와 `CubismModel`을 Core Runtime에 넣으면 headless Linux test가 어려워지고 Unity/runtime upgrade가 domain 전체에 영향을 준다. Character, Agent, Memory와 model orchestration은 Unity 없이도 유효한 application logic이다.

## Decision

Unity를 Presentation과 Composition Root 계층으로 제한한다.

- Domain/Application에는 Unity assembly reference와 Unity type을 허용하지 않는다.
- Core는 `CharacterState`, `CharacterAction`, `Emotion`, `Conversation`, `ToolCall`과 같은 neutral type/event만 노출한다.
- Unity adapter가 model/application event를 main-thread dispatcher로 전달하고 UI, scene, Live2D mapping을 수행한다.
- `UnityCharacterController`와 `Live2DCharacterAdapter`는 Core interface를 소비하며 역방향 참조를 만들지 않는다.
- Native polling과 managed stream 소비는 background worker에서 수행한다. Unity object 변경만 main thread에서 실행한다.
- Core source는 Unity-compatible `netstandard2.1` API 범위를 유지하고 .NET 10 test host에서 독립적으로 검증한다.
- Live2D SDK나 asset이 없어도 fallback presenter로 채팅 기능이 동작해야 한다.

## Alternatives

- **모든 runtime을 MonoBehaviour로 구현:** prototype은 빠르지만 lifecycle, testability와 재사용성이 낮다.
- **Unity package와 Core를 하나의 assembly로 구성:** dependency 방향을 강제하기 어렵고 headless build가 Unity license에 묶인다.
- **Unity를 사용하지 않고 native UI만 구현:** Live2D/상호작용 확장 목표와 현재 기술 방향을 바꾼다.

## Consequences

- 대부분의 기능을 Linux의 .NET test로 빠르게 검증하고 Unity/Live2D를 교체할 수 있다.
- DTO/event mapping과 composition code가 추가된다.
- managed runtime API를 Unity 호환 범위로 제한해야 한다.
- main-thread dispatch, scene disposal과 domain/application lifecycle의 경계를 명시적으로 관리해야 한다.
- Live2D 실패나 frame degradation을 inference/application failure와 분리할 수 있다.

## Status

Accepted

## Date

2026-08-27

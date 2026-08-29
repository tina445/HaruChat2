# Architecture Decision Records

ADR은 구현 전후에 유지해야 할 중요한 기술 결정을 기록한다. 기존 ADR의 결론을 바꿀 때는 파일을 조용히 수정하지 않고 새 ADR에서 이전 결정을 `Superseded` 처리한다. 상태는 `Proposed`, `Accepted`, `Deprecated`, `Superseded` 중 하나를 사용한다.

| ADR | 결정 | 상태 | 날짜 |
|---|---|---|---|
| [ADR-0001](0001-use-llama-cpp.md) | Local inference engine으로 llama.cpp 사용 | Accepted | 2026-08-27 |
| [ADR-0002](0002-stable-c-abi.md) | Managed/native 경계에 안정적인 C ABI 사용 | Accepted | 2026-08-27 |
| [ADR-0003](0003-separate-model-adapter-and-backend.md) | Model Adapter와 inference backend 분리 | Accepted | 2026-08-27 |
| [ADR-0004](0004-use-sqlite-fts5-memory.md) | MVP 이후 memory에 SQLite/FTS5 사용 | Accepted | 2026-08-27 |
| [ADR-0005](0005-build-apple-targets-on-macos-ci.md) | Apple target만 macOS CI에서 빌드 | Accepted | 2026-08-27 |
| [ADR-0006](0006-limit-unity-to-presentation.md) | Unity를 Presentation 계층으로 제한 | Accepted | 2026-08-27 |
| [ADR-0007](0007-use-native-polling-event-queue.md) | Native streaming 경계에 polling event queue 사용 | Accepted | 2026-08-27 |
| [ADR-0008](0008-use-data-driven-model-profiles.md) | 모델 차이를 data-driven profile로 표현 | Accepted | 2026-08-27 |
| [ADR-0009](0009-platform-neutral-native-abi.md) | Platform-neutral native ABI와 Android delivery 보류 | Accepted | 2026-08-28 |
| [ADR-0010](0010-use-xcross-only-as-phase-3-device-probe-host.md) | xcross를 Phase 3 Flutter device probe host로만 사용 | Accepted | 2026-08-29 |

각 ADR은 `Context`, `Decision`, `Alternatives`, `Consequences`, `Status`, `Date`를 포함한다.

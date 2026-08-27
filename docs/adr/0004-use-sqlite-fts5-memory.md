# ADR-0004: Device-local Memory에 SQLite와 FTS5 사용

## Context

Character conversation, session summary와 long-term memory는 device-local persistence와 검색이 필요하다. MVP 이후 초기 retrieval은 keyword relevance, recency와 importance로 충분하며 embedding model과 vector database를 함께 배포하면 mobile storage/memory, build와 운영 복잡도가 커진다. Memory는 model provider와 독립적이어야 한다.

## Decision

Memory subsystem의 첫 persistence adapter로 SQLite와 FTS5를 사용한다.

- Domain/Application은 `IMemoryStore`와 retrieval contract만 참조하고 SQLite type/SQL을 노출하지 않는다.
- schema migration을 versioned forward migration으로 관리하며 conversation, summary, memory item과 FTS index를 명시적으로 분리한다.
- 기본 ranking은 normalized FTS relevance, recency decay와 stored importance의 가중 결합으로 구현하고 test fixture에서 deterministic해야 한다.
- character/session scope를 모든 query에 강제한다. 기본 저장 위치는 device-local application data다.
- connection 정책은 connection factory와 short transaction을 사용하고 write는 serialize한다. inference나 Unity main thread에서 blocking DB I/O를 수행하지 않는다.
- embedding retrieval은 향후 별도 retriever implementation으로 추가할 수 있지만 schema와 MVP에 vector column/database를 선제 도입하지 않는다.
- database encryption은 MVP 필수가 아니며 플랫폼 file protection, export/delete와 log redaction을 먼저 적용한다.

## Alternatives

- **JSON/file-per-session:** 작은 prototype은 쉽지만 migration, transaction과 검색 안정성이 낮다.
- **Vector database:** semantic retrieval에 유리하지만 embedding runtime과 mobile 운영 비용이 현재 요구보다 크다.
- **Cloud memory:** offline/privacy 우선과 맞지 않고 계정·동기화가 필요하다.
- **In-memory only:** restart 후 conversation/memory 요구를 충족하지 못한다.

## Consequences

- 별도 server 없이 transaction, migration과 FTS retrieval을 제공한다.
- SQLite/FTS5가 포함된 실제 iOS provider 조합을 device 이전에 Apple integration에서 확인해야 한다.
- DB growth, migration과 corruption/locked/disk-full 오류 처리 책임이 생긴다.
- 향후 embedding을 추가해도 `IMemoryStore`와 Character/Agent의 provider 독립성을 유지할 수 있다.

## Status

Accepted

## Date

2026-08-27

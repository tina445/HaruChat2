# ADR-0013: Use opt-in SQLite v1 memory schema

## Context

Phase 7 needs durable, provider-neutral memory without making SQLite or raw transcripts part of Character Runtime. Persistent user data also needs clear retention and deletion behavior.

## Decision

- Keep `MemoryItem`, `MemorySession`, store/retriever ports and persistence policy in the Unity-free runtime; put SQLite/FTS5 in `com.haruchat.memory.sqlite`.
- Use the v2 `schema_migrations`, `memory_sessions`, `memory_items`, `memory_settings`, and external-content FTS5 schema with triggers. Migrations are forward-only and newer databases are rejected.
- Require explicit opt-in plus positive retention before writes. Store a bounded latest-turn session summary and only long-term items supplied by an explicit candidate factory.
- Bound memory injection by item/token/session-summary policy. Context-window and temperature controls remain model runtime settings, while hardware recommendations are labelled estimates unless device telemetry supplied the cap.
- Use Linux system SQLite for managed tests; defer iOS SQLite provider linkage and FTS5 device validation to the Apple gate.

## Alternatives

- Persist every transcript turn: rejected because it conflicts with minimal retention and increases privacy risk.
- Put SQLite calls in Character Runtime: rejected because it violates dependency direction and Unity-free testability.
- Bundle a SQLite binary for Linux now: rejected because the system library suffices for Linux validation and iOS packaging remains platform-specific.

## Consequences

Memory failures do not roll back completed canonical conversation turns. Callers must provide an intentional long-term-memory candidate policy. The Apple gate must prove the selected iOS SQLite provider includes FTS5.

## Status

Accepted

## Date

2026-09-02

# ADR-0012: Use the Flutter native probe as the Phase 6 UI test host

## Context

Phase 6's production Presentation remains Unity, but the current environment cannot reliably build the Unity application. The repository already has `flutter/xcross_native_probe`, which consumes the native C ABI and can run Flutter widget tests on Linux. It is therefore a useful way to exercise the same visible control hierarchy and event projection while Unity-specific build work is unavailable.

## Decision

- Rework `flutter/xcross_native_probe` as a P6 UI test host that mirrors the Unity scene's control rail, compact drawer, chat surface, status banner, diagnostics, and incremental native-token projection.
- Keep its `hc_llm_*` lifecycle calls and xcross iPad route. The UI host remains explicitly non-production and does not import Unity code.
- Retain Unity as the P6 MVP Presentation and final M4 device gate. Flutter widget tests are evidence for UI structure and native event projection only.

## Alternatives

- Create a separate Flutter app: rejected because it duplicates the existing native probe and adds another test path.
- Treat Flutter as the production P6 client: rejected because it changes the approved Unity/Live2D product direction.
- Pause all P6 work until Unity builds: rejected because the existing probe can safely validate a bounded subset now.

## Consequences

Linux can validate P6 UI layout and interaction state without a Unity editor. Character conversation semantics remain the raw diagnostic prompt path, so this host does not prove the managed `CharacterChatService`, Unity composition root, Metal activation, or Apple packaging. Those stay as explicit Unity/M4 gates.

## Status

Accepted

## Date

2026-09-01

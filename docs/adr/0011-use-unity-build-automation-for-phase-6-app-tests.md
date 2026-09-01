# ADR-0011: Use Unity Build Automation for Phase 6 iOS app test builds

## Context

The native Apple artifact pipeline is intentionally independent of Unity Build Automation (UBA), but Phase 6 needs a repeatable signed Unity iOS test build to exercise the staged XCFramework, scene, and iOS document picker on a device. UBA iOS configuration requires a credentials set, while artifact validation must remain reproducible in the repository.

## Decision

Use a manual UBA iOS target named `haruchat-ios-device-test` after Apple Developer Program signing material is available. The target runs the repository pre-build hook to download a short-lived, checksum-verified Phase 2 artifact and stage it under the ignored Unity plugin directory. It invokes `HaruChat.Editor.HaruChatBuildAutomation.ValidateIosBuildInputs` before export and runs EditMode tests. Apple credentials and artifact URL/digest remain UBA secrets. Auto-build is disabled until one signed device-test build succeeds.

## Alternatives

- Use UBA for the native XCFramework producer: rejected because it couples CMake/Metal feedback to Unity and duplicates the existing native CI.
- Commit the XCFramework or provisioning files: rejected because they are generated artifact or credential material.
- Use UBA as an unsigned iOS export target: rejected because its iOS target configuration requires a credentials set and therefore does not satisfy the current no-membership constraint.
- Export locally on Linux: rejected because iOS Xcode export and signing require macOS.

## Consequences

The Unity test path becomes reproducible without making the native pipeline depend on UBA. UBA account access, source-control connection, quota, signed URL issuance, credential rotation, and physical-device installation remain user-owned gates. A UBA success is packaging/signing evidence only, not Metal runtime evidence.

## Status

Accepted

## Date

2026-09-01

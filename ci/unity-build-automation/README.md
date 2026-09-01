# Unity Build Automation — future signed iOS device-test target

This directory documents the dashboard-held configuration for the Unity app test build. It deliberately contains no Unity, Apple, source-control, artifact-download, or signing credential.

## Repository hooks

Configure the iOS target to run these repository paths:

| UBA setting | Value |
|---|---|
| Pre-build script | `scripts/prepare-unity-uba-ios.sh` |
| Pre-export method | `HaruChat.Editor.HaruChatBuildAutomation.ValidateIosBuildInputs` |
| Project subfolder | `HaruChat2` |
| Scene override | `Scenes/SampleScene.unity` |
| Tests | EditMode; fail the build on test failure |

The pre-build hook downloads a short-lived, verified XCFramework from a Phase 2 artifact source, stages it outside version control, then validates the iOS device slice. The pre-export hook repeats the local plugin check inside the Unity Editor before it creates the Xcode project.

## Dashboard configuration

Create `haruchat-ios-device-test` in **DevOps → Build Automation → Configurations** only after an Apple Developer Program membership is available:

1. Connect the repository and select the branch under test. Set **Project subfolder** to `HaruChat2`; enable Unity version auto-detection from `ProjectSettings/ProjectVersion.txt`.
2. Select **iOS**, a macOS builder, and the Unity architecture available for the selected Apple Silicon image. Keep the target manual-only initially.
3. Add a development `.p12`, its password, and a development `.mobileprovision` through UBA Credentials. Register the test iPad UDID in the profile. Do not place any of them in this repository.
4. Add secret environment variables `HARUCHAT_LLMCORE_ARTIFACT_URL` and `HARUCHAT_LLMCORE_ARTIFACT_SHA256`. The URL must be a short-lived download URL for the unsigned Phase 2 `LlmCore.xcframework.zip`; the SHA-256 must be its expected digest.
5. Add the repository hooks from the table, select the `Development` iOS build output, and run one manual build. Preserve only the UBA build URL, source commit, Xcode image, Unity version, artifact checksum, and result in the device-test record.

## Boundaries

UBA builds the Unity application only. `scripts/build-apple-xcframework.sh` and the existing macOS native CI remain the authoritative producer of the unsigned native artifact. An iOS UBA configuration cannot be saved without signing credentials, so it is not a no-Apple-membership export path. A successful UBA build does not prove Metal activation or M4 iPad inference; complete the P6 smoke checklist on a physical device.

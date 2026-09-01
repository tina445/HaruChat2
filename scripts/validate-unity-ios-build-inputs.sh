#!/usr/bin/env bash
# Validate only repository-visible prerequisites for an iOS Unity export.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project_root="$repo_root/HaruChat2"
plugin_root="$project_root/Assets/Plugins/iOS/LlmCore.xcframework"

[[ -f "$project_root/ProjectSettings/ProjectVersion.txt" ]] || { printf '%s\n' 'Unity ProjectVersion.txt is missing.' >&2; exit 1; }
grep -q '^m_EditorVersion: 6000.3.11f1$' "$project_root/ProjectSettings/ProjectVersion.txt" || { printf '%s\n' 'Unexpected Unity version; update the UBA target deliberately.' >&2; exit 1; }
grep -q 'path: Assets/Scenes/SampleScene.unity' "$project_root/ProjectSettings/EditorBuildSettings.asset" || { printf '%s\n' 'SampleScene is not enabled for build.' >&2; exit 1; }
[[ -d "$plugin_root" ]] || { printf '%s\n' 'Verified LlmCore.xcframework is not staged.' >&2; exit 1; }
[[ -d "$plugin_root/ios-arm64" || -d "$plugin_root/ios-arm64_arm64e" ]] || { printf '%s\n' 'LlmCore.xcframework has no iOS device arm64 slice.' >&2; exit 1; }
printf '%s\n' 'Unity iOS build inputs validated.'

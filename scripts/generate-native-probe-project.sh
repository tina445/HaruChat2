#!/usr/bin/env bash
# Generate a local Xcode project after importing an unsigned Phase 2 artifact.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "$(uname -s)" != "Darwin" ]]; then
  printf '%s\n' 'The native probe Xcode project requires macOS with Xcode.' >&2
  exit 1
fi
[[ -d "$repo_root/native/probe/Vendor/LlmCore.xcframework" ]] || {
  printf '%s\n' 'Run scripts/prepare-native-probe.sh first.' >&2
  exit 1
}
cmake -S "$repo_root/native/probe" -B "$repo_root/native/probe/out/iphoneos" -G Xcode \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
  -DCMAKE_OSX_SYSROOT=iphoneos
printf 'Open %s\n' "$repo_root/native/probe/out/iphoneos/HaruChatNativeProbe.xcodeproj"

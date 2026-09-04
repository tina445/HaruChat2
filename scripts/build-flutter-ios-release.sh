#!/usr/bin/env bash
set -euo pipefail

if [[ "$(uname -s)" != "Darwin" ]]; then
  printf '%s\n' 'Flutter iOS release/AOT builds require macOS with Xcode.' >&2
  exit 1
fi

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
probe_dir="$repo_root/flutter/xcross_native_probe"
vendor_dir="$probe_dir/packages/hc_llm_flutter/ios/hc_llm_flutter/Vendor/LlmCore.xcframework"

if ! command -v xcodebuild >/dev/null 2>&1; then
  printf '%s\n' 'Xcode command line tools are required.' >&2
  exit 1
fi

if [[ ! -d "$vendor_dir" ]]; then
  printf '%s\n' 'Stage the checksum-verified LlmCore.xcframework with scripts/prepare-xcross-native-probe.sh first.' >&2
  exit 1
fi

staged_header="$vendor_dir/ios-arm64/Headers/hc_llm.h"
source_header="$repo_root/native/llmcore/include/hc_llm.h"
if ! cmp -s "$staged_header" "$source_header"; then
  printf '%s\n' 'The staged LlmCore.xcframework does not match native/llmcore/include/hc_llm.h.' >&2
  printf '%s\n' 'Rebuild the XCFramework from this revision and stage it with scripts/prepare-xcross-native-probe.sh.' >&2
  exit 1
fi

cd "$probe_dir"
flutter pub get
flutter build ipa --release --export-method development "$@"

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

cd "$probe_dir"
flutter pub get
flutter build ipa --release --export-method development "$@"

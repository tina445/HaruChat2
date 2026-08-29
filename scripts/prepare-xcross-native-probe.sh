#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  printf 'Usage: %s /absolute/path/LlmCore.xcframework.zip\n' "$0" >&2
  exit 2
fi

artifact="$1"
[[ -f "$artifact" ]] || { printf 'Artifact not found: %s\n' "$artifact" >&2; exit 2; }

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
vendor_dir="$repo_root/flutter/xcross_native_probe/vendor"
target="$vendor_dir/LlmCore.xcframework"
[[ ! -e "$target" ]] || { printf 'Refusing to overwrite existing import: %s\n' "$target" >&2; exit 2; }

mkdir -p "$vendor_dir"
unzip -q "$artifact" -d "$vendor_dir"
[[ -d "$target" && -f "$target/ios-arm64/Headers/hc_llm.h" ]] || {
  printf '%s\n' 'Archive does not contain the expected LlmCore.xcframework device header.' >&2
  exit 1
}
printf 'xcross native probe artifact imported: %s\n' "$target"

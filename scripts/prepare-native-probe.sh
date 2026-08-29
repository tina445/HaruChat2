#!/usr/bin/env bash
# Import a verified Phase 2 artifact without placing binaries in source control.
set -euo pipefail

if [[ $# -ne 1 ]]; then
  printf 'Usage: %s /absolute/path/LlmCore.xcframework.zip\n' "$0" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifact_zip="$1"
vendor_dir="$repo_root/native/probe/Vendor"

[[ -f "$artifact_zip" ]] || { printf 'Artifact zip not found: %s\n' "$artifact_zip" >&2; exit 1; }
if [[ -e "$vendor_dir" && -n "$(find "$vendor_dir" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  printf 'Probe Vendor directory must be empty: %s\n' "$vendor_dir" >&2
  exit 1
fi
command -v unzip >/dev/null || { printf '%s\n' 'unzip is required.' >&2; exit 1; }

mkdir -p "$vendor_dir"
unzip -q "$artifact_zip" -d "$vendor_dir"
[[ -d "$vendor_dir/LlmCore.xcframework" ]] || {
  printf '%s\n' 'Archive does not contain LlmCore.xcframework.' >&2
  exit 1
}
printf 'Native probe artifact imported: %s\n' "$vendor_dir/LlmCore.xcframework"

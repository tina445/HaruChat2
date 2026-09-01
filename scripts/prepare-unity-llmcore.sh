#!/usr/bin/env bash
# Stage a verified Phase 2 artifact for Unity locally. The XCFramework is ignored.
set -euo pipefail

if [[ $# -ne 2 ]]; then
  printf 'Usage: %s /absolute/path/LlmCore.xcframework.zip /absolute/path/LlmCore.xcframework.zip.sha256\n' "$0" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifact_zip="$1"
checksum_file="$2"
target="$repo_root/HaruChat2/Assets/Plugins/iOS/LlmCore.xcframework"

[[ -f "$artifact_zip" ]] || { printf 'Artifact zip not found: %s\n' "$artifact_zip" >&2; exit 1; }
[[ -f "$checksum_file" ]] || { printf 'Checksum file not found: %s\n' "$checksum_file" >&2; exit 1; }
command -v shasum >/dev/null || { printf '%s\n' 'shasum is required.' >&2; exit 1; }
command -v unzip >/dev/null || { printf '%s\n' 'unzip is required.' >&2; exit 1; }

(cd "$(dirname "$artifact_zip")" && shasum -a 256 -c "$checksum_file")
[[ ! -e "$target" ]] || { printf 'Unity plugin target already exists: %s\nRefusing to overwrite it. Remove it manually after verifying it is safe.\n' "$target" >&2; exit 1; }
stage="$(mktemp -d "${TMPDIR:-/tmp}/haruchat-unity-xcframework.XXXXXX")"
trap 'rm -rf "$stage"' EXIT
unzip -q "$artifact_zip" -d "$stage"
[[ -d "$stage/LlmCore.xcframework" ]] || { printf '%s\n' 'Archive does not contain LlmCore.xcframework.' >&2; exit 1; }
mkdir -p "$(dirname "$target")"
mv "$stage/LlmCore.xcframework" "$target"
printf 'Unity iOS plugin staged: %s\n' "$target"

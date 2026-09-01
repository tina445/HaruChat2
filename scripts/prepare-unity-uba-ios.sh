#!/usr/bin/env bash
# UBA pre-build hook: download a short-lived verified native artifact and stage it for Unity.
set -euo pipefail

: "${HARUCHAT_LLMCORE_ARTIFACT_URL:?Set a secret signed URL for LlmCore.xcframework.zip.}"
: "${HARUCHAT_LLMCORE_ARTIFACT_SHA256:?Set the expected SHA-256 as a secret environment variable.}"

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/haruchat-uba-ios.XXXXXX")"
trap 'rm -rf "$work_dir"' EXIT

command -v curl >/dev/null || { printf '%s\n' 'curl is required.' >&2; exit 1; }
curl --fail --location --silent --show-error --retry 3 \
  "$HARUCHAT_LLMCORE_ARTIFACT_URL" \
  --output "$work_dir/LlmCore.xcframework.zip"
printf '%s  %s\n' "$HARUCHAT_LLMCORE_ARTIFACT_SHA256" 'LlmCore.xcframework.zip' >"$work_dir/LlmCore.xcframework.zip.sha256"

bash "$repo_root/scripts/prepare-unity-llmcore.sh" \
  "$work_dir/LlmCore.xcframework.zip" \
  "$work_dir/LlmCore.xcframework.zip.sha256"
bash "$repo_root/scripts/validate-unity-ios-build-inputs.sh"

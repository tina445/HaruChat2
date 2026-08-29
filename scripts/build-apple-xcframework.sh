#!/usr/bin/env bash
# Build the unsigned, static iOS XCFramework consumed by the later Unity plugin.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ "$(uname -s)" != "Darwin" ]]; then
  printf '%s\n' 'Apple XCFramework builds require macOS with Xcode.' >&2
  exit 1
fi

for command_name in cmake xcodebuild xcrun shasum zip lipo; do
  command -v "$command_name" >/dev/null || {
    printf 'Required command is missing: %s\n' "$command_name" >&2
    exit 1
  }
done

output_root="${HARUCHAT_APPLE_ARTIFACT_DIR:-$repo_root/artifacts/apple}"
if [[ -e "$output_root" && -n "$(find "$output_root" -mindepth 1 -maxdepth 1 -print -quit)" ]]; then
  printf 'Artifact directory must be empty: %s\n' "$output_root" >&2
  printf '%s\n' 'Choose HARUCHAT_APPLE_ARTIFACT_DIR for a separate build instead of deleting files automatically.' >&2
  exit 1
fi
mkdir -p "$output_root"

build_root="$(mktemp -d "${TMPDIR:-/tmp}/haruchat-apple.XXXXXX")"
trap 'rm -rf "$build_root"' EXIT
package_root="$build_root/package"
mkdir -p "$package_root"

configure_slice() {
  local slice_name="$1"
  local sdk="$2"
  local build_dir="$build_root/$slice_name"
  local archive_dir="$package_root/$slice_name"

  # Use Xcode's generator: it is bundled with the required Apple SDK and avoids
  # assuming that a hosted runner also preinstalls Ninja.
  cmake -S native -B "$build_dir" -G Xcode \
    -DCMAKE_BUILD_TYPE=Release \
    -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
    -DCMAKE_OSX_SYSROOT="$sdk" \
    -DBUILD_SHARED_LIBS=OFF \
    -DLLMCORE_BUILD_TESTS=OFF \
    -DLLMCORE_ENABLE_LLAMA_CPP=ON \
    -DGGML_METAL=ON \
    -DGGML_METAL_EMBED_LIBRARY=ON
  cmake --build "$build_dir" --target llmcore

  mkdir -p "$archive_dir/Headers"
  cp native/llmcore/include/hc_llm.h "$archive_dir/Headers/"

  # macOS ships Bash 3.x, which has no mapfile/readarray builtin.
  static_archives=()
  while IFS= read -r archive; do
    static_archives+=("$archive")
  done < <(find "$build_dir" -type f -name '*.a' -print | sort)
  if (( ${#static_archives[@]} == 0 )); then
    printf 'No static archives were generated for %s.\n' "$slice_name" >&2
    exit 1
  fi
  xcrun --sdk "$sdk" libtool -static -o "$archive_dir/libLlmCore.a" "${static_archives[@]}"
  lipo -archs "$archive_dir/libLlmCore.a" | grep -Fxq arm64 || {
    printf 'Unexpected architecture in %s archive.\n' "$slice_name" >&2
    exit 1
  }
  xcrun --sdk "$sdk" nm -gU "$archive_dir/libLlmCore.a" >"$archive_dir/symbols.txt"
  for symbol in hc_llm_get_abi_version hc_llm_runtime_create hc_llm_runtime_destroy; do
    grep -Eq "[[:space:]]_${symbol}$" "$archive_dir/symbols.txt" || {
      printf 'Missing required public symbol in %s: %s\n' "$slice_name" "$symbol" >&2
      exit 1
    }
  done
}

configure_slice ios-arm64 iphoneos
configure_slice ios-arm64-simulator iphonesimulator

xcframework="$output_root/LlmCore.xcframework"
xcodebuild -create-xcframework \
  -library "$package_root/ios-arm64/libLlmCore.a" -headers "$package_root/ios-arm64/Headers" \
  -library "$package_root/ios-arm64-simulator/libLlmCore.a" -headers "$package_root/ios-arm64-simulator/Headers" \
  -output "$xcframework"

simulator_library="$xcframework/ios-arm64-simulator/libLlmCore.a"
simulator_headers="$xcframework/ios-arm64-simulator/Headers"
xcrun --sdk iphonesimulator clang -fobjc-arc -arch arm64 -mios-simulator-version-min=15.0 \
  -isysroot "$(xcrun --sdk iphonesimulator --show-sdk-path)" \
  -I "$simulator_headers" tests/apple/consumer_smoke.m "$simulator_library" \
  -framework Accelerate -framework Foundation -framework Metal -framework MetalKit -lc++ \
  -o "$output_root/consumer-smoke-simulator"

for expected_path in \
  "$xcframework/Info.plist" \
  "$xcframework/ios-arm64/libLlmCore.a" \
  "$xcframework/ios-arm64/Headers/hc_llm.h" \
  "$xcframework/ios-arm64-simulator/libLlmCore.a" \
  "$xcframework/ios-arm64-simulator/Headers/hc_llm.h"; do
  [[ -e "$expected_path" ]] || { printf 'XCFramework content missing: %s\n' "$expected_path" >&2; exit 1; }
done

manifest="$output_root/build-manifest.txt"
{
  printf 'artifact=LlmCore.xcframework\n'
  printf 'abi_version=0x00010000\n'
  printf 'git_commit=%s\n' "$(git rev-parse HEAD)"
  printf 'llama_cpp_commit=%s\n' "$(git -C native/third_party/llama.cpp rev-parse HEAD)"
  printf 'xcode=%s\n' "$(xcodebuild -version | tr '\n' ';')"
  printf 'iphoneos_sdk=%s\n' "$(xcrun --sdk iphoneos --show-sdk-version)"
  printf 'iphonesimulator_sdk=%s\n' "$(xcrun --sdk iphonesimulator --show-sdk-version)"
  printf 'cmake=%s\n' "$(cmake --version | head -n 1)"
  printf 'options=Release;arm64;iOS15.0;BUILD_SHARED_LIBS=OFF;LLMCORE_ENABLE_LLAMA_CPP=ON;GGML_METAL=ON;GGML_METAL_EMBED_LIBRARY=ON\n'
} >"$manifest"

(
  cd "$output_root"
  zip -qry LlmCore.xcframework.zip LlmCore.xcframework
)
shasum -a 256 "$output_root/LlmCore.xcframework.zip" >"$output_root/LlmCore.xcframework.zip.sha256"
printf 'Apple artifact ready: %s\n' "$output_root"

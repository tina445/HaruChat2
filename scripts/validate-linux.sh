#!/usr/bin/env bash
# Run the M1 Linux validation path. It assumes solution/native targets are present.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

"$repo_root/scripts/verify-archlinux.sh"

dotnet restore HaruChat.slnx --locked-mode
dotnet build HaruChat.slnx -c Release --no-restore -m:1
dotnet test HaruChat.slnx -c Release --no-build
dotnet run --project tests/managed/HaruChat.Runtime.Contracts.Tests/HaruChat.Runtime.Contracts.Tests.csproj \
  -c Release --no-build --no-restore

native_build_dir="${HARUCHAT_NATIVE_BUILD_DIR:-$repo_root/native/out/linux-debug}"
native_tmp_dir="${HARUCHAT_NATIVE_TMP_DIR:-$repo_root/native/out/tmp}"
mkdir -p "$native_tmp_dir"
export TMPDIR="$native_tmp_dir"

cmake -S native -B "$native_build_dir" -G Ninja \
  -DCMAKE_BUILD_TYPE=Debug \
  -DLLMCORE_BUILD_TESTS=ON \
  -DLLMCORE_ENABLE_SANITIZERS=ON \
  -DLLMCORE_DISABLE_LEAK_SANITIZER=OFF
cmake --build "$native_build_dir"
ctest --test-dir "$native_build_dir" --output-on-failure

valgrind_build_dir="${HARUCHAT_VALGRIND_NATIVE_BUILD_DIR:-$repo_root/native/out/linux-valgrind}"
cmake -S native -B "$valgrind_build_dir" -G Ninja \
  -DCMAKE_BUILD_TYPE=Debug \
  -DLLMCORE_BUILD_TESTS=ON \
  -DLLMCORE_ENABLE_SANITIZERS=OFF
cmake --build "$valgrind_build_dir" --target llmcore_tests

valgrind_log="$(mktemp)"
trap 'rm -f "$valgrind_log"' EXIT
if valgrind --quiet --leak-check=full --show-leak-kinds=definite --error-exitcode=1 \
  "$valgrind_build_dir/llmcore_tests" >"$valgrind_log" 2>&1; then
  printf '%s\n' 'Valgrind lifecycle check passed.'
elif grep -Fq 'Fatal error at startup: a function redirection' "$valgrind_log"; then
  printf '%s\n' 'Valgrind is unavailable: this glibc loader lacks the required debug symbols.' >&2
  printf '%s\n' 'ASan/UBSan passed; record Valgrind as deferred or rerun with HARUCHAT_REQUIRE_VALGRIND=1 on a debug-symbol-capable host.' >&2
  if [[ "${HARUCHAT_REQUIRE_VALGRIND:-0}" == "1" ]]; then
    cat "$valgrind_log" >&2
    exit 1
  fi
else
  cat "$valgrind_log" >&2
  exit 1
fi

if [[ -n "${HARUCHAT_TEST_MODEL_PATH:-}" ]]; then
  model_build_dir="${HARUCHAT_MODEL_NATIVE_BUILD_DIR:-$repo_root/native/out/linux-model}"
  cmake -S native -B "$model_build_dir" -G Ninja \
    -DCMAKE_BUILD_TYPE=Release \
    -DLLMCORE_BUILD_TESTS=ON \
    -DLLMCORE_ENABLE_SANITIZERS=ON \
    -DLLMCORE_DISABLE_LEAK_SANITIZER=OFF \
    -DLLMCORE_ENABLE_LLAMA_CPP=ON
  cmake --build "$model_build_dir" --target llmcore_model_smoke
  ctest --test-dir "$model_build_dir" -R '^llmcore\.model_smoke$' --output-on-failure
else
  printf '%s\n' 'HARUCHAT_TEST_MODEL_PATH is unset; model-smoke intentionally skipped.'
fi

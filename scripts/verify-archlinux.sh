#!/usr/bin/env bash
# Verify the Linux toolchain without changing the host. This script never installs packages.
set -euo pipefail

if [[ "$(uname -s)" != "Linux" ]]; then
  printf '%s\n' 'This verifier is intended for Linux hosts.' >&2
  exit 1
fi

if [[ -r /etc/os-release ]]; then
  # shellcheck disable=SC1091
  . /etc/os-release
  case "${ID:-}" in
    arch|manjaro|endeavouros) ;;
    *) printf 'Warning: detected Linux distribution "%s"; package guidance is Arch-specific.\n' "${ID:-unknown}" >&2 ;;
  esac
fi

required=(dotnet cmake ninja clang git ccache valgrind)
missing=()
for command_name in "${required[@]}"; do
  if command -v "$command_name" >/dev/null 2>&1; then
    "$command_name" --version 2>/dev/null | head -n 1 || true
  else
    missing+=("$command_name")
  fi
done

if (( ${#missing[@]} > 0 )); then
  printf 'Missing required commands: %s\n' "${missing[*]}" >&2
  printf '%s\n' 'Review and run the minimal Arch Linux installation command yourself:' >&2
  printf '%s\n' '  sudo pacman -S --needed ccache valgrind' >&2
  printf '%s\n' 'Do not install JDK, Android SDK/NDK, or Unity Android modules for M1.' >&2
  exit 1
fi

printf '%s\n' 'Arch Linux toolchain verification passed.'

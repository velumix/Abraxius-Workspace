#!/usr/bin/env bash
set -euo pipefail

path="${1:?usage: sign-macos.sh <path> [--required]}"
required="${2:-}"
identity="${ABRAXIUS_MACOS_SIGNING_IDENTITY:-}"
if [[ -z "$identity" ]]; then
  if [[ "$required" == "--required" ]]; then
    echo "Production macOS signing requires ABRAXIUS_MACOS_SIGNING_IDENTITY." >&2
    exit 1
  fi
  echo "macOS signing skipped: identity is not configured."
  exit 0
fi

codesign --force --deep --options runtime --timestamp --sign "$identity" "$path"
codesign --verify --deep --strict --verbose=2 "$path"

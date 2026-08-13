#!/usr/bin/env bash
set -euo pipefail

artifact="${1:?usage: notarize-macos.sh <pkg-or-dmg> [--required]}"
required="${2:-}"
if [[ -z "${ABRAXIUS_APPLE_ID:-}" || -z "${ABRAXIUS_APPLE_TEAM_ID:-}" || -z "${ABRAXIUS_APPLE_APP_PASSWORD:-}" ]]; then
  if [[ "$required" == "--required" ]]; then
    echo "Production notarization requires Apple ID, team ID, and app-specific password secrets." >&2
    exit 1
  fi
  echo "macOS notarization skipped: credentials are not configured."
  exit 0
fi

xcrun notarytool submit "$artifact" \
  --apple-id "$ABRAXIUS_APPLE_ID" \
  --team-id "$ABRAXIUS_APPLE_TEAM_ID" \
  --password "$ABRAXIUS_APPLE_APP_PASSWORD" \
  --wait
xcrun stapler staple "$artifact"

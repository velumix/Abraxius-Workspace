#!/usr/bin/env bash
set -euo pipefail

version="${1:?usage: create-release-manifest.sh <version> <channel> <output> <platform>...}"
channel="${2:?missing channel}"
output="${3:?missing output}"
shift 3

platform_json=''
for platform in "$@"; do
  if [[ -n "$platform_json" ]]; then platform_json+=','; fi
  platform_directory="artifacts/releases/desktop-$platform"
  if [[ ! -d "$platform_directory" ]]; then platform_directory="artifacts/releases/$platform"; fi
  package="$(find "$platform_directory" -maxdepth 1 -type f ! -name 'SHA256SUMS.txt' ! -name 'RELEASES*' ! -name '*.json' | head -n 1)"
  sha256=""
  size=""
  if [[ -n "$package" ]]; then
    sha256="$(sha256sum "$package" | awk '{print $1}')"
    size="$(stat -c '%s' "$package")"
  fi
  platform_json+="\"$platform\":{\"runtimeIdentifier\":\"$platform\",\"packageSha256\":\"$sha256\",\"packageSize\":${size:-null}}"
done

mkdir -p "$(dirname "$output")"
printf '{\n  "version": "%s",\n  "channel": "%s",\n  "minimumProtocol": "1.0",\n  "releasedAt": "%s",\n  "severity": "normal",\n  "platforms": {%s}\n}\n' \
  "$version" "$channel" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$platform_json" > "$output"

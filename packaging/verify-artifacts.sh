#!/usr/bin/env bash
set -euo pipefail

root="${1:?usage: verify-artifacts.sh <release-directory> }"
shopt -s nullglob
files=("$root"/*)
if (( ${#files[@]} == 0 )); then
  echo "No release artifacts found in $root" >&2
  exit 1
fi

for checksum in "$root"/SHA256SUMS.txt; do
  if [[ -f "$checksum" ]]; then
    (cd "$root" && sha256sum --check "$(basename "$checksum")")
  fi
done

while IFS= read -r -d '' candidate; do
  name="$(basename "$candidate")"
  case "$name" in
    .env|.env.*|*credentials*|*secret*)
      echo "Potential secret-bearing release file detected: $candidate" >&2
      exit 1
      ;;
  esac
done < <(find "$root" -type f -print0)
echo "Release artifacts verified: $root"

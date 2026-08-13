#!/usr/bin/env bash
set -euo pipefail

tag="${1:?usage: validate-tag.sh <tag>}"
version="$(sed -n 's/.*<AbraxiusVersion[^>]*>\([^<]*\)<.*/\1/p' build/Version.props | head -n 1)"
expected="v$version"
if [[ "$tag" != "$expected" ]]; then
  echo "Release tag '$tag' does not match canonical version '$expected'." >&2
  exit 1
fi
echo "Validated release tag $tag"

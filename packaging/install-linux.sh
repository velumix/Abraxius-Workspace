#!/usr/bin/env bash
set -euo pipefail

source_image="${1:?usage: install-linux.sh <AppImage> [--portable]}"
if [[ ! -f "$source_image" ]]; then
  echo "AppImage not found: $source_image" >&2
  exit 1
fi
if [[ "${2:-}" == "--portable" ]]; then
  echo "Portable mode selected; no desktop integration was created."
  exit 0
fi

home_directory="${HOME:?HOME is required}"
data_home="${XDG_DATA_HOME:-$home_directory/.local/share}"
target="$data_home/Abraxius/Abraxius.AppImage"
mkdir -p "$(dirname "$target")"
install -m 0755 "$source_image" "$target"
echo "Installed managed AppImage at $target"
echo "Launch the managed AppImage once to reconcile its XDG desktop entry."

#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:?usage: build-desktop.sh <rid> [version] [channel]}"
version="${2:-$(sed -n 's/.*<AbraxiusVersion[^>]*>\([^<]*\)<.*/\1/p' "$repo_root/build/Version.props" | head -n 1)}"
channel="${3:-stable}"
dotnet_path="${DOTNET_EXE:-dotnet}"
pack_id="${ABRAXIUS_PACK_ID:-Abraxius}"
stage="$repo_root/artifacts/staging/$rid"
output="$repo_root/artifacts/releases/$rid"

case "$rid" in
  win-x64|win-arm64) main_exe="Abraxius.Desktop.exe"; runtime="$rid" ;;
  linux-x64|linux-arm64) main_exe="Abraxius.Desktop"; runtime="${rid/linux-/linux-}" ;;
  osx-x64|osx-arm64) main_exe="Abraxius.Desktop"; runtime="$rid" ;;
  *) echo "Unsupported desktop RID: $rid" >&2; exit 2 ;;
esac

rm -rf "$stage" "$output"
mkdir -p "$stage" "$output"

"$dotnet_path" restore "$repo_root/Abraxius.sln"
"$dotnet_path" publish "$repo_root/src/Abraxius.App.Desktop/Abraxius.App.Desktop.csproj" \
  --configuration Release \
  --runtime "$runtime" \
  --self-contained true \
  --output "$stage" \
  -p:AbraxiusVersion="$version" \
  -p:AbraxiusReleaseChannel="$channel" \
  -p:AbraxiusGitCommit="${GITHUB_SHA:-unknown}" \
  -p:AbraxiusBuildTimestamp="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

if [[ ! -f "$stage/$main_exe" ]]; then
  echo "Published executable was not found: $stage/$main_exe" >&2
  exit 1
fi

if [[ "$rid" == osx-* ]]; then
  if [[ -z "${ABRAXIUS_MACOS_SIGNING_IDENTITY:-}" && "${ABRAXIUS_REQUIRE_SIGNING:-false}" == "true" ]]; then
    echo "Production macOS packaging requires ABRAXIUS_MACOS_SIGNING_IDENTITY." >&2
    exit 1
  fi
fi

pushd "$repo_root" >/dev/null
"$dotnet_path" tool restore
icon_args=()
if [[ -n "${ABRAXIUS_ICON_PATH:-}" ]]; then
  icon_args+=(--icon "$ABRAXIUS_ICON_PATH")
fi
if [[ "$rid" == osx-* ]]; then
  icon_args+=(--bundleId "${ABRAXIUS_BUNDLE_ID:-com.abraxius.Abraxius}")
  if [[ -n "${ABRAXIUS_MACOS_SIGNING_IDENTITY:-}" ]]; then
    icon_args+=(--signAppIdentity "$ABRAXIUS_MACOS_SIGNING_IDENTITY" --signInstallIdentity "$ABRAXIUS_MACOS_SIGNING_IDENTITY")
  fi
fi
"$dotnet_path" vpk pack \
  --packId "$pack_id" \
  --packTitle "Abraxius" \
  --packVersion "$version" \
  --packDir "$stage" \
  --mainExe "$main_exe" \
  --outputDir "$output" \
  --channel "$channel" \
  --runtime "$runtime" \
  "${icon_args[@]}"
popd >/dev/null

sha256sum "$output"/* > "$output/SHA256SUMS.txt"
echo "Created $rid packages in $output"

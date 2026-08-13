#!/usr/bin/env bash
set -Eeuo pipefail

benchmark_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_cli="${ABRAXIUS_DOTNET_CLI:-$(command -v dotnet || true)}"
active_child=""

if [[ -z "${dotnet_cli}" || ! -x "${dotnet_cli}" ]]; then
    echo "dotnet was not found. Set ABRAXIUS_DOTNET_CLI to the SDK executable." >&2
    exit 127
fi

cleanup_benchmark_processes() {
    local exit_code=$?
    trap - EXIT INT TERM HUP
    if [[ -n "${active_child}" ]] && kill -0 "${active_child}" 2>/dev/null; then
        kill -TERM -- "-${active_child}" 2>/dev/null || kill -TERM "${active_child}" 2>/dev/null || true
        for _ in 1 2 3 4 5; do kill -0 "${active_child}" 2>/dev/null || break; sleep 0.2; done
        kill -0 "${active_child}" 2>/dev/null && { kill -KILL -- "-${active_child}" 2>/dev/null || true; }
        wait "${active_child}" 2>/dev/null || true
    fi
    "${dotnet_cli}" build-server shutdown >/dev/null 2>&1 || true
    exit "${exit_code}"
}

trap cleanup_benchmark_processes EXIT INT TERM HUP
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDDISABLENODEREUSE=1

filter="${1:-*Progression*}"
cd "${benchmark_root}"

# Build once, serially. BenchmarkDotNet itself defaults to in-process ShortRun
# in Program.cs, so it cannot create a fan-out of child build/test hosts.
setsid "${dotnet_cli}" build benchmarks/Abraxius.Benchmarks/Abraxius.Benchmarks.csproj \
    -c Release --disable-build-servers -m:1 &
active_child=$!
wait "${active_child}"
active_child=""

setsid nice -n 10 "${dotnet_cli}" benchmarks/Abraxius.Benchmarks/bin/Release/net10.0/Abraxius.Benchmarks.dll \
    --filter "${filter}" --job short --inProcess &
active_child=$!
wait "${active_child}"
active_child=""

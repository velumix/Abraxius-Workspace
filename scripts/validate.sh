#!/usr/bin/env bash
set -Eeuo pipefail

validation_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet_cli="${ABRAXIUS_DOTNET_CLI:-}"
active_child=""

if [[ -z "${dotnet_cli}" ]]; then
    dotnet_cli="$(command -v dotnet || true)"
fi

if [[ -z "${dotnet_cli}" || ! -x "${dotnet_cli}" ]]; then
    echo "dotnet was not found. Set ABRAXIUS_DOTNET_CLI to the SDK executable." >&2
    exit 127
fi

cleanup_validation_processes() {
    local exit_code=$?
    trap - EXIT INT TERM HUP

    if [[ -n "${active_child}" ]] && kill -0 "${active_child}" 2>/dev/null; then
        # Every command is started in its own session. Only that owned process
        # group is terminated; unrelated dotnet/editor processes are untouched.
        kill -TERM -- "-${active_child}" 2>/dev/null || kill -TERM "${active_child}" 2>/dev/null || true
        for _ in 1 2 3 4 5; do
            kill -0 "${active_child}" 2>/dev/null || break
            sleep 0.2
        done
        if kill -0 "${active_child}" 2>/dev/null; then
            kill -KILL -- "-${active_child}" 2>/dev/null || kill -KILL "${active_child}" 2>/dev/null || true
        fi
        wait "${active_child}" 2>/dev/null || true
    fi

    "${dotnet_cli}" build-server shutdown >/dev/null 2>&1 || true
    exit "${exit_code}"
}

trap cleanup_validation_processes EXIT INT TERM HUP

run_owned() {
    local command_status
    setsid "$@" &
    active_child=$!
    set +e
    wait "${active_child}"
    command_status=$?
    set -e
    active_child=""
    return "${command_status}"
}

export DOTNET_CLI_USE_MSBUILD_SERVER=0
export MSBUILDDISABLENODEREUSE=1

cd "${validation_root}"
run_owned "${dotnet_cli}" restore Abraxius.sln --disable-parallel --disable-build-servers
run_owned "${dotnet_cli}" build Abraxius.sln --no-restore --disable-build-servers -m:1
run_owned "${dotnet_cli}" test Abraxius.sln --no-build --no-restore --disable-build-servers -m:1 \
    --blame-hang --blame-hang-timeout 5m --blame-hang-dump-type none

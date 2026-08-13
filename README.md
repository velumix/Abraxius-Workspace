# Abraxius

Abraxius is a C#/.NET 10 parallel agent runtime with an Avalonia 12.1.1 workstation shell.

```bash
export PATH=/home/velumix/.dotnet:$PATH
dotnet restore Abraxius.sln
dotnet build Abraxius.sln
dotnet test Abraxius.sln
dotnet run --project src/Abraxius.Cli -- demo
dotnet run --project src/Abraxius.Cli -- intelligence status
dotnet run --project src/Abraxius.App.Desktop
```

The CLI and Avalonia application use the same runtime, scheduler, providers, event hub, evidence store, and ledger. The built-in demo runs a root task, three independent branches, synthesis, and verification. The branch operations overlap in real time; the UI graph is populated from scheduler events through a bounded frame-coalescing state aggregator.

Configuration is strongly typed in `AbraxiusConfiguration`. `appsettings.json` is optional, and environment overrides use the `ABRAXIUS_` prefix with double underscores for nested keys, for example `ABRAXIUS_SCHEDULER__DEFAULTQUEUECAPACITY=64`.

See [architecture.md](docs/architecture.md), [concurrency-model.md](docs/concurrency-model.md), [scheduler-architecture.md](docs/scheduler-architecture.md), and [performance-baseline.md](docs/performance-baseline.md) for implementation boundaries and measured results.

## Distribution and updates

The canonical product version is in `build/Version.props`. Desktop packages use the pinned
Velopack 1.2.0 tool and the release workflows under `.github/workflows/`; direct desktop updates
use the configured GitHub Releases source, while store-managed mobile installs use their store's
update mechanism. Developer builds deliberately report that updates are unavailable.

See [distribution-architecture.md](docs/distribution-architecture.md),
[update-architecture.md](docs/update-architecture.md), [release-process.md](docs/release-process.md),
[release-security.md](docs/release-security.md), and [rollback-and-recovery.md](docs/rollback-and-recovery.md).

The intelligence fabric is free-first by policy. It can use an explicitly classified OmniRoute
free/quota route, LiteLLM groups, or configured frontier adapters without nesting autonomous
routers. Paid inference is disabled by default, unknown costs are not treated as free, and route
decisions expose their rejection reasons. See [intelligence-architecture.md](docs/intelligence-architecture.md),
[routing-policy.md](docs/routing-policy.md), [omniroute-integration.md](docs/omniroute-integration.md),
[litellm-integration.md](docs/litellm-integration.md), and [escalation-policy.md](docs/escalation-policy.md).

The Avalonia workstation is a runtime observer and controller: graph/lanes rendering reads immutable coalesced snapshots, activity is represented as typed virtualized blocks, and command palette, rail, keyboard shortcuts, terminal, inspector, and mission views share one command system. See [ui-architecture.md](docs/ui-architecture.md), [mission-canvas.md](docs/mission-canvas.md), [interaction-model.md](docs/interaction-model.md), [design-system.md](docs/design-system.md), and [ui-performance.md](docs/ui-performance.md).

## Platform support

Abraxius has one shared Protocol/Core/Runtime/App architecture with platform hosts for Windows, Linux, macOS, Android, iOS, WebAssembly, and embedded Linux. The shared Avalonia UI is reused across hosts; platform adapters advertise capabilities rather than exposing operating-system branches to the core.

Supported application platforms and local capabilities are separate concepts. A workstation may run local processes, files, models, and Lattice; a phone may use constrained local services plus remote execution; a browser is a first-class remote client; embedded Linux can expose a targeted local capability set. See [platform-architecture.md](docs/platform-architecture.md) and [platform-compatibility.md](docs/platform-compatibility.md) for the current matrix and truthful build status.

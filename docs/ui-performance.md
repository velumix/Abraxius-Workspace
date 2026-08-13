# UI performance baseline and policy

## Implemented controls

* Runtime events use a bounded lossy UI subscription. Control-plane correctness remains in the runtime/ledger path.
* `RuntimeUiStateAggregator` coalesces changes on a 16 ms periodic frame and publishes immutable snapshots.
* Events are retained to 512 rows and typed activity blocks to 10,000 records in presentation state.
* The graph and lanes are one custom control each. Nodes and bars are drawn directly and hit-tested by geometry.
* Graph level-of-detail switches from labels to compact nodes and then phase aggregates for very large inputs.
* Activity, terminal, agent, and event lists use standard collection controls rather than materializing a control for every runtime record.
* Compiled bindings are enabled by `AvaloniaUseCompiledBindingsByDefault`.

## Measurement commands

```bash
/home/velumix/.dotnet/dotnet build Abraxius.sln -c Release
/home/velumix/.dotnet/dotnet test Abraxius.sln -c Release --no-build
/home/velumix/.dotnet/dotnet run --project src/Abraxius.Cli -- demo
/home/velumix/.dotnet/dotnet run --project src/Abraxius.App.Desktop
```

The runtime test `UiStateCoalescesHighFrequencyEvents` verifies that 250 progress events do not cause 250 dispatcher posts. The presentation test verifies that the same runtime events become typed blocks, task state, evidence references, and agent cards. Intelligence route selection is also represented as a typed activity event and inspector state.

## Validation recorded 2026-08-12

On Linux Mint 22.3, x64, .NET SDK 10.0.302, the final Release build after route-capacity integration completed in 4.41 seconds with zero warnings/errors; restore was already up to date. The full test solution passed 62 tests. The latest CLI demo completed its six-task graph in 839 ms and reported the three independent branch tasks overlapping before synthesis; the model route event selected the deterministic zero-cost mock route because no external gateway was configured. The desktop host remained alive for the prior eight-second X-display smoke launch without an initialization exception; interactive pixel/frame measurements require a human graphical session or a headless Avalonia test harness, neither of which is claimed here.

The supporting graph benchmark was run with BenchmarkDotNet 0.15.8, ShortRun, .NET 10, concurrent workstation GC, Intel Core i5-10600K (12 logical / 6 physical cores):

| Graph operation | 100 nodes | 1,000 nodes | 10,000 nodes |
| --- | ---: | ---: | ---: |
| Validate | 7.894 µs | 136.101 µs | 2.359 ms |
| Compile | 24.557 µs | 316.656 µs | 6.415 ms |

These are graph/runtime baselines, not a claim about rendered frame time. The custom canvas intentionally aggregates beyond 2,000 tasks so visualization cost does not grow as a full-detail control tree.

## Profiling targets

The first UI pass is intentionally measurable and conservative. The next profiling pass should capture cold/warm startup, 100/1,000/10,000 node canvas snapshots, activity filtering, terminal output floods, and repeated mission open/close cycles on a graphical host. Avalonia DevTools and a .NET profiler should be used for render/layout/retention measurements. A headless visual test workload is not claimed here because no Avalonia headless test package is part of the repository.

No universal FPS claim is made. The design target is smooth 60 Hz interaction, quiet idle rendering, and no scheduler-induced UI stalls; actual frame time depends on renderer, display scale, graph size, and device.

## Intentional deferrals

Composition custom visuals, Dock.Avalonia, Semi.Avalonia, and a third-party terminal control are not mandatory dependencies in this pass. The graph custom control meets the current architecture without adding a package-specific render boundary. Each candidate remains behind a replaceable surface if profiling shows a real benefit.

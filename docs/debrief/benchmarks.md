# Debrief benchmarks

`benchmarks/Abraxius.Benchmarks/DebriefBenchmarks.cs` measures deterministic plan construction and grounded chapter composition with `MemoryDiagnoser`. Run it with:

```bash
dotnet run --project benchmarks/Abraxius.Benchmarks -c Release -- --filter '*Debrief*' --job short
```

Record the BenchmarkDotNet output for the actual machine, .NET SDK, corpus size, mean latency, allocations, and error bars. Do not infer time-to-first-audio from these CPU-only benchmarks: that measurement must include retrieval, planning, claim validation, TTS, and playback on a configured voice route.

The required quality measures are supported by the test seams: unsupported-claim rejection, source/citation preservation, session reopen, pause/resume generation cancellation, and safe live-question behavior. Provider-specific TTS real-time factor, barge-in latency, audio underruns, and local GPU power are deployment measurements and are not fabricated in this document.

## Measured baseline

ShortRun BenchmarkDotNet run on Linux Mint 22.3, Intel Core i5-10600K, .NET 10.0.10, six physical/twelve logical cores:

| Operation | Mean | Allocated |
|---|---:|---:|
| PlanEpisode, 64 fixture evidence entries | 5.839 μs | 11.75 KB |
| ComposeGroundedChapter | 12.964 μs | 26.49 KB |

These are deterministic in-process CPU measurements, not end-to-end audio latency. They exclude provider inference, retrieval backends, TTS, and playback.

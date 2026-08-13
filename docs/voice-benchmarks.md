# Voice benchmark plan and current baseline

The benchmark project contains `VoiceBenchmarks` for:

- one normalized PCM VAD frame;
- generation gate advancement;
- streaming response segmentation.

The test project proves deterministic behavior for VAD hysteresis, pre-roll, private route filtering, incremental segmentation, stale-generation rejection, and barge-in cancellation.

Run the benchmark after restoring the solution:

```bash
/home/velumix/.dotnet/dotnet run -c Release --project benchmarks/Abraxius.Benchmarks -- --filter '*VoiceBenchmarks*' --job short
```

Live WER, technical-vocabulary accuracy, time-to-first-partial, finalization latency, time-to-first-audio, user-stop-to-speech, interruption stop latency, and TTS real-time factor require configured providers and recordings. They are intentionally not fabricated by offline unit tests.

## Current managed hot-path baseline

Measured 2026-08-12 on Linux Mint 22.3, Intel i5-10600K, .NET 10.0.10, .NET SDK 10.0.302, BenchmarkDotNet 0.15.8, workstation GC, ShortRun (3 warmup/3 measurement iterations):

| Operation | Mean | Allocated |
|---|---:|---:|
| normalized PCM VAD frame | 305.2 ns | 0 B |
| generation gate advance | 4.20 ns | 0 B |
| two-chunk response segmentation | 1.766 us | 1,432 B |

These are orchestration primitives, not end-to-end speech latency or a provider quality claim. The segmentation allocation is a candidate for future pooling only if profiling shows it matters in real sessions.

# Presence benchmarks

`PresenceBenchmarks` measures deterministic attention classification and tray aggregation without launching an OS adapter. Run through `scripts/benchmark.sh`, which forces an in-process short job, serial build, owned process-group cleanup, and .NET build-server shutdown.

Operational targets are negligible classification overhead, no idle loops, bounded notification and Needs You collections, tray updates only on meaningful transitions, and zero retained test hosts after validation. Long hidden-session and actual desktop restore latency remain manual/platform benchmarks.

Measured on Linux Mint 22.3, .NET 10.0.10, Intel i5-10600K, in-process ShortRun:

| Operation | Mean | Allocation |
|---|---:|---:|
| Attention policy classification | 17.64 ns | 80 B |
| Tray snapshot aggregation | 79.97 ns | 320 B |

The permission warning emitted by BenchmarkDotNet only indicates that the unprivileged process could not raise itself to high scheduling priority; benchmark execution completed normally.

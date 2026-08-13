# Security benchmarks

`SecurityBenchmarks` measures the allocation and latency of cached deterministic workspace-read and external-network decisions. It intentionally excludes DNS, SQLite audit I/O, model latency, and tool execution.

Run only through `scripts/benchmark.sh '*SecurityBenchmarks*'`. The wrapper uses a bounded in-process ShortRun and terminates its owned process group/build servers on exit. Record measured values here after validation; authorization should remain negligible compared with capability execution.

## 2026-08-13 baseline

Measured on Linux Mint 22.3, Intel Core i5-10600K, .NET 10.0.10, BenchmarkDotNet 0.15.8 (`ShortRun`, in-process, three warmups and three measured iterations):

| Decision | Mean | Standard deviation | Allocation |
|---|---:|---:|---:|
| Approved workspace read | 890.8 ns | 2.12 ns | 896 B |
| External network policy | 799.4 ns | 2.50 ns | 664 B |

These figures cover canonical in-memory policy evaluation only. Resource discovery, DNS resolution, SQLite audit persistence, approval delivery, and the authorized capability itself are measured separately because they are I/O paths.

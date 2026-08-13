# Progression benchmarks

Run:

```bash
dotnet run -c Release --project benchmarks/Abraxius.Benchmarks -- --filter '*Progression*'
```

The preferred workstation command is `scripts/benchmark.sh '*Progression*'`; it owns and reaps its process group and shuts down build servers after completion.

On memory-constrained development desktops, use the already-built assembly in-process so BenchmarkDotNet does not fan out compiler child processes:

```bash
dotnet benchmarks/Abraxius.Benchmarks/bin/Release/net10.0/Abraxius.Benchmarks.dll \
  --filter '*Progression*' --job short --inProcess
```

The benchmark records reward-evaluation and achievement-predicate latency plus allocations. Release reports should also record machine, OS, .NET version, reward count, snapshot load time, SQLite commit latency, page load, and 10k/100k/1m rebuild throughput. Benchmark trajectories are explicitly ineligible and cannot alter user progression.

## Phase 15 baseline

Measured 2026-08-13 on Linux Mint 22.3, Intel Core i5-10600K (6 cores / 12 logical), .NET SDK 10.0.302, .NET runtime 10.0.10, x64 RyuJIT, workstation GC. BenchmarkDotNet 0.15.8 ShortRun used one in-process launch, three warmups, and three measured iterations.

| Operation | Mean | Allocated |
| --- | ---: | ---: |
| Reward evaluation | 1.314 µs | 2.32 KB |
| 21 achievement predicates | 2.308 µs | 4.31 KB |

The first attempted out-of-process run coincided with system memory exhaustion while the editor already held about 11.8 GB RSS; the kernel killed the editor. The measured rerun used `--inProcess`, workstation GC, and one process. Large 100k/1m rebuild measurements are intentionally deferred until a dedicated memory-capped environment is available rather than risking the interactive workstation.

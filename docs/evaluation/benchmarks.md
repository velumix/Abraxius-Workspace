# Evaluation benchmarks

`EvaluationBenchmarks` measures paired comparison across 10,000 case identities, aggregation of 100,000 metric samples, and querying a 10,000-run in-memory summary store. Run only through the guarded filtered harness:

```text
ABRAXIUS_DOTNET_CLI=/home/velumix/.dotnet/dotnet ./scripts/benchmark.sh '*EvaluationBenchmarks*'
```

The repository benchmark harness uses one in-process job, serialized build, an owned process group, cleanup traps, and build-server shutdown. Always perform the documented process audit afterward. SQLite million-row, provider-model, GPU, mobile, voice-corpus, and distributed-node measurements remain future environment-specific runs; no fabricated values are reported.

## Reference run — 2026-08-13

Linux Mint 22.3, Intel Core i5-10600K (6 cores/12 logical), .NET 10.0.10, Release, BenchmarkDotNet ShortRun/in-process:

| Operation | Mean | Managed allocation |
| --- | ---: | ---: |
| Compare 10,000 paired cases | 12.455 ms | 1,708.55 KB |
| Aggregate 100,000 metric samples | 12.705 ms | 1,954.58 KB |
| Query 100 summaries from a 10,000-run in-memory store | 354.973 µs | 378.63 KB |

These are local reference measurements, not cross-machine release thresholds. The raw reports are under `BenchmarkDotNet.Artifacts/results/`.

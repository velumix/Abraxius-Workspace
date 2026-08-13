# Skill benchmark protocol

`benchmarks/Abraxius.Benchmarks/SkillBenchmarks.cs` measures matching and plan compilation at registry sizes 10, 1,000, and 10,000 with BenchmarkDotNet and `MemoryDiagnoser`. Run:

```text
dotnet run --project benchmarks/Abraxius.Benchmarks -c Release -- --filter '*Skill*'
```

Record machine, OS, .NET SDK, build configuration, registry size, mean latency, allocations, and outliers. The benchmark intentionally measures infrastructure without model/provider latency. Repeated-task comparisons should additionally record fresh-planning calls, Skill model calls, verified success, time-to-verified-result, and cost.

The current implementation does not claim planning or token savings without a completed controlled comparison. Built-in Skills are validated but have zero historical executions in a fresh registry; their initial smoothed reliability is therefore 50%, not 100%.

## Initial measured run

Measured on Linux Mint 22.3, Intel Core i5-10600K (6 physical / 12 logical
cores), .NET 10.0.10, x64 RyuJIT, Release, BenchmarkDotNet 0.15.8 ShortRun
(three warmups and three measured iterations). These are infrastructure-only
measurements; they exclude model/provider latency.

| Operation | Registry | Mean | Allocated |
| --- | ---: | ---: | ---: |
| `MatchSelectiveRegistry` | 10 | 3.037 us | 4.87 KB |
| `MatchSelectiveRegistry` | 1,000 | 26.247 us | 4.90 KB |
| `MatchSelectiveRegistry` | 10,000 | 51.538 us | 4.90 KB |

The selective benchmark uses a registry-indexed technical token and remains
approximately flat in allocation and low-latency as registry size grows. A
common-token worst-case query (`MatchRegistry`) intentionally returns a broad
candidate set and therefore remains dominated by candidate scoring: the
observed 10,000-entry run was approximately 26.3 ms and 20.0 MB allocated.
That is a useful stress result, not a claim that every natural-language query
is constant time; broad queries need result limits, stronger structured
signals, or a later inverted-index/reranker refinement.

Plan compilation is stable across the same registry sizes at approximately
0.66–0.68 us and 1.26 KB allocated in the existing benchmark corpus.

No controlled repeated-task experiment has yet been run, so planning-call
reduction, token savings, cost reduction, and verified-success improvement are
not claimed by this phase.

# Performance baseline

Date: 2026-08-12

Environment:

- OS: Linux Mint 22.3 (Zena), Linux x64
- CPU: Intel Core i5-10600K, 4.10 GHz (reported max 4.49 GHz), 6 physical / 12 logical cores
- .NET SDK: 10.0.302
- Runtime: .NET 10.0.10
- BenchmarkDotNet: 0.15.8
- Configuration: one launch, one warmup iteration, three measured iterations; local SDK supplied with `--cli`

Command:

```bash
env PATH=/home/velumix/.dotnet:$PATH \
  /home/velumix/.dotnet/dotnet run -c Release \
  --project benchmarks/Abraxius.Benchmarks --no-restore -- \
  --cli /home/velumix/.dotnet/dotnet \
  --filter '*SchedulerBenchmarks*' --job short \
  --warmupCount 1 --iterationCount 3 --launchCount 1
```

Measured means from the completed BenchmarkDotNet run:

| Branches | Work per branch | Serial baseline | Compiled parallel DAG | Ratio |
|---:|---:|---:|---:|---:|
| 4 | 1 ms | 4.289 ms | 1.254 ms | 0.29 |
| 4 | 5 ms | 20.385 ms | 5.338 ms | 0.26 |
| 16 | 1 ms | 17.116 ms | 1.548 ms | 0.09 |
| 16 | 5 ms | 81.606 ms | 5.632 ms | 0.07 |
| 64 | 1 ms | 68.385 ms | 5.374 ms | 0.08 |
| 64 | 5 ms | 325.611 ms | 21.598 ms | 0.07 |

The benchmark compares four, sixteen, and sixty-four independent I/O-shaped branches. The baseline awaits each delay in sequence. The compiled DAG creates explicit independent nodes and admits up to 32 I/O workers. Four and sixteen branches track one delay interval; 64 branches require two 32-worker waves. The measurements show critical-path behavior rather than the sum of branch delays.

The parallel benchmark allocates more than the serial baseline because it intentionally measures graph construction, compilation, executor-pool setup, event-free scheduling, and cleanup for every operation. The measured managed allocations were 114,434 B (4 branches), 323,225 B (16), and 923,118 B (64). The next optimization targets are shared worker reuse across executions and reducing per-session graph/result allocations; pooling should wait for a representative production profile.

The deterministic CLI demo measured 757–863 ms wall-clock across two runs for a root, three overlapping ~500 ms branches, ~200 ms synthesis, and ~100 ms verification. That is a functional demonstration measurement, not a BenchmarkDotNet result.

BenchmarkDotNet reported a permission warning while attempting to raise process priority; all 12 benchmark cases nevertheless completed successfully. The process reported 12 logical cores and concurrent workstation GC. The `ParallelDag` benchmark currently creates and disposes a scheduler per operation, so its allocation and startup costs must not be interpreted as steady-state per-task overhead.

## Phase 2 graph IR baseline

Date: 2026-08-12

The Phase 2 graph benchmark validates and compiles linear DAGs with 100, 1,000, and 10,000 nodes. Each node has at most one dependency. The benchmark uses the same host and .NET runtime as the scheduler baseline.

Command:

```bash
env PATH=/home/velumix/.dotnet:$PATH \
  /home/velumix/.dotnet/dotnet run -c Release \
  --project benchmarks/Abraxius.Benchmarks --no-restore -- \
  --filter '*GraphBenchmarks*' --job short \
  --warmupCount 1 --iterationCount 3 --launchCount 1
```

Measured means from the completed graph benchmark run:

| Operation | Nodes | Mean | Allocated |
|---|---:|---:|---:|
| Validate | 100 | 8.327 us | 22.22 KB |
| Compile | 100 | 23.726 us | 78.09 KB |
| Validate | 1,000 | 141.738 us | 220.62 KB |
| Compile | 1,000 | 315.714 us | 718.83 KB |
| Validate | 10,000 | 2.415 ms | 2,095.57 KB |
| Compile | 10,000 | 6.405 ms | 6,943.51 KB |

These are construction/validation baselines, not scheduler execution limits. The measured work scales with nodes and edges for this graph shape; the implementation uses indexed reverse edges and iterative Kahn traversal rather than nested graph scans or recursive cycle detection. The allocation cost is intentionally concentrated in one-time graph compilation. The runtime reuses compiled graphs; pooled runtime-state optimization remains a later benchmark target.

BenchmarkDotNet reported a permission warning while attempting to raise process priority; the run completed successfully without elevated priority.

## Runtime profile

The local `dotnet-trace`/`dotnet-counters` tools were not installed. A representative CLI demo was measured with Linux `perf`:

```text
Elapsed: 0.957 s
User CPU: 0.342 s
System CPU: 0.043 s
CPU cycles/instructions: unavailable (host perf permissions)
```

The profile includes process startup and the deterministic demo. It is a smoke profile for scheduler/worker responsiveness, not a steady-state microbenchmark. The observed runtime result was 757 ms with maximum observed task concurrency of 3.

## Phase 6 intelligence routing baseline

The route-policy benchmark was run on the same machine with BenchmarkDotNet 0.15.8, .NET 10.0.10,
concurrent workstation GC, one launch, three warmups, and three measured iterations:

```bash
/home/velumix/.dotnet/dotnet run -c Release \
  --project benchmarks/Abraxius.Benchmarks --no-restore -- \
  --cli /home/velumix/.dotnet/dotnet --filter '*Intelligence*' --job short
```

It measures Abraxius policy overhead only; no gateway or provider request is made.

| Candidate catalog | Route selection mean | Managed allocation |
|---:|---:|---:|
| 10 | 2.617 us | 5.1 KB |
| 100 | 20.519 us | 39.22 KB |
| 500 | 111.076 us | 192.42 KB |

The current route engine scans the configured catalog once per decision. This is appropriate for
the expected gateway catalog sizes and remains observable through `RouteDecision`; if a future
catalog becomes much larger, candidate indexes and cached eligibility sets are the next measured
optimization target. The 500-candidate run remained approximately linear in catalog size. No
OmniRoute or LiteLLM service was configured on this host, and no paid or live provider request was
made.

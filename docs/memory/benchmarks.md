# Memory Benchmarks

`benchmarks/Abraxius.Benchmarks/MemoryBenchmarks.cs` measures hybrid retrieval and context compilation on deterministic in-memory corpora of 100 and 1,000 entries. The SQLite integration tests exercise restart persistence and FTS-backed retrieval; the benchmark is intentionally separate from disk I/O so retrieval algorithm cost is visible.

Record benchmark output with:

```text
machine / OS / .NET
entries / chunks / embeddings
SQLite bytes
ingestion files/sec and MB/sec
symbol, lexical, semantic, hybrid latency
context compilation latency and allocations
```

The local hash embedding provider is a correctness/performance baseline only. It must not be described as a production semantic-quality winner. Provider evaluation and retrieval precision/recall corpora belong in the project-specific evaluation harness as real repositories and language coverage grow.

Initial local measurement (Linux Mint 22.3, Intel Core i5-10600K, .NET 10.0.10, BenchmarkDotNet 0.15.8, deterministic in-memory corpus, one cold-start iteration) recorded:

| Operation | Entries | Mean | Managed allocation |
|---|---:|---:|---:|
| Hybrid retrieval | 100 | 34.19 ms | 81.93 KB |
| Hybrid retrieval | 1,000 | 38.52 ms | 564.26 KB |
| Context compilation | 100 | 46.67 ms | 120.69 KB |
| Context compilation | 1,000 | 52.49 ms | 591.20 KB |

These are harness baselines, not production latency guarantees: the run used one iteration and includes cold-start effects. The current vector adapter is a bounded cosine scan, so a production ANN index should be evaluated before very large stores are enabled.

# AXL benchmarks

The BenchmarkDotNet project contains `AxlBenchmarks` covering parse, compact formatting, binary encode, and binary decode. Run the focused CLI measurement without executing any capability:

```bash
dotnet run -c Release --project src/Abraxius.Cli -- axl benchmark
```

For full allocation and throughput measurements:

```bash
dotnet run -c Release --project benchmarks/Abraxius.Benchmarks --filter '*AxlBenchmarks*'
```

The corpus includes independent tool roots, a synthesis dependency, and verification. Compare AXL with `System.Text.Json` using equivalent semantic data, not a shortened JSON fixture. Record .NET SDK, AXL version, machine, corpus, bytes, tokenizer estimates, parse/format/compile latency, binary size, and allocations. The current binary codec intentionally carries canonical AXL text, so binary size improvements over text are framing/transport properties rather than claims of a packed IR.

The repository also includes a 10,000-input randomized parser safety test. It is a crash-safety test, not a statistical security proof or a substitute for a coverage-guided fuzzing job.

## Measured baseline

On 2026-08-12, Linux Mint 22.3, Intel Core i5-10600K, .NET 10.0.10, BenchmarkDotNet 0.15.8, ShortRun (3 iterations, 1 warmup), the representative batch measured:

| Operation | Mean | Allocated |
| --- | ---: | ---: |
| Parse | 3.180 us | 8.34 KB |
| Compact format | 954.8 ns | 3.52 KB |
| Binary encode | 4.727 us | 8.36 KB |
| Binary decode | 10.973 us | 13.63 KB |

The decode result has high variance in this short run; use a longer job before making release decisions. The CLI's 10,000-iteration measurement reported 133,588 parses/sec in the same workspace, while the isolated BDN parse benchmark reported 3.180 us per representative batch. These are different harnesses and should not be compared as interchangeable throughput claims.

For the single-command example `axl/1 find code q="ExecutionGraph" lim=20`, the measured UTF-8 payload is 41 bytes versus 89 bytes for the equivalent JSON object used in the documentation. This is a byte comparison only; tokenizer-specific model counts and end-to-end generation retry rates have not yet been benchmarked with live providers.

# Phase 11 benchmarks

`benchmarks/Abraxius.Benchmarks/AgentBenchmarks.cs` measures kernel overhead with a zero-latency runner:

- four-assignment build mission;
- direct Orion investigation.

These numbers intentionally exclude model, tool, filesystem, and verification latency. Use the benchmark to measure coordination overhead, then compare real mission runs using runtime event timestamps. The kernel test suite also proves two Orion assignments overlap when the configured concurrency allows it.

No claim about specialist quality or model cost is made by the infrastructure benchmark. Those require configured Phase 6 providers and a task corpus.

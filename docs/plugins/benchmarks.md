# Benchmarks

`PluginBenchmarks` measures static manifest validation and contribution-registry lookup with allocation reporting. Host-start, real IPC unary/streaming latency, package verification, virtualized UI paging, and active-host resource measurements require controlled executable fixtures and must be reported separately from these in-process microbenchmarks.

Run only through `./scripts/benchmark.sh '*PluginBenchmarks*'` so repository benchmark safety limits remain active.

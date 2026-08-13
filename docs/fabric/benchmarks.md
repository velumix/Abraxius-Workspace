# Fabric benchmarks

`FabricBenchmarks` measures deterministic placement at 4, 100, and 1,000 synthetic nodes and duplicate canonical commit overhead. Run only through the guarded filtered benchmark script:

`ABRAXIUS_DOTNET_CLI=/home/velumix/.dotnet/dotnet ./scripts/benchmark.sh '*FabricBenchmarks*'`

Network RPC, large transfer, reconnect, and multi-machine makespan measurements require the manual LAN harness and must report hardware, TLS, payload, and network conditions.

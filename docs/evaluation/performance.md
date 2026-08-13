# Performance evaluation

Microbenchmarks, macrobenchmarks, and end-to-end mission evals are distinct result classes. Latency uses monotonic `Stopwatch` timing. Environment and execution mode are part of every result; optimized/AOT/JIT and cold/warm measurements are not mixed.

Resource metrics may include CPU, allocation, GC generations, RAM/VRAM, disk, network, and power where trustworthy. Missing telemetry is unavailable, never zero. Benchmark mode may suppress nonessential telemetry but preserves required provenance and outcomes.

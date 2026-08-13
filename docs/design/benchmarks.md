# Design benchmarks

The useful measurements are:

```text
surface capture latency and peak memory
source resolution and brief compilation latency
provider request and artifact persistence latency
Design Studio first render and candidate comparison latency
implementation validation duration
```

Benchmark provider traffic only with an explicit configured account. Synthetic
unit fixtures cover orchestration and avoid paid network calls. Provider
credentials and design payloads are excluded from benchmark telemetry.

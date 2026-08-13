# Model evaluation and routing evidence

The route engine records per-model outcome history for:

* transport/task success;
* deterministic verification pass rate;
* structured tool-call validity;
* latency and token usage;
* configured estimated cost.

The current implementation uses transparent rolling statistics rather than a learned router. This
keeps decisions testable and avoids training on untrusted provider output before the evidence
model is mature.

## Offline evaluation suite

The deterministic routing suite covers:

* free route success with zero frontier calls;
* same-tier alternate gateway after a provider failure;
* quota exhaustion and strict-free refusal;
* low-cost permission and cost ceiling;
* frontier escalation decision;
* required tools, structured output, coding, and context filters;
* local-only privacy routing;
* manual model pinning;
* streamed route metadata and cancellation shape.

`FakeOmniRouteModelProvider`, `FakeLiteLlmModelProvider`, and `FakeFrontierModelProvider` make all
of these tests credential-free and deterministic.

## Benchmark

`IntelligenceBenchmarks` measures route selection over catalogs of 10, 100, and 500 candidates
with BenchmarkDotNet and `MemoryDiagnoser`. It measures only policy overhead; provider latency is
not mixed into the routing benchmark. The command is:

```bash
dotnet run -c Release --project benchmarks/Abraxius.Benchmarks --filter '*Intelligence*'
```

The latest ShortRun baseline on the validation host is 2.617 us for 10 candidates, 20.519 us for
100, and 111.076 us for 500; allocations were 5.1 KB, 39.22 KB, and 192.42 KB respectively.

Live provider evaluation is opt-in and should use small, harmless requests. The Phase 6 validation
environment had no configured OmniRoute or LiteLLM gateway, so no live inference, premium call, or
free-quota claim is reported here.

# Test and benchmark process safety

Use `scripts/validate.sh` for full local validation and `scripts/benchmark.sh` for benchmarks. These entry points enforce the workstation-safe execution policy:

- restore is non-parallel;
- MSBuild uses one worker and node reuse is disabled;
- tests use one MSBuild worker and a five-minute per-test-host hang timeout;
- each command runs in a dedicated process group;
- EXIT, cancellation, and termination traps stop and reap only that owned group;
- .NET build servers are shut down after the run;
- benchmarks default to a short, in-process host;
- unrelated editor, runtime, and `dotnet` processes are never targeted with broad `pkill` commands.

```bash
ABRAXIUS_DOTNET_CLI=/path/to/dotnet scripts/validate.sh
ABRAXIUS_DOTNET_CLI=/path/to/dotnet scripts/benchmark.sh '*Progression*'
```

Out-of-process BenchmarkDotNet execution is disabled by default because it can fan out compiler processes. It may be explicitly enabled with `ABRAXIUS_ALLOW_OUT_OF_PROCESS_BENCHMARKS=1`, but only inside an isolated environment with a memory limit. CI should place the validation process in a cgroup/container or equivalent job-level memory boundary.

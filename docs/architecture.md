# Abraxius architecture

Abraxius is split into protocol, domain, scheduling, integration, persistence, runtime, and presentation layers. The dependency direction is intentionally one-way:

```text
Protocol
   ↑
Core ────────────────┐
   ↑                 │
Scheduler            │
   ↑                 │
Runtime ─ Models / Memory / Lattice / Ledger / Telemetry
   ↑
App or CLI
```

`Abraxius.Core` contains the explicit execution graph and domain contracts. A `WorkNode` carries its dependencies, executor category, priority, timeout, deadline, retry policy, and operation delegate. `ExecutionPlan.Validate()` rejects missing dependencies, self-dependencies, duplicate IDs, and cycles before any work is admitted.

`Abraxius.Scheduler` runs compiled graphs through independent bounded channels per `ExecutorKind`. The coordinator owns graph state and dependency propagation. Worker loops own execution and report terminal completions through a bounded internal completion channel with a single coordinator reader. Executor concurrency and queue capacities are configured separately. The legacy `ExecutionPlan` overload remains only for compatibility; the runtime and benchmarks use the compiled engine.

`Abraxius.Runtime` composes the same scheduler and providers used by the CLI and Avalonia application. The deterministic demo has a root task, three independent branches (tool/tool/memory), model synthesis, and verification. The three branch delays overlap because their only common dependency is the completed root.

`Abraxius.Lattice` exposes capability discovery, schema descriptions, policy validation, and execution. The model layer only produces structured requests; it does not invoke unrestricted system operations. The initial development capability is deterministic. Filesystem inspection is also available as a bounded read-only capability rooted at a configured directory.

`Abraxius.Memory` provides hot and file-backed evidence storage plus an asynchronous memory-provider abstraction. Work results carry `EvidenceId` references rather than duplicating large content.

`Abraxius.Ledger` appends flattened event records to a buffered JSONL file. Event writes are batched and asynchronous. `Abraxius.Telemetry.RuntimeEventHub` assigns sequence numbers and broadcasts to bounded subscribers; the ledger subscriber is lossless, while the UI subscriber is lossy/coalescing by design.

`Abraxius.App` is a consumer of runtime state. It contains a single custom `ExecutionGraphView` renderer, standard controls for command input and inspection, and `RuntimeUiStateAggregator`, which turns raw events into immutable frame snapshots. No scheduler or provider work runs on the Avalonia UI thread.

The OpenAI-compatible provider is deliberately transport-only and configurable by endpoint/model. Credentials are not stored in source or configuration defaults.

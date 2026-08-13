# Phase 4 scheduler architecture

The Phase 4 engine executes `CompiledExecutionGraph` instances. Graph definitions remain immutable; a private `GraphExecutionSession` owns the mutable node state for one execution. Public identity stays strongly typed (`ExecutionId`, `TaskId`, `NodeId`), while the hot path uses the compiled graph's integer indexes and reverse-edge arrays.

```text
CompiledExecutionGraph
          │
          ▼
GraphExecutionSession (single coordinator)
  ├─ dependency counters
  ├─ priority ready set
  ├─ bounded completion channel
  └─ node runtime state
          │
          ▼
ExecutorKind queues
  ├─ Model
  ├─ Tool
  ├─ Memory
  ├─ CPU
  ├─ I/O
  ├─ Verification
  └─ Background
          │
          ▼
typed IWorkExecutor adapters
```

The coordinator is the authority for graph transitions. A worker claims a queued item, executes it through `IWorkExecutor`, and publishes one structured completion. Workers never mutate dependency counters or commit results directly. This keeps the multi-field graph invariant in one place without a global scheduler lock.

## Admission and queues

Each executor kind has a bounded `Channel<WorkItem>` set behind a small priority queue. The queue tracks total occupancy independently of the four priority channels, so the configured capacity is a true total capacity rather than four times the configured value. Saturation returns pressure to the coordinator, which retains the node in `Ready` and retries admission after processing completions. Queue depth/capacity are emitted as `QueuePressure` events when pressure is elevated.

Admission considers:

- the configured pool limit;
- the current `ExecutionBudget` (including maximum total concurrency and per-kind limits);
- `ExecutionConstraints.MaxParallelism`;
- cancellation and terminal state.

`MutableExecutionBudgetProvider` allows a host to change a budget for future admissions without rebuilding the scheduler. Existing operations are not forcibly preempted by a budget reduction.

The ready set prefers `Critical`/`Interactive` work, but after a bounded high-priority burst it admits older lower-priority work. Aging uses the observed queue age at each scheduling decision, preventing permanent background starvation while preserving interactive latency.

## Completion and dependency propagation

When a success arrives, the coordinator decrements only the completed node's reverse-edge dependents. A dependent enters `Ready` exactly when its remaining required dependency count reaches zero. Failure, cancellation, and timeout walk only reachable descendants and mark them `Skipped`; no global graph scan is required. The resulting execution work is proportional to nodes plus dependency edges, apart from provider execution time.

Retries move the same logical node to `Waiting`, delay outside worker slots, then create a new attempt with the same `TaskId`. Cancellation, policy, validation, and malformed requests are not retried by default. Late results are checked against attempt and node state before commit; a cancelled or timed-out operation cannot restore success.

## Control plane and telemetry

Control events (`TaskStarted`, `TaskCompleted`, `TaskFailed`, `TaskCancelled`, and `ExecutionCompleted`) flow through the runtime event sink. Scheduler metrics use atomic counters and monotonic `Stopwatch` durations. Progress is sent through a separate progress event path and can be coalesced by future UI/telemetry consumers. Event sinks should remain bounded and should not be used as the source of graph correctness.

The scheduler has no Avalonia, OS probing, model SDK, MCP, shell, or database dependency. Provider adapters live in `Abraxius.Runtime` and route model/tool/memory work through the Phase 2 contracts. A CLI and Avalonia host can therefore observe the same execution without changing the engine.

## Lifecycle

`DagScheduler.ExecuteAsync` admits a compiled graph, starts shared executor workers lazily, and returns an `ExecutionResult` only after all reachable nodes are terminal. `IAsyncDisposable` shutdown stops admission, cancels workers, completes queues, waits the configured grace period, and disposes queue signaling resources. A non-cooperative provider is isolated behind its worker boundary; its late output is still state-checked before it can affect the graph.

The old `ExecutionPlan` overload remains temporarily as a compatibility surface for Phase 1 consumers. New runtime code and benchmarks use the compiled graph engine directly.

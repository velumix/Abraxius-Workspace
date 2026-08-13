# Concurrency model

The scheduler follows dependency edges, not source-code order.

1. Validate the DAG.
2. Mark zero-dependency nodes `Ready`.
3. Dequeue ready work in deterministic priority/creation order.
4. Route each node to the bounded channel for its executor category.
5. Worker loops transition `Queued → Running`, execute with a linked cancellation/timeout token, and report a terminal completion through the bounded completion channel.
6. The coordinator decrements dependency state. A child becomes ready only when every required dependency is `Succeeded`. A child with a failed, cancelled, timed-out, or skipped dependency becomes `Skipped`.
7. When all nodes are terminal, the execution result is returned. Shared executor workers remain available for other admitted executions and are closed during scheduler shutdown.

The hot path uses bounded `Channel<T>` instances for executor queues and completion delivery, plus bounded event subscriptions. Queue writes use bounded admission, so pressure creates actual backpressure. Queue depth and capacity are emitted as metrics/events. There is no unbounded global `ConcurrentQueue<object>`.

Cancellation is associated with an execution token. Pending, ready, and queued descendants are cancelled without being started; running operations receive the linked token. Timeout cancellation is distinguished from user cancellation and becomes a structured `Timeout` error. External operations are expected to honor their token; shutdown waits only for the configured worker grace period.

Retries are local to a worker attempt. A retry is allowed only when the configured policy permits it, and transient classification is explicit. A retry does not create a second DAG node or duplicate result identity.

The UI uses a separate bounded lossy subscription. `UiStateStore` applies all events it receives into a compact task map and event ring, while the frame loop posts at most one snapshot per 16 ms cadence when state is dirty. The ledger retains complete event fidelity independently of UI retention.

The scheduler is intentionally conservative about synchronization: node state transitions use atomic lifecycle changes, result propagation is coordinator-owned, metrics use atomic counters, and providers remain independent. No unsafe code, native boundary, or Rust module is present; a future native implementation can satisfy the existing provider/capability contracts if profiling demonstrates a material benefit. See [scheduler-architecture.md](scheduler-architecture.md) for the Phase 4 coordinator/worker flow.

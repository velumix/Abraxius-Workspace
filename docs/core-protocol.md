# Abraxius core protocol and execution IR

Phase 2 establishes the typed language shared by the planner, scheduler, providers, Lattice, memory, verification, and future transport adapters.

## Boundaries

`Abraxius.Protocol` contains small process/wire-facing contracts: typed IDs, protocol versions, causality metadata, evidence/result/artifact references, capability requests/results, and provider-neutral model, memory, and verification messages.

`Abraxius.Core` contains the immutable execution IR and domain rules. It references Protocol but has no Avalonia, model SDK, MCP, database, or Agent Framework dependency. Existing Phase 1 `WorkNode`/`ExecutionPlan` types remain as a compatibility surface for the demo scheduler; the Phase 2 `ExecutionGraph` is the scheduler-ready IR for Phase 3.

## Identifiers and causality

Execution identities are distinct value types, so a `TaskId` cannot be passed where an `ExecutionId` is required. GUID-backed IDs are serialized as compact JSON strings through `ProtocolJson`. Capability IDs are validated human-readable names.

The normal causal chain is represented directly:

```text
IntentId / CorrelationId
        ↓
ExecutionId
        ↓
TaskId / NodeId
        ↓
model, capability, memory, or verification request
```

`CausalityContext` carries the execution, correlation, task, parent task, and optional causal predecessor when a request crosses an adapter boundary.

## Execution graph

`ExecutionGraph` is immutable after construction. Each `ExecutionNodeDefinition` contains:

- `NodeId` and `TaskId`;
- the owning `ExecutionId`;
- explicit `ImmutableArray<NodeId>` dependencies;
- optional parent and speculation group;
- typed `WorkDescriptor`;
- priority, deadline, timeout, retry policy, and resource hints.

The graph is data, not prose. Work descriptors are sealed record variants such as `ModelWorkDescriptor`, `ToolWorkDescriptor`, `MemoryWorkDescriptor`, `CpuWorkDescriptor`, `VerificationWorkDescriptor`, and `SynthesisWorkDescriptor`. The scheduler never needs to parse a natural-language label to decide how work is dispatched.

Validation is iterative and approximately `O(V + E)`. It reports structured errors for duplicate IDs, missing references, wrong execution ownership, invalid roots, self-dependencies, speculation-group errors, and cycles. `ExecutionGraph.Compile()` produces `CompiledExecutionGraph`, which precomputes:

```text
NodeId → compact integer index
dependency indexes
reverse dependent indexes
initial dependency counts
topological order
root indexes
leaf indexes
```

Phase 3 can therefore use integer indexes in its hot path while preserving typed IDs at public boundaries.

## Runtime state

Graph definitions never become mutable scheduler records. `ExecutionNodeRuntimeState` is an immutable snapshot, and `ExecutionStateRules` defines valid transitions:

```text
Pending → Ready → Queued → Running → Succeeded
                                  ├── Failed
                                  ├── Cancelled
                                  └── TimedOut
```

Terminal states cannot be reactivated. Runtime timestamps and attempt counts live in the state snapshot, not the definition.

`WorkState` remains in Protocol for the Phase 1 scheduler/UI compatibility contract. New graph IR code uses `ExecutionState`; the two enums intentionally have the same lifecycle values until the scheduler migrates in Phase 3.

## Results and large data

`WorkOutcome` separates success, failure, cancellation, and timeout. `WorkOutput` can contain a small inline JSON value plus `ResultReference`, `EvidenceId` references, and `ArtifactId` references. Large content is not copied through graph messages. `EvidenceReference` carries content type, size, creation time, metadata, and an optional content hash.

`RuntimeError` is machine-readable through `ErrorCategory`, `Code`, `Message`, transient classification, detail, and metadata. Exception text is not used as state.

## Model, memory, capability, and verification contracts

The Protocol project defines provider-neutral request/result contracts. The existing `Abraxius.Models`, `Abraxius.Memory`, and `Abraxius.Lattice` projects are adapters around those contracts and may evolve independently.

The policy boundary is explicit:

```text
model response
    ↓
ProposedAction
    ↓
IActionPolicy / PolicyDecision
    ↓
CapabilityRequest
    ↓
Lattice capability
```

`CapabilityDescriptor` describes permissions, schemas, operations, cancellation, and streaming support. Lattice executes a resolved capability; it does not select the user’s objective. `DelegationRequest` carries structured evidence and an `OutputContract` instead of a role-play conversation.

## Concrete graph example

The intent “Diagnose failing project tests” can compile into this DAG:

```text
        root
       / |  \
   tests git memory
      \   |  /
       diagnosis
           |
         verify
```

The corresponding construction is deliberately explicit:

```csharp
var execution = ExecutionId.New();
var root = new ExecutionNodeDefinition(
    NodeId.New(), TaskId.New(), execution,
    new BackgroundWorkDescriptor("intent-root"));

var tests = new ExecutionNodeDefinition(
    NodeId.New(), TaskId.New(), execution,
    new ToolWorkDescriptor(
        new CapabilityId("filesystem"),
        "search_files",
        new ActionTarget("tests")),
    [root.Id]);

var git = new ExecutionNodeDefinition(
    NodeId.New(), TaskId.New(), execution,
    new ToolWorkDescriptor(
        new CapabilityId("git"),
        "status",
        new ActionTarget(".")),
    [root.Id]);

var memory = new ExecutionNodeDefinition(
    NodeId.New(), TaskId.New(), execution,
    new MemoryWorkDescriptor("prior failures", 8),
    [root.Id]);

var diagnosis = new ExecutionNodeDefinition(
    NodeId.New(), TaskId.New(), execution,
    new SynthesisWorkDescriptor(
        "diagnose failing tests",
        [tests.Id, git.Id, memory.Id]),
    [tests.Id, git.Id, memory.Id]);

var verify = new ExecutionNodeDefinition(
    NodeId.New(), TaskId.New(), execution,
    new VerificationWorkDescriptor("verify diagnosis", [diagnosis.Id]),
    [diagnosis.Id]);

var graph = new ExecutionGraph(
    execution,
    CorrelationId.New(),
    [root, tests, git, memory, diagnosis, verify]);
var compiled = graph.Compile();
```

The graph compiler verifies the references and gives the future scheduler ready queues and reverse edges without repeated LINQ scans or string dispatch.

## Deliberately deferred

Phase 2 does not implement worker queues, retries in the scheduler, speculative winner selection, persistence, vector retrieval, model transport, MCP, UI, or distributed execution. Those systems now have stable contracts to target without changing the core graph language.

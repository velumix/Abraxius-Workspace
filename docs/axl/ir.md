# AXL IR and runtime mapping

`Abraxius.Axl` is the provider-independent in-process representation. `AxlCommand` is a sealed discriminated hierarchy and `AxlValue` is a typed value hierarchy; runtime code does not use `Dictionary<string, object>` as the semantic source of truth.

The compiler is pure. It validates an `AxlDocument`, then maps it to existing Phase 2 types:

```text
AxlFindCode       → ToolWorkDescriptor(code.search)
AxlCapabilityCall → ToolWorkDescriptor / CapabilityRequest / ProposedAction
AxlMemoryQuery    → MemoryWorkDescriptor
AxlSynthesis      → SynthesisWorkDescriptor
AxlVerification   → VerificationWorkDescriptor
AxlIntent         → Phase 2 Intent
AxlDelegation     → ModelWorkDescriptor
AxlResult/State   → observations, no execution node
```

Command references become `NodeId` dependencies only after the referenced command has compiled. Independent roots therefore remain independent scheduler work. Forward references are validated first and resolved by the compiler only when a dependency's node exists; an unresolved or non-command reference is a structured error.

The graph is still the Phase 2/4 `ExecutionGraph`. AXL does not replace the scheduler graph, Lattice capability contracts, or policy engine. Existing `ExecutionId`, `CorrelationId`, `TaskId`, and `NodeId` types remain authoritative.

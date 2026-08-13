# Phase 11 specialist runtime

Phase 11 adds one provider-neutral `AgentKernel` over the existing Abraxius runtime. Athena, Orion, Daedalus, and Argus are `SpecialistDefinition` values, not separate runtime classes and not model identities.

```text
User Intent
    ↓
AgentKernel / Athena policy
    ↓
typed AgentAssignment + MissionSuccessContract
    ↓
Phase 10 context compiler + Phase 6 model provider
    ↓
AXL handoff/result projection
    ↓
Phase 4 ExecutionGraph / DagScheduler
    ↓
Lattice policy and capabilities
    ↓
Argus verification
```

The kernel owns lifecycle, budgets, bounded coordination, specialist instance state, policy checks, cancellation, and mission outcomes. `IAgentAssignmentRunner` is the runtime adapter boundary. The current runtime implementation is `SchedulerAgentAssignmentRunner`; it compiles assignments to scheduler work and returns proposals/results. It does not grant mutation or write files.

## Role separation

| Identity | Semantic role | Default authority | Primary output |
|---|---|---|---|
| Athena | Coordinator | mission planning and delegation | assignments and success contract |
| Orion | Investigator | read-only evidence gathering | findings and evidence references |
| Daedalus | Builder | proposed/gated implementation | implementation proposal |
| Argus | Verifier | independent read-only verification | passed/failed/inconclusive result |

Display names are presentation data. Scheduler and policy code uses `SpecialistRole` and capability IDs. Legacy Butler/Scout/Smith aliases remain only in the compatibility map.

## Mission execution

Simple investigation and explicit specialist targets use one assignment. Build/fix missions use two independent Orion assignments, then Daedalus, then Argus. A failed Argus result can create one bounded Daedalus repair followed by a second Argus check. All assignments pass through the same bounded Phase 4 scheduler capacity.

The classifier is deterministic for obvious requests. It does not call an LLM to decide that “find callers” is an investigation. More ambiguous planning can be added behind the same kernel boundary later.

## Trust boundaries

Parsing AXL, producing a model result, and producing an implementation proposal are not authorization. The kernel checks the specialist policy, and actual capability execution remains behind Lattice policy. The current builder adapter emits a model proposal; repository integration/worktree mutation requires an explicit host adapter and user/policy approval.

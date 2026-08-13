# Abraxius Skills architecture

Skills are versioned, typed procedures. Memory records what happened; a Skill records a reusable way to accomplish a class of task.

```text
Mission / Assignment
        │
        ▼
   SkillMatcher ─────► Phase 10 memory and scope signals
        │
   eligible procedure?
      /          \
    yes            no
     │              │
 SkillExecutor   AgentKernel planning
     │              │
     └──────┬───────┘
            ▼
      AXL / ExecutionGraph
            │
       Phase 4 scheduler
            │
       Lattice / tools
            │
         Argus
            │
       stats + candidates
```

`Abraxius.Skills` is provider-independent and references existing AXL, Core, Memory, and Agent contracts. `Abraxius.Runtime` supplies the adapter that sends specialist steps through AgentKernel and capability steps through the existing scheduler/executor registry. There is no Skill-specific scheduler.

The current vertical slice includes four built-in procedures:

- `repo.inspect-project`
- `git.regression-investigation`
- `dotnet.build-and-test`
- `verify.standard-code-change`

They are shipped as `Validated`, not `Trusted`, and are selected only when structured triggers, scope, capabilities, safety policy, and lifecycle state allow it.

Skill execution is bounded by step count, concurrency, optional duration, cancellation, explicit dependencies, and typed input contracts. A Skill is data and a plan; parsing or importing one never executes it.

Composition is supported through a bounded SkillCompositionStep. It resolves a
pinned or current registry version, maps typed inputs, re-enters the executor,
and reapplies current policy. Active version keys and a maximum depth prevent
recursive composition cycles.

Explicit model cognition steps use the ISkillModelOperator seam. The Runtime
adapter sends them through the existing Phase 6 IModelProvider; model output
is returned as data and cannot issue capabilities from that step.

# Skill security boundary

```text
Skill != permission
Skill != tool
Skill != executable code
Skill != policy
```

Every execution remains subject to current mission policy, specialist policy, platform capability availability, Lattice authorization, scheduler constraints, and user approval where required. Trust measures historical reliability; it does not cache permission and cannot expand capabilities.

Imported and generated definitions begin disabled/untrusted. Unknown capabilities, invalid AXL, dependency cycles, unsafe side effects, missing isolation for experimental mutation, malformed inputs, and policy conflicts fail before execution. Source/description text is data and cannot change runtime instructions.

There is no raw shell escape, `eval`, arbitrary embedded program, direct Skill-specific file mutation, or permission bypass. The Runtime adapter sends allowed capability work through existing `ExecutionGraph`/`DagScheduler` and Lattice executor boundaries. Cancellation and stale mission generations prevent late results from committing to superseded work.


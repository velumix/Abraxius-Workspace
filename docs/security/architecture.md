# Security architecture

Phase 17 establishes one deterministic authorization boundary:

```text
Intent → structured proposal → canonical resource → risk → policy → grant → Lattice → audit
                                                   ↘ Needs You ↗
```

`SecurityKernel` is provider- and UI-independent. Runtime Lattice calls use `SecurityLatticePolicy`; unknown principals, capabilities, resources, and malformed requests default to deny. Model output and retrieved content can propose actions but cannot issue grants. Phase 16 delivers approval requests, while the durable authorization decision remains security state.

Authority is bounded by the intersection of global/user/workspace/project/mission/specialist/Skill/plugin policy, platform availability, and a current grant. Explicit deny and canonical workspace boundaries override child allowances.

The current implementation provides application-level authorization, canonical path/symlink checks, brokered secrets, bounded grants, audit, LocalOnly egress rules, workspace sandbox declarations, and a runtime Lattice gate. It does not claim universal OS-kernel containment.

# Fabric architecture

Phase 20 extends, rather than replaces, the Phase 4 scheduler. The execution path is:

`Mission → Phase 4 Scheduler → Placement → ExecutionLease → Worker → Lattice/executor → Artifact → canonical result commit`.

One coordinator epoch owns canonical mission commits. Workers enforce admission, authorization envelopes, resource reservations, and node-local policy. Phase 6 still chooses a model; Fabric chooses an eligible execution node. Phase 17 authorizes, and Phase 18 owns immutable outputs.

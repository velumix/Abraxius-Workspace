# Delegation and lifecycle

`Mission` is the user objective and success contract. `AgentAssignment` is one bounded unit of specialist work. `SpecialistInstance` is an ephemeral execution identity created from a definition.

The kernel enforces:

- cognitive and autonomy budgets;
- maximum concurrent specialists through a semaphore;
- maximum assignment count and delegation depth foundations;
- cancellation propagation;
- explicit role targeting (`@Orion`, `@Daedalus`, `@Argus`, `@Athena`);
- policy checks before an assignment runner is admitted;
- bounded repair after an independent verification failure.

Agent messages use typed envelopes (`AssignmentMessage`, `EvidenceResponseMessage`, `ImplementationReadyMessage`, `VerificationResultMessage`, and related messages) over bounded channels. `AgentKernel.ToAxlHandoff` projects a handoff/result pair through existing Phase 9 `AxlDelegation` and `AxlResult` types. In-process code retains typed objects and does not serialize unnecessarily.

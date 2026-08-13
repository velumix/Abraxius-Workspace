# Routing policy

The default policy is `FreeFirst` with paid inference disabled. The available modes are:

| Mode | Behavior |
| --- | --- |
| `FreeFirst` | deterministic work, zero-cost/included candidates, then explicitly permitted paid tiers |
| `FreeFirstStrict` | zero-cost or included candidates only; exhaustion returns a structured routing error |
| `Balanced` | still respects hard filters and budgets, with more weight on quality and latency |
| `QualityFirst` | prefers quality and reliability within the configured maximum tier/cost |
| `Manual` | uses the explicitly selected model/route; it is never silently replaced |

The ladder is:

```text
Deterministic -> Free -> Included -> UltraLow -> Standard -> Frontier -> Specialist
```

The route engine first filters candidates. A candidate is rejected when it lacks a required
capability, cannot fit the required context, violates privacy, is disabled/unhealthy, has no
quota, exceeds a cost ceiling, or is above the maximum tier. The decision includes a
`RouteCandidateEvaluation` for every candidate, including a machine-readable rejection reason.

The score is deliberately inspectable. Its configured components are capability/task fit,
quality history, quota remaining and reset proximity, health, latency, route affinity,
verification reliability, and cost. Cost is not allowed to turn an incapable candidate into a
valid one.

## Cost and quota rules

`ModelCostClass.Unknown` is not free. A route must be explicitly classified as `Zero` or
`Included` to participate in strict-free routing. Quota state is evidence, not a fabricated global
allowance; it may contain remaining tokens, a reset time, period, source, and confidence.

`AllowPaidInference` and `MaximumEstimatedCost` are separate controls. The former permits a paid
route to be considered; the latter caps the configured estimate. A mission-level zero-cost policy
therefore cannot be bypassed by a gateway fallback.

Execution-scoped model budgets are enforced by an in-memory `IntelligenceBudgetLedger`. Runtime
requests reserve model calls and estimated cost before submission; frontier requests also reserve
their configured output-token allowance when a premium-token ceiling is present. Reservations are
conservative by design, preventing concurrent requests from overspending. The ledger exposes a
point-in-time usage snapshot for telemetry and future durable accounting.

## Failover versus escalation

```text
HTTP 429 / transient provider failure -> another eligible route in the same tier
verification failure                 -> bounded repair or quality escalation
budget/privacy/capability failure    -> structured inability, no hidden fallback
```

Every quality escalation is bounded by `MaximumEscalations` and `MaximumTier`. The UI can show the
route decision and escalation reason without exposing gateway credentials.

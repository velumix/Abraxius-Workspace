# Escalation policy

Escalation is evidence-driven, bounded, and observable.

```text
deterministic
    -> free/included attempt
    -> same-tier retry or failover
    -> repair with concise verification evidence
    -> ultra-low/standard route
    -> frontier only when permitted and justified
```

Transient provider failures use same-tier failover. They do not imply that a stronger model is
needed. Quality escalation is appropriate for repeated verification failures, invalid structured
output, repeated invalid tool proposals, contradictory results, insufficient context, or a
high-risk task whose verification threshold was not met.

The `EscalationController` returns `Stay`, `Retry`, `SameTierFailover`, `EscalateOneTier`,
`EscalateToSpecialist`, `Abort`, or `RequestUserApproval`. It receives the current route,
structured outcome, and escalation count. A request's maximum tier, maximum escalation count,
premium token ceiling, estimated cost ceiling, and privacy policy remain authoritative.

Frontier models may be used as a critic or diagnosis step rather than regenerating a complete
solution. The future verification executor should pass deterministic build/test/schema evidence
back into this controller; model self-confidence is not verification.

## Approval behavior

`AllowPaidInference=false` is the safe default. A zero-cost mission must remain zero-cost. A
future UI policy can allow automatic paid inference up to a mission ceiling or require approval
above a threshold; neither behavior is implemented by silently falling through to a premium
provider.


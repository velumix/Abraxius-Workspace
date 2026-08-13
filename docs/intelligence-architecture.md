# Intelligence fabric architecture

Phase 6 adds an intelligence fabric without changing the scheduler's model-executor boundary:

```text
Execution graph
    |
    v
ModelWorkExecutor
    |
    v
RoutedModelProvider
    |
    v
IntelligenceRouteEngine  ---- transparent policy, budget, privacy, quality history
    |                 \
    v                  v
OmniRoute peer       LiteLLM peer       Frontier peer
free/quota fabric    normalized fabric  explicit premium adapter
```

Abraxius owns the policy decision for a request. A selected gateway may still perform its own
provider/deployment routing, retries, health checks, or quota handling, but that internal choice is
not silently repeated by another Abraxius router. The `RouteDecision` records both the Abraxius
decision and the candidates that were rejected.

The existing `IModelProvider` remains the in-process typed boundary. No JSON is used between the
scheduler and the route engine. JSON exists only at the OpenAI-compatible gateway boundary.

## Request lifecycle

1. The deterministic runtime decides whether a model node is needed.
2. `ModelRequest` carries task class, required capabilities, context requirement, routing policy,
   privacy policy, and cost limits.
3. The route engine applies hard filters for capability, context, privacy, health, quota, tier,
   and cost. Impossible candidates are never scored.
4. Eligible candidates receive a transparent score using quality history, quota/headroom, health,
   latency, affinity, task fit, and cost.
5. `RoutedModelProvider` delegates to exactly one peer gateway. A provider failure first attempts
   another eligible candidate in the same tier.
6. Verification feedback can be passed to `IEscalationController`. Quality failure escalates one
   tier at a time within the request budget; provider failure is not automatically a quality
   escalation.
7. The selected route is attached to `ModelResult` and published as a runtime event for the
   ledger and UI inspector.

Before submission, `IntelligenceBudgetLedger` reserves the request against the execution's model
call, estimated-cost, and premium-token ceilings. This admission point is shared by streaming and
non-streaming requests, so a concurrent burst cannot race past a mission budget.

If a catalog entry advertises `RouteCapacity.MaxConcurrentRequests`, `RoutedModelProvider` also
holds a bounded per-route semaphore across the complete request or stream. Gateway-level RPM/TPM
headroom is retained as metadata for policy and diagnostics; Abraxius does not invent quota values
when the gateway does not expose them.

## Safety defaults

External gateways are disabled by the generic runtime unless an application host configures them.
The production desktop shell enables its local OmniRoute endpoint with an explicit zero-cost
`auto/coding:free` candidate. The runtime never inserts a deterministic mock into production
composition; tests and explicit offline fixtures must opt into that provider. Paid inference is
disabled by default. Unknown cost is never treated as free; connected production routes must be
explicitly classified and supply quota evidence where available.

Credentials are read only from a configured environment-variable name and are never included in
route decisions, logs, or ledger events.

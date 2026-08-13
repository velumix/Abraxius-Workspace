# Skill lifecycle

```text
Candidate → Experimental → Validated → Trusted
                         └────────────► Deprecated
Candidate/any state ───────────────────► Rejected or Disabled
Trusted ────────────────────────────────► NeedsRevalidation
```

- **Candidate**: extracted, generated, imported, or authored; disabled/untrusted by default.
- **Experimental**: structurally and policy validated; suitable for controlled use.
- **Validated**: at least one execution passed the declared verification boundary.
- **Trusted**: repeated verified use meets configured execution, reliability, and recent-failure thresholds. Promotion is runtime policy, never model-controlled.
- **NeedsRevalidation**: trusted performance degraded or an environment/schema change invalidated confidence.
- **Deprecated**: retained for history/replay but not automatically selected.
- **Rejected**: validation failed; diagnostics are retained.
- **Disabled**: explicitly unavailable for matching.

Imported and model-generated Skills cannot enter `Trusted` directly. A successful dry run is not a verified execution. Reliability uses a smoothed estimate `(verified successes + 1) / (executions + 2)` so one success is not reported as certainty.


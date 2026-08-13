# Metrics

Every value records metric ID, unit, aggregation, availability, and sample count. Missing values are Unknown/Unavailable/NotApplicable; they are never converted to zero.

- `VerifiedSuccessRate = verified passing product cases / product cases`. Infrastructure failures, cancellation, and skips are excluded from that denominator.
- `Recall@K = relevant items retrieved in top K / relevant items`.
- `Precision@K = relevant items retrieved in top K / returned items in top K`.
- `MRR = 1 / rank of first relevant result`, or zero when absent.
- Frontier escalation is frontier-routed executions divided by eligible executions.
- Cost per verified result requires known cost and at least one verified result.

Distributions support mean, median, p90, p95, p99, min, and max. Percentiles use linear interpolation over sorted samples. Composite scores are not a default release decision.

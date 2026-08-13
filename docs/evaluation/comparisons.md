# Comparisons

Comparison requires the same suite identity, suite version, and case set. Case matching is keyed, not positional, and therefore linear rather than quadratic. Different workloads are rejected.

Hardware/runtime fingerprint mismatch marks performance and resource deltas Inconclusive while still permitting environment-independent correctness comparison. Each metric keeps baseline, candidate, absolute delta, relative delta, direction, sample count, and explanation. Improvements and regressions remain separate so cost/correctness/latency tradeoffs stay visible.

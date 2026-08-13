# Regressions

`EvalRegression` pins comparison, suite, optional case, metric, baseline, candidate, delta, severity, evidence, artifact references, and trajectory. Lifecycle is Open, Investigating, FixCandidate, Resolved, Accepted, or WontFix.

“Create Mission” is explicit. It carries regression, suite, baseline run, candidate run, and metric references to Athena. The repair must produce a verified artifact and rerun the same suite/case before the regression can be considered resolved. Phase 19 does not automatically launch self-modification.

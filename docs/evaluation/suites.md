# Suites

Suites are identified by `EvalSuiteId + Version`; cases, metric definitions, gates, and execution policy are version-pinned. Lifecycle states are Draft, Experimental, Validated, ReleaseGate, Deprecated, and Archived.

Built-ins are `core.mission-smoke`, `axl.core`, `memory.retrieval`, `scheduler.parallelism`, `security.adversarial`, `skills.effectiveness`, and `artifacts.integrity`. The current built-ins are deterministic foundation suites. Provider-backed model, voice corpus, full specialist mission, and cross-platform device packs plug into `IEvalCaseExecutor` without changing suite semantics.

Smoke caps work for developer feedback; Standard broadens coverage; Full uses the suite's complete configured case set. All retain completed case results after cancellation.

# Evaluation architecture

Phase 19 implements `Suite → Cases → Phase 4 Scheduler → Verification → Metrics → Comparison → Regression → Gate`. `EvalRunner` batches independent case executions to the suite concurrency bound and submits each batch as a Phase 4 `ExecutionPlan`; it does not own a second scheduler. Each completed case is checkpointed before suite completion.

The headless `Abraxius.Evaluation` assembly owns domain logic. SQLite stores suite/run metadata and one normalized row per case result plus metric samples. Large reports are immutable Phase 18 `evaluation-report` artifacts. Avalonia and CLI are consumers, never sources of evaluation truth.

Evaluation execution is progression-ineligible. Replays inspect stored results and do not execute external side effects. A regression can be converted explicitly into a mission carrying run, suite, metric, and regression references.

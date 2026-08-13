# Security audit

The append-only SQLite/in-memory audit records requests, decisions, approvals, grants, secret use, execution results, policy changes, revocations, and sandbox violations. Events correlate principal, mission, assignment, agent instance, Skill execution, action, decision, and grant IDs.

Audit is distinct from debug logging and excludes secret values. Replay consumes the decision provenance but blocks mutation and external side effects by default.

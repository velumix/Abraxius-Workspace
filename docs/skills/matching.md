# Skill matching

Matching is deterministic and explainable. It evaluates:

- structured concepts, task classes, error codes, symbols, and framework triggers;
- Skill name tokens as a secondary signal;
- specialist role compatibility;
- project/repository/language/framework scope;
- capability availability and current mutation policy;
- lifecycle state and smoothed verified reliability.

Exact structured trigger matches outrank weak lexical matches. Candidate, rejected, disabled, deprecated, and `NeedsRevalidation` Skills are excluded from ordinary automatic matching. An explicit request can surface an ineligible Skill with a rejection reason, but it does not bypass validation or policy.

The matcher returns a ranked `SkillMatch` with weighted `SkillMatchReason` entries. This is exposed by the CLI and the Avalonia Skills explorer so a user can see why a procedure was selected. Simple known tasks do not require an LLM routing call.


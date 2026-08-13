# Skill versioning

Skill definitions are immutable by `(SkillId, SkillVersion)`. Versions use `major.minor.patch[-preRelease]`:

- **Patch**: metadata or implementation correction without changing the input/output or semantic contract.
- **Minor**: compatible procedure improvement or optional addition.
- **Major**: breaking input, output, precondition, capability, or semantic change.

The registry retains old versions for diagnostics and future replay. Mission/trajectory records should pin the exact version used; `latest` is not a reproducible replay selector. Imported versions are persisted as untrusted candidates. Built-in updates are registered separately from learned/user data and do not overwrite learned definitions.

AXL schema compatibility is checked through the Phase 9 parser/migration boundary. Text is parsed to typed IR before migration; Skill text is never rewritten with regular expressions.


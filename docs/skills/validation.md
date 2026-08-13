# Skill validation

Validation has independent layers:

1. **Structural** — identifiers, descriptions, steps, duplicate IDs, dependencies, cycles, conditional/composition references, step limits, and typed AXL projection.
2. **Capability** — declared capabilities are known/available when an availability set is supplied.
3. **Policy** — mutation, external side effects, specialist roles, current approval, and workspace constraints remain external authorities.
4. **Safety** — imported/generated trust restrictions and experimental mutation isolation requirements.
5. **Execution** — the Skill executor runs bounded dependency levels and records controlled failures/cancellation.
6. **Outcome** — verification steps and Argus/specialist results determine whether the procedure passed.

Execution validates required inputs, input kinds, workspace/isolation requirements, and project/repository scope before running. Capability steps are rejected unless the Skill declares the capability; the Runtime adapter then submits the actual operation through the existing Phase 4 scheduler and executor registry.

No validation path executes raw shell text, embedded C#, Python, `eval`, or arbitrary model output.

Composed Skills are resolved by the executor from the registry using an exact
or current version reference. Missing, disabled, rejected, or recursively
active child versions return a controlled diagnostic; they are never silently
replaced by an unrelated procedure.

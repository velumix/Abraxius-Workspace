# AXL Skill representation

AXL 1 includes a declarative `skill` command for interchange and diagnostics:

```text
axl/1
skill id="repo.inspect-project" ver="1.0.0" trigger=["inspect" "repository"] steps=["memory:ContextQuery" "inspect:SpecialistAssignment"] verify=["source evidence"] safety=readonly
```

The typed `SkillDefinition` is the in-process source of truth. `SkillAxlProjection` maps it to `AxlSkill`, formats canonical text, and validates the round trip through the strict AXL parser. The projection intentionally carries compact schema metadata; the complete typed procedure remains in the Skill registry.

The projection/validation boundary is:

```text
SkillDefinition → AXL Skill IR → canonical AXL text
                              ↘ strict parser/validator
```

An `AxlSkill` is not an executable command and `AxlExecutionCompiler` treats it as declarative data. The full typed procedure remains registry-owned; import/export of the compact AXL projection is intentionally not a lossy substitute for the typed JSON registry body. Execution requires a validated `SkillDefinition`, current mission policy, a runtime adapter, and the existing scheduler/Lattice path.

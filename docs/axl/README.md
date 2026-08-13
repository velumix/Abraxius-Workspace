# AXL

AXL is Abraxius's compact, typed machine representation. It connects model output, runtime protocol objects, execution graphs, capability requests, and future memory/trajectory storage without turning the runtime into a scripting language.

The implementation currently ships as `Abraxius.Axl` plus the optional `Abraxius.Axl.Model` boundary adapter.

The safety boundary is deliberate:

```text
AXL text → parser → typed IR → schema/semantic validation → policy → compiler → scheduler/Lattice
```

Parsing and compilation never execute a capability. There is no `eval`, shell escape, or implicit authorization.

## Current surface

- AXL `1.0`, canonical header `axl/1`.
- Strict UTF-8 and string parsing.
- Typed commands for `find.code`, `call`, `memory.query`, `synth`, `verify`, `intent`, `delegate`, `ret`, and `state`.
- Typed references such as `c#1`, `e#42`, `@cap:git.status`, and `@agent:scout`.
- Compact and pretty deterministic formatting.
- Schema and semantic validation with bounded input limits.
- Conservative model-output fence repair.
- Bounded incremental text parsing that reports `Incomplete` until a complete document arrives.
- Pure mapping to Phase 2 `ExecutionGraph`, `CapabilityRequest`, `ProposedAction`, and work descriptors.
- Versioned, bounded, checksummed binary framing. The initial codec carries canonical AXL UTF-8 as its payload; it is a stable boundary foundation, not yet a field-packed binary IR.
- CLI and Avalonia diagnostic inspection.

## Usage

```bash
dotnet run --project src/Abraxius.Cli -- axl parse tests/fixtures/axl/valid/acceptance.axl
dotnet run --project src/Abraxius.Cli -- axl format tests/fixtures/axl/valid/acceptance.axl --pretty
dotnet run --project src/Abraxius.Cli -- axl compile tests/fixtures/axl/valid/acceptance.axl
```

The compile command prints the proposed runtime graph only. It does not execute it.

See the focused documents for syntax, IR, binary framing, versioning, model generation, and security.

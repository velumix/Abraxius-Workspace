# AXL security boundary

AXL is untrusted input when it comes from a model, remote peer, file, or user. The following are separate stages:

```text
parse → schema validation → semantic validation → policy → capability resolution → schedule
```

Parsing does not execute. Compilation does not execute. A model cannot grant itself capabilities by writing `mutation=true`, `allow=everything`, or a provider-specific field. The default validator rejects mutation-capable calls and can restrict registered capability IDs.

There is no `eval`, no arbitrary function/lambda syntax, no loop, and no raw shell escape. Shell-like behavior, if later supported, must be a registered capability request subject to normal policy and confirmation.

The parser enforces document, string, command, list, record, and nesting limits before unbounded materialization. Binary lengths are checked before allocation. Invalid UTF-8, malformed frames, unsupported versions, duplicate fields, unknown commands, and ambiguous dependencies fail with structured diagnostics. Secrets should be represented as opaque secure-store references; raw credentials must not enter AXL logs, memory, or trajectories.

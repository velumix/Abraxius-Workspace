# Generating AXL from models

Phase 6 can request `ModelOutputFormat.Axl` using `Abraxius.Axl.Model`. `WithAxlOutput` adds a compact, task-scoped schema pack and records the AXL version/schema selection in request metadata.

Only expose schemas the task needs. A minimal request might provide:

```text
axl/1 schemas=find.code,call
```

Models should return one complete AXL document, not Markdown prose. A response may be fenced for display; the optional repair pipeline removes only an explicit ` ```axl ` / ` ```text ` wrapper and then runs the strict parser again. It never invents a missing command, field, capability, or destructive intent. Malformed or ambiguous output is rejected.

Structured provider output can map directly to the same typed IR in a future adapter; AXL text is not mandatory on every provider path. The important invariant is one validated typed representation before compilation.

Models receive concise diagnostics such as missing fields or invalid references rather than a natural-language repair essay. Phase 6 should measure first-pass valid AXL rate, repair rate, retries, and verified task success before using AXL reliability as a routing signal. The current formatter emits diagnostic codes such as `AXL011` for a missing field.

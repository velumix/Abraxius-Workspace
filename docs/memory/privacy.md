# Memory Privacy

Memory has explicit `Normal`, `Private`, and `Sensitive` classes and every query has a maximum privacy class. Scope and project keys are mandatory on persistent records. A local-only deployment uses the local store and can select a local embedding provider; external model/rerank adapters must be policy-gated by the caller.

Raw secrets must not be stored as memory content. Use existing secure-secret references instead. Source-derived memory keeps a path and content hash, while model-derived facts keep source and confidence metadata. Generated reflections are candidates, not facts, until evidence or user attribution supports them.

Forget is a retrieval guarantee, not only a UI action: deletion removes the authoritative row and all current lexical/vector/chunk paths. Memory archives/imports are data and are never executed, even if their content resembles AXL capability calls.


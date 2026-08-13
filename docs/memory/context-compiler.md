# Context Compiler

`MemoryContextCompiler` converts a retrieval result into a bounded, ordered `MemoryContextPackage`. Sections are explicit:

1. objective;
2. constraints;
3. current state;
4. prior attempts;
5. relevant knowledge with source/stale/conflict markers and evidence IDs.

It reserves output space, deduplicates repeated content, and returns the included memory/evidence IDs plus a stable SHA-256 content hash. `WithMemoryContext` integrates the package into the existing Phase 6 `ModelRequest` contract and records context identity in request metadata. Large artifacts remain references until materialization is needed.

The package also exposes a compact AXL projection of the retrieval query. AXL remains data: context compilation, export, and import never authorize or execute a capability.


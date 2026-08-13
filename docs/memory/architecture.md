# Abraxius Memory Architecture

Phase 10 is a local-first knowledge fabric, not a vector-search feature.

```text
source / execution events
          |
          v
  ingestion + promotion
      |       |       |
      v       v       v
    SQLite   FTS5   embeddings
      |       |       |
      +--- knowledge edges
              |
              v
       hybrid retriever
              |
       fusion + rerank
              |
       Context Compiler
              |
        Phase 6 model
```

`Abraxius.Memory` owns provider-neutral contracts and the local implementation. Phase 2 `MemoryWorkDescriptor` and Phase 4 runtime events remain the execution boundary. A runtime host exposes the same store through `IMemoryStore` and uses `PersistentMemoryProvider` for memory graph nodes; there is no second scheduler or agent runtime.

The first implementation uses SQLite WAL mode, FTS5, and a rebuildable persisted embedding table. Vector search is intentionally an abstraction: the current local implementation performs bounded cosine scans, which is useful for small and medium workspaces and gives us a benchmark baseline before adding a native ANN index. A future index can replace that adapter without changing memory records or retrieval contracts.

Memory records carry kind, scope, lifecycle, privacy, provenance, confidence, source hashes, timestamps, and evidence links. Derived FTS, embedding, and graph indexes are disposable; the record store and source files are authoritative.

The runtime wires the retriever into both existing memory lookup work and synthesis model work. A synthesis node receives a bounded `MemoryContextPackage` before Phase 6 inference, preserving Phase 4 graph execution and Phase 6 route/budget ownership. Execution completion also promotes a compact verified episodic record so project/runtime history survives restart.

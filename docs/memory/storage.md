# Memory Storage

The default desktop path is the platform application-data directory under `Abraxius/data/memory/knowledge.db`. Application binaries and project directories are not used as state roots. Browser/non-file targets use an in-memory store until a remote memory transport is selected.

SQLite contains:

- `memory_entries`: durable records and provenance.
- `memory_chunks`: structure-aware source/document slices.
- `memory_fts`: FTS5 lexical index for titles, content, paths, and symbols.
- `memory_embeddings`: model/version/dimension-tagged vectors.
- `knowledge_nodes` and `knowledge_edges`: typed relationships.
- `indexed_files`: content-hash checkpoints for incremental ingestion.
- `schema_meta`: additive store migration version.

Writes are serialized through a bounded gate and use transactions for record/index deletion. Startup migration is additive and repairs missing derived tables from early development databases without deleting authoritative memory. `ForgetAsync` removes the record, FTS row, chunks, embeddings, graph nodes, and incident graph edges.

# Indexing

`RepositoryIngestionService` walks an explicit project root, ignores build/vendor directories, hashes content, and skips unchanged files. It records Git branch/commit metadata when available, creates typed file/content graph nodes and `Defines` edges, and writes an indexed-file checkpoint. The watcher/reconciliation boundary is intentionally represented by `RepositoryIngestionOptions`; a future host can schedule it through Phase 4 without putting one DAG node per source line.

The current structure provider extracts common C#/Rust/TypeScript/Python/Lua symbols with a deterministic adapter and recognizes XAML `x:Class`. It is an extensible `ICodeStructureProvider`, not a claim to be a compiler for every language. Chunks are symbol-aware where symbols are available and line-bounded otherwise. Each chunk records file, symbol, language, line range, and content hash.

Embeddings use `IEmbeddingProvider`. `HashEmbeddingProvider` is a deterministic offline baseline for tests and small local installations; it is not a quality claim about semantic embeddings. A production provider can be local or routed through a configured Phase 6 embedding adapter. Embedding model and dimensions are stored with every vector so dual-index migrations can be performed safely.

# Retrieval

`HybridMemoryRetriever` runs independent lexical, symbol, graph, semantic, and recent paths concurrently. Graph retrieval searches typed nodes and follows one-hop knowledge edges, then merges candidates with rank-weighted fusion and adds explicit authority, recency, confidence, and staleness signals. Exact symbol candidates are deliberately strong: source identifiers should not be buried by vague semantic neighbors.

The retriever applies scope, project, kind, evidence, privacy, and lifecycle filters before ranking. Conflicting semantic facts sharing a `FactKey` are surfaced as `MemoryConflict`; they are not silently merged. A hit includes a score breakdown suitable for diagnostics:

```text
lex · semantic · graph · authority · recent · confidence · stale penalty
```

Simple symbol queries use the symbol/FTS fast path. Complex queries can use all paths. No retrieval path requires an LLM. Reranking is currently deterministic; a future local/cheap reranker can sit behind the same boundary.

# Retrieval evaluation

Retrieval datasets pin queries, relevant and irrelevant source identities, authority, freshness, project scope, and classification. Metrics include Recall@K, Precision@K, MRR, future nDCG, latency, stale-source rate, and relevant-context tokens divided by total context tokens.

Conflict fixtures include stale versus current facts and project-local versus global memory. The context compiler must retain citations and classification. The initial built-in validates ranking metrics and fixture semantics; full provider/index comparisons remain adapters over the same dataset contract.

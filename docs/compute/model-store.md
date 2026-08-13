# Model store

Abraxius-managed models use streamed SHA-256 content addressing, temporary staging, atomic installation, immutable manifests, and integrity verification. Backend-managed and external models are referenced in place to avoid duplicate multi-gigabyte copies.

Downloads support cancellation, range resume when advertised, disk-headroom checks, hash verification, and real byte progress. Exact upstream revisions are required for reproducibility.

# Artifact transfer

The data plane uses Phase 18 blob IDs and hashes. Transfers stream bounded chunks, verify each chunk, resume from the receiver's committed offset, and verify the final content hash before materialization. `LocalOnly` content cannot cross physical nodes.

The interface permits future direct worker-to-worker transfer. Large payloads are never one protobuf message or one managed-memory allocation.

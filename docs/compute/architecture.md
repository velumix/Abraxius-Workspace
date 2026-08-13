# Compute architecture

Phase 21 is the node-local compute authority. The control path is:

```text
Phase 6 logical model target
  → Phase 20 node placement
  → Phase 21 exact variant/backend/device plan
  → memory estimate and atomic reservation
  → residency manager
  → backend adapter
  → compute device
```

Phase 4 still schedules work, Phase 17 authorizes operations, and Phase 19 determines quality. A local offer is advisory and versioned; final admission always runs on the selected node against a current resource snapshot.

Core depends on normalized descriptors and interfaces, never Ollama, CUDA, DXGI, or MLX types. `Microsoft.Extensions.AI` chat/embedding abstractions may reduce adapter duplication, while residency, admission, device selection, and memory accounting remain Abraxius responsibilities.

Backends own inference mechanics. The governor owns shared capacity. Models and variants are revision-pinned, and no quality downgrade is implicit.

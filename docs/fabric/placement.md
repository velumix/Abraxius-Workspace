# Placement

Placement first applies hard filters: trust, Worker role, health, capability, classification, platform/architecture, sandbox, RAM/VRAM, and affinity. Only eligible nodes are scored.

The deterministic score rewards CPU/RAM headroom, exact Artifact cache hits, repository locality, suitable GPU capacity, and user preference; it penalizes latency and background work on battery. Every decision retains reasons and rejections for UI and diagnostics.

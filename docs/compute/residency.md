# Residency

Residency states are installed, loading, resident/busy/idle, evicting, and failed. Loading is serialized per exact variant. Active sessions cannot be evicted by ordinary unload. Weighted eviction considers idle time, footprint, load cost, pinning, and pressure; cooldown prevents A/B load thrashing.

Pins protect models under normal pressure but policy may override them at critical system pressure.

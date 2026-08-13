# Failure recovery

Heartbeat loss progresses through Healthy, Suspect, and Offline with hysteresis in the hosting policy. Pure work can be reassigned after lease expiry. Side-effecting work enters reconciliation. Reconnecting workers report leases and result hashes; stale coordinator epochs cannot commit.

Automatic multi-master election is intentionally absent. Phase 14 state is the recovery authority for coordinator restart.

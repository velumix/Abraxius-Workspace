# Execution leases

An `ExecutionLease` pins execution, graph node, mission, coordinator epoch, worker, attempt, idempotency key, capability, operation, authorization, TTL, reservation, and trace parent. Duplicate delivery shares one logical worker execution by idempotency key. Long work is bounded by lease lifetime and cancellation.

Node drain rejects new leases while allowing explicit handling of active work.

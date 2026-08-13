# Delivery semantics

Network delivery may duplicate. Execution attempts may duplicate. A side effect can be ambiguous after disconnect. Fabric therefore does not claim network-level exactly-once execution.

Pure work may be retried under policy. Canonical logical results commit once per `ExecutionId`; late attempts are stale. External mutations are not blindly retried or hedged and require authoritative-state reconciliation first.

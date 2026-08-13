# WASI

WASI is an experimental optional tier. The intended default guest has no filesystem, network, environment-secret, or process access. Phase 17 grants map explicitly to capability-oriented host functions and bounded resources. Core plugin contracts do not depend on a Wasmtime runtime; production enablement requires passing isolation and compatibility evals.

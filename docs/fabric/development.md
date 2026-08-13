# Development and test harness

`InMemoryFabricTransport` provides deterministic multi-node and chaos semantics without opening a network. `Abraxius.Node` is the headless HTTP/2/TLS worker host and refuses startup without explicit Fabric/node identity, certificate, and pinned coordinator fingerprint.

An Aspire AppHost may later orchestrate local coordinator/worker processes, but Aspire is not a shipping runtime dependency.

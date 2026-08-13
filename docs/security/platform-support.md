# Platform support and guarantees

Shared guarantees are deterministic policy, default deny, resource canonicalization, scoped grants, secret references, audit, and sandbox minimum enforcement.

Desktop workspace isolation is implemented. Linux/macOS/Windows restricted-process and secure credential-store adapters are not yet complete, so Abraxius does not advertise OS sandbox guarantees there. Browser/mobile hosts must use their platform secure storage and lifecycle restrictions. Remote-node identity and verifiable grant fields are prepared but distributed enforcement is deferred.

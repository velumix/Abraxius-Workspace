# Fabric security

Both coordinator and worker enforce Phase 17. A lease carries decision/grant identifiers, mission subject, exact capabilities, resource prefixes, constraints, expiry, and classification—never secret values. Workers reject stale coordinator epochs, invalid envelopes, wrong node identity, revoked trust, unavailable capacity, and prohibited classifications.

`LocalOnly` is bound to the originating physical node. Discovery is not trust. Authentication is not authorization. Trace baggage must not contain credentials or source payloads.

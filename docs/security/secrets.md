# Secret broker

Callers use opaque `secret://provider/name` references. `ISecretBroker.UseAsync` injects material only inside an already-authorized transport callback. There is no model-facing raw getter. Metadata and audit contain the reference, destination, principal, mission, and time—never the value.

Raw secret extraction is denied by built-in global policy. The in-memory store is intended for runtime/tests and zeroes buffers on removal/disposal. The environment adapter is explicit development configuration. Production platform stores (Windows credential storage, macOS Keychain, Linux Secret Service, and mobile secure storage) remain adapter work; plaintext SQLite storage is forbidden.

Configured Phase 6 gateway credentials are represented as `secret://model/<gateway>` references. A brokered HTTP authentication handler obtains the credential only for the authorized request callback, removes the header from the request object afterward, and records secret use by reference. The runtime issues this transport identity a session-scoped grant only when that gateway and environment-variable mapping were explicitly configured.

Configured Phase 7 cloud speech credentials use the same boundary as `secret://voice/deepgram` and `secret://voice/elevenlabs`. Voice providers receive a credential callback, not a retained API-key field; the callback authenticates the WebSocket/HTTP transport and the broker records use by opaque reference.

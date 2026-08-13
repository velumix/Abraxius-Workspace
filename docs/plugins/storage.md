# Storage

Plugin storage is namespaced by `PluginId`, quota-bound, and separated into settings, durable state, blobs, and cache. Cross-plugin reads are denied by construction. State migrations should operate on a copy or transaction and retain the last known-good schema/version until activation succeeds.

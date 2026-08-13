# Backend contracts

`ILocalInferenceBackend` provides discovered capabilities, model inventory, residency, load/unload, streaming inference, and optional `IChatClient`. Capabilities are probed, not inferred from a product name.

Unavailable platform adapters report `Unavailable`; they do not fabricate devices or performance. Managed servers must bind to loopback and be supervised outside the core process.

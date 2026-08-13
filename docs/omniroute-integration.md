# OmniRoute integration

OmniRoute is treated as Abraxius's primary free/quota-aware peer fabric. The adapter speaks its
OpenAI-compatible `/v1/chat/completions` endpoint and supports:

* configurable local or remote endpoint;
* named environment-variable authentication;
* streaming SSE;
* structured JSON response requests;
* tool schema emission and structured tool-call parsing;
* cancellation, timeout, health probing, and model discovery;
* route names such as `auto/coding:free`, when the configured OmniRoute version advertises them.

The route engine does not assume any particular free-token pool. OmniRoute's live quota, account,
provider, and model state is discovered at runtime when the gateway is enabled. Discovered models
with unknown cost are retained for diagnostics but are not silently treated as free.

The upstream documentation currently describes `auto`, `auto/coding`, `auto/fast`, `auto/cheap`,
`auto/offline`, and `auto/smart`, plus category/tier variants. Abraxius passes configured route
strings through the adapter and does not hard-code their availability. See the [OmniRoute
repository](https://github.com/diegosouzapw/OmniRoute) and its [auto-combo
documentation](https://github.com/diegosouzapw/OmniRoute/blob/release/v3.8.49/docs/routing/AUTO-COMBO.md).

No OmniRoute service was installed or running on the Phase 6 validation machine. Consequently,
the repository's passing tests use `FakeOmniRouteModelProvider`, and no real provider or paid
request was made. Enable a pinned, locally managed OmniRoute deployment explicitly before doing a
live smoke test.

Example configuration shape:

```json
"Intelligence": {
  "OmniRoute": {
    "Enabled": true,
    "Endpoint": "http://localhost:20128/v1/chat/completions",
    "DefaultModel": "auto/coding:free",
    "ApiKeyEnvironmentVariable": "OMNIROUTE_API_KEY"
  },
  "Models": [
    {
      "ModelId": "free-coding-route",
      "Provider": "omniroute",
      "Gateway": "OmniRoute",
      "Route": "auto/coding:free",
      "Tier": "Free",
      "CostClass": "Zero",
      "Coding": true,
      "ToolCalling": true,
      "StructuredOutput": true,
      "ContextWindow": 128000
    }
  ]
}
```

The model catalog is intentionally explicit: it prevents a gateway's opaque `auto` fallback
chain from being mistaken for a known zero-cost entitlement.

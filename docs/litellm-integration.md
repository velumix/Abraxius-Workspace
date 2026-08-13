# LiteLLM integration

LiteLLM is treated as a peer normalization and controlled fallback fabric. It is appropriate for
configured low-cost groups, standard model groups, provider normalization, and frontier adapters.
The Abraxius adapter uses the same OpenAI-compatible chat-completions boundary but keeps LiteLLM
route ownership separate from OmniRoute route ownership.

The upstream LiteLLM documentation describes a common OpenAI input/output format, routing across
deployments, retries/fallbacks, rate limiting, spend tracking, and proxy virtual keys. Those
gateway-level controls remain inside LiteLLM when LiteLLM owns a request. Abraxius still owns the
mission/task tier, privacy, maximum spend, and escalation decision.

See the [LiteLLM getting started documentation](https://docs.litellm.ai/) and [routing
documentation](https://docs.litellm.ai/docs/routing). The repository does not embed Python or
depend on the LiteLLM SDK; it communicates through the configured HTTP gateway.

The LiteLLM gateway is disabled by default and was not installed on the validation machine. No
live LiteLLM request was made. Set a pinned deployment endpoint and explicitly classify its model
groups in the `Intelligence.Models` catalog. Never use a floating `latest` deployment tag for a
reproducible environment; record the exact gateway image/release in deployment configuration
outside the core solution.

Optional `MaxConcurrentRequests`, RPM, and TPM headroom metadata can be attached to a catalog
entry. The routed provider enforces the concurrency ceiling locally while LiteLLM remains the
owner of provider deployment health, retries, and provider-specific budget enforcement.

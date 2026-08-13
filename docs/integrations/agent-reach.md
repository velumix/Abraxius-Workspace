# Agent Reach in Abraxius Chat

Abraxius exposes Agent Reach as the read-only `agent-reach.web` Lattice capability.
The Chat surface invokes it only when the user explicitly includes an `http` or
`https` URL in a message. Ordinary conversation does not browse automatically.

The call path is:

```text
Chat
  -> AbraxiusRuntimeHost.ReadPublicWebAsync
  -> LatticeExecutor
  -> SecurityLatticePolicy
  -> AgentReachWebCapability
  -> Agent Reach-compatible Jina Reader route
  -> bounded web content + Phase 18-style Evidence
```

The capability is deliberately not a shell bridge. It supports only `read`,
does not accept credentials, does not follow redirects, rejects local/private/
metadata-network targets, and limits the response to 1 MiB. Chat limits the
material inserted into a model request to 120,000 characters. Web content is
marked as untrusted reference data so instructions in a page do not gain
Abraxius authority.

The local Agent Reach CLI remains useful for backend health and setup:

```text
agent-reach doctor --json
agent-reach watch
```

Additional social/search backends remain optional. This integration does not
pretend that a missing backend is available and does not silently fall back to
the old offline/demo response path.

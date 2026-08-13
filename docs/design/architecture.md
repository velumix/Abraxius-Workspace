# Abraxius Design Studio

Design Studio is a provider-neutral workflow layered over the existing runtime:

```text
Chat/Athena intent
  -> DesignOrchestrator
  -> surface capture + source snapshot
  -> DesignContextCompiler
  -> Phase 17 egress decision
  -> IDesignGenerationProvider
  -> Phase 18 Design artifacts
  -> user selection/refinement
  -> existing Phase 11 implementation mission
  -> Argus/Phase 19 verification
```

The domain lives in `Abraxius.Design`. Google Stitch is an adapter in
`Abraxius.Design.Google`; provider-specific MCP JSON does not cross the domain
boundary. A provider can produce reference markup and images, but it cannot
write the repository. Daedalus implements the selected intent in the existing
Avalonia architecture.

Design identities are immutable and explicit: request, session, generation,
candidate, surface, project, and source snapshot. Candidates are tied to the
source hashes that produced them and persisted as `ArtifactKind.Design`.

The current UI exposes Design Studio from the navigation rail and command
palette. Chat requests containing an explicit redesign intent route to the
same orchestrator rather than calling the model directly.

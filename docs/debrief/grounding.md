# Grounding and citations

The grounding pipeline is:

```text
source resolution → claims → dialogue claim IDs → deterministic validation → TTS
```

Every factual turn should carry claim IDs. Every speakable claim must have evidence when `DebriefOptions.RequireEvidence` is enabled. Unsupported, stale, conflicted, or missing-evidence claims are rejected before speech. Inference may be retained only when the claim is explicitly marked inferred by the planner.

The visual transcript exposes the turn's `e:`/`m:` source references. The source snapshot stores the source-set identity, content hash, resolved memory IDs, resolved evidence IDs, and source-content hashes where available. A later source change does not rewrite an old episode; it makes the older snapshot auditable as historical.

Source text is untrusted data. It is never treated as runtime instructions, and a Debrief cannot execute a capability. A request to change a workspace is redirected to an ordinary Phase 11 mission subject to policy.

Generated Debrief text is never promoted as proof of the source facts it discusses. Any useful new conclusion must pass through the normal Phase 10 memory-candidate and provenance pipeline.


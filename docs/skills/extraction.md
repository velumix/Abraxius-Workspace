# Skill candidate extraction

After a successful AgentKernel mission, the Runtime host performs best-effort, asynchronous candidate extraction from structured mission data:

```text
MissionResult + assignment outcomes + evidence + verification criteria
                         │
                         ▼
             DeterministicSkillCandidateExtractor
                         │
                         ▼
                 disabled Candidate definition
```

The extractor requires evidence and verification criteria, and normally requires multiple successful steps. It uses assignment outcome summaries—not Debrief dialogue or hidden reasoning—and records mission/evidence provenance. The candidate is added to the candidate store and registry as disabled `Candidate` data. It never delays the user result, self-promotes, or grants capabilities.

The current extractor is deliberately conservative. Model-assisted abstraction and replay across multiple environments are extension points, not implicit trust mechanisms. Failed procedures remain available as execution diagnostics and can later support anti-pattern extraction.


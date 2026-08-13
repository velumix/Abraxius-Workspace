# Episode planning

Planning precedes dialogue. A request resolves a bounded source set, captures a content hash, classifies claims, chooses participants, and builds a chapter list with dependencies and duration budgets.

Supported modes are `Briefing`, `DeepDive`, `Postmortem`, `ArchitectureReview`, `Debate`, `TeachMe`, `ReleaseOverview`, and `MissionReplay`. The current deterministic planner provides the first four chapter templates and a conservative generic template for the remaining modes.

Claims are created from retrieved evidence. Source-code, tool-result, verified-execution, and user-originated evidence are supported by default; model-derived material is marked inferred. Stale and conflicted material is retained as metadata but cannot be presented as an unqualified supported fact.

The playback engine composes and verifies one chapter at a time. This gives time-to-first-dialogue/audio before the rest of an episode exists. The current implementation is chapter-incremental rather than a full speculative prefetch DAG; generation and playback are bounded by session turn and cache limits.


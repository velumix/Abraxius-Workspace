# Debrief architecture

Debrief is the source-grounded spoken explanation surface for Abraxius. It is a derived view over existing project, mission, memory, evidence, specialist, model, and voice services.

```text
DebriefSourceSet
       ↓
Memory/RAG source resolver
       ↓
SourceSnapshot + evidence
       ↓
EpisodePlan
       ↓
claim-grounded dialogue turns
       ↓
Argus-style deterministic grounding gate
       ↓
Phase 7 TTS / playback
       ↓
Avalonia player or CLI export
```

`DebriefSession` owns resumable presentation state. `EpisodePlan` owns the immutable planning snapshot. `DialogueTurn` carries claim and evidence references. Generated dialogue is derived content and is never authoritative evidence.

The first implementation uses a deterministic planner and composer so offline operation is predictable. `Phase6DebriefDialogueComposer` is an optional model adapter; it only accepts model turns that identify known, speakable, evidence-backed claims and otherwise falls back to deterministic dialogue.

The runtime continues to use Phase 4 for surrounding execution, Phase 6 for model requests, Phase 7 for TTS/playback, Phase 9 AXL for diagnostic projection, Phase 10 for retrieval, and Phase 11 AgentKernel for eligible live read-only specialist answers.


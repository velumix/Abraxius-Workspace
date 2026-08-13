# Debrief UI

The Avalonia workstation exposes a DEBRIEF rail destination. It observes `DebriefEventHub` and renders:

- title, mode, source snapshot, status, and active speaker;
- chapter readiness and grounded transcript turns;
- source references attached to each turn;
- create, pause, resume, and live-question controls.

The UI does not plan dialogue, retrieve memory, call models, synthesize speech, or encode audio. Those operations remain in `Abraxius.Debrief` and Phase 7 services. This keeps the shell responsive and makes the headless/CLI path use the same engine.

The first surface deliberately avoids decorative fake waveforms and heavy avatar animation. A later player can add actual waveform/timeline rendering from cached audio duration and turn events without changing the grounding contracts.

CLI:

```text
abraxius debrief create <objective> [--mode Postmortem]
abraxius debrief list
abraxius debrief inspect <id>
abraxius debrief export <id> [path]
```


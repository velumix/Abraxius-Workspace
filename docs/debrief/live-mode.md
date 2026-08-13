# Live mode

Playback interruption uses the same generation/cancellation boundary as Phase 7 voice. `AskAsync` cancels current playback, marks the session interrupted, resolves the requested specialist role, and returns a typed `DialogueTurn` with current-session evidence references where available.

The current AgentKernel bridge is read-only for live Debrief questions. Coordinator, investigator, and verifier questions may use the configured AgentKernel; builder action requests are explicitly refused as direct Debrief mutations and must be turned into a normal mission. This keeps spoken explanation separate from execution authority.

After an answer, the session can resume from its logical chapter position. A generation ID prevents late playback from a cancelled or replaced generation from being treated as current audio. Future work can add chapter editing and seek-prioritized generation without changing the contract.


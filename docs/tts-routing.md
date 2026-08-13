# TTS routing

`ITextToSpeechProvider` yields normalized audio frames. Text is segmented at sentence/clause boundaries instead of sending one request per token. The first completed segment can enter TTS while later model output is still arriving.

The implemented realtime adapter targets ElevenLabs streaming WebSocket semantics and requests PCM output where supported. Local seams represent Kokoro and Chatterbox; they throw `SidecarUnavailable` until an explicitly installed runtime is supervised by a host adapter. `InMemoryTextToSpeechProvider` exists only for deterministic tests and never drives the product UI.

Playback receives a `VoiceGenerationId`. The orchestrator cancels the generation on barge-in, stops playback, increments the generation gate, and verifies the generation before each segment. A late provider result therefore cannot be spoken after an interruption.

TTS route modes are `Quality`, `BalancedQuality`, `LocalFirst`, `Private`, and `Manual`. Speech quality and latency are separate from Phase 6 LLM cost routing; STT and TTS may select different providers.

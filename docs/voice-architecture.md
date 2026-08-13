# Abraxius voice architecture

Phase 7 adds a dedicated `Abraxius.Voice` project. It is a platform-neutral streaming subsystem and does not depend on Avalonia, a provider SDK, or a desktop shell.

```text
platform audio adapter
        │ normalized PCM16 frames
        ▼
bounded capture / VAD / pre-roll
        │ utterance channel
        ├──────────────► streaming STT ──► partial/final transcript
        │                                      │
        │                                      ▼
        │                           RuntimeVoiceIntentSink
        │                                      │
        │                                      ▼
        │                             Phase 4 scheduler
        │
        └─ while speaking: VAD remains active and can barge in

Phase 6 model stream or scheduler response
        ▼
speech segmenter
        ▼
streaming TTS
        ▼
generation-checked playback adapter
```

Control events (`SpeechDetected`, `TranscriptFinalized`, `VoiceInterrupted`, playback state) use `VoiceEventHub`'s reliable bounded channel. Audio meters use a separate lossy telemetry channel. Raw PCM frames are never runtime ledger events and are not persisted by default.

`VoiceGenerationId` and `AudioGenerationGate` ensure that a cancelled response cannot enqueue or play stale audio after a new response begins. Barge-in cancels the active TTS generation and calls the playback adapter immediately while capture and VAD continue.

The runtime bridge is `RuntimeVoiceIntentSink`. It creates the same `Intent` used by typed commands, so voice does not create a second agent or scheduler. The response generator can also call the existing `IModelProvider` stream directly for conversational first-audio latency.

## Provider boundary

STT and TTS are independent. The project contains protocol-level adapters for ElevenLabs realtime STT/TTS, Deepgram realtime STT, OpenAI-compatible transcription, plus explicit local-runtime seams for sherpa-onnx, whisper.cpp, Kokoro, and Chatterbox. Local model engines are intentionally not embedded into the C# process until a native package/sidecar is installed and validated.

The default factory builds a route engine over the configured cloud adapters plus local sherpa/Kokoro seams. The route engine applies the active mode, language, privacy, capability, health, and quality policy at session time. A transient STT failure replays a bounded audio window into the next eligible provider; TTS only fails over before a segment emits audio. No credentials are logged.

## Threading and lifecycle

Capture/VAD, utterance processing, model streaming, TTS, and playback are asynchronous and cancellation-aware. The Avalonia UI only consumes high-level voice events through a coalesced observer. UI disposal cancels voice sessions before disposing the voice event hub.

## Safety

Final transcript submission is the default authority for mission execution. Partial transcripts are presentation data. Private mode is enforced in `SpeechRouteEngine`: cloud providers are rejected before scoring. Provider/model names and voice IDs remain adapter/configuration concerns. `VoiceSettings` provides the user-facing mode, route, device, preprocessing, wake-word, barge-in, and privacy settings without leaking provider-specific options into Core.

# Audio pipeline

Debrief uses `ITextToSpeechProvider` and `IAudioPlaybackService` from Phase 7. It does not contain provider SDK integrations. `DebriefRequest` carries provider-neutral role voice preferences; defaults are `athena`, `orion`, `daedalus`, and `argus`, while the configured Phase 7 route decides the actual provider voice.

Speech is generated per turn and cached by canonical spoken text, role voice, language, and privacy mode. The cache is bounded and holds normalized PCM frames. Playback can start after the first verified turn; no complete episode audio file is required. `GenerateAudio=false` provides a text-only/headless path.

Private mode routes speech as `SpeechRoutingMode.Private` and marks the speech context private. Provider availability and local model/TTS installation remain deployment concerns: the runtime does not claim a local provider is installed merely because the interface exists.

Raw microphone input is not stored by Debrief. Saved/exported audio is explicit user data; temporary audio is cache data and can be cleared independently.

The current export implementation writes a normalized PCM16 WAV from cached turn segments. Export is explicit and fails clearly if the episode has not produced cached audio. Higher-compression formats remain a platform/codec decision rather than a Core codec implementation.

# Audio pipeline

The normalized internal format is PCM16, mono, 16 kHz (`AudioFormat.NormalizedSpeech`). Adapters may capture or synthesize another format only behind their boundary; the playback/capture host is responsible for an explicit conversion adapter when needed.

Audio flow uses bounded stages:

```text
capture → preprocessing → VAD/pre-roll/post-roll → bounded utterance channel → STT
TTS → bounded generation playback → output device
```

`AudioFrame` carries format, sequence, timestamp, and read-only payload. The current managed pipeline avoids unbounded queues and keeps audio data separate from control-plane events.

`EnergyVoiceActivityDetector` uses RMS level, start/end hysteresis, and explicit `PossibleSpeech`/`PossibleEnd` states. `PreRollAudioBuffer` retains the frames immediately before speech-start detection so initial phonemes are not discarded. `PostRollFrames` delays utterance submission after end detection so final phonemes are retained. An explicit noisy-environment profile can apply the managed PCM noise gate; platform AEC/NS implementations remain injectable through the preprocessor contracts.

The Linux desktop host includes a direct, no-shell PulseAudio adapter using `parec` and `pacat`. Other platforms must provide an `IAudioCaptureService`/`IAudioPlaybackService` adapter; absence is reported as a structured speech error rather than simulated UI activity.

Echo cancellation, noise suppression, device hot-plug, Bluetooth routing, and browser/mobile native permission implementations remain host adapter responsibilities. The contracts expose the required seams without making desktop assumptions part of `Abraxius.Voice`.

# STT routing

`ISpeechToTextProvider` produces typed session, partial, final, language, timing, provider-change, error, and completion events. `SpeechRouteEngine` filters before scoring:

- required streaming/partial/language/keyterm capabilities;
- local/private policy;
- language advertisement;
- provider health and rate-limit state;
- zero-cost policy where configured.

The score is intentionally inspectable rather than a hidden model. Quality, local preference, expressive/noise capabilities, health, and cost class contribute independently. A cloud provider is never considered for `RequireLocal` or private mode.

Implemented adapter boundaries:

- ElevenLabs realtime Scribe-style WebSocket JSON/audio route;
- Deepgram streaming WebSocket binary PCM route and `Results` parsing;
- OpenAI-compatible `/audio/transcriptions` multipart fallback for non-streaming environments;
- local sherpa-onnx and whisper.cpp seams with honest unavailable status until a native runtime or supervised sidecar is installed.

Current development validation uses deterministic providers only. No live STT provider was called during repository tests.

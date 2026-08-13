# Voice platform support matrix

| Platform | Capture/playback boundary | Local VAD | Local STT/TTS | Cloud speech | Status |
|---|---|---:|---:|---:|---|
| Linux desktop | PulseAudio adapter (`parec`/`pacat`) | Managed | Adapter seams | Configured WebSocket adapters | Compiled; host backend present; physical mic not tested |
| Windows | Platform host contract | Managed | Adapter seams | Configured adapters | Architected; native backend not implemented on this host |
| macOS | Platform host contract | Managed | Adapter seams | Configured adapters | Architected; native backend not implemented on this host |
| Android | Permission/audio host contract | Managed/local adapter seam | sherpa/sidecar seam | Remote/cloud route | Architected; mobile workload unavailable on current host |
| iOS | Permission/audio host contract | Managed/local adapter seam | sherpa/sidecar seam | Remote/cloud route | Architected; iOS workload unavailable on current host |
| Browser/WASM | Browser audio host contract | Managed | Cloud/remote preferred | Configured adapters | Architected; browser audio backend not runtime-tested |
| Embedded Linux | Host audio contract | Managed | Lightweight local seam | Optional remote | Architected; embedded target not available |

No raw microphone audio is persisted by the voice subsystem by default. Private routing rejects cloud speech before provider selection.

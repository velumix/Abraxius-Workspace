# Ollama

The initial Ollama adapter uses loopback HTTP only. It normalizes installed models, running residency, size-in-VRAM, context, quantization, streaming output, keep-alive load/unload, and native timing fields. User-managed model files are referenced rather than copied.

Ollama remains a backend. Abraxius performs admission before requests enter its internal queue.

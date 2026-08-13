# llama.cpp

The provider boundary targets a supervised `llama-server` sidecar for native crash/version isolation. Expected capabilities include GGUF, CPU/GPU hybrid execution, and CUDA/HIP/Metal/Vulkan/SYCL depending on the actual build.

The sidecar is not installed by the baseline implementation. llama.cpp RPC is explicitly not used as the Abraxius Fabric transport.

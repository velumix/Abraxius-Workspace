# Devices

`ComputeDevice` represents CPU, GPU, NPU, or accelerator with stable identity where available. Memory architecture is explicit: `Dedicated`, `Shared`, or `Unified`. Unknown telemetry remains null.

The baseline discovers CPU and Linux DRM devices. NVML, AMD SMI, Intel Level Zero Sysman, and DXGI are normalized provider boundaries; native probes can be registered without changing the domain. Effective memory budgets, not marketing capacity, drive admission.

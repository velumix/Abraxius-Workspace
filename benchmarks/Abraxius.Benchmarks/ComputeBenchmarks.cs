using System.Collections.Immutable;
using Abraxius.Compute;
using Abraxius.Models;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class ComputeBenchmarks
{
    private readonly CalibratingModelMemoryEstimator _estimator = new();
    private readonly ModelVariantDescriptor _variant = new(new("qwen/rev/q4"), new("qwen"), new("rev"), "Qwen", "gguf", "Q4_K_M", 7_000_000_000, "qwen", 65536, 4L << 30,
        ImmutableDictionary<string, string>.Empty, [ModelCapabilityKind.Chat, ModelCapabilityKind.Code], [ModelCapabilityKind.Chat], [new BackendId("llama.cpp")], new("test", null, true, LicenseAcceptance.NotRequired), new("fixture", "benchmark", "rev", DataClassification.LocalOnly), ModelValidationState.LoadValidated, ModelStorageKind.External, "fixture", 32, 4096);
    private readonly InferenceBackendDescriptor _backend = new(new("llama.cpp"), "benchmark", ["gguf"], [ComputeDeviceClass.Gpu], true, true, true, false, false, false, true, true, ["Q4_K_M"], BackendHealth.Healthy);
    private readonly ImmutableArray<ComputeDevice> _devices = [new(new("gpu"), "test", "GPU", ComputeDeviceClass.Gpu, "test", ["llama.cpp"], ComputeMemoryArchitecture.Dedicated, 16L << 30, null, [], null, TelemetryCapability.Partial, "gpu")];
    private readonly BoundedFairInferenceBuffer _queue = new(1024, 8);
    private readonly CpuComputeDeviceProvider _cpu = new();

    [Benchmark]
    public ModelMemoryEstimate MemoryEstimate32k() => _estimator.Estimate(_variant, _backend, 32768, 1, _devices);

    [Benchmark]
    public bool QueueRoundTrip()
    {
        var request = new LocalInferenceRequest(new("qwen"), [], 4096, 128, InferencePriority.InteractiveMission, true, false, DataClassification.LocalOnly, "benchmark");
        return _queue.TryEnqueue(request) && _queue.TryDequeue() is not null;
    }

    [Benchmark]
    public async ValueTask<ImmutableArray<ComputeDevice>> CpuDiscovery() => await _cpu.DiscoverAsync();
}

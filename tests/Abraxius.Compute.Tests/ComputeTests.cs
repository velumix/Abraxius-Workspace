using System.Collections.Immutable;
using Abraxius.Compute;
using Abraxius.Models;
using Xunit;

namespace Abraxius.Compute.Tests;

public sealed class ComputeTests
{
    [Fact]
    public async Task InventoryNormalizesCpuGpuAndNpuAsDistinctResources()
    {
        var inventory = new CompositeComputeDeviceInventory([new FixedProvider(Device("cpu", ComputeDeviceClass.Cpu), Device("gpu", ComputeDeviceClass.Gpu), Device("npu", ComputeDeviceClass.Npu))]);
        var devices = await inventory.RefreshAsync();
        Assert.Equal(3, devices.Length); Assert.Contains(devices, value => value.DeviceClass == ComputeDeviceClass.Npu); Assert.Equal(3, devices.Select(value => value.Id).Distinct().Count());
    }

    [Fact]
    public async Task VendorTelemetryMapsActualValuesAndKeepsUnknownUnknown()
    {
        var gpu = Device("gpu", ComputeDeviceClass.Gpu, "AMD") with { StableHardwareIdentity = "pci:1", Telemetry = TelemetryCapability.Partial };
        var provider = new AmdSmiTelemetryProvider(new StaticVendorTelemetryProbe("AMD", new VendorTelemetryReading("pci:1", 7, 10, .5, 70, null)));
        var state = Assert.Single(await provider.ReadAsync([gpu]));
        Assert.Equal(7, state.MemoryUsedBytes); Assert.Equal(.5, state.Utilization); Assert.Null(state.PowerWatts); Assert.Equal(ResourcePressure.Elevated, state.Pressure);
    }

    [Fact]
    public void MemoryEstimatorAccountsForContextConcurrencyAndHeadroom()
    {
        var estimator = new CalibratingModelMemoryEstimator(); var variant = Variant(fileSize: 4L << 30); var backend = Backend(); var devices = ImmutableArray.Create(Device("gpu", ComputeDeviceClass.Gpu));
        var small = estimator.Estimate(variant, backend, 4096, 1, devices); var large = estimator.Estimate(variant, backend, 32768, 4, devices);
        Assert.True(large.KvCacheBytes > small.KvCacheBytes * 20); Assert.True(large.TotalDeviceReservation > large.WeightsBytes); Assert.True(large.HeadroomBytes > 0);
    }

    [Fact]
    public async Task GovernorRejectsOvercommitAndRollsNoPartialMultiGpuState()
    {
        var governor = new ComputeResourceGovernor(new(MinimumRamHeadroomBytes: 0, MinimumDeviceHeadroomBytes: 0, DeviceHeadroomFraction: 0)); var one = new ComputeDeviceId("one"); var two = new ComputeDeviceId("two");
        var snapshot = new ComputeResourceSnapshot(DateTimeOffset.UtcNow, null, 100, 100, ResourcePressure.Normal, [new(one, 0, 100, null, null, null, ResourcePressure.Normal, DateTimeOffset.UtcNow), new(two, 90, 100, null, null, null, ResourcePressure.High, DateTimeOffset.UtcNow)], 0, TimeSpan.Zero);
        var request = new ResourceReservationRequest(ComputeWorkloadId.New(), InferencePriority.NormalMission, [one, two], 1, ImmutableDictionary<ComputeDeviceId, long>.Empty.Add(one, 10).Add(two, 20), 1, TimeSpan.FromMinutes(1), false, "atomic");
        Assert.Equal(ReservationState.Rejected, (await governor.ReserveAsync(request, snapshot)).State); Assert.DoesNotContain(governor.Reservations, value => value.State == ReservationState.Granted);
    }

    [Fact]
    public async Task EffectiveDeviceBudgetWinsOverPhysicalCapacity()
    {
        var governor = new ComputeResourceGovernor(new(MinimumRamHeadroomBytes: 0, MinimumDeviceHeadroomBytes: 0, DeviceHeadroomFraction: 0)); var id = new ComputeDeviceId("dxgi");
        var snapshot = new ComputeResourceSnapshot(DateTimeOffset.UtcNow, null, 1L << 30, 1L << 30, ResourcePressure.Normal, [new(id, 0, 2L << 30, null, null, null, ResourcePressure.Normal, DateTimeOffset.UtcNow)], 0, TimeSpan.Zero);
        var request = new ResourceReservationRequest(ComputeWorkloadId.New(), InferencePriority.InteractiveUser, [id], 1, ImmutableDictionary<ComputeDeviceId, long>.Empty.Add(id, 3L << 30), 1, TimeSpan.FromMinutes(1), false, "budget");
        Assert.Equal(ReservationState.Rejected, (await governor.ReserveAsync(request, snapshot)).State);
    }

    [Fact]
    public void InferenceQueueIsBoundedAndAllowsBackgroundProgress()
    {
        var queue = new BoundedFairInferenceBuffer(4, 2); var background = Request(InferencePriority.Background); Assert.True(queue.TryEnqueue(background)); Assert.True(queue.TryEnqueue(Request(InferencePriority.InteractiveUser))); Assert.True(queue.TryEnqueue(Request(InferencePriority.InteractiveUser))); Assert.True(queue.TryEnqueue(Request(InferencePriority.InteractiveUser))); Assert.False(queue.TryEnqueue(Request(InferencePriority.RealtimeVoice)));
        Assert.Equal(InferencePriority.InteractiveUser, queue.TryDequeue()!.Priority); Assert.Equal(InferencePriority.InteractiveUser, queue.TryDequeue()!.Priority); Assert.Equal(InferencePriority.Background, queue.TryDequeue()!.Priority);
    }

    [Fact]
    public async Task ModelStoreStreamsHashesAndDetectsTampering()
    {
        var root = Temp(); using var store = new ContentAddressedModelStore(root);
        try
        {
            await using var content = new MemoryStream("GGUFpayload"u8.ToArray()); var model = await store.ImportAsync(content, "model.gguf", Metadata());
            Assert.True(await store.VerifyAsync(model.Id)); await File.AppendAllTextAsync(model.StorageReference, "tamper"); Assert.False(await store.VerifyAsync(model.Id));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LicenseAcceptanceBlocksImport()
    {
        var root = Temp(); using var store = new ContentAddressedModelStore(root);
        try { await using var content = new MemoryStream("GGUFpayload"u8.ToArray()); await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.ImportAsync(content, "model.gguf", Metadata() with { License = new("restricted", new("https://example.invalid/terms"), false, LicenseAcceptance.Required) })); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void OllamaRejectsPublicBinding()
    {
        using var http = new HttpClient(); Assert.Throws<ArgumentException>(() => new OllamaBackend(http, new("http://0.0.0.0:11434"))); Assert.Throws<ArgumentException>(() => new OllamaBackend(http, new("http://192.168.1.4:11434")));
    }

    [Fact]
    public void UnifiedMemoryIsRepresentedOnceRatherThanAsFakeDedicatedVram()
    {
        var unified = Device("apple", ComputeDeviceClass.Gpu) with { MemoryArchitecture = ComputeMemoryArchitecture.Unified, DedicatedMemoryBytes = null, SharedMemoryBytes = 32L << 30 };
        Assert.Null(unified.DedicatedMemoryBytes); Assert.Equal(32L << 30, unified.SharedMemoryBytes); Assert.Equal(ComputeMemoryArchitecture.Unified, unified.MemoryArchitecture);
    }

    [Fact]
    public void BackendBoundariesReportUnavailableInsteadOfFabricatingSupport()
    {
        Assert.Equal(BackendHealth.Unavailable, BackendBoundaries.WindowsMl().Descriptor.Health); Assert.Equal(BackendHealth.Unavailable, BackendBoundaries.Mlx().Descriptor.Health); Assert.Equal(BackendHealth.Unavailable, BackendBoundaries.OnnxExperimental().Descriptor.Health);
    }

    private static LocalInferenceRequest Request(InferencePriority priority) => new(new("model"), [], 1024, 16, priority, true, false, DataClassification.LocalOnly, "test");
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "abraxius-compute-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static ModelImportMetadata Metadata() => new(new("model"), new("revision"), "Q4", "test", 4096, new("MIT", null, true, LicenseAcceptance.NotRequired), new("test", "fixture", "revision", DataClassification.LocalOnly), [new BackendId("llama.cpp")]);
    private static ComputeDevice Device(string id, ComputeDeviceClass kind, string vendor = "Test") => new(new(id), vendor, id, kind, "test", ["test"], kind == ComputeDeviceClass.Cpu ? ComputeMemoryArchitecture.Shared : ComputeMemoryArchitecture.Dedicated, kind == ComputeDeviceClass.Cpu ? null : 24L << 30, kind == ComputeDeviceClass.Cpu ? 64L << 30 : null, [], null, TelemetryCapability.Partial, id);
    private static InferenceBackendDescriptor Backend() => new(new("test"), "1", ["gguf"], [ComputeDeviceClass.Gpu], true, false, false, false, false, false, false, false, [], BackendHealth.Healthy);
    private static ModelVariantDescriptor Variant(long fileSize) => new(new("model/rev/q4"), new("model"), new("rev"), "Model", "gguf", "Q4", 7_000_000_000, "test", 65536, fileSize, ImmutableDictionary<string, string>.Empty, [ModelCapabilityKind.Chat], [ModelCapabilityKind.Chat], [new BackendId("test")], new("MIT", null, true, LicenseAcceptance.NotRequired), new("test", "fixture", "rev", DataClassification.LocalOnly), ModelValidationState.LoadValidated, ModelStorageKind.External, "fixture", 32, 4096);
    private sealed class FixedProvider(params ComputeDevice[] devices) : IComputeDeviceProvider { public ValueTask<ImmutableArray<ComputeDevice>> DiscoverAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(devices.ToImmutableArray()); }
}

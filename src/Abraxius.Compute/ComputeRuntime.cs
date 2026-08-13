namespace Abraxius.Compute;

public sealed class ComputeRuntime : IAsyncDisposable
{
    private readonly HttpClient _ollamaHttp;
    private readonly ContentAddressedModelStore _store;
    private int _disposed;
    public ComputeRuntime(string root, Uri? ollamaEndpoint = null, ComputePolicyProfile? policy = null)
    {
        Policy = policy ?? new();
        Devices = new CompositeComputeDeviceInventory([new CpuComputeDeviceProvider(), new LinuxDrmComputeDeviceProvider()]);
        Telemetry = new ComputeTelemetryService(Devices, []);
        _ollamaHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        Ollama = new OllamaBackend(_ollamaHttp, ollamaEndpoint);
        Backends = [Ollama, BackendBoundaries.LlamaCpp(), BackendBoundaries.WindowsMl(), BackendBoundaries.OnnxExperimental(), BackendBoundaries.Mlx()];
        _store = new ContentAddressedModelStore(Path.Combine(root, "models"));
        Models = new CompositeModelInventory(Backends, _store);
        Estimator = new CalibratingModelMemoryEstimator();
        Governor = new ComputeResourceGovernor(Policy);
        Residency = new ModelResidencyManager(Backends, Models, Governor, Policy);
        Admission = new InferenceAdmissionController(Models, Devices, Telemetry, Backends, Estimator, Governor, Residency);
        Inference = new LocalInferenceManager(Admission, Residency, Backends, Estimator, Governor, Policy);
        Downloads = new HttpModelDownloadManager(_ollamaHttp, _store, Path.Combine(root, "downloads"));
    }

    public ComputePolicyProfile Policy { get; }
    public IComputeDeviceInventory Devices { get; }
    public IComputeTelemetryService Telemetry { get; }
    public ImmutableArray<ILocalInferenceBackend> Backends { get; }
    public OllamaBackend Ollama { get; }
    public IModelInventory Models { get; }
    public IModelMemoryEstimator Estimator { get; }
    public IComputeResourceGovernor Governor { get; }
    public IModelResidencyManager Residency { get; }
    public IInferenceAdmissionController Admission { get; }
    public ILocalInferenceManager Inference { get; }
    public IModelDownloadManager Downloads { get; }
    public IModelStore Store => _store;

    public async ValueTask RefreshAsync(CancellationToken cancellationToken = default)
    {
        await Devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await Models.RefreshAsync(cancellationToken).ConfigureAwait(false);
        await Telemetry.SnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var instance in Residency.Instances.Where(static value => value.ActiveSessions == 0).ToArray())
            await Residency.UnloadAsync(instance.VariantId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        _store.Dispose(); _ollamaHttp.Dispose();
    }
}

public static class ComputeDiagnostics
{
    public static ImmutableArray<string> Inspect(ComputeRuntime runtime)
    {
        var findings = ImmutableArray.CreateBuilder<string>();
        if (runtime.Devices.Current.IsDefaultOrEmpty) findings.Add("No compute devices have been inventoried.");
        foreach (var device in runtime.Devices.Current)
        {
            if (device.Telemetry != TelemetryCapability.Full) findings.Add($"{device.Id}: telemetry is {device.Telemetry}.");
            if (device.DeviceClass != ComputeDeviceClass.Cpu && device.DedicatedMemoryBytes is null && device.MemoryArchitecture == ComputeMemoryArchitecture.Dedicated) findings.Add($"{device.Id}: dedicated memory budget is unknown.");
        }
        foreach (var backend in runtime.Backends.Where(static value => value.Descriptor.Health is BackendHealth.Unavailable or BackendHealth.Quarantined)) findings.Add($"{backend.Descriptor.Id}: {backend.Descriptor.Health}.");
        return findings.ToImmutable();
    }
}

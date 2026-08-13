using Microsoft.Extensions.AI;

namespace Abraxius.Compute;

public readonly record struct LogicalModelId(string Value) { public override string ToString() => Value; }
public readonly record struct ModelRevisionId(string Value) { public override string ToString() => Value; }
public readonly record struct ModelVariantId(string Value) { public override string ToString() => Value; }
public readonly record struct ModelPackageId(string Value) { public override string ToString() => Value; }
public readonly record struct ModelInstanceId(Guid Value) { public static ModelInstanceId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct BackendId(string Value) { public override string ToString() => Value; }
public readonly record struct ComputeDeviceId(string Value) { public override string ToString() => Value; }
public readonly record struct ResourceReservationId(Guid Value) { public static ResourceReservationId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct InferenceSessionId(Guid Value) { public static InferenceSessionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct ComputeWorkloadId(Guid Value) { public static ComputeWorkloadId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }

public enum ComputeDeviceClass { Cpu, Gpu, Npu, Accelerator }
public enum ComputeMemoryArchitecture { Dedicated, Shared, Unified }
public enum TelemetryCapability { None, Partial, Full }
public enum ResourcePressure { Normal, Elevated, High, Critical }
public enum ComputePowerMode { Performance, Balanced, Quiet, Battery, Custom }
public enum BackendHealth { Unknown, Healthy, Degraded, Unavailable, Quarantined }
public enum ModelCapabilityKind { Chat, Code, Reasoning, Tools, StructuredOutput, Vision, Embedding, AudioInput, FillInMiddle }
public enum ModelValidationState { Unvalidated, LoadValidated, PerformanceValidated, QualityValidated, Preferred, Deprecated }
public enum ModelStorageKind { AbraxiusManaged, BackendManaged, External }
public enum ModelResidencyState { NotInstalled, Installed, Loading, Resident, Busy, IdleResident, Evicting, Failed }
public enum ReservationState { Pending, Granted, Active, Released, Preempting, Preempted, Rejected, Expired }
public enum InferencePriority { RealtimeVoice, InteractiveUser, InteractiveMission, Verification, NormalMission, Background, Maintenance }
public enum InferenceAdmissionStatus { Granted, Queued, Rejected, AlternativeOffered }
public enum EstimateConfidence { Low, Medium, High }
public enum ModelAvailability { Installed, Resident, Downloadable, Unavailable }
public enum DownloadState { Pending, Downloading, Paused, Verifying, Installing, Completed, Failed, Cancelled }
public enum LicenseAcceptance { NotRequired, Required, Accepted, Rejected }

public sealed record ComputeDevice(
    ComputeDeviceId Id, string Vendor, string Model, ComputeDeviceClass DeviceClass, string Architecture,
    ImmutableHashSet<string> BackendCapabilities, ComputeMemoryArchitecture MemoryArchitecture,
    long? DedicatedMemoryBytes, long? SharedMemoryBytes, ImmutableHashSet<string> ComputeCapabilities,
    string? DriverVersion, TelemetryCapability Telemetry, string? StableHardwareIdentity = null,
    int? LogicalCores = null, int? PhysicalCores = null, ImmutableHashSet<string>? Simd = null);

public sealed record DeviceResourceState(
    ComputeDeviceId DeviceId, long? MemoryUsedBytes, long? MemoryBudgetBytes, double? Utilization,
    double? TemperatureCelsius, double? PowerWatts, ResourcePressure Pressure, DateTimeOffset ObservedAt);

public sealed record ComputeResourceSnapshot(
    DateTimeOffset Timestamp, double? CpuUtilization, long? RamAvailableBytes, long? RamTotalBytes,
    ResourcePressure RamPressure, ImmutableArray<DeviceResourceState> Devices, long ProcessWorkingSetBytes,
    TimeSpan ProcessCpuTime)
{
    public DeviceResourceState? Find(ComputeDeviceId id) => Devices.FirstOrDefault(value => value.DeviceId == id);
}

public sealed record InferenceBackendDescriptor(
    BackendId Id, string Version, ImmutableHashSet<string> SupportedFormats, ImmutableHashSet<ComputeDeviceClass> DeviceClasses,
    bool Streaming, bool ToolCalling, bool StructuredOutput, bool Embeddings, bool Vision, bool Multimodal,
    bool ParallelRequests, bool PromptCaching, ImmutableHashSet<string> QuantizationSupport, BackendHealth Health,
    bool UserManaged = true);

public sealed record ModelLicense(string? Identifier, Uri? Terms, bool CommercialUseKnown, LicenseAcceptance Acceptance, string? Attribution = null);
public sealed record ModelSourceDescriptor(string Provider, string Reference, string? Revision, Abraxius.Models.DataClassification Classification);
public sealed record ModelFileManifest(string RelativePath, long SizeBytes, string Sha256);

public sealed record ModelManifest(
    ModelPackageId PackageId, LogicalModelId LogicalModel, ModelRevisionId Revision, ModelSourceDescriptor Source,
    ImmutableArray<ModelFileManifest> Files, string Format, string Quantization, string? Tokenizer,
    ModelLicense License, ImmutableHashSet<BackendId> CompatibleBackends, DateTimeOffset CreatedAt);

public sealed record ModelVariantDescriptor(
    ModelVariantId Id, LogicalModelId LogicalModel, ModelRevisionId Revision, string DisplayName, string Format,
    string Quantization, long? ParameterCount, string Architecture, int ContextMaximum, long FileSizeBytes,
    ImmutableDictionary<string, string> Hashes, ImmutableHashSet<ModelCapabilityKind> ClaimedCapabilities,
    ImmutableHashSet<ModelCapabilityKind> ValidatedCapabilities, ImmutableHashSet<BackendId> CompatibleBackends,
    ModelLicense License, ModelSourceDescriptor Source, ModelValidationState ValidationState,
    ModelStorageKind StorageKind, string StorageReference, int? LayerCount = null, int? HiddenSize = null,
    int BytesPerKvElement = 2, ModelVariantId? DerivedFrom = null, string? DerivationTool = null,
    string? DerivationToolVersion = null);

public sealed record ResidentModelInstance(
    ModelInstanceId Id, ModelVariantId VariantId, BackendId BackendId, ImmutableArray<ComputeDeviceId> Devices,
    ModelResidencyState State, long RamBytes, long DeviceMemoryBytes, int ContextTokens, DateTimeOffset LoadedAt,
    DateTimeOffset LastUsedAt, TimeSpan LoadDuration, bool Pinned, int ActiveSessions, DateTimeOffset? KeepAliveUntil = null);

public sealed record ModelMemoryEstimate(
    long WeightsBytes, long KvCacheBytes, long ScratchBytes, long BackendOverheadBytes, long HostRamBytes,
    long DeviceMemoryBytes, long HeadroomBytes, EstimateConfidence Confidence)
{
    public long TotalDeviceReservation => checked(DeviceMemoryBytes + HeadroomBytes);
    public long TotalHostReservation => HostRamBytes;
}

public sealed record ComputePolicyProfile(
    ComputePowerMode Mode = ComputePowerMode.Balanced, long MinimumRamHeadroomBytes = 2L << 30,
    long MinimumDeviceHeadroomBytes = 1L << 30, double DeviceHeadroomFraction = .10,
    int MaximumQueuedInference = 128, int MaximumConcurrentInference = 4, int InteractiveBurstBeforeFairness = 8,
    TimeSpan IdleResidency = default, TimeSpan EvictionCooldown = default,
    bool AllowBackgroundOnBattery = false)
{
    public TimeSpan EffectiveIdleResidency => IdleResidency == default ? TimeSpan.FromMinutes(5) : IdleResidency;
    public TimeSpan EffectiveEvictionCooldown => EvictionCooldown == default ? TimeSpan.FromSeconds(30) : EvictionCooldown;
}

public sealed record ResourceReservationRequest(
    ComputeWorkloadId WorkloadId, InferencePriority Priority, ImmutableArray<ComputeDeviceId> Devices,
    long RamBytes, ImmutableDictionary<ComputeDeviceId, long> DeviceMemoryBytes, int CpuWeight,
    TimeSpan ExpectedDuration, bool Preemptible, string Purpose);

public sealed record ResourceReservation(
    ResourceReservationId Id, ResourceReservationRequest Request, ReservationState State, DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null, string? Reason = null);

public sealed record LocalInferenceRequest(
    LogicalModelId LogicalModelTarget, ImmutableHashSet<ModelCapabilityKind> CapabilityRequirements,
    int ContextTokens, int ExpectedOutputTokens, InferencePriority Priority, bool Streaming,
    bool ToolsRequired, Abraxius.Models.DataClassification Privacy, string Prompt, string? MissionId = null,
    ModelVariantId? RequiredVariant = null, string? SessionKey = null, TimeSpan? Timeout = null);

public sealed record LocalInferenceExecutionPlan(
    ModelVariantDescriptor Variant, InferenceBackendDescriptor Backend, ImmutableArray<ComputeDeviceId> Devices,
    int ContextTokens, int Parallelism, ModelMemoryEstimate Memory, ResourceReservation Reservation,
    bool ReuseResident, string Explanation);

public sealed record LocalInferenceOffer(
    long OfferVersion, DateTimeOffset SnapshotTime, LogicalModelId LogicalModel, ModelVariantId Variant,
    BackendId Backend, ImmutableArray<ComputeDeviceId> Devices, int MaximumSafeContext,
    ModelMemoryEstimate EstimatedMemory, ModelResidencyState CurrentResidency, TimeSpan ExpectedLoadLatency,
    double? ExpectedPromptTokensPerSecond, double? ExpectedGenerationTokensPerSecond, int CurrentQueue,
    ResourcePressure Pressure, ModelAvailability Availability);

public sealed record InferenceAdmissionDecision(
    InferenceAdmissionStatus Status, LocalInferenceExecutionPlan? Plan, ImmutableArray<LocalInferenceOffer> Alternatives,
    string Explanation);

public sealed record InferenceTelemetry(
    InferenceSessionId SessionId, ModelVariantId Variant, BackendId Backend, ImmutableArray<ComputeDeviceId> Devices,
    TimeSpan QueueDelay, TimeSpan LoadDuration, TimeSpan TimeToFirstToken, int PromptTokens,
    TimeSpan PromptDuration, int OutputTokens, TimeSpan GenerationDuration, long ReservedRamBytes,
    long ReservedDeviceMemoryBytes, long? PeakDeviceMemoryBytes, bool ColdStart, DateTimeOffset CompletedAt)
{
    public double? PromptTokensPerSecond => PromptDuration.TotalSeconds > 0 ? PromptTokens / PromptDuration.TotalSeconds : null;
    public double? GenerationTokensPerSecond => GenerationDuration.TotalSeconds > 0 ? OutputTokens / GenerationDuration.TotalSeconds : null;
}

public abstract record LocalInferenceEvent(DateTimeOffset Timestamp)
{
    public sealed record Started(DateTimeOffset Timestamp, LocalInferenceExecutionPlan Plan) : LocalInferenceEvent(Timestamp);
    public sealed record Token(DateTimeOffset Timestamp, string Text) : LocalInferenceEvent(Timestamp);
    public sealed record Completed(DateTimeOffset Timestamp, string Text, InferenceTelemetry Telemetry) : LocalInferenceEvent(Timestamp);
    public sealed record Failed(DateTimeOffset Timestamp, string Code, string Message) : LocalInferenceEvent(Timestamp);
}

public interface IComputeDeviceProvider { ValueTask<ImmutableArray<ComputeDevice>> DiscoverAsync(CancellationToken cancellationToken = default); }
public interface IComputeDeviceInventory { ValueTask<ImmutableArray<ComputeDevice>> RefreshAsync(CancellationToken cancellationToken = default); ImmutableArray<ComputeDevice> Current { get; } }
public interface IComputeTelemetryProvider { string Id { get; } ValueTask<ImmutableArray<DeviceResourceState>> ReadAsync(ImmutableArray<ComputeDevice> devices, CancellationToken cancellationToken = default); }
public interface IComputeTelemetryService { ValueTask<ComputeResourceSnapshot> SnapshotAsync(CancellationToken cancellationToken = default); ComputeResourceSnapshot? Latest { get; } }
public interface IModelMemoryEstimator { ModelMemoryEstimate Estimate(ModelVariantDescriptor variant, InferenceBackendDescriptor backend, int contextTokens, int parallelism, ImmutableArray<ComputeDevice> devices); void Observe(ModelVariantId variant, BackendId backend, ImmutableArray<ComputeDeviceId> devices, ModelMemoryEstimate estimate, long actualRamBytes, long actualDeviceBytes); }
public interface IComputeResourceGovernor { ValueTask<ResourceReservation> ReserveAsync(ResourceReservationRequest request, ComputeResourceSnapshot snapshot, CancellationToken cancellationToken = default); ValueTask ReleaseAsync(ResourceReservationId id, CancellationToken cancellationToken = default); ImmutableArray<ResourceReservation> Reservations { get; } }
public interface IInferenceAdmissionController { ValueTask<InferenceAdmissionDecision> AdmitAsync(LocalInferenceRequest request, CancellationToken cancellationToken = default); }
public interface IModelResidencyManager { ImmutableArray<ResidentModelInstance> Instances { get; } ValueTask<ResidentModelInstance> EnsureResidentAsync(LocalInferenceExecutionPlan plan, CancellationToken cancellationToken = default); bool BeginSession(ModelVariantId variant); void EndSession(ModelVariantId variant); ValueTask<bool> UnloadAsync(ModelVariantId variant, bool allowActiveCancellation = false, CancellationToken cancellationToken = default); ValueTask<ImmutableArray<ModelVariantId>> RelievePressureAsync(long requiredBytes, ResourcePressure pressure, CancellationToken cancellationToken = default); }
public interface IModelInventory { ImmutableArray<ModelVariantDescriptor> Variants { get; } ValueTask<ImmutableArray<ModelVariantDescriptor>> RefreshAsync(CancellationToken cancellationToken = default); ModelVariantDescriptor? Find(ModelVariantId id); }
public interface ILocalInferenceBackend
{
    InferenceBackendDescriptor Descriptor { get; }
    IChatClient? ChatClient { get; }
    ValueTask<ImmutableArray<ModelVariantDescriptor>> DiscoverModelsAsync(CancellationToken cancellationToken = default);
    ValueTask<ImmutableArray<ResidentModelInstance>> GetResidencyAsync(CancellationToken cancellationToken = default);
    ValueTask<ResidentModelInstance> LoadAsync(ModelVariantDescriptor variant, LocalInferenceExecutionPlan plan, CancellationToken cancellationToken = default);
    ValueTask UnloadAsync(ModelVariantDescriptor variant, CancellationToken cancellationToken = default);
    IAsyncEnumerable<BackendInferenceEvent> InferAsync(ModelVariantDescriptor variant, LocalInferenceRequest request, LocalInferenceExecutionPlan plan, CancellationToken cancellationToken = default);
}

public abstract record BackendInferenceEvent
{
    public sealed record Token(string Text) : BackendInferenceEvent;
    public sealed record Completed(string Text, long LoadNanoseconds, int PromptTokens, long PromptNanoseconds, int OutputTokens, long GenerationNanoseconds, long? PeakDeviceBytes = null) : BackendInferenceEvent;
}

public interface IModelStore { ValueTask<ModelVariantDescriptor> ImportAsync(Stream content, string fileName, ModelImportMetadata metadata, CancellationToken cancellationToken = default); ValueTask<Stream> OpenReadAsync(ModelVariantId id, CancellationToken cancellationToken = default); ValueTask<bool> VerifyAsync(ModelVariantId id, CancellationToken cancellationToken = default); ValueTask<bool> RemoveAsync(ModelVariantId id, CancellationToken cancellationToken = default); ValueTask<ImmutableArray<ModelVariantDescriptor>> ListAsync(CancellationToken cancellationToken = default); }
public sealed record ModelImportMetadata(LogicalModelId LogicalModel, ModelRevisionId Revision, string Quantization, string Architecture, int ContextMaximum, ModelLicense License, ModelSourceDescriptor Source, ImmutableHashSet<BackendId> Backends, long? ParameterCount = null, int? LayerCount = null, int? HiddenSize = null);
public interface IModelSourceProvider { string Id { get; } ValueTask<ModelDownloadDescriptor?> ResolveAsync(string reference, CancellationToken cancellationToken = default); }
public sealed record ModelDownloadDescriptor(Uri Uri, string FileName, long? SizeBytes, string? Sha256, ModelImportMetadata Metadata, bool SupportsResume);
public sealed record ModelDownloadProgress(Guid Id, DownloadState State, long BytesReceived, long? TotalBytes, string Message, ModelVariantId? InstalledVariant = null);
public interface IModelDownloadManager { IAsyncEnumerable<ModelDownloadProgress> DownloadAsync(ModelDownloadDescriptor descriptor, CancellationToken cancellationToken = default); }
public interface IModelVariantBuilder { BackendId BackendId { get; } ValueTask<ModelVariantDescriptor> BuildAsync(ModelVariantDescriptor source, string quantization, CancellationToken cancellationToken = default); }
public interface IInferenceBackendSupervisor { ValueTask<BackendHealth> GetHealthAsync(BackendId backend, CancellationToken cancellationToken = default); }
public interface ILocalInferenceManager { ValueTask<InferenceAdmissionDecision> PlanAsync(LocalInferenceRequest request, CancellationToken cancellationToken = default); IAsyncEnumerable<LocalInferenceEvent> InferAsync(LocalInferenceRequest request, CancellationToken cancellationToken = default); ImmutableArray<LocalInferenceOffer> GetOffers(); }

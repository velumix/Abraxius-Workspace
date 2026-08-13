namespace Abraxius.Fabric;

public readonly record struct FabricId(Guid Value) { public static FabricId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct FabricNodeId(Guid Value) { public static FabricNodeId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct FabricSessionId(Guid Value) { public static FabricSessionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct FabricEpoch(ulong Value) { public FabricEpoch Next() => new(checked(Value + 1)); public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
public readonly record struct ExecutionLeaseId(Guid Value) { public static ExecutionLeaseId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct FabricTransferId(Guid Value) { public static FabricTransferId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct PairingInvitationId(Guid Value) { public static PairingInvitationId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct NodeFingerprint(string Value) { public override string ToString() => Value; }

[Flags] public enum FabricNodeRole { None = 0, Coordinator = 1, Worker = 2, ControlClient = 4, ArtifactHost = 8, ModelHost = 16, EvaluationWorker = 32 }
public enum NodeTrustState { Unknown, Pairing, Trusted, Restricted, Revoked, Quarantined }
public enum FabricNodeHealth { Healthy, Busy, Degraded, Suspect, Offline, Draining, Incompatible, Quarantined }
public enum FabricConnectivity { Disconnected, Connecting, Connected, Reconnecting }
public enum NodePowerState { Ac, Battery, LowPower, Unknown }
public enum FabricControlPriority { Critical, Cancellation, Lease, Attention, Progress, Telemetry }
public enum LeaseAdmissionStatus { Accepted, Rejected }
public enum LeaseRejectReason { None, InsufficientResources, CapabilityUnavailable, PolicyDenied, NodeDraining, VersionMismatch, ArtifactUnavailable, StaleCoordinator, DuplicateSession, ClassificationDenied }
public enum LeaseExecutionStatus { Offered, Accepted, Running, CancellationPending, Completed, Failed, Cancelled, Suspect, ReconciliationRequired, Stale, Rejected }
public enum ResultCommitStatus { Accepted, AlreadyCommitted, RejectedStale, RejectedEpoch, RejectedRevokedNode, ReconciliationRequired }
public enum PlacementAffinityKind { None, PreferNode, RequireNode, AvoidNode }
public enum FabricTransportKind { InMemory, GrpcHttp2, Quic }

public sealed record FabricProtocolVersion(uint Minimum, uint Maximum, uint Preferred, ImmutableHashSet<string> Features)
{
    public static FabricProtocolVersion Current { get; } = new(1, 1, 1, ImmutableHashSet.Create(StringComparer.Ordinal, "leases", "resumable-transfer", "bounded-control", "canonical-commit"));
    public ProtocolNegotiationResult Negotiate(FabricProtocolVersion other)
    {
        var minimum = Math.Max(Minimum, other.Minimum); var maximum = Math.Min(Maximum, other.Maximum);
        if (minimum > maximum) return new(false, 0, [], "No compatible Fabric protocol version.");
        var selected = Math.Clamp(Math.Min(Preferred, other.Preferred), minimum, maximum);
        return new(true, selected, Features.Intersect(other.Features, StringComparer.Ordinal).ToImmutableHashSet(StringComparer.Ordinal), "Compatible.");
    }
}

public sealed record ProtocolNegotiationResult(bool Compatible, uint SelectedVersion, ImmutableHashSet<string> Features, string Reason);
public sealed record FabricCapability(string Id, string Version, bool ReadOnly = true, ImmutableDictionary<string, string>? Properties = null);
public sealed record FabricGpuDescriptor(string DeviceId, string Vendor, string Architecture, long TotalMemoryBytes, long FreeMemoryBytes, string Backend, ImmutableHashSet<string> Capabilities);
public sealed record FabricModelDescriptor(string ModelId, string Provider, string Quantization, int ContextTokens, bool ToolCapable, bool StructuredOutput, long RequiredVramBytes, bool Loaded, bool Healthy);
public sealed record NetworkQualitySnapshot(TimeSpan? RoundTripTime, long? EstimatedBytesPerSecond, double ErrorRate = 0, DateTimeOffset? ObservedAt = null);
public sealed record NodeResourceSnapshot(int LogicalCpu, double CpuUtilization, long TotalRamBytes, long FreeRamBytes, ImmutableArray<FabricGpuDescriptor> Gpus, long FreeDiskBytes, NodePowerState PowerState, bool ThermalConstrained, NetworkQualitySnapshot Network, DateTimeOffset CapturedAt)
{
    public double CpuHeadroom => Math.Clamp(1 - CpuUtilization, 0, 1);
}

public sealed record RepositoryLocality(string ProjectId, string RepositoryId, string CommitId, bool Clean);
public sealed record NodeArtifactInventory(ImmutableHashSet<string> ContentHashes, long CacheBytes, long CacheLimitBytes);
public sealed record FabricNodeDescriptor(
    FabricNodeId Id, string DisplayName, NodeFingerprint Fingerprint, NodeTrustState TrustState, FabricNodeRole Roles,
    string Platform, string Architecture, string RuntimeVersion, FabricProtocolVersion Protocol,
    ImmutableArray<FabricCapability> Capabilities, ImmutableArray<SandboxLevel> Sandboxes,
    ImmutableArray<FabricModelDescriptor> Models, NodeResourceSnapshot Resources, NodeArtifactInventory Artifacts,
    ImmutableArray<RepositoryLocality> Repositories, FabricNodeHealth Health, FabricConnectivity Connectivity,
    FabricSessionId? SessionId = null, ulong NodeSequence = 0, DateTimeOffset? LastSeen = null, bool AcceptingLeases = true);

public sealed record PlacementAffinity(PlacementAffinityKind Kind, FabricNodeId? NodeId = null);
public sealed record ExecutionPlacementRequest(
    ExecutionId ExecutionId, NodeId ExecutionNodeId, WorkKind WorkKind, string RequiredCapability,
    DataClassification Classification, FabricNodeId OriginNode, string? RequiredPlatform = null, string? RequiredArchitecture = null,
    SandboxLevel MinimumSandbox = SandboxLevel.None, long RequiredMemoryBytes = 0, long RequiredVramBytes = 0,
    ImmutableHashSet<string>? RequiredArtifactHashes = null, string? RequiredRepositoryId = null,
    PlacementAffinity? Affinity = null, bool Background = false, bool SideEffecting = false);

public sealed record PlacementCandidate(FabricNodeId NodeId, bool Eligible, double Score, ImmutableArray<string> Reasons, ImmutableArray<string> Rejections);
public sealed record ExecutionPlacementDecision(FabricNodeId? NodeId, bool Local, ImmutableArray<PlacementCandidate> Candidates, string Explanation)
{
    public bool Placed => NodeId.HasValue;
}

public sealed record RemoteAuthorizationContext(
    AuthorizationDecisionId DecisionId, AuthorizationGrantId? GrantId, MissionId? MissionId, SecuritySubject Subject,
    ImmutableHashSet<string> Capabilities, ImmutableArray<string> ResourcePrefixes, AuthorizationConstraints? Constraints,
    DateTimeOffset ExpiresAt, DataClassification Classification)
{
    public bool IsValidAt(DateTimeOffset now) => now < ExpiresAt && Capabilities.Count > 0;
}

public sealed record FabricResourceReservation(int CpuWeight, long MemoryBytes, long VramBytes, bool ExclusiveGpu = false);
public sealed record ExecutionLease(
    ExecutionLeaseId Id, ExecutionId ExecutionId, NodeId ExecutionNodeId, MissionId? MissionId, FabricEpoch CoordinatorEpoch,
    FabricNodeId WorkerNodeId, int Attempt, string IdempotencyKey, string Capability, WorkKind WorkKind,
    string Operation, ImmutableDictionary<string, string> Parameters, RemoteAuthorizationContext Authorization,
    TimeSpan Ttl, FabricResourceReservation Reservation, DateTimeOffset IssuedAt, bool SideEffecting = false,
    string? TraceParent = null)
{
    public DateTimeOffset ExpiresAt => IssuedAt + Ttl;
}

public sealed record LeaseAdmission(LeaseAdmissionStatus Status, LeaseRejectReason Reason, string Explanation);
public sealed record RemoteExecutionResult(
    ExecutionId ExecutionId, ExecutionLeaseId LeaseId, int Attempt, FabricEpoch CoordinatorEpoch, FabricNodeId WorkerNodeId,
    LeaseExecutionStatus Status, WorkResult? Result, ImmutableArray<ArtifactRevisionId> ArtifactRefs,
    string ResultHash, ImmutableDictionary<string, double> Metrics, DateTimeOffset CompletedAt, bool ExternalStateVerified = false);
public sealed record ResultCommitDecision(ResultCommitStatus Status, string Explanation, RemoteExecutionResult? CanonicalResult = null);

public sealed record FabricHeartbeat(FabricNodeId NodeId, FabricSessionId SessionId, FabricEpoch CoordinatorEpoch, ulong NodeSequence, NodeResourceSnapshot Resources, ImmutableArray<ExecutionLeaseId> ActiveLeases, FabricNodeHealth Health, DateTimeOffset ObservedAt);
public sealed record FabricControlMessage(FabricNodeId NodeId, ulong NodeSequence, FabricControlPriority Priority, string Kind, ReadOnlyMemory<byte> Payload, string? TraceParent = null);
public sealed record FabricEndpoint(Uri Address, FabricTransportKind Transport, string? Overlay = null);
public sealed record DiscoveredFabricNode(FabricEndpoint Endpoint, FabricNodeId? ClaimedNodeId, string DisplayName, NodeFingerprint? Fingerprint, NodeTrustState Trust = NodeTrustState.Unknown);

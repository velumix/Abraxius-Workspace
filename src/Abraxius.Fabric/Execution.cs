namespace Abraxius.Fabric;

public interface IFabricTransport : IAsyncDisposable
{
    FabricTransportKind Kind { get; }
    bool IsSupported { get; }
    ValueTask<ProtocolNegotiationResult> NegotiateAsync(FabricNodeId nodeId, FabricProtocolVersion local, CancellationToken cancellationToken = default);
    ValueTask<RemoteExecutionResult> OfferLeaseAsync(FabricNodeId nodeId, ExecutionLease lease, CancellationToken cancellationToken = default);
    ValueTask SendControlAsync(FabricNodeId nodeId, FabricControlMessage message, CancellationToken cancellationToken = default);
}

public interface IQuicFabricTransport : IFabricTransport { }
public sealed class UnavailableQuicFabricTransport : IQuicFabricTransport
{
    public FabricTransportKind Kind => FabricTransportKind.Quic;
    public bool IsSupported => System.Net.Quic.QuicConnection.IsSupported;
    public ValueTask<ProtocolNegotiationResult> NegotiateAsync(FabricNodeId nodeId, FabricProtocolVersion local, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ProtocolNegotiationResult(false, 0, [], "QUIC Fabric transport is not configured; gRPC/HTTP2 remains the baseline."));
    public ValueTask<RemoteExecutionResult> OfferLeaseAsync(FabricNodeId nodeId, ExecutionLease lease, CancellationToken cancellationToken = default) => throw new NotSupportedException("QUIC Fabric transport is unavailable.");
    public ValueTask SendControlAsync(FabricNodeId nodeId, FabricControlMessage message, CancellationToken cancellationToken = default) => throw new NotSupportedException("QUIC Fabric transport is unavailable.");
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public interface IFabricLeaseExecutor { ValueTask<WorkResult> ExecuteAsync(ExecutionLease lease, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken); }
public sealed class DelegateFabricLeaseExecutor(Func<ExecutionLease, IProgress<ProgressUpdate>, CancellationToken, ValueTask<WorkResult>> execute) : IFabricLeaseExecutor
{
    public ValueTask<WorkResult> ExecuteAsync(ExecutionLease lease, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken) => execute(lease, progress, cancellationToken);
}

public sealed class NodeResourceGovernor
{
    private readonly object _gate = new(); private long _memory; private long _vram; private int _cpu; private readonly NodeResourceSnapshot _capacity;
    public NodeResourceGovernor(NodeResourceSnapshot capacity) => _capacity = capacity;
    public bool TryReserve(FabricResourceReservation request, out IDisposable reservation)
    {
        lock (_gate)
        {
            var vram = _capacity.Gpus.Sum(static gpu => gpu.FreeMemoryBytes);
            if (_cpu + request.CpuWeight > Math.Max(1, _capacity.LogicalCpu) || _memory + request.MemoryBytes > _capacity.FreeRamBytes || _vram + request.VramBytes > vram) { reservation = NullReservation.Instance; return false; }
            _cpu += request.CpuWeight; _memory += request.MemoryBytes; _vram += request.VramBytes; reservation = new Reservation(this, request); return true;
        }
    }
    private void Release(FabricResourceReservation value) { lock (_gate) { _cpu -= value.CpuWeight; _memory -= value.MemoryBytes; _vram -= value.VramBytes; } }
    private sealed class Reservation(NodeResourceGovernor owner, FabricResourceReservation value) : IDisposable { private int _disposed; public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(value); } }
    private sealed class NullReservation : IDisposable { public static NullReservation Instance { get; } = new(); public void Dispose() { } }
}

public sealed class FabricWorker
{
    private static readonly ActivitySource Activity = new("Abraxius.Fabric");
    private FabricNodeDescriptor _node; private readonly IFabricLeaseExecutor _executor; private readonly NodeResourceGovernor _resources; private readonly ConcurrentDictionary<string, Task<RemoteExecutionResult>> _executions = new(StringComparer.Ordinal); private readonly ConcurrentDictionary<ExecutionLeaseId, CancellationTokenSource> _cancellations = new(); private FabricEpoch _epoch;
    public FabricWorker(FabricNodeDescriptor node, FabricEpoch epoch, IFabricLeaseExecutor executor) { _node = node; _epoch = epoch; _executor = executor; _resources = new(node.Resources); }
    public FabricNodeDescriptor Descriptor => Volatile.Read(ref _node); public FabricEpoch Epoch => _epoch;
    public ProtocolNegotiationResult Negotiate(FabricProtocolVersion version) => _node.Protocol.Negotiate(version);
    public ValueTask<RemoteExecutionResult> OfferAsync(ExecutionLease lease, CancellationToken cancellationToken = default)
    {
        if (lease.CoordinatorEpoch != _epoch) return ValueTask.FromResult(Rejected(lease, LeaseRejectReason.StaleCoordinator, "Coordinator epoch is stale."));
        var node = Descriptor;
        if (node.TrustState != NodeTrustState.Trusted || !node.AcceptingLeases) return ValueTask.FromResult(Rejected(lease, LeaseRejectReason.NodeDraining, "Node is not admitting work."));
        if (lease.WorkerNodeId != node.Id) return ValueTask.FromResult(Rejected(lease, LeaseRejectReason.PolicyDenied, "Lease targets a different node identity."));
        if (!lease.Authorization.IsValidAt(DateTimeOffset.UtcNow) || !lease.Authorization.Capabilities.Contains(lease.Capability)) return ValueTask.FromResult(Rejected(lease, LeaseRejectReason.PolicyDenied, "Authorization envelope is expired or does not include the capability."));
        if (!lease.Authorization.ResourcePrefixes.Any(prefix => prefix.Equals($"remote://node/{node.Id}", StringComparison.OrdinalIgnoreCase) || prefix.Equals($"remote://node/{node.Id}/", StringComparison.OrdinalIgnoreCase))) return ValueTask.FromResult(Rejected(lease, LeaseRejectReason.PolicyDenied, "Authorization envelope is not scoped to this node."));
        if (lease.Authorization.Classification == DataClassification.LocalOnly && lease.Authorization.Subject.RemoteNodeId is not null && !lease.Authorization.Subject.RemoteNodeId.Equals(_node.Id.ToString(), StringComparison.Ordinal)) return ValueTask.FromResult(Rejected(lease, LeaseRejectReason.ClassificationDenied, "LocalOnly authority is bound to a different physical node."));
        var task = _executions.GetOrAdd(lease.IdempotencyKey, _ => ExecuteCoreAsync(lease, cancellationToken)); return new(task);
    }
    public bool Cancel(ExecutionLeaseId leaseId) { if (!_cancellations.TryGetValue(leaseId, out var value)) return false; value.Cancel(); return true; }
    public void Drain() => Interlocked.Exchange(ref _node, Descriptor with { AcceptingLeases = false, Health = FabricNodeHealth.Draining });
    public void Resume() => Interlocked.Exchange(ref _node, Descriptor with { AcceptingLeases = true, Health = FabricNodeHealth.Healthy });
    public void AdvanceEpoch(FabricEpoch epoch) { if (epoch.Value > _epoch.Value) _epoch = epoch; }
    private async Task<RemoteExecutionResult> ExecuteCoreAsync(ExecutionLease lease, CancellationToken cancellationToken)
    {
        if (!_resources.TryReserve(lease.Reservation, out var reservation)) return Rejected(lease, LeaseRejectReason.InsufficientResources, "Worker admission control rejected the reservation.");
        using (reservation) using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            linked.CancelAfter(lease.Ttl); _cancellations[lease.Id] = linked;
            using var activity = Activity.StartActivity("fabric.worker.execute", ActivityKind.Server, lease.TraceParent); activity?.SetTag("abraxius.execution_id", lease.ExecutionId.ToString()); activity?.SetTag("abraxius.fabric.node_id", _node.Id.ToString());
            try
            {
                var result = await _executor.ExecuteAsync(lease, new Progress<ProgressUpdate>(), linked.Token).ConfigureAwait(false); var hash = HashResult(lease, result);
                return new(lease.ExecutionId, lease.Id, lease.Attempt, lease.CoordinatorEpoch, _node.Id, LeaseExecutionStatus.Completed, result, [], hash, ImmutableDictionary<string, double>.Empty, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) { return new(lease.ExecutionId, lease.Id, lease.Attempt, lease.CoordinatorEpoch, _node.Id, LeaseExecutionStatus.Cancelled, null, [], "", ImmutableDictionary<string, double>.Empty, DateTimeOffset.UtcNow); }
            catch (Exception exception) { return new(lease.ExecutionId, lease.Id, lease.Attempt, lease.CoordinatorEpoch, _node.Id, LeaseExecutionStatus.Failed, WorkResult.Empty(exception.Message), [], "", ImmutableDictionary<string, double>.Empty, DateTimeOffset.UtcNow); }
            finally { _cancellations.TryRemove(lease.Id, out _); }
        }
    }
    private RemoteExecutionResult Rejected(ExecutionLease lease, LeaseRejectReason reason, string explanation) => new(lease.ExecutionId, lease.Id, lease.Attempt, lease.CoordinatorEpoch, _node.Id, LeaseExecutionStatus.Rejected, WorkResult.Empty($"{reason}: {explanation}"), [], "", ImmutableDictionary<string, double>.Empty, DateTimeOffset.UtcNow);
    private static string HashResult(ExecutionLease lease, WorkResult result) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{lease.ExecutionId}|{lease.IdempotencyKey}|{result.ResultId}|{result.Summary}|{result.Value}"))).ToLowerInvariant();
}

public sealed class InMemoryFabricTransport : IFabricTransport
{
    private readonly ConcurrentDictionary<FabricNodeId, FabricWorker> _workers = new(); private readonly Channel<FabricControlMessage> _control;
    public InMemoryFabricTransport(int controlCapacity = 1024) => _control = Channel.CreateBounded<FabricControlMessage>(new BoundedChannelOptions(Math.Clamp(controlCapacity, 16, 65_536)) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    public FabricTransportKind Kind => FabricTransportKind.InMemory; public bool IsSupported => true; public ChannelReader<FabricControlMessage> ControlReader => _control.Reader;
    public void Register(FabricWorker worker) => _workers[worker.Descriptor.Id] = worker;
    public ValueTask<ProtocolNegotiationResult> NegotiateAsync(FabricNodeId nodeId, FabricProtocolVersion local, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_workers.TryGetValue(nodeId, out var worker) ? worker.Negotiate(local) : new(false, 0, [], "Worker is unavailable.")); }
    public ValueTask<RemoteExecutionResult> OfferLeaseAsync(FabricNodeId nodeId, ExecutionLease lease, CancellationToken cancellationToken = default) => _workers.TryGetValue(nodeId, out var worker) ? worker.OfferAsync(lease, cancellationToken) : throw new InvalidOperationException($"Worker {nodeId} is unavailable.");
    public ValueTask SendControlAsync(FabricNodeId nodeId, FabricControlMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_control.Writer.TryWrite(message)) return ValueTask.CompletedTask;
        if (message.Priority is FabricControlPriority.Critical or FabricControlPriority.Cancellation)
            throw new InvalidOperationException("Critical Fabric control queue is full.");
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() { _control.Writer.TryComplete(); return ValueTask.CompletedTask; }
}

public sealed class CanonicalResultCommitter(IFabricNodeRegistry nodes)
{
    private readonly ConcurrentDictionary<ExecutionId, RemoteExecutionResult> _results = new();
    public ResultCommitDecision Commit(RemoteExecutionResult candidate)
    {
        if (candidate.CoordinatorEpoch != nodes.Epoch) return new(ResultCommitStatus.RejectedEpoch, "Result was issued by an obsolete coordinator epoch.");
        if (!nodes.TryGet(candidate.WorkerNodeId, out var node) || node.TrustState != NodeTrustState.Trusted) return new(ResultCommitStatus.RejectedRevokedNode, "Worker is not currently trusted.");
        if (candidate.Status != LeaseExecutionStatus.Completed) return new(candidate.Status == LeaseExecutionStatus.ReconciliationRequired ? ResultCommitStatus.ReconciliationRequired : ResultCommitStatus.RejectedStale, "Only completed results can become canonical.");
        if (_results.TryAdd(candidate.ExecutionId, candidate)) return new(ResultCommitStatus.Accepted, "Canonical result committed once.", candidate);
        var current = _results[candidate.ExecutionId]; return current.LeaseId == candidate.LeaseId ? new(ResultCommitStatus.AlreadyCommitted, "The same logical result was already committed.", current) : new(ResultCommitStatus.RejectedStale, "A newer or alternate attempt already committed the canonical result.", current);
    }
    public bool TryGet(ExecutionId executionId, out RemoteExecutionResult result) => _results.TryGetValue(executionId, out result!);
}

public sealed record AuthorizedRemoteContext(RemoteAuthorizationContext Context, AuthorizationRequest Request, AuthorizationDecision Decision);
public interface IRemoteAuthorizationProvider
{
    ValueTask<AuthorizedRemoteContext> AuthorizeAsync(RemoteWorkRequest request, SchedulerWorkContext context, FabricNodeId worker, string capability, DataClassification classification);
    ValueTask RecordExecutionResultAsync(AuthorizedRemoteContext authorization, bool succeeded, string? resultCode, CancellationToken cancellationToken = default);
}

public sealed class SecurityKernelRemoteAuthorizationProvider(
    ISecurityKernel security,
    IResourceCanonicalizer resources,
    FabricNodeId localNode) : IRemoteAuthorizationProvider
{
    public async ValueTask<AuthorizedRemoteContext> AuthorizeAsync(
        RemoteWorkRequest request,
        SchedulerWorkContext context,
        FabricNodeId worker,
        string capability,
        DataClassification classification)
    {
        if (classification == DataClassification.LocalOnly && worker != localNode)
            throw Denied("fabric_localonly", "LocalOnly work is bound to its physical origin node.");

        var subject = ResolveSubject(context.Execution.SecurityContext);
        var target = $"remote://node/{worker}";
        var authorizationRequest = await AuthorizationRequestFactory.CreateAsync(
            resources,
            subject,
            capability,
            SecurityActions.FabricExecute,
            ResourceKind.RemoteNode,
            target,
            new AuthorizationContext(
                ProjectId: Value(context.Execution.SecurityContext, "project.id"),
                Classification: classification,
                AvailableSandbox: SandboxLevel.RemoteSandbox,
                PlatformCapabilityAvailable: true),
            parameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["worker"] = worker.ToString(),
                ["work_kind"] = request.Node.WorkKind.ToString(),
                ["execution"] = request.ExecutionId.ToString()
            },
            mutation: !request.Node.Work.IsReadOnly,
            external: false,
            minimumSandbox: SandboxLevel.None,
            cancellationToken: context.CancellationToken).ConfigureAwait(false);
        var decision = await security.AuthorizeAsync(authorizationRequest, context.CancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
            throw Denied(decision.Outcome == AuthorizationOutcome.RequireApproval ? "fabric_approval_required" : "fabric_authorization_denied", decision.HumanExplanation);

        var expiry = decision.Expiry ?? DateTimeOffset.UtcNow.Add(request.Node.Timeout ?? TimeSpan.FromMinutes(5)).AddMinutes(1);
        var remote = new RemoteAuthorizationContext(
            decision.DecisionId,
            decision.Grant?.GrantId,
            subject.MissionId,
            subject,
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, capability),
            ImmutableArray.Create(target),
            decision.Constraints,
            expiry,
            classification);
        return new(remote, authorizationRequest, decision);
    }

    public ValueTask RecordExecutionResultAsync(AuthorizedRemoteContext authorization, bool succeeded, string? resultCode, CancellationToken cancellationToken = default) =>
        security.RecordExecutionResultAsync(authorization.Request, authorization.Decision, succeeded, resultCode, cancellationToken);

    private static SecuritySubject ResolveSubject(IReadOnlyDictionary<string, string>? values)
    {
        if (values is null || !values.TryGetValue("principal.id", out var principal) || string.IsNullOrWhiteSpace(principal))
            return SecuritySubject.System("fabric-coordinator");
        var type = Enum.TryParse<PrincipalType>(Value(values, "principal.type"), true, out var parsedType) ? parsedType : PrincipalType.System;
        var mission = TryGuid(Value(values, "mission.id"), static value => new MissionId(value));
        var assignment = TryGuid(Value(values, "assignment.id"), static value => new AssignmentId(value));
        var agent = TryGuid(Value(values, "agent.instance"), static value => new SpecialistInstanceId(value));
        SpecialistRole? role = Enum.TryParse<SpecialistRole>(Value(values, "specialist.role"), true, out var parsedRole) ? parsedRole : null;
        return new(new PrincipalId(principal), type, MissionId: mission, AssignmentId: assignment, AgentInstanceId: agent, SpecialistRole: role);
    }

    private static T? TryGuid<T>(string? value, Func<Guid, T> factory) where T : struct => Guid.TryParse(value, out var parsed) ? factory(parsed) : null;
    private static string? Value(IReadOnlyDictionary<string, string>? values, string key) => values is not null && values.TryGetValue(key, out var value) ? value : null;
    private static WorkExecutionException Denied(string code, string message) => new(new RuntimeError(ErrorCategory.Policy, code, message));
}

public sealed class FabricRemoteExecutor : IRemoteWorkExecutor
{
    private readonly IFabricTransport _transport;
    private readonly IFabricNodeRegistry _nodes;
    private readonly IRemoteAuthorizationProvider _authorization;
    private readonly CanonicalResultCommitter _commits;
    private readonly IExecutionPlacementEngine _placement;
    private readonly FabricNodeId _localNode;

    public FabricRemoteExecutor(IFabricTransport transport, IFabricNodeRegistry nodes, IRemoteAuthorizationProvider authorization, CanonicalResultCommitter commits, IExecutionPlacementEngine placement, FabricNodeId localNode)
    {
        _transport = transport; _nodes = nodes; _authorization = authorization; _commits = commits; _placement = placement; _localNode = localNode;
    }

    public async ValueTask<WorkResult> ExecuteAsync(RemoteWorkRequest request, SchedulerWorkContext context)
    {
        var capability = CapabilityFor(request.Node);
        var placement = _placement.Place(new ExecutionPlacementRequest(
            request.ExecutionId, request.Node.Id, request.Node.WorkKind, capability, DataClassification.Internal, _localNode,
            RequiredMemoryBytes: request.Node.ResourceHints.EstimatedMemoryBytes ?? 0,
            RequiredVramBytes: request.Node.ResourceHints.Gpu == WorkIntensity.None ? 0 : request.Node.ResourceHints.EstimatedMemoryBytes ?? 1,
            SideEffecting: !request.Node.Work.IsReadOnly), _nodes.Nodes);
        var worker = placement.NodeId ?? new FabricNodeId(request.HostId.Value);
        if (!_nodes.TryGet(worker, out _)) throw new WorkExecutionException(new(ErrorCategory.Transport, "fabric_node_missing", $"Fabric node {worker} is unknown."));
        var authorized = await _authorization.AuthorizeAsync(request, context, worker, capability, DataClassification.Internal).ConfigureAwait(false);
        var auth = authorized.Context;
        var lease = new ExecutionLease(ExecutionLeaseId.New(), request.ExecutionId, request.Node.Id, TryMission(auth.Subject), _nodes.Epoch, worker, 1, $"{request.ExecutionId}:{request.Node.Id}:1", capability, request.Node.WorkKind, OperationFor(request.Node), ParametersFor(request.Node), auth, request.Node.Timeout ?? TimeSpan.FromMinutes(5), ReservationFor(request.Node), DateTimeOffset.UtcNow, !request.Node.Work.IsReadOnly, Activity.Current?.Id);
        try
        {
            var negotiation = await _transport.NegotiateAsync(worker, FabricProtocolVersion.Current, context.CancellationToken).ConfigureAwait(false); if (!negotiation.Compatible) throw new WorkExecutionException(new(ErrorCategory.Transport, "fabric_protocol_incompatible", negotiation.Reason));
            var result = await _transport.OfferLeaseAsync(worker, lease, context.CancellationToken).ConfigureAwait(false); if (result.Status != LeaseExecutionStatus.Completed) { var code = lease.SideEffecting ? "fabric_reconciliation_required" : "fabric_remote_failed"; throw new WorkExecutionException(new(ErrorCategory.Transport, code, result.Result?.Summary ?? result.Status.ToString(), IsTransient: !lease.SideEffecting)); }
            var commit = _commits.Commit(result); if (commit.Status is not ResultCommitStatus.Accepted and not ResultCommitStatus.AlreadyCommitted) throw new WorkExecutionException(new(ErrorCategory.Transport, "fabric_stale_result", commit.Explanation));
            await _authorization.RecordExecutionResultAsync(authorized, true, "fabric_remote_completed", CancellationToken.None).ConfigureAwait(false);
            return commit.CanonicalResult!.Result!;
        }
        catch
        {
            await _authorization.RecordExecutionResultAsync(authorized, false, "fabric_remote_failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }
    public static string CapabilityFor(ExecutionNodeDefinition node) => node.Work switch { ToolWorkDescriptor tool => tool.Capability.Value, ModelWorkDescriptor => "ModelInference", MemoryWorkDescriptor => "Memory", VerificationWorkDescriptor => "Verification", CpuWorkDescriptor => "Cpu", IoWorkDescriptor => "Io", BackgroundWorkDescriptor => "Background", _ => node.WorkKind.ToString() };
    private static string OperationFor(ExecutionNodeDefinition node) => node.Work switch { ToolWorkDescriptor value => value.Operation, CpuWorkDescriptor value => value.OperationCode, IoWorkDescriptor value => value.OperationCode, BackgroundWorkDescriptor value => value.OperationCode, ModelWorkDescriptor => "infer", MemoryWorkDescriptor => "retrieve", VerificationWorkDescriptor => "verify", _ => node.WorkKind.ToString() };
    private static ImmutableDictionary<string, string> ParametersFor(ExecutionNodeDefinition node) => node.Work switch { ToolWorkDescriptor value => (value.Parameters ?? ImmutableDictionary<string, string>.Empty).ToImmutableDictionary(StringComparer.Ordinal), CpuWorkDescriptor value => (value.Parameters ?? ImmutableDictionary<string, string>.Empty).ToImmutableDictionary(StringComparer.Ordinal), IoWorkDescriptor value => (value.Parameters ?? ImmutableDictionary<string, string>.Empty).ToImmutableDictionary(StringComparer.Ordinal), _ => ImmutableDictionary<string, string>.Empty };
    private static FabricResourceReservation ReservationFor(ExecutionNodeDefinition node) => new(Math.Max(1, (int)node.ResourceHints.Cpu), node.ResourceHints.EstimatedMemoryBytes ?? 0, node.ResourceHints.Gpu == WorkIntensity.None ? 0 : node.ResourceHints.EstimatedMemoryBytes ?? 1);
    private static MissionId? TryMission(SecuritySubject subject) => subject.MissionId;
}

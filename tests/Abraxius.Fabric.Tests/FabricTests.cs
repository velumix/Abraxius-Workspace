using System.Collections.Immutable;
using System.Text;
using Abraxius.Artifacts;
using Abraxius.Core;
using Abraxius.Fabric;
using Abraxius.Fabric.Protocol;
using Abraxius.Memory;
using Abraxius.Platform;
using Abraxius.Protocol;
using Abraxius.Scheduler;
using Abraxius.Security;
using Xunit;
using Google.Protobuf;

namespace Abraxius.Fabric.Tests;

public sealed class FabricTests
{
    [Fact]
    public void ProtocolNegotiatesIntersectionAndRejectsMismatch()
    {
        var current = FabricProtocolVersion.Current;
        var compatible = current.Negotiate(new(1, 2, 2, ["leases", "future"]));
        Assert.True(compatible.Compatible); Assert.Equal((uint)1, compatible.SelectedVersion); Assert.Contains("leases", compatible.Features); Assert.DoesNotContain("future", compatible.Features);
        Assert.False(current.Negotiate(new(2, 3, 2, [])).Compatible);
    }

    [Fact]
    public async Task PairingIsExplicitSingleUseAndRevocable()
    {
        var registry = new InMemoryFabricNodeRegistry(FabricId.New(), new(1));
        using var credentials = new InMemoryNodeCredentialStore(); using var pairing = new FabricPairingService(registry, credentials);
        var (invitation, code) = pairing.CreateInvitation(TimeSpan.FromMinutes(1)); var unknown = Node(FabricNodeId.New(), "Cpu") with { TrustState = NodeTrustState.Unknown };
        Assert.False((await pairing.PairAsync(invitation.Id, "00", unknown)).Paired);
        var paired = await pairing.PairAsync(invitation.Id, code, unknown);
        Assert.True(paired.Paired); Assert.Equal(NodeTrustState.Trusted, paired.Node!.TrustState); Assert.NotNull(await credentials.GetAsync(unknown.Id));
        Assert.False((await pairing.PairAsync(invitation.Id, code, unknown)).Paired);
        await pairing.UnpairAsync(unknown.Id); Assert.Equal(NodeTrustState.Revoked, registry.Nodes.Single().TrustState); Assert.Null(await credentials.GetAsync(unknown.Id));
        paired.Credential?.Dispose();
    }

    [Fact]
    public void PlacementUsesHardFiltersBeforeLocalityScore()
    {
        var origin = FabricNodeId.New(); var gpu = Node(FabricNodeId.New(), "ModelInference", vram: 16L << 30); var cached = Node(FabricNodeId.New(), "ModelInference", vram: 16L << 30) with { Artifacts = new(["abc"], 1, 100) };
        var request = new ExecutionPlacementRequest(ExecutionId.New(), NodeId.New(), WorkKind.ModelInference, "ModelInference", DataClassification.Internal, origin, RequiredVramBytes: 8L << 30, RequiredArtifactHashes: ["abc"]);
        var decision = new DeterministicPlacementEngine().Place(request, [gpu, cached]);
        Assert.Equal(cached.Id, decision.NodeId); Assert.Contains("cached", decision.Explanation);
        var localOnly = new DeterministicPlacementEngine().Place(request with { Classification = DataClassification.LocalOnly }, [gpu, cached]);
        Assert.False(localOnly.Placed); Assert.All(localOnly.Candidates, candidate => Assert.Contains(candidate.Rejections, reason => reason.Contains("LocalOnly", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task DuplicateLeaseExecutesLogicalWorkOnce()
    {
        var calls = 0; var node = Node(FabricNodeId.New(), "Cpu"); var worker = new FabricWorker(node, new(4), new DelegateFabricLeaseExecutor((_, _, _) => { Interlocked.Increment(ref calls); return ValueTask.FromResult(WorkResult.Empty("done")); }));
        var lease = Lease(node.Id, new(4), "same-key");
        var results = await Task.WhenAll(worker.OfferAsync(lease).AsTask(), worker.OfferAsync(lease).AsTask());
        Assert.Equal(1, calls); Assert.All(results, result => Assert.Equal(LeaseExecutionStatus.Completed, result.Status)); Assert.Equal(results[0].ResultHash, results[1].ResultHash);
    }

    [Fact]
    public async Task WorkerRejectsStaleEpochAndVramOvercommit()
    {
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var node = Node(FabricNodeId.New(), "Cpu", vram: 1024);
        var worker = new FabricWorker(node, new(2), new DelegateFabricLeaseExecutor(async (_, _, token) => { await blocker.Task.WaitAsync(token); return WorkResult.Empty("done"); }));
        Assert.Equal(LeaseExecutionStatus.Rejected, (await worker.OfferAsync(Lease(node.Id, new(1), "stale"))).Status);
        var first = worker.OfferAsync(Lease(node.Id, new(2), "one", vram: 800)).AsTask(); await Task.Delay(20);
        var second = await worker.OfferAsync(Lease(node.Id, new(2), "two", vram: 800));
        Assert.Equal(LeaseExecutionStatus.Rejected, second.Status); blocker.SetResult(); Assert.Equal(LeaseExecutionStatus.Completed, (await first).Status);
    }

    [Fact]
    public void CanonicalCommitRejectsLateAttemptAndRevokedNode()
    {
        var registry = new InMemoryFabricNodeRegistry(FabricId.New(), new(8)); var firstNode = Node(FabricNodeId.New(), "Cpu"); var otherNode = Node(FabricNodeId.New(), "Cpu"); registry.Upsert(firstNode); registry.Upsert(otherNode);
        var commit = new CanonicalResultCommitter(registry); var execution = ExecutionId.New(); var first = Result(execution, firstNode.Id, new(8), 2);
        Assert.Equal(ResultCommitStatus.Accepted, commit.Commit(first).Status);
        Assert.Equal(ResultCommitStatus.RejectedStale, commit.Commit(Result(execution, otherNode.Id, new(8), 1)).Status);
        registry.Revoke(otherNode.Id); Assert.Equal(ResultCommitStatus.RejectedRevokedNode, commit.Commit(Result(ExecutionId.New(), otherNode.Id, new(8), 1)).Status);
    }

    [Fact]
    public async Task SecurityKernelAuthorizesReadButBlocksLocalOnlyRemoteExecution()
    {
        var resources = new ResourceCanonicalizer(); var audit = new InMemorySecurityAuditStore();
        var kernel = new SecurityKernel(new DeterministicPolicyEngine(), new DeterministicRiskClassifier(), new InMemoryAuthorizationGrantStore(), audit, resources);
        var local = FabricNodeId.New(); var remote = FabricNodeId.New(); var provider = new SecurityKernelRemoteAuthorizationProvider(kernel, resources, local);
        var request = RemoteRequest(); var context = WorkContext(request.Node);
        var allowed = await provider.AuthorizeAsync(request, context, remote, "Cpu", DataClassification.Internal);
        Assert.True(allowed.Decision.IsAllowed); Assert.Equal(SecurityActions.FabricExecute, allowed.Request.Action.Operation);
        var exception = await Assert.ThrowsAsync<WorkExecutionException>(async () => await provider.AuthorizeAsync(request, context, remote, "Cpu", DataClassification.LocalOnly));
        Assert.Equal("fabric_localonly", exception.Error.Code);
    }

    [Fact]
    public async Task ChunkedTransferResumesAndChecksFinalHash()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 300_000)); var source = new InMemoryArtifactContentStore(); await using var input = new MemoryStream(bytes); var descriptor = await source.PutAsync(input, "application/octet-stream", "blob.bin");
        var request = new FabricTransferRequest(FabricTransferId.New(), descriptor, FabricNodeId.New(), FabricNodeId.New(), DataClassification.Internal, 32 * 1024); using var receiver = new InterruptingReceiver(source, failAfter: 100_000);
        await Assert.ThrowsAsync<IOException>(async () => await new ChunkedFabricArtifactTransfer().TransferAsync(request, source, receiver));
        var resumedAt = await receiver.GetCommittedLengthAsync(request); Assert.InRange(resumedAt, 1, bytes.Length - 1); receiver.FailAfter = long.MaxValue;
        var result = await new ChunkedFabricArtifactTransfer().TransferAsync(request, source, receiver);
        Assert.True(result.Complete); Assert.Equal(bytes.Length, result.CommittedBytes); Assert.Equal(descriptor.ContentHash, receiver.Completed!.ContentHash);
    }

    [Fact]
    public async Task CorruptChunkIsRejected()
    {
        var target = new InMemoryArtifactContentStore(); var root = Path.Combine(Path.GetTempPath(), "abraxius-fabric-" + Guid.NewGuid().ToString("N")); var receiver = new FileResumableBlobReceiver(root, target);
        var descriptor = new ArtifactContentDescriptor(new("deadbeef"), "00", 3, "application/octet-stream", null, new ArtifactLocation(ArtifactLocationKind.ContentStore, "memory"));
        var request = new FabricTransferRequest(FabricTransferId.New(), descriptor, FabricNodeId.New(), FabricNodeId.New(), DataClassification.Internal);
        try { await Assert.ThrowsAsync<InvalidDataException>(async () => await receiver.AppendAsync(request, new(request.Id, descriptor.BlobId, 0, new byte[] { 1, 2, 3 }, "bad", descriptor.ContentHash, 3, true))); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ProtobufRoundTripPreservesEpochAndIdentity()
    {
        var node = Node(FabricNodeId.New(), "Cpu"); var lease = Lease(node.Id, new(44), "proto"); var proto = GrpcFabricMapper.ToProto(FabricId.New(), FabricSessionId.New(), lease); var roundTrip = GrpcFabricMapper.FromProto(LeaseOffer.Parser.ParseFrom(proto.ToByteArray()));
        Assert.Equal(lease.Id, roundTrip.Id); Assert.Equal(lease.ExecutionId, roundTrip.ExecutionId); Assert.Equal(lease.CoordinatorEpoch, roundTrip.CoordinatorEpoch); Assert.Equal(lease.WorkerNodeId, roundTrip.WorkerNodeId);
    }

    [Fact]
    public async Task RuntimeIdentityPersistsAcrossRestart()
    {
        var root = Path.Combine(Path.GetTempPath(), "abraxius-fabric-identity-" + Guid.NewGuid().ToString("N")); var path = Path.Combine(root, "identity.json");
        try { var first = FabricIdentityPersistence.LoadOrCreate(path); var second = FabricIdentityPersistence.LoadOrCreate(path); Assert.Equal(first, second); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        await Task.CompletedTask;
    }

    [Fact]
    public async Task BoundedControlQueueCannotDropCriticalControlSilently()
    {
        await using var transport = new InMemoryFabricTransport(16); var node = FabricNodeId.New();
        for (var index = 0; index < 16; index++) await transport.SendControlAsync(node, new(node, (ulong)index, FabricControlPriority.Telemetry, "telemetry", ReadOnlyMemory<byte>.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await transport.SendControlAsync(node, new(node, 17, FabricControlPriority.Cancellation, "cancel", ReadOnlyMemory<byte>.Empty)));
    }

    [Fact]
    public async Task HeartbeatHealthUsesHysteresisAndSequenceOrdering()
    {
        var registry = new InMemoryFabricNodeRegistry(FabricId.New(), new(3)); var node = Node(FabricNodeId.New(), "Cpu"); registry.Upsert(node);
        await using var transport = new InMemoryFabricTransport(); var worker = new FabricWorker(node, new(3), new DelegateFabricLeaseExecutor((_, _, _) => ValueTask.FromResult(WorkResult.Empty()))); transport.Register(worker);
        await using var manager = new FabricConnectionManager(transport, registry); var session = await manager.ConnectAsync(node.Id); var now = DateTimeOffset.UtcNow;
        manager.Observe(new(node.Id, session.SessionId, new(3), 2, node.Resources, [], FabricNodeHealth.Healthy, now));
        manager.Observe(new(node.Id, session.SessionId, new(3), 1, node.Resources, [], FabricNodeHealth.Degraded, now.AddSeconds(1)));
        Assert.Equal((ulong)2, Assert.Single(manager.Sessions).LastNodeSequence);
        Assert.Empty(manager.Sweep(now.AddSeconds(6), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10))); Assert.Equal(FabricNodeHealth.Suspect, registry.Nodes.Single().Health);
        Assert.Single(manager.Sweep(now.AddSeconds(11), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10))); Assert.Equal(FabricNodeHealth.Offline, registry.Nodes.Single().Health);
    }

    private static FabricNodeDescriptor Node(FabricNodeId id, string capability, long vram = 0) => new(
        id, id.ToString(), new("fingerprint-" + id), NodeTrustState.Trusted, FabricNodeRole.Worker,
        "Linux", "X64", "10.0", FabricProtocolVersion.Current, [new(capability, "1")], [SandboxLevel.None], [],
        new(8, .1, 16L << 30, 12L << 30, vram == 0 ? [] : [new("gpu0", "test", "test", vram, vram, "test", [])], 100L << 30, NodePowerState.Ac, false, new(TimeSpan.FromMilliseconds(1), 1L << 30), DateTimeOffset.UtcNow),
        new([], 0, 1L << 30), [], FabricNodeHealth.Healthy, FabricConnectivity.Connected, FabricSessionId.New());

    private static ExecutionLease Lease(FabricNodeId node, FabricEpoch epoch, string key, long vram = 0)
    {
        var subject = SecuritySubject.System("test"); var auth = new RemoteAuthorizationContext(AuthorizationDecisionId.New(), null, null, subject, ["Cpu"], [$"remote://node/{node}"], null, DateTimeOffset.UtcNow.AddMinutes(5), DataClassification.Internal);
        return new(ExecutionLeaseId.New(), ExecutionId.New(), NodeId.New(), null, epoch, node, 1, key, "Cpu", WorkKind.Cpu, "test", [], auth, TimeSpan.FromMinutes(1), new(1, 1, vram), DateTimeOffset.UtcNow);
    }

    private static RemoteExecutionResult Result(ExecutionId execution, FabricNodeId node, FabricEpoch epoch, int attempt) => new(execution, ExecutionLeaseId.New(), attempt, epoch, node, LeaseExecutionStatus.Completed, WorkResult.Empty("done"), [], "hash", [], DateTimeOffset.UtcNow);

    private static RemoteWorkRequest RemoteRequest()
    {
        var execution = ExecutionId.New(); var node = new ExecutionNodeDefinition(NodeId.New(), TaskId.New(), execution, new CpuWorkDescriptor("noop"));
        return new(new RemoteHostId(FabricNodeId.New().Value), execution, node.TaskId, CorrelationId.New(), node, new Dictionary<NodeId, WorkResult>());
    }

    private static SchedulerWorkContext WorkContext(ExecutionNodeDefinition node)
    {
        var environment = PlatformEnvironmentFactory.CreateCurrent();
        return new SchedulerWorkContext { Node = node, DependencyResults = new Dictionary<NodeId, WorkResult>(), EvidenceStore = new InMemoryEvidenceStore(), Progress = new Progress<ProgressUpdate>(), CancellationToken = CancellationToken.None, Execution = new SchedulerExecutionContext { ExecutionId = node.ExecutionId, CorrelationId = CorrelationId.New(), Environment = environment, Executors = new WorkExecutorRegistry([]), SecurityContext = new Dictionary<string, string> { ["principal.type"] = "System", ["principal.id"] = "system:test" } } };
    }

    private sealed class InterruptingReceiver(IArtifactContentStore target, long failAfter) : IResumableBlobReceiver, IDisposable
    {
        private readonly MemoryStream _bytes = new(); public long FailAfter { get; set; } = failAfter; public ArtifactContentDescriptor? Completed { get; private set; }
        public ValueTask<long> GetCommittedLengthAsync(FabricTransferRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult(_bytes.Length);
        public ValueTask AppendAsync(FabricTransferRequest request, FabricBlobChunk chunk, CancellationToken cancellationToken = default)
        {
            if (_bytes.Length >= FailAfter) throw new IOException("synthetic disconnect");
            Assert.Equal(_bytes.Length, chunk.Offset); _bytes.Write(chunk.Content.Span); return ValueTask.CompletedTask;
        }
        public async ValueTask<ArtifactContentDescriptor> CompleteAsync(FabricTransferRequest request, CancellationToken cancellationToken = default)
        {
            _bytes.Position = 0; Completed = await target.PutAsync(_bytes, request.Content.MediaType, request.Content.FileName, cancellationToken); return Completed;
        }
        public void Dispose() => _bytes.Dispose();
    }
}

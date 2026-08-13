namespace Abraxius.Fabric;

public sealed record NodeSession(
    FabricNodeId NodeId,
    FabricSessionId SessionId,
    FabricEpoch CoordinatorEpoch,
    FabricConnectivity Connectivity,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastHeartbeat,
    ulong LastNodeSequence,
    string? Failure = null);

public interface IFabricConnectionManager : IAsyncDisposable
{
    IReadOnlyCollection<NodeSession> Sessions { get; }
    ValueTask<NodeSession> ConnectAsync(FabricNodeId nodeId, CancellationToken cancellationToken = default);
    ValueTask DisconnectAsync(FabricNodeId nodeId, CancellationToken cancellationToken = default);
    void Observe(FabricHeartbeat heartbeat);
    ImmutableArray<FabricNodeId> Sweep(DateTimeOffset now, TimeSpan suspectAfter, TimeSpan offlineAfter);
}

/// <summary>Tracks authenticated long-lived sessions; transports own reusable channels.</summary>
public sealed class FabricConnectionManager(IFabricTransport transport, IFabricNodeRegistry nodes) : IFabricConnectionManager
{
    private readonly ConcurrentDictionary<FabricNodeId, NodeSession> _sessions = new();
    public IReadOnlyCollection<NodeSession> Sessions => _sessions.Values.ToArray();

    public async ValueTask<NodeSession> ConnectAsync(FabricNodeId nodeId, CancellationToken cancellationToken = default)
    {
        if (!nodes.TryGet(nodeId, out var node) || node.TrustState != NodeTrustState.Trusted) throw new InvalidOperationException("Only explicitly trusted nodes can establish a Fabric session.");
        var negotiation = await transport.NegotiateAsync(nodeId, FabricProtocolVersion.Current, cancellationToken).ConfigureAwait(false);
        if (!negotiation.Compatible)
        {
            nodes.Upsert(node with { Health = FabricNodeHealth.Incompatible, Connectivity = FabricConnectivity.Disconnected, AcceptingLeases = false });
            throw new InvalidOperationException(negotiation.Reason);
        }
        var now = DateTimeOffset.UtcNow; var session = new NodeSession(nodeId, FabricSessionId.New(), nodes.Epoch, FabricConnectivity.Connected, now, now, 0);
        _sessions[nodeId] = session; nodes.Upsert(node with { SessionId = session.SessionId, Connectivity = FabricConnectivity.Connected, Health = FabricNodeHealth.Healthy, LastSeen = now }); return session;
    }

    public ValueTask DisconnectAsync(FabricNodeId nodeId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); _sessions.TryRemove(nodeId, out _);
        if (nodes.TryGet(nodeId, out var node)) nodes.Upsert(node with { Connectivity = FabricConnectivity.Disconnected, Health = FabricNodeHealth.Offline, AcceptingLeases = false });
        return ValueTask.CompletedTask;
    }

    public void Observe(FabricHeartbeat heartbeat)
    {
        if (heartbeat.CoordinatorEpoch != nodes.Epoch || !nodes.TryGet(heartbeat.NodeId, out var node) || node.TrustState != NodeTrustState.Trusted) return;
        _sessions.AddOrUpdate(heartbeat.NodeId,
            _ => new(heartbeat.NodeId, heartbeat.SessionId, heartbeat.CoordinatorEpoch, FabricConnectivity.Connected, heartbeat.ObservedAt, heartbeat.ObservedAt, heartbeat.NodeSequence),
            (_, current) => heartbeat.SessionId != current.SessionId || heartbeat.NodeSequence <= current.LastNodeSequence
                ? current
                : current with { Connectivity = FabricConnectivity.Connected, LastHeartbeat = heartbeat.ObservedAt, LastNodeSequence = heartbeat.NodeSequence, Failure = null });
        nodes.Upsert(node with { SessionId = heartbeat.SessionId, NodeSequence = heartbeat.NodeSequence, Resources = heartbeat.Resources, Health = heartbeat.Health, Connectivity = FabricConnectivity.Connected, LastSeen = heartbeat.ObservedAt });
    }

    public ImmutableArray<FabricNodeId> Sweep(DateTimeOffset now, TimeSpan suspectAfter, TimeSpan offlineAfter)
    {
        var offline = ImmutableArray.CreateBuilder<FabricNodeId>();
        foreach (var pair in _sessions)
        {
            var elapsed = now - pair.Value.LastHeartbeat; if (!nodes.TryGet(pair.Key, out var node)) continue;
            if (elapsed >= offlineAfter) { nodes.Upsert(node with { Health = FabricNodeHealth.Offline, Connectivity = FabricConnectivity.Disconnected, AcceptingLeases = false }); offline.Add(pair.Key); }
            else if (elapsed >= suspectAfter && node.Health != FabricNodeHealth.Suspect) nodes.Upsert(node with { Health = FabricNodeHealth.Suspect });
        }
        return offline.ToImmutable();
    }

    public ValueTask DisposeAsync() { _sessions.Clear(); return transport.DisposeAsync(); }
}

public interface ISideEffectReconciler
{
    ValueTask<SideEffectReconciliation> ReconcileAsync(ExecutionLease lease, RemoteExecutionResult? reportedResult, CancellationToken cancellationToken = default);
}

public sealed record SideEffectReconciliation(bool Succeeded, bool ActionOccurred, bool RetrySafe, string Explanation);

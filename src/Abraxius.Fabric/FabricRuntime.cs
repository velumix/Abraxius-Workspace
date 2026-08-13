using Abraxius.Platform;

namespace Abraxius.Fabric;

/// <summary>
/// Process-local composition root for Fabric membership and placement. Phase 4 remains the
/// scheduler; this runtime only exposes eligible remote hosts and a remote executor adapter.
/// </summary>
public sealed class FabricRuntime : IAsyncDisposable
{
    private readonly IFabricTransport _transport;
    private readonly IFabricNodeRegistry _nodes;
    private readonly ConcurrentDictionary<FabricNodeId, FabricWorker> _workers = new();

    public FabricRuntime(
        FabricNodeDescriptor localNode,
        IFabricTransport transport,
        IFabricNodeRegistry nodes,
        IExecutionPlacementEngine placement,
        IRemoteAuthorizationProvider authorization)
    {
        LocalNode = localNode;
        _transport = transport;
        _nodes = nodes;
        Placement = placement;
        _nodes.Upsert(localNode);
        ResultCommitter = new CanonicalResultCommitter(nodes);
        RemoteExecutor = new FabricRemoteExecutor(transport, nodes, authorization, ResultCommitter, placement, localNode.Id);
    }

    public FabricNodeDescriptor LocalNode { get; private set; }
    public FabricId Id => _nodes.FabricId;
    public FabricEpoch Epoch => _nodes.Epoch;
    public IReadOnlyCollection<FabricNodeDescriptor> Nodes => _nodes.Nodes;
    public IExecutionPlacementEngine Placement { get; }
    public CanonicalResultCommitter ResultCommitter { get; }
    public IRemoteWorkExecutor RemoteExecutor { get; }

    public ImmutableArray<RemoteCapabilityAdvertisement> RemoteHosts => _nodes.Nodes
        .Where(node => node.Id != LocalNode.Id && node.TrustState == NodeTrustState.Trusted && node.Connectivity == FabricConnectivity.Connected && node.AcceptingLeases)
        .Select(ToAdvertisement)
        .ToImmutableArray();

    public void RegisterInMemoryWorker(FabricWorker worker)
    {
        if (_transport is not InMemoryFabricTransport memory) throw new InvalidOperationException("The configured transport is not the deterministic in-memory transport.");
        _nodes.Upsert(worker.Descriptor);
        _workers[worker.Descriptor.Id] = worker;
        memory.Register(worker);
    }

    public bool Drain(FabricNodeId nodeId)
    {
        if (!_nodes.TryGet(nodeId, out var node)) return false;
        _workers.TryGetValue(nodeId, out var worker); worker?.Drain();
        _nodes.Upsert(node with { AcceptingLeases = false, Health = FabricNodeHealth.Draining });
        return true;
    }

    public bool Resume(FabricNodeId nodeId)
    {
        if (!_nodes.TryGet(nodeId, out var node) || node.TrustState != NodeTrustState.Trusted) return false;
        _workers.TryGetValue(nodeId, out var worker); worker?.Resume();
        _nodes.Upsert(node with { AcceptingLeases = true, Health = FabricNodeHealth.Healthy });
        return true;
    }

    public void UpdateLocalNode(Func<FabricNodeDescriptor, FabricNodeDescriptor> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        LocalNode = update(LocalNode);
        _nodes.Upsert(LocalNode);
    }

    public ValueTask DisposeAsync() => _transport.DisposeAsync();

    private static RemoteCapabilityAdvertisement ToAdvertisement(FabricNodeDescriptor node)
    {
        var family = Enum.TryParse<PlatformFamily>(node.Platform, true, out var parsedFamily) ? parsedFamily : PlatformFamily.Unknown;
        var architecture = Enum.TryParse<System.Runtime.InteropServices.Architecture>(node.Architecture, true, out var parsedArchitecture) ? parsedArchitecture : System.Runtime.InteropServices.Architecture.X64;
        var platform = new PlatformDescriptor(family, node.Platform, node.Platform, architecture, node.RuntimeVersion, Environment.Is64BitProcess);
        var capabilities = node.Capabilities.Select(capability => new CapabilityAdvertisement(new CapabilityId(capability.Id), CapabilityAvailability.Available, capability.Version, capability.Properties)).ToImmutableArray();
        return new(new RemoteHostId(node.Id.Value), node.DisplayName, AbraxiusProtocol.CurrentVersion, platform, RuntimeExecutionMode.Remote, capabilities);
    }
}

public static class FabricNodeFactory
{
    public static FabricNodeDescriptor CreateLocal(FabricNodeId id, IPlatformEnvironment environment, string displayName = "This device")
    {
        var memory = checked((long)Math.Min(environment.Device.ApproximateMemoryBytes ?? 0UL, long.MaxValue));
        var power = environment.Device.PowerSource switch
        {
            PowerSource.Battery => NodePowerState.Battery,
            PowerSource.LowBattery => NodePowerState.LowPower,
            PowerSource.Ac => NodePowerState.Ac,
            _ => NodePowerState.Unknown
        };
        var capabilities = environment.Capabilities.Values
            .Where(static capability => capability.Availability == CapabilityAvailability.Available)
            .Select(static capability => new FabricCapability(capability.Id.Value, capability.Version ?? "1", true, capability.Constraints?.ToImmutableDictionary(StringComparer.Ordinal)))
            .ToImmutableArray();
        return new(id, displayName, new NodeFingerprint("local-process"), NodeTrustState.Trusted,
            FabricNodeRole.Coordinator | FabricNodeRole.Worker | FabricNodeRole.ControlClient | FabricNodeRole.ArtifactHost,
            environment.Platform.Family.ToString(), environment.Platform.Architecture.ToString(), Environment.Version.ToString(), FabricProtocolVersion.Current,
            capabilities, [SandboxLevel.None, SandboxLevel.IsolatedWorkspace], [],
            new(environment.Device.LogicalProcessorCount, 0, memory, memory, [], 0, power, false, new(null, null), DateTimeOffset.UtcNow),
            new([], 0, 0), [], FabricNodeHealth.Healthy, FabricConnectivity.Connected, FabricSessionId.New(), LastSeen: DateTimeOffset.UtcNow);
    }
}

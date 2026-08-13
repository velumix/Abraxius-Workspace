namespace Abraxius.Fabric;

public interface IExecutionPlacementEngine
{
    ExecutionPlacementDecision Place(ExecutionPlacementRequest request, IReadOnlyCollection<FabricNodeDescriptor> nodes);
}

public sealed class DeterministicPlacementEngine : IExecutionPlacementEngine
{
    public ExecutionPlacementDecision Place(ExecutionPlacementRequest request, IReadOnlyCollection<FabricNodeDescriptor> nodes)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(nodes);
        var candidates = nodes.Select(node => Evaluate(request, node)).OrderByDescending(static item => item.Score).ThenBy(static item => item.NodeId.Value).ToImmutableArray();
        var selected = candidates.FirstOrDefault(static item => item.Eligible);
        if (selected is null) return new(null, false, candidates, "No node satisfies capability, trust, policy, platform, sandbox, locality, and resource requirements.");
        return new(selected.NodeId, selected.NodeId == request.OriginNode, candidates, $"Placed on {nodes.First(node => node.Id == selected.NodeId).DisplayName}: {string.Join("; ", selected.Reasons)}");
    }

    private static PlacementCandidate Evaluate(ExecutionPlacementRequest request, FabricNodeDescriptor node)
    {
        var reject = ImmutableArray.CreateBuilder<string>(); var reasons = ImmutableArray.CreateBuilder<string>(); double score = 0;
        if (node.TrustState != NodeTrustState.Trusted) reject.Add($"trust is {node.TrustState}");
        if (node.Health is FabricNodeHealth.Offline or FabricNodeHealth.Incompatible or FabricNodeHealth.Quarantined or FabricNodeHealth.Draining || !node.AcceptingLeases) reject.Add($"health is {node.Health}");
        if (!node.Roles.HasFlag(FabricNodeRole.Worker)) reject.Add("not a worker");
        if (!node.Capabilities.Any(item => item.Id.Equals(request.RequiredCapability, StringComparison.OrdinalIgnoreCase))) reject.Add($"missing {request.RequiredCapability}");
        if (request.Classification == DataClassification.LocalOnly && node.Id != request.OriginNode) reject.Add("LocalOnly is bound to the originating physical node");
        if (request.RequiredPlatform is not null && !node.Platform.Equals(request.RequiredPlatform, StringComparison.OrdinalIgnoreCase)) reject.Add($"requires {request.RequiredPlatform}");
        if (request.RequiredArchitecture is not null && !node.Architecture.Equals(request.RequiredArchitecture, StringComparison.OrdinalIgnoreCase)) reject.Add($"requires {request.RequiredArchitecture}");
        if (!node.Sandboxes.Contains(request.MinimumSandbox)) reject.Add($"missing {request.MinimumSandbox} sandbox");
        if (node.Resources.FreeRamBytes < request.RequiredMemoryBytes) reject.Add("insufficient RAM");
        if (request.RequiredVramBytes > 0 && !node.Resources.Gpus.Any(gpu => gpu.FreeMemoryBytes >= request.RequiredVramBytes)) reject.Add("insufficient VRAM");
        if (request.Affinity is { Kind: PlacementAffinityKind.RequireNode, NodeId: { } required } && node.Id != required) reject.Add("different from required node");
        if (request.Affinity is { Kind: PlacementAffinityKind.AvoidNode, NodeId: { } avoided } && node.Id == avoided) reject.Add("node explicitly avoided");
        if (reject.Count > 0) return new(node.Id, false, double.NegativeInfinity, [], reject.ToImmutable());

        score += node.Resources.CpuHeadroom * 30; reasons.Add($"{node.Resources.CpuHeadroom:P0} CPU headroom");
        score += Math.Clamp(node.Resources.FreeRamBytes / (double)Math.Max(1, node.Resources.TotalRamBytes), 0, 1) * 15;
        if (request.RequiredArtifactHashes is { Count: > 0 })
        {
            var hits = request.RequiredArtifactHashes.Count(node.Artifacts.ContentHashes.Contains);
            score += 25d * hits / request.RequiredArtifactHashes.Count; if (hits > 0) reasons.Add($"{hits}/{request.RequiredArtifactHashes.Count} inputs cached");
        }
        if (request.RequiredRepositoryId is not null && node.Repositories.Any(repo => repo.RepositoryId == request.RequiredRepositoryId)) { score += 20; reasons.Add("repository is local"); }
        if (request.RequiredVramBytes > 0 && node.Resources.Gpus.Any(gpu => gpu.FreeMemoryBytes >= request.RequiredVramBytes)) { score += 15; reasons.Add("GPU/VRAM eligible"); }
        if (request.Affinity is { Kind: PlacementAffinityKind.PreferNode, NodeId: { } preferred } && node.Id == preferred) { score += 30; reasons.Add("user preference"); }
        if (request.Background && node.Resources.PowerState is NodePowerState.Battery or NodePowerState.LowPower) { score -= 30; reasons.Add("background penalty on battery"); }
        if (node.Resources.Network.RoundTripTime is { } rtt) score -= Math.Min(20, rtt.TotalMilliseconds / 10);
        return new(node.Id, true, score, reasons.ToImmutable(), []);
    }
}

public interface IFabricNodeDiscovery { IAsyncEnumerable<DiscoveredFabricNode> DiscoverAsync(CancellationToken cancellationToken = default); }
public sealed class ManualFabricNodeDiscovery(IEnumerable<FabricEndpoint> endpoints) : IFabricNodeDiscovery
{
    private readonly ImmutableArray<FabricEndpoint> _endpoints = endpoints.ToImmutableArray();
    public async IAsyncEnumerable<DiscoveredFabricNode> DiscoverAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var endpoint in _endpoints) { cancellationToken.ThrowIfCancellationRequested(); yield return new(endpoint, null, endpoint.Address.Host, null); await Task.Yield(); }
    }
}

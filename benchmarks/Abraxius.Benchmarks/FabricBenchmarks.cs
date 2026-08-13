using System.Collections.Immutable;
using Abraxius.Core;
using Abraxius.Fabric;
using Abraxius.Protocol;
using Abraxius.Security;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class FabricBenchmarks
{
    private FabricNodeDescriptor[] _nodes = null!;
    private ExecutionPlacementRequest _request = null!;
    private CanonicalResultCommitter _committer = null!;
    private RemoteExecutionResult _result = null!;

    [Params(4, 100, 1000)] public int NodeCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var origin = FabricNodeId.New();
        _nodes = Enumerable.Range(0, NodeCount).Select(index => Node(FabricNodeId.New(), index)).ToArray();
        _request = new(ExecutionId.New(), NodeId.New(), WorkKind.Cpu, "Cpu", DataClassification.Internal, origin, RequiredMemoryBytes: 64 * 1024 * 1024);
        var registry = new InMemoryFabricNodeRegistry(FabricId.New(), new(1)); foreach (var node in _nodes) registry.Upsert(node);
        _committer = new(registry); _result = new(ExecutionId.New(), ExecutionLeaseId.New(), 1, new(1), _nodes[0].Id, LeaseExecutionStatus.Completed, WorkResult.Empty("done"), [], "hash", [], DateTimeOffset.UtcNow);
    }

    [Benchmark] public ExecutionPlacementDecision Place() => new DeterministicPlacementEngine().Place(_request, _nodes);
    [Benchmark] public ResultCommitDecision DuplicateCanonicalCommit() { _committer.Commit(_result); return _committer.Commit(_result); }

    private static FabricNodeDescriptor Node(FabricNodeId id, int index) => new(id, $"node-{index}", new($"fp-{index}"), NodeTrustState.Trusted, FabricNodeRole.Worker,
        "Linux", "X64", "10", FabricProtocolVersion.Current, [new("Cpu", "1")], [SandboxLevel.None], [],
        new(16, index / 1000d, 32L << 30, 24L << 30, [], 100L << 30, NodePowerState.Ac, false, new(TimeSpan.FromMilliseconds(index % 20), 1L << 30), DateTimeOffset.UtcNow),
        new(ImmutableHashSet<string>.Empty, 0, 1L << 30), [], FabricNodeHealth.Healthy, FabricConnectivity.Connected, FabricSessionId.New());
}

using System.Collections.Immutable;
using Abraxius.Core;
using Abraxius.Protocol;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class GraphBenchmarks
{
    [Params(100, 1_000, 10_000)]
    public int Nodes { get; set; }

    private ExecutionGraph _graph = null!;

    [GlobalSetup]
    public void Setup()
    {
        var executionId = ExecutionId.New();
        var definitions = new List<ExecutionNodeDefinition>(Nodes);
        NodeId? previous = null;
        for (var index = 0; index < Nodes; index++)
        {
            var nodeId = NodeId.New();
            var dependencies = previous is { } dependency ? [dependency] : Array.Empty<NodeId>();
            definitions.Add(new ExecutionNodeDefinition(
                nodeId,
                TaskId.New(),
                executionId,
                new CpuWorkDescriptor("benchmark"),
                dependencies.ToImmutableArray(),
                creationOrder: index));
            previous = nodeId;
        }

        _graph = new ExecutionGraph(executionId, CorrelationId.New(), definitions);
    }

    [Benchmark]
    public GraphValidationResult Validate() => ExecutionGraphValidator.Validate(_graph);

    [Benchmark]
    public CompiledExecutionGraph Compile() => _graph.Compile();
}

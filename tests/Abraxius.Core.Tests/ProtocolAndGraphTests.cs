using System.Collections.Immutable;
using System.Text.Json;
using Abraxius.Core;
using Abraxius.Protocol;
using Xunit;

namespace Abraxius.Core.Tests;

public sealed class ProtocolAndGraphTests
{
    [Fact]
    public void StrongIdentifiersRoundTripAndRemainDistinctTypes()
    {
        var taskId = TaskId.New();
        var executionId = ExecutionId.New();
        var capabilityId = new CapabilityId("filesystem");
        var options = ProtocolJson.CreateOptions();

        var json = JsonSerializer.Serialize(new ProtocolEnvelope<CapabilityId>(
            AbraxiusProtocol.CurrentVersion,
            "capability",
            CorrelationId.New(),
            executionId,
            DateTimeOffset.UtcNow,
            capabilityId), options);
        var copy = JsonSerializer.Deserialize<ProtocolEnvelope<CapabilityId>>(json, options);

        Assert.Equal(AbraxiusProtocol.CurrentVersion, copy!.Version);
        Assert.Equal(capabilityId, copy.Payload);
        Assert.NotEqual(taskId.Value, executionId.Value);
        Assert.Equal(taskId.ToString(), taskId.Value.ToString("N"));
    }

    [Fact]
    public void ValidGraphCompilesWithRootsLeavesAndReverseEdges()
    {
        var executionId = ExecutionId.New();
        var root = Node(executionId);
        var left = Node(executionId, root.Id);
        var right = Node(executionId, root.Id);
        var join = Node(executionId, left.Id, right.Id);
        var graph = new ExecutionGraph(executionId, CorrelationId.New(), [root, left, right, join]);

        var compiled = graph.Compile();

        Assert.Equal(4, compiled.Nodes.Length);
        Assert.Single(compiled.RootIndexes);
        Assert.Single(compiled.LeafIndexes);
        Assert.Equal(2, compiled.GetDependents(root.Id).Length);
        Assert.Equal(join.Id, compiled.Nodes[compiled.TopologicalOrder[^1]].Id);
        Assert.True(compiled.TryGetIndex(join.Id, out var joinIndex));
        Assert.Equal(2, compiled.DependencyIndexes[joinIndex].Length);
    }

    [Fact]
    public void TopologicalOrderPlacesEveryDependencyBeforeDependent()
    {
        var executionId = ExecutionId.New();
        var nodes = new[]
        {
            Node(executionId),
            Node(executionId),
            Node(executionId)
        };
        var dependent = Node(executionId, nodes[0].Id, nodes[1].Id, nodes[2].Id);
        var compiled = new ExecutionGraph(executionId, CorrelationId.New(), [.. nodes, dependent]).Compile();
        var positions = compiled.TopologicalOrder.Select((nodeIndex, position) => (nodeIndex, position)).ToDictionary(item => item.nodeIndex, item => item.position);

        foreach (var nodeIndex in Enumerable.Range(0, compiled.Nodes.Length))
        {
            foreach (var dependencyIndex in compiled.DependencyIndexes[nodeIndex])
            {
                Assert.True(positions[dependencyIndex] < positions[nodeIndex]);
            }
        }
    }

    [Fact]
    public void MissingDependencyIsStructuredValidationError()
    {
        var executionId = ExecutionId.New();
        var missing = NodeId.New();
        var graph = new ExecutionGraph(executionId, CorrelationId.New(), [Node(executionId, missing)]);

        var result = ExecutionGraphValidator.Validate(graph);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == GraphValidationErrorCode.MissingDependency);
        var exception = Assert.Throws<ExecutionGraphValidationException>(() => graph.Compile());
        Assert.Contains(exception.Errors, error => error.Code == GraphValidationErrorCode.MissingDependency);
    }

    [Fact]
    public void DuplicateSelfAndWrongExecutionNodesAreRejected()
    {
        var executionId = ExecutionId.New();
        var duplicateId = NodeId.New();
        var first = Node(executionId, id: duplicateId);
        var second = Node(executionId, id: duplicateId);
        var wrongExecution = Node(ExecutionId.New());
        var self = Node(executionId, id: NodeId.New());
        self = new ExecutionNodeDefinition(self.Id, self.TaskId, executionId, self.Work, [self.Id]);

        var result = ExecutionGraphValidator.Validate(new ExecutionGraph(executionId, CorrelationId.New(), [first, second, wrongExecution, self]));

        Assert.Contains(result.Errors, error => error.Code == GraphValidationErrorCode.DuplicateNodeId);
        Assert.Contains(result.Errors, error => error.Code == GraphValidationErrorCode.WrongExecution);
        Assert.Contains(result.Errors, error => error.Code == GraphValidationErrorCode.SelfDependency);
    }

    [Fact]
    public void CyclesAreRejectedWithoutRecursiveTraversal()
    {
        var executionId = ExecutionId.New();
        var a = Node(executionId);
        var b = Node(executionId, a.Id);
        var c = Node(executionId, b.Id);
        a = new ExecutionNodeDefinition(a.Id, a.TaskId, executionId, a.Work, [c.Id]);

        var result = ExecutionGraphValidator.Validate(new ExecutionGraph(executionId, CorrelationId.New(), [a, b, c]));

        Assert.Contains(result.Errors, error => error.Code == GraphValidationErrorCode.Cycle);
    }

    [Fact]
    public void LargeLinearGraphCompilesWithLinearEdgeStorage()
    {
        var executionId = ExecutionId.New();
        var nodes = new List<ExecutionNodeDefinition>(10_000);
        NodeId? previous = null;
        for (var index = 0; index < 10_000; index++)
        {
            var node = previous is { } previousId
                ? Node(executionId, dependencies: [previousId], creationOrder: index)
                : Node(executionId, creationOrder: index);
            nodes.Add(node);
            previous = node.Id;
        }

        var compiled = new ExecutionGraph(executionId, CorrelationId.New(), nodes).Compile();

        Assert.Equal(10_000, compiled.Nodes.Length);
        Assert.Equal(9_999, compiled.DependencyIndexes.Sum(static dependencies => dependencies.Length));
        Assert.Equal(10_000, compiled.TopologicalOrder.Length);
    }

    [Fact]
    public void StateRulesRejectInvalidTransitionsAndTrackRuntimeTimestamps()
    {
        Assert.True(ExecutionStateRules.CanTransition(ExecutionState.Pending, ExecutionState.Ready));
        Assert.False(ExecutionStateRules.CanTransition(ExecutionState.Succeeded, ExecutionState.Running));
        Assert.Throws<InvalidExecutionStateTransitionException>(() =>
            ExecutionStateRules.EnsureTransition(ExecutionState.Succeeded, ExecutionState.Running));

        var created = new ExecutionNodeRuntimeState(NodeId.New(), ExecutionId.New());
        var ready = created.TransitionTo(ExecutionState.Ready, DateTimeOffset.UnixEpoch);
        var running = ready.TransitionTo(ExecutionState.Queued, DateTimeOffset.UnixEpoch.AddSeconds(1))
            .TransitionTo(ExecutionState.Running, DateTimeOffset.UnixEpoch.AddSeconds(2), 1);

        Assert.Equal(1, running.Attempt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(2), running.StartedAt);
    }

    [Fact]
    public void SpeculationGroupsMustReferenceTheirCandidates()
    {
        var executionId = ExecutionId.New();
        var first = Node(executionId, speculationGroupId: SpeculationGroupId.New());
        var group = new SpeculationGroupDefinition(first.SpeculationGroupId!.Value, [first.Id]);
        var result = ExecutionGraphValidator.Validate(new ExecutionGraph(executionId, CorrelationId.New(), [first], speculationGroups: [group]));

        Assert.Contains(result.Errors, error => error.Code == GraphValidationErrorCode.InvalidSpeculationGroup);
    }

    private static ExecutionNodeDefinition Node(
        ExecutionId executionId,
        params NodeId[] dependencies) => Node(executionId, null, dependencies, 0);

    private static ExecutionNodeDefinition Node(
        ExecutionId executionId,
        NodeId? id = null,
        NodeId[]? dependencies = null,
        int creationOrder = 0,
        SpeculationGroupId? speculationGroupId = null) =>
        new(
            id ?? NodeId.New(),
            TaskId.New(),
            executionId,
            new CpuWorkDescriptor("test"),
            dependencies?.ToImmutableArray() ?? ImmutableArray<NodeId>.Empty,
            priority: WorkPriority.Normal,
            speculationGroupId: speculationGroupId,
            creationOrder: creationOrder);
}

using System.Collections.Immutable;
using System.Collections.Frozen;
using Abraxius.Protocol;

namespace Abraxius.Core;

public enum WorkKind
{
    ModelInference,
    ToolExecution,
    MemoryLookup,
    Cpu,
    Io,
    Verification,
    Synthesis,
    Background
}

public sealed record OutputContract(string Name, string? JsonSchema = null);

public abstract record WorkDescriptor
{
    public abstract WorkKind Kind { get; }
    public virtual bool IsReadOnly => true;
}

public sealed record ModelWorkDescriptor(
    string Instruction,
    string? Model = null,
    OutputContract? ExpectedOutput = null,
    int? MaxOutputTokens = null,
    bool Stream = false) : WorkDescriptor
{
    public override WorkKind Kind => WorkKind.ModelInference;
}

public sealed record ToolWorkDescriptor(
    CapabilityId Capability,
    string Operation,
    ActionTarget Target,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool Mutation = false) : WorkDescriptor
{
    public override WorkKind Kind => WorkKind.ToolExecution;
    public override bool IsReadOnly => !Mutation;
}

public sealed record MemoryWorkDescriptor(
    string Query,
    int Limit = 8,
    IReadOnlyList<string>? Scopes = null) : WorkDescriptor
{
    public override WorkKind Kind => WorkKind.MemoryLookup;
}

public sealed record CpuWorkDescriptor(
    string OperationCode,
    IReadOnlyDictionary<string, string>? Parameters = null) : WorkDescriptor
{
    public override WorkKind Kind => WorkKind.Cpu;
}

public sealed record IoWorkDescriptor(
    string OperationCode,
    IReadOnlyDictionary<string, string>? Parameters = null) : WorkDescriptor
{
    public override WorkKind Kind => WorkKind.Io;
}

public sealed record VerificationWorkDescriptor(
    string Objective,
    ImmutableArray<NodeId> Inputs,
    string? Profile = null) : WorkDescriptor
{
    public override WorkKind Kind => WorkKind.Verification;
}

public sealed record SynthesisWorkDescriptor(
    string Objective,
    ImmutableArray<NodeId> Inputs,
    OutputContract? ExpectedOutput = null) : WorkDescriptor
{
    public override WorkKind Kind => WorkKind.Synthesis;
}

public sealed record BackgroundWorkDescriptor(
    string OperationCode,
    IReadOnlyDictionary<string, string>? Parameters = null) : WorkDescriptor
{
    public override WorkKind Kind => WorkKind.Background;
}

public sealed record ActionTarget(string Value, string? Scope = null)
{
    public override string ToString() => Scope is null ? Value : $"{Scope}:{Value}";
}

public enum SpeculationPolicy
{
    FirstSuccessful,
    HighestScore,
    BestVerified,
    LowestCostSuccessful
}

public sealed record SpeculationGroupDefinition(
    SpeculationGroupId Id,
    ImmutableArray<NodeId> Candidates,
    SpeculationPolicy WinnerPolicy = SpeculationPolicy.FirstSuccessful);

public sealed record ExecutionNodeDefinition
{
    public ExecutionNodeDefinition(
        NodeId id,
        TaskId taskId,
        ExecutionId executionId,
        WorkDescriptor work,
        ImmutableArray<NodeId> dependencies = default,
        NodeId? parentNodeId = null,
        WorkPriority priority = WorkPriority.Normal,
        DateTimeOffset? deadline = null,
        TimeSpan? timeout = null,
        RetryPolicy? retryPolicy = null,
        ResourceHints resourceHints = default,
        SpeculationGroupId? speculationGroupId = null,
        int creationOrder = 0)
    {
        Id = id;
        TaskId = taskId;
        ExecutionId = executionId;
        Work = work ?? throw new ArgumentNullException(nameof(work));
        Dependencies = dependencies.IsDefault ? ImmutableArray<NodeId>.Empty : dependencies;
        ParentNodeId = parentNodeId;
        Priority = priority;
        Deadline = deadline;
        Timeout = timeout;
        RetryPolicy = retryPolicy ?? new RetryPolicy();
        ResourceHints = resourceHints;
        SpeculationGroupId = speculationGroupId;
        CreationOrder = creationOrder;
    }

    public NodeId Id { get; }
    public TaskId TaskId { get; }
    public ExecutionId ExecutionId { get; }
    public WorkDescriptor Work { get; }
    public ImmutableArray<NodeId> Dependencies { get; }
    public NodeId? ParentNodeId { get; }
    public WorkKind WorkKind => Work.Kind;
    public WorkPriority Priority { get; }
    public DateTimeOffset? Deadline { get; }
    public TimeSpan? Timeout { get; }
    public RetryPolicy RetryPolicy { get; }
    public ResourceHints ResourceHints { get; }
    public SpeculationGroupId? SpeculationGroupId { get; }
    public int CreationOrder { get; }
}

public sealed class ExecutionGraph
{
    public ExecutionGraph(
        ExecutionId executionId,
        CorrelationId correlationId,
        IEnumerable<ExecutionNodeDefinition> nodes,
        IEnumerable<NodeId>? rootNodeIds = null,
        IEnumerable<SpeculationGroupDefinition>? speculationGroups = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ExecutionId = executionId;
        CorrelationId = correlationId;
        Nodes = (nodes ?? throw new ArgumentNullException(nameof(nodes))).ToImmutableArray();
        RootNodeIds = rootNodeIds?.ToImmutableArray() ?? ImmutableArray<NodeId>.Empty;
        HasExplicitRoots = rootNodeIds is not null;
        SpeculationGroups = speculationGroups?.ToImmutableArray() ?? ImmutableArray<SpeculationGroupDefinition>.Empty;
        Metadata = metadata is null
            ? ImmutableDictionary<string, string>.Empty
            : metadata.ToImmutableDictionary(StringComparer.Ordinal);
    }

    public ExecutionId ExecutionId { get; }
    public CorrelationId CorrelationId { get; }
    public ImmutableArray<ExecutionNodeDefinition> Nodes { get; }
    public ImmutableArray<NodeId> RootNodeIds { get; }
    public bool HasExplicitRoots { get; }
    public ImmutableArray<SpeculationGroupDefinition> SpeculationGroups { get; }
    public ImmutableDictionary<string, string> Metadata { get; }

    public CompiledExecutionGraph Compile() => ExecutionGraphCompiler.Compile(this);
}

public enum GraphValidationErrorCode
{
    EmptyGraph,
    EmptyNodeId,
    EmptyTaskId,
    WrongExecution,
    DuplicateNodeId,
    DuplicateTaskId,
    MissingDependency,
    SelfDependency,
    MissingParent,
    InvalidRoot,
    DuplicateDependency,
    Cycle,
    DuplicateSpeculationGroup,
    MissingSpeculationGroup,
    InvalidSpeculationGroup
}

public sealed record GraphValidationError(
    GraphValidationErrorCode Code,
    string Message,
    NodeId? NodeId = null,
    NodeId? RelatedNodeId = null);

public sealed class GraphValidationResult
{
    public GraphValidationResult(ImmutableArray<GraphValidationError> errors) => Errors = errors;
    public ImmutableArray<GraphValidationError> Errors { get; }
    public bool IsValid => Errors.IsEmpty;
}

public sealed class ExecutionGraphValidationException : Exception
{
    public ExecutionGraphValidationException(ImmutableArray<GraphValidationError> errors)
        : base($"Execution graph validation failed with {errors.Length} error(s).") => Errors = errors;

    public ImmutableArray<GraphValidationError> Errors { get; }
}

public sealed class CompiledExecutionGraph
{
    internal CompiledExecutionGraph(
        ExecutionGraph source,
        ImmutableArray<ExecutionNodeDefinition> nodes,
        FrozenDictionary<NodeId, int> indexById,
        ImmutableArray<ImmutableArray<int>> dependencyIndexes,
        ImmutableArray<ImmutableArray<int>> dependentIndexes,
        ImmutableArray<int> initialDependencyCounts,
        ImmutableArray<int> topologicalOrder,
        ImmutableArray<int> rootIndexes,
        ImmutableArray<int> leafIndexes)
    {
        Source = source;
        Nodes = nodes;
        IndexById = indexById;
        DependencyIndexes = dependencyIndexes;
        DependentIndexes = dependentIndexes;
        InitialDependencyCounts = initialDependencyCounts;
        TopologicalOrder = topologicalOrder;
        RootIndexes = rootIndexes;
        LeafIndexes = leafIndexes;
    }

    public ExecutionGraph Source { get; }
    public ImmutableArray<ExecutionNodeDefinition> Nodes { get; }
    public FrozenDictionary<NodeId, int> IndexById { get; }
    public ImmutableArray<ImmutableArray<int>> DependencyIndexes { get; }
    public ImmutableArray<ImmutableArray<int>> DependentIndexes { get; }
    public ImmutableArray<int> InitialDependencyCounts { get; }
    public ImmutableArray<int> TopologicalOrder { get; }
    public ImmutableArray<int> RootIndexes { get; }
    public ImmutableArray<int> LeafIndexes { get; }

    public bool TryGetIndex(NodeId nodeId, out int index) => IndexById.TryGetValue(nodeId, out index);

    public ImmutableArray<NodeId> GetDependents(NodeId nodeId) =>
        IndexById.TryGetValue(nodeId, out var index)
            ? DependentIndexes[index].Select(dependent => Nodes[dependent].Id).ToImmutableArray()
            : ImmutableArray<NodeId>.Empty;
}

public static class ExecutionGraphValidator
{
    public static GraphValidationResult Validate(ExecutionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var errors = ImmutableArray.CreateBuilder<GraphValidationError>();
        if (graph.Nodes.IsEmpty)
        {
            errors.Add(new(GraphValidationErrorCode.EmptyGraph, "An execution graph must contain at least one node."));
            return new GraphValidationResult(errors.ToImmutable());
        }

        var indexes = new Dictionary<NodeId, int>(graph.Nodes.Length);
        var taskIds = new HashSet<TaskId>();
        for (var index = 0; index < graph.Nodes.Length; index++)
        {
            var node = graph.Nodes[index];
            if (node.Id == NodeId.Empty)
            {
                errors.Add(new(GraphValidationErrorCode.EmptyNodeId, "A graph node cannot use the empty node ID.", node.Id));
            }

            if (node.TaskId == TaskId.Empty)
            {
                errors.Add(new(GraphValidationErrorCode.EmptyTaskId, "A graph node cannot use the empty task ID.", node.Id));
            }

            if (node.ExecutionId != graph.ExecutionId)
            {
                errors.Add(new(GraphValidationErrorCode.WrongExecution, $"Node {node.Id} belongs to another execution.", node.Id));
            }

            if (!indexes.TryAdd(node.Id, index))
            {
                errors.Add(new(GraphValidationErrorCode.DuplicateNodeId, $"Node ID {node.Id} occurs more than once.", node.Id));
            }

            if (!taskIds.Add(node.TaskId))
            {
                errors.Add(new(GraphValidationErrorCode.DuplicateTaskId, $"Task ID {node.TaskId} occurs more than once in the graph.", node.Id));
            }
        }

        foreach (var node in graph.Nodes)
        {
            if (node.ParentNodeId is { } parent && !indexes.ContainsKey(parent))
            {
                errors.Add(new(GraphValidationErrorCode.MissingParent, $"Node {node.Id} references missing parent {parent}.", node.Id, parent));
            }

            foreach (var dependency in node.Dependencies)
            {
                if (dependency == node.Id)
                {
                    errors.Add(new(GraphValidationErrorCode.SelfDependency, $"Node {node.Id} depends on itself.", node.Id));
                }
                else if (!indexes.ContainsKey(dependency))
                {
                    errors.Add(new(GraphValidationErrorCode.MissingDependency, $"Node {node.Id} depends on missing node {dependency}.", node.Id, dependency));
                }
            }

            if (node.Dependencies.Length > 1 && node.Dependencies.Distinct().Count() != node.Dependencies.Length)
            {
                errors.Add(new(GraphValidationErrorCode.DuplicateDependency, $"Node {node.Id} lists the same dependency more than once.", node.Id));
            }
        }

        if (graph.HasExplicitRoots)
        {
            if (graph.RootNodeIds.IsEmpty)
            {
                errors.Add(new(GraphValidationErrorCode.InvalidRoot, "An explicit root definition must contain at least one root node."));
            }

            var roots = new HashSet<NodeId>();
            foreach (var root in graph.RootNodeIds)
            {
                if (!roots.Add(root))
                {
                    errors.Add(new(GraphValidationErrorCode.InvalidRoot, $"Root node {root} is listed more than once.", root));
                }

                if (!indexes.TryGetValue(root, out var index))
                {
                    errors.Add(new(GraphValidationErrorCode.InvalidRoot, $"Root node {root} does not exist.", root));
                }
                else if (!graph.Nodes[index].Dependencies.IsEmpty)
                {
                    errors.Add(new(GraphValidationErrorCode.InvalidRoot, $"Root node {root} has dependencies.", root));
                }
            }
        }

        ValidateSpeculationGroups(graph, indexes, errors);
        ValidateAcyclic(graph, indexes, errors);
        return new GraphValidationResult(errors.ToImmutable());
    }

    private static void ValidateSpeculationGroups(
        ExecutionGraph graph,
        Dictionary<NodeId, int> indexes,
        ImmutableArray<GraphValidationError>.Builder errors)
    {
        var groups = new HashSet<SpeculationGroupId>();
        foreach (var group in graph.SpeculationGroups)
        {
            if (!groups.Add(group.Id))
            {
                errors.Add(new(GraphValidationErrorCode.DuplicateSpeculationGroup, $"Speculation group {group.Id} occurs more than once."));
            }

            if (group.Candidates.Length < 2)
            {
                errors.Add(new(GraphValidationErrorCode.InvalidSpeculationGroup, $"Speculation group {group.Id} must contain at least two candidates."));
            }

            foreach (var candidate in group.Candidates)
            {
                if (!indexes.TryGetValue(candidate, out var index))
                {
                    errors.Add(new(GraphValidationErrorCode.InvalidSpeculationGroup, $"Speculation group {group.Id} references missing node {candidate}.", candidate));
                }
                else if (graph.Nodes[index].SpeculationGroupId != group.Id)
                {
                    errors.Add(new(GraphValidationErrorCode.InvalidSpeculationGroup, $"Node {candidate} is not assigned to speculation group {group.Id}.", candidate));
                }
            }
        }

        foreach (var node in graph.Nodes)
        {
            if (node.SpeculationGroupId is { } groupId && !groups.Contains(groupId))
            {
                errors.Add(new(GraphValidationErrorCode.MissingSpeculationGroup, $"Node {node.Id} references missing speculation group {groupId}.", node.Id));
            }
        }
    }

    private static void ValidateAcyclic(
        ExecutionGraph graph,
        Dictionary<NodeId, int> indexes,
        ImmutableArray<GraphValidationError>.Builder errors)
    {
        var indegree = new int[graph.Nodes.Length];
        var dependents = new List<int>[graph.Nodes.Length];
        for (var i = 0; i < dependents.Length; i++)
        {
            dependents[i] = [];
        }

        for (var i = 0; i < graph.Nodes.Length; i++)
        {
            foreach (var dependency in graph.Nodes[i].Dependencies)
            {
                if (indexes.TryGetValue(dependency, out var dependencyIndex) && dependency != graph.Nodes[i].Id)
                {
                    indegree[i]++;
                    dependents[dependencyIndex].Add(i);
                }
            }
        }

        var queue = new Queue<int>(graph.Nodes.Length);
        for (var i = 0; i < indegree.Length; i++)
        {
            if (indegree[i] == 0)
            {
                queue.Enqueue(i);
            }
        }

        var visited = 0;
        while (queue.TryDequeue(out var current))
        {
            visited++;
            foreach (var dependent in dependents[current])
            {
                if (--indegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (visited != graph.Nodes.Length)
        {
            errors.Add(new(GraphValidationErrorCode.Cycle, "The execution graph contains a dependency cycle."));
        }
    }
}

public static class ExecutionGraphCompiler
{
    public static CompiledExecutionGraph Compile(ExecutionGraph graph)
    {
        var validation = ExecutionGraphValidator.Validate(graph);
        if (!validation.IsValid)
        {
            throw new ExecutionGraphValidationException(validation.Errors);
        }

        var nodes = graph.Nodes;
        var indexById = nodes.Select((node, index) => (node.Id, index)).ToFrozenDictionary(item => item.Id, item => item.index);
        var dependencies = new ImmutableArray<int>.Builder[nodes.Length];
        var dependents = new ImmutableArray<int>.Builder[nodes.Length];
        for (var i = 0; i < nodes.Length; i++)
        {
            dependencies[i] = ImmutableArray.CreateBuilder<int>(nodes[i].Dependencies.Length);
            dependents[i] = ImmutableArray.CreateBuilder<int>();
        }

        for (var i = 0; i < nodes.Length; i++)
        {
            foreach (var dependency in nodes[i].Dependencies)
            {
                var dependencyIndex = indexById[dependency];
                dependencies[i].Add(dependencyIndex);
                dependents[dependencyIndex].Add(i);
            }
        }

        var initialCounts = dependencies.Select(static values => values.Count).ToImmutableArray();
        var topologicalOrder = BuildTopologicalOrder(dependencies, dependents, nodes.Length);
        var rootIndexes = Enumerable.Range(0, nodes.Length).Where(index => dependencies[index].Count == 0).ToImmutableArray();
        var leafIndexes = Enumerable.Range(0, nodes.Length).Where(index => dependents[index].Count == 0).ToImmutableArray();

        return new CompiledExecutionGraph(
            graph,
            nodes,
            indexById,
            dependencies.Select(static values => values.ToImmutable()).ToImmutableArray(),
            dependents.Select(static values => values.ToImmutable()).ToImmutableArray(),
            initialCounts,
            topologicalOrder,
            rootIndexes,
            leafIndexes);
    }

    private static ImmutableArray<int> BuildTopologicalOrder(
        ImmutableArray<int>.Builder[] dependencies,
        ImmutableArray<int>.Builder[] dependents,
        int nodeCount)
    {
        var remaining = dependencies.Select(static values => values.Count).ToArray();
        var queue = new Queue<int>(nodeCount);
        for (var i = 0; i < remaining.Length; i++)
        {
            if (remaining[i] == 0)
            {
                queue.Enqueue(i);
            }
        }

        var order = ImmutableArray.CreateBuilder<int>(nodeCount);
        while (queue.TryDequeue(out var current))
        {
            order.Add(current);
            foreach (var dependent in dependents[current])
            {
                if (--remaining[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        return order.ToImmutable();
    }
}

public static class ExecutionStateRules
{
    public static bool IsTerminal(ExecutionState state) => state is
        ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.TimedOut or ExecutionState.Skipped;

    public static bool CanTransition(ExecutionState current, ExecutionState next) => current switch
    {
        ExecutionState.Pending => next is ExecutionState.Ready or ExecutionState.Cancelled or ExecutionState.Skipped,
        ExecutionState.Ready => next is ExecutionState.Queued or ExecutionState.Cancelled or ExecutionState.Skipped,
        ExecutionState.Queued => next is ExecutionState.Running or ExecutionState.Cancelled or ExecutionState.Skipped,
        ExecutionState.Running => next is ExecutionState.Waiting or ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.TimedOut,
        ExecutionState.Waiting => next is ExecutionState.Running or ExecutionState.Cancelled or ExecutionState.TimedOut,
        _ => false
    };

    public static void EnsureTransition(ExecutionState current, ExecutionState next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidExecutionStateTransitionException(current, next);
        }
    }
}

public sealed class InvalidExecutionStateTransitionException : Exception
{
    public InvalidExecutionStateTransitionException(ExecutionState current, ExecutionState next)
        : base($"Invalid execution state transition: {current} -> {next}.")
    {
        Current = current;
        Next = next;
    }

    public ExecutionState Current { get; }
    public ExecutionState Next { get; }
}

public sealed record ExecutionNodeRuntimeState(
    NodeId NodeId,
    ExecutionId ExecutionId,
    ExecutionState State = ExecutionState.Pending,
    int Attempt = 0,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    ResultId? ResultId = null,
    IReadOnlyList<EvidenceId>? Evidence = null,
    RuntimeError? Error = null)
{
    public ExecutionNodeRuntimeState TransitionTo(ExecutionState next, DateTimeOffset? timestamp = null) =>
        TransitionTo(next, timestamp, Attempt);

    public ExecutionNodeRuntimeState TransitionTo(ExecutionState next, DateTimeOffset? timestamp, int attempt)
    {
        ExecutionStateRules.EnsureTransition(State, next);
        var now = timestamp ?? DateTimeOffset.UtcNow;
        return this with
        {
            State = next,
            Attempt = attempt,
            CreatedAt = CreatedAt ?? now,
            StartedAt = next == ExecutionState.Running ? now : StartedAt,
            CompletedAt = ExecutionStateRules.IsTerminal(next) ? now : CompletedAt
        };
    }
}

public abstract record WorkOutcome(
    ResultId ResultId,
    IReadOnlyList<EvidenceId> Evidence,
    string? Summary);

public sealed record WorkSuccess(
    ResultId ResultId,
    IReadOnlyList<EvidenceId> Evidence,
    string? Summary,
    WorkOutput Output) : WorkOutcome(ResultId, Evidence, Summary);

public sealed record WorkFailure(
    ResultId ResultId,
    IReadOnlyList<EvidenceId> Evidence,
    string? Summary,
    RuntimeError Error) : WorkOutcome(ResultId, Evidence, Summary);

public sealed record WorkCancelled(
    ResultId ResultId,
    IReadOnlyList<EvidenceId> Evidence,
    string? Summary,
    string? Reason = null) : WorkOutcome(ResultId, Evidence, Summary);

public sealed record WorkTimedOut(
    ResultId ResultId,
    IReadOnlyList<EvidenceId> Evidence,
    string? Summary,
    TimeSpan Timeout) : WorkOutcome(ResultId, Evidence, Summary);

public sealed record WorkOutput(
    string? InlineJson = null,
    ResultReference? Result = null,
    IReadOnlyList<EvidenceId>? Evidence = null,
    IReadOnlyList<ArtifactId>? Artifacts = null);

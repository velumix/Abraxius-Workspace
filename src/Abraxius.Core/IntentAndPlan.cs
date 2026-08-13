using System.Collections.ObjectModel;
using System.Text.Json;
using Abraxius.Protocol;

namespace Abraxius.Core;

public sealed record Intent
{
    public Intent(
        string objective,
        CorrelationId correlationId,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        if (string.IsNullOrWhiteSpace(objective))
        {
            throw new ArgumentException("An intent objective is required.", nameof(objective));
        }

        IntentId = IntentId.New();
        Objective = objective.Trim();
        CorrelationId = correlationId;
        Attributes = attributes;
    }

    public IntentId IntentId { get; init; }
    public string Objective { get; init; }
    public CorrelationId CorrelationId { get; init; }
    public string? Scope { get; init; }
    public ExecutionConstraints Constraints { get; init; } = new();
    public WorkPriority Priority { get; init; } = WorkPriority.Interactive;
    public OutputContract? RequestedOutput { get; init; }
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }

    public IReadOnlyDictionary<string, string> SafeAttributes =>
        Attributes ?? ReadOnlyDictionary<string, string>.Empty;
}

public sealed record ExecutionConstraints
{
    public ExecutionConstraints(
        DateTimeOffset? deadline = null,
        TimeSpan? defaultTimeout = null,
        bool allowSpeculation = false,
        int maxParallelism = 0)
    {
        Deadline = deadline;
        DefaultTimeout = defaultTimeout;
        AllowSpeculation = allowSpeculation;
        MaxParallelism = maxParallelism;
    }

    public DateTimeOffset? Deadline { get; init; }
    public TimeSpan? DefaultTimeout { get; init; }
    public bool AllowSpeculation { get; init; }
    public int MaxParallelism { get; init; }
    public bool AllowMutation { get; init; }
    public bool AllowNetwork { get; init; }
    public int? MaxModelCalls { get; init; }
    public decimal? MaxCost { get; init; }
    public bool RequireVerification { get; init; }
}

public sealed record WorkExecutionContext(
    TaskId TaskId,
    ExecutionId ExecutionId,
    CorrelationId CorrelationId,
    IReadOnlyDictionary<TaskId, WorkResult> DependencyResults,
    IEvidenceStore EvidenceStore,
    IProgress<ProgressUpdate> Progress,
    CancellationToken CancellationToken);

public sealed record ProgressUpdate(double Value, string? Message = null)
{
    public double ClampedValue => Math.Clamp(Value, 0, 1);
}

public sealed record WorkResult(
    ResultId ResultId,
    string? Summary,
    IReadOnlyList<EvidenceId> Evidence,
    JsonElement? Value = null)
{
    public static WorkResult Empty(string? summary = null) =>
        new(ResultId.New(), summary, Array.Empty<EvidenceId>());
}

public delegate ValueTask<WorkResult> WorkOperation(WorkExecutionContext context);

public sealed class WorkNode
{
    public WorkNode(
        TaskId taskId,
        ExecutionId executionId,
        string label,
        ExecutorKind executor,
        WorkOperation operation,
        IReadOnlyList<TaskId>? dependencies = null,
        TaskId? parentTaskId = null,
        WorkPriority priority = WorkPriority.Normal,
        DateTimeOffset? deadline = null,
        TimeSpan? timeout = null,
        RetryPolicy? retryPolicy = null,
        ResourceHint? resourceHint = null,
        int creationOrder = 0)
    {
        TaskId = taskId;
        ExecutionId = executionId;
        Label = string.IsNullOrWhiteSpace(label) ? taskId.ToString() : label;
        Executor = executor;
        Operation = operation ?? throw new ArgumentNullException(nameof(operation));
        Dependencies = dependencies is null ? Array.Empty<TaskId>() : dependencies.ToArray();
        ParentTaskId = parentTaskId;
        Priority = priority;
        Deadline = deadline;
        Timeout = timeout;
        RetryPolicy = retryPolicy ?? new RetryPolicy();
        ResourceHint = resourceHint ?? new ResourceHint();
        CreationOrder = creationOrder;
    }

    public TaskId TaskId { get; }
    public ExecutionId ExecutionId { get; }
    public string Label { get; }
    public ExecutorKind Executor { get; }
    public WorkOperation Operation { get; }
    public IReadOnlyList<TaskId> Dependencies { get; }
    public TaskId? ParentTaskId { get; }
    public WorkPriority Priority { get; }
    public DateTimeOffset? Deadline { get; }
    public TimeSpan? Timeout { get; }
    public RetryPolicy RetryPolicy { get; }
    public ResourceHint ResourceHint { get; }
    public int CreationOrder { get; }
}

public sealed class ExecutionPlan
{
    private readonly Dictionary<TaskId, WorkNode> _nodes = new();

    public ExecutionPlan(ExecutionId executionId, CorrelationId correlationId, IEnumerable<WorkNode> nodes)
    {
        ExecutionId = executionId;
        CorrelationId = correlationId;

        foreach (var node in nodes ?? throw new ArgumentNullException(nameof(nodes)))
        {
            if (node.ExecutionId != executionId)
            {
                throw new PlanValidationException(
                    $"Task {node.TaskId} belongs to execution {node.ExecutionId}, not {executionId}.");
            }

            if (!_nodes.TryAdd(node.TaskId, node))
            {
                throw new PlanValidationException($"Duplicate task id {node.TaskId}.");
            }
        }

        if (_nodes.Count == 0)
        {
            throw new PlanValidationException("An execution plan must contain at least one task.");
        }
    }

    public ExecutionId ExecutionId { get; }
    public CorrelationId CorrelationId { get; }
    public IReadOnlyCollection<WorkNode> Nodes => _nodes.Values;

    public WorkNode GetNode(TaskId taskId) =>
        _nodes.TryGetValue(taskId, out var node)
            ? node
            : throw new PlanValidationException($"Unknown task id {taskId}.");

    public void Validate()
    {
        foreach (var node in _nodes.Values)
        {
            foreach (var dependency in node.Dependencies)
            {
                if (!_nodes.ContainsKey(dependency))
                {
                    throw new PlanValidationException(
                        $"Task {node.TaskId} depends on missing task {dependency}.");
                }

                if (dependency == node.TaskId)
                {
                    throw new PlanValidationException($"Task {node.TaskId} depends on itself.");
                }
            }
        }

        var colors = new Dictionary<TaskId, VisitColor>(_nodes.Count);
        foreach (var node in _nodes.Values.OrderBy(static n => n.CreationOrder).ThenBy(static n => n.TaskId.Value))
        {
            Visit(node.TaskId, colors, new Stack<TaskId>());
        }
    }

    private void Visit(TaskId taskId, Dictionary<TaskId, VisitColor> colors, Stack<TaskId> path)
    {
        if (colors.TryGetValue(taskId, out var color))
        {
            if (color == VisitColor.Gray)
            {
                throw new PlanValidationException($"Cycle detected at task {taskId}.");
            }

            if (color == VisitColor.Black)
            {
                return;
            }
        }

        colors[taskId] = VisitColor.Gray;
        path.Push(taskId);
        foreach (var dependency in _nodes[taskId].Dependencies.OrderBy(static id => id.Value))
        {
            Visit(dependency, colors, path);
        }

        path.Pop();
        colors[taskId] = VisitColor.Black;
    }

    private enum VisitColor
    {
        Gray,
        Black
    }
}

public sealed class PlanValidationException(string message) : Exception(message);

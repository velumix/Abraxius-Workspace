using System.Collections.Immutable;
using System.Text.Json;
using Abraxius.Protocol;

namespace Abraxius.Core;

/// <summary>Explicit transport serialization for the execution graph IR.</summary>
public static class ExecutionGraphJson
{
    public static string Serialize(ExecutionGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var options = ProtocolJson.CreateOptions();
        var dto = new ExecutionGraphDto
        {
            ExecutionId = graph.ExecutionId,
            CorrelationId = graph.CorrelationId,
            HasExplicitRoots = graph.HasExplicitRoots,
            RootNodeIds = graph.RootNodeIds,
            SpeculationGroups = graph.SpeculationGroups,
            Metadata = graph.Metadata,
            Nodes = graph.Nodes.Select(node => new ExecutionNodeDto
            {
                Id = node.Id,
                TaskId = node.TaskId,
                ExecutionId = node.ExecutionId,
                Dependencies = node.Dependencies,
                ParentNodeId = node.ParentNodeId,
                WorkKind = node.WorkKind,
                WorkPayload = JsonSerializer.SerializeToElement(node.Work, node.Work.GetType(), options),
                Priority = node.Priority,
                Deadline = node.Deadline,
                Timeout = node.Timeout,
                RetryPolicy = node.RetryPolicy,
                ResourceHints = node.ResourceHints,
                SpeculationGroupId = node.SpeculationGroupId,
                CreationOrder = node.CreationOrder
            }).ToImmutableArray()
        };
        return JsonSerializer.Serialize(dto, options);
    }

    public static ExecutionGraph Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Serialized execution graph JSON is required.", nameof(json));
        }

        var options = ProtocolJson.CreateOptions();
        var dto = JsonSerializer.Deserialize<ExecutionGraphDto>(json, options)
            ?? throw new JsonException("The execution graph JSON payload was empty.");
        var nodes = dto.Nodes.Select(node => new ExecutionNodeDefinition(
            node.Id,
            node.TaskId,
            node.ExecutionId,
            DeserializeWork(node.WorkKind, node.WorkPayload, options),
            node.Dependencies,
            node.ParentNodeId,
            node.Priority,
            node.Deadline,
            node.Timeout,
            node.RetryPolicy,
            node.ResourceHints,
            node.SpeculationGroupId,
            node.CreationOrder)).ToImmutableArray();

        return new ExecutionGraph(
            dto.ExecutionId,
            dto.CorrelationId,
            nodes,
            dto.HasExplicitRoots ? dto.RootNodeIds : null,
            dto.SpeculationGroups,
            dto.Metadata);
    }

    private static WorkDescriptor DeserializeWork(WorkKind kind, JsonElement payload, JsonSerializerOptions options) => kind switch
    {
        WorkKind.ModelInference => Deserialize<ModelWorkDescriptor>(payload, options),
        WorkKind.ToolExecution => Deserialize<ToolWorkDescriptor>(payload, options),
        WorkKind.MemoryLookup => Deserialize<MemoryWorkDescriptor>(payload, options),
        WorkKind.Cpu => Deserialize<CpuWorkDescriptor>(payload, options),
        WorkKind.Io => Deserialize<IoWorkDescriptor>(payload, options),
        WorkKind.Verification => Deserialize<VerificationWorkDescriptor>(payload, options),
        WorkKind.Synthesis => Deserialize<SynthesisWorkDescriptor>(payload, options),
        WorkKind.Background => Deserialize<BackgroundWorkDescriptor>(payload, options),
        _ => throw new JsonException($"Unsupported work kind '{kind}'.")
    } ?? throw new JsonException($"Work payload for '{kind}' was null.");

    static WorkDescriptor Deserialize<T>(JsonElement payload, JsonSerializerOptions options) where T : WorkDescriptor =>
        JsonSerializer.Deserialize<T>(payload.GetRawText(), options)
        ?? throw new JsonException("A work descriptor payload was null.");

    private sealed class ExecutionGraphDto
    {
        public ExecutionId ExecutionId { get; set; }
        public CorrelationId CorrelationId { get; set; }
        public bool HasExplicitRoots { get; set; }
        public ImmutableArray<NodeId> RootNodeIds { get; set; } = ImmutableArray<NodeId>.Empty;
        public ImmutableArray<ExecutionNodeDto> Nodes { get; set; } = ImmutableArray<ExecutionNodeDto>.Empty;
        public ImmutableArray<SpeculationGroupDefinition> SpeculationGroups { get; set; } = ImmutableArray<SpeculationGroupDefinition>.Empty;
        public ImmutableDictionary<string, string> Metadata { get; set; } = ImmutableDictionary<string, string>.Empty;
    }

    private sealed class ExecutionNodeDto
    {
        public NodeId Id { get; set; }
        public TaskId TaskId { get; set; }
        public ExecutionId ExecutionId { get; set; }
        public ImmutableArray<NodeId> Dependencies { get; set; } = ImmutableArray<NodeId>.Empty;
        public NodeId? ParentNodeId { get; set; }
        public WorkKind WorkKind { get; set; }
        public JsonElement WorkPayload { get; set; }
        public WorkPriority Priority { get; set; }
        public DateTimeOffset? Deadline { get; set; }
        public TimeSpan? Timeout { get; set; }
        public RetryPolicy RetryPolicy { get; set; } = new();
        public ResourceHints ResourceHints { get; set; }
        public SpeculationGroupId? SpeculationGroupId { get; set; }
        public int CreationOrder { get; set; }
    }
}

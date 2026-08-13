using System.Collections.Immutable;

namespace Abraxius.Protocol;

/// <summary>Portable description of an executable capability.</summary>
public sealed record CapabilityDescriptor
{
    public CapabilityDescriptor(
        string name,
        string description,
        string inputSchema,
        ExecutorKind executor,
        bool readOnly,
        IReadOnlyList<string> operations,
        string? scope = null,
        bool supportsCancellation = true,
        bool supportsStreaming = false,
        string? outputSchema = null)
    {
        Id = new CapabilityId(name);
        Name = name;
        Description = description;
        InputSchema = inputSchema;
        Executor = executor;
        ReadOnly = readOnly;
        Operations = operations?.ToImmutableArray() ?? ImmutableArray<string>.Empty;
        Scope = scope;
        SupportsCancellation = supportsCancellation;
        SupportsStreaming = supportsStreaming;
        OutputSchema = outputSchema;
    }

    public CapabilityId Id { get; }
    public string Name { get; }
    public string Description { get; }
    public string InputSchema { get; }
    public ExecutorKind Executor { get; }
    public bool ReadOnly { get; }
    public ImmutableArray<string> Operations { get; }
    public string? Scope { get; }
    public bool SupportsCancellation { get; }
    public bool SupportsStreaming { get; }
    public string? OutputSchema { get; }
}

public sealed record CapabilityRequest(
    CapabilityId Capability,
    string Operation,
    string Target,
    IReadOnlyDictionary<string, string>? Parameters,
    CorrelationId CorrelationId,
    ExecutionId ExecutionId,
    TaskId TaskId,
    IReadOnlyList<EvidenceId>? Evidence = null,
    IReadOnlyDictionary<string, string>? SecurityContext = null);

public sealed record CapabilityResult(
    bool Succeeded,
    string? Summary,
    IReadOnlyList<EvidenceId> Evidence,
    IReadOnlyDictionary<string, string>? Values = null,
    RuntimeError? Error = null,
    ResultId? ResultId = null,
    IReadOnlyList<ArtifactReference>? Artifacts = null)
{
    public IReadOnlyList<ArtifactReference> SafeArtifacts => Artifacts ?? Array.Empty<ArtifactReference>();
}

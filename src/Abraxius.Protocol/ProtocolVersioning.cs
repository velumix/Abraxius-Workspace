namespace Abraxius.Protocol;

/// <summary>Wire contract version for Abraxius messages.</summary>
public readonly record struct ProtocolVersion(ushort Major, ushort Minor)
{
    public override string ToString() => $"{Major}.{Minor}";
}

public static class AbraxiusProtocol
{
    public static readonly ProtocolVersion CurrentVersion = new(1, 0);
}

/// <summary>Common metadata carried by process or transport-facing messages.</summary>
public sealed record ProtocolEnvelope<TPayload>(
    ProtocolVersion Version,
    string MessageType,
    CorrelationId CorrelationId,
    ExecutionId? ExecutionId,
    DateTimeOffset CreatedAt,
    TPayload Payload);

public static class ProtocolEnvelope
{
    public static ProtocolEnvelope<TPayload> Create<TPayload>(
        string messageType,
        TPayload payload,
        CorrelationId? correlationId = null,
        ExecutionId? executionId = null) =>
        new(AbraxiusProtocol.CurrentVersion, messageType, correlationId ?? CorrelationId.New(), executionId, DateTimeOffset.UtcNow, payload);
}

public sealed record CausalityContext(
    ExecutionId ExecutionId,
    CorrelationId CorrelationId,
    TaskId? TaskId = null,
    TaskId? ParentTaskId = null,
    CorrelationId? CausedBy = null);

public sealed record EvidenceReference(
    EvidenceId Id,
    string Kind,
    string? Name,
    string ContentType,
    long SizeBytes,
    string Sha256,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ResultReference(
    ResultId Id,
    string ContentType,
    long? SizeBytes = null,
    string? Sha256 = null);

public sealed record ArtifactReference(
    ArtifactId Id,
    string Name,
    string ContentType,
    long SizeBytes,
    string? Sha256 = null,
    string? RevisionId = null,
    string? RevisionHash = null,
    string? Kind = null);

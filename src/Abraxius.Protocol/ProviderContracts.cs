using System.Collections.Immutable;

namespace Abraxius.Protocol;

public sealed record ModelMessage(string Role, string Content);

public sealed record ModelUsage(
    int InputTokens,
    int OutputTokens,
    decimal? EstimatedCost = null);

/// <summary>Provider-neutral model request suitable for adapters and transport boundaries.</summary>
public sealed record ModelRequestContract(
    ModelRequestId RequestId,
    CorrelationId CorrelationId,
    ExecutionId? ExecutionId,
    TaskId? TaskId,
    ImmutableArray<ModelMessage> Messages,
    string? Model = null,
    WorkPriority Priority = WorkPriority.Normal,
    string? ExpectedJsonSchema = null,
    int? MaxOutputTokens = null,
    decimal? Temperature = null,
    bool Stream = false,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record ModelResponseContract(
    ModelRequestId RequestId,
    ResultId ResultId,
    string Text,
    string? StructuredJson,
    string Model,
    ModelUsage? Usage,
    string? FinishReason = null,
    string? Provider = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<EvidenceId>? Evidence = null);

public abstract record ModelStreamEventContract(DateTimeOffset Timestamp)
{
    public sealed record Started(DateTimeOffset Timestamp, string? Model) : ModelStreamEventContract(Timestamp);
    public sealed record TextDelta(DateTimeOffset Timestamp, string Text) : ModelStreamEventContract(Timestamp);
    public sealed record Completed(DateTimeOffset Timestamp, ModelResponseContract Response) : ModelStreamEventContract(Timestamp);
    public sealed record Failed(DateTimeOffset Timestamp, RuntimeError Error) : ModelStreamEventContract(Timestamp);
}

public sealed record ModelActionProposal(
    CapabilityId Capability,
    string Operation,
    string Target,
    IReadOnlyDictionary<string, string>? Parameters = null);

public enum MemoryTier
{
    Execution,
    Recent,
    Persistent,
    Archive
}

public sealed record MemoryQueryContract(
    string Text,
    int Limit = 8,
    IReadOnlyList<string>? Scopes = null,
    ExecutionId? ExecutionId = null,
    MemoryTier? MinimumTier = null);

public sealed record MemoryEntryReference(
    string Key,
    string Text,
    double Score,
    MemoryTier Tier,
    IReadOnlyList<EvidenceId> Evidence,
    string? Source = null,
    double? Confidence = null);

public sealed record MemoryResultContract(
    IReadOnlyList<MemoryEntryReference> Entries,
    TimeSpan Latency,
    string? Provider = null);

public enum VerificationStatus
{
    Passed,
    Failed,
    Inconclusive,
    Skipped
}

public sealed record VerificationRequest(
    ExecutionId ExecutionId,
    TaskId TaskId,
    string Objective,
    IReadOnlyList<ResultId> Results,
    IReadOnlyList<EvidenceId> Evidence,
    string? Profile = null);

public sealed record VerificationResult(
    VerificationStatus Status,
    string Summary,
    IReadOnlyList<EvidenceId> Evidence,
    IReadOnlyList<string>? Reasons = null);

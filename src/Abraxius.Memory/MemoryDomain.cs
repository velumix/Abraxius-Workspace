using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Abraxius.Protocol;

namespace Abraxius.Memory;

public readonly record struct MemoryId(Guid Value)
{
    public static MemoryId New() => new(Guid.NewGuid());
    public static MemoryId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString("N");
    public static bool TryParse(string? value, out MemoryId id)
    {
        var parsed = Guid.TryParse(value, out var guid);
        id = new(parsed ? guid : Guid.Empty);
        return parsed;
    }
}

public readonly record struct KnowledgeNodeId(Guid Value)
{
    public static KnowledgeNodeId New() => new(Guid.NewGuid());
    public static KnowledgeNodeId Empty => new(Guid.Empty);
    public static bool TryParse(string? value, out KnowledgeNodeId id)
    {
        var parsed = Guid.TryParse(value, out var guid);
        id = new(parsed ? guid : Guid.Empty);
        return parsed;
    }
    public override string ToString() => Value.ToString("N");
}

public readonly record struct KnowledgeEdgeId(Guid Value)
{
    public static KnowledgeEdgeId New() => new(Guid.NewGuid());
    public static KnowledgeEdgeId Empty => new(Guid.Empty);
    public static bool TryParse(string? value, out KnowledgeEdgeId id)
    {
        var parsed = Guid.TryParse(value, out var guid);
        id = new(parsed ? guid : Guid.Empty);
        return parsed;
    }
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ChunkId(Guid Value)
{
    public static ChunkId New() => new(Guid.NewGuid());
    public static ChunkId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString("N");
}

public readonly record struct EmbeddingId(Guid Value)
{
    public static EmbeddingId New() => new(Guid.NewGuid());
    public static EmbeddingId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString("N");
}

public enum MemoryKind
{
    Working,
    Episodic,
    Semantic,
    Project,
    Procedural,
    Source,
    Evidence,
    Summary
}

public enum MemoryScopeKind
{
    Execution,
    Project,
    Repository,
    Workspace,
    User,
    Global
}

public enum MemorySourceKind
{
    User,
    SourceCode,
    ToolResult,
    VerifiedExecution,
    Documentation,
    ModelInference,
    ImportedKnowledge,
    System
}

public enum MemoryLifecycleState
{
    Active,
    Superseded,
    Deprecated,
    Conflicted,
    Archived,
    Deleted
}

public enum MemoryPrivacyClass
{
    Normal,
    Private,
    Sensitive
}

public enum MemoryRetrievalMode
{
    Lexical,
    Semantic,
    Hybrid,
    Recent,
    Symbol
}

public enum KnowledgeRelationType
{
    Defines,
    References,
    DependsOn,
    ProducedBy,
    ConsumedBy,
    Supports,
    Contradicts,
    Supersedes,
    VerifiedBy,
    RelatedTo,
    BelongsTo,
    TestedBy,
    Imports
}

public sealed record MemoryEvidenceLink(
    EvidenceId? EvidenceId = null,
    ResultId? ResultId = null,
    ArtifactId? ArtifactId = null,
    ExecutionId? ExecutionId = null,
    TaskId? TaskId = null,
    string? SourceReference = null);

public sealed record MemoryProvenance(
    MemorySourceKind Source,
    double Confidence = 0.5,
    DateTimeOffset? ObservedAt = null,
    string? SourceModel = null,
    string? SourceCommit = null,
    string? SourcePath = null,
    string? SourceHash = null,
    string? FactKey = null,
    string? FactValue = null)
{
    public double ClampedConfidence => Math.Clamp(Confidence, 0, 1);
}

public sealed record MemoryEntry
{
    public MemoryId Id { get; init; } = MemoryId.New();
    public required MemoryKind Kind { get; init; }
    public required MemoryScopeKind Scope { get; init; }
    public required string ScopeKey { get; init; }
    public required string Title { get; init; }
    public required string Content { get; init; }
    public MemoryLifecycleState State { get; init; } = MemoryLifecycleState.Active;
    public MemoryPrivacyClass Privacy { get; init; } = MemoryPrivacyClass.Normal;
    public MemoryProvenance Provenance { get; init; } = new(MemorySourceKind.System);
    public ImmutableArray<MemoryEvidenceLink> Evidence { get; init; } = ImmutableArray<MemoryEvidenceLink>.Empty;
    public ImmutableDictionary<string, string> Metadata { get; init; } = ImmutableDictionary<string, string>.Empty;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastObservedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public DateTimeOffset? SupersededAt { get; init; }
    public MemoryId? Supersedes { get; init; }

    public bool IsEvidenceBacked => !Evidence.IsDefaultOrEmpty ||
        Provenance.Source is MemorySourceKind.SourceCode or MemorySourceKind.VerifiedExecution or MemorySourceKind.ToolResult;

    public static MemoryEntry Create(
        MemoryKind kind,
        MemoryScopeKind scope,
        string scopeKey,
        string title,
        string content,
        MemoryProvenance provenance,
        IEnumerable<MemoryEvidenceLink>? evidence = null,
        MemoryId? id = null) => new()
        {
            Id = id ?? MemoryId.New(),
            Kind = kind,
            Scope = scope,
            ScopeKey = string.IsNullOrWhiteSpace(scopeKey) ? "default" : scopeKey.Trim(),
            Title = string.IsNullOrWhiteSpace(title) ? kind.ToString() : title.Trim(),
            Content = content ?? string.Empty,
            Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance)),
            Evidence = evidence?.ToImmutableArray() ?? ImmutableArray<MemoryEvidenceLink>.Empty
        };

    public string StableHash()
    {
        var value = $"{Kind}|{Scope}|{ScopeKey}|{Title}|{Content}|{Provenance.SourcePath}|{Provenance.SourceHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

public sealed record MemoryChunk(
    ChunkId Id,
    MemoryId MemoryId,
    string Text,
    int Ordinal,
    string? FilePath = null,
    string? SymbolName = null,
    string? SymbolKind = null,
    string? Language = null,
    int? StartLine = null,
    int? EndLine = null,
    string? ContentHash = null);

public sealed record KnowledgeNode(
    KnowledgeNodeId Id,
    MemoryId MemoryId,
    string Type,
    string Key,
    string Label,
    string? ScopeKey = null);

public sealed record KnowledgeEdge(
    KnowledgeEdgeId Id,
    KnowledgeNodeId From,
    KnowledgeRelationType Relation,
    KnowledgeNodeId To,
    double Confidence = 1,
    DateTimeOffset? ObservedAt = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record IndexedFileRecord(
    string ProjectKey,
    string RelativePath,
    string ContentHash,
    long SizeBytes,
    DateTimeOffset IndexedAt,
    IReadOnlyList<MemoryId> MemoryIds,
    string? Branch = null,
    string? Commit = null);

public sealed record MemorySearchQuery(
    string Text,
    int Limit = 8,
    string? ProjectKey = null,
    MemoryScopeKind? Scope = null,
    IReadOnlySet<MemoryKind>? Kinds = null,
    MemoryRetrievalMode Mode = MemoryRetrievalMode.Hybrid,
    bool RequireEvidence = false,
    bool IncludeStale = true,
    MemoryPrivacyClass MaximumPrivacy = MemoryPrivacyClass.Sensitive,
    IReadOnlyDictionary<string, string>? CurrentSourceHashes = null,
    string? ExactSymbol = null,
    DateTimeOffset? Since = null,
    string? Branch = null);

public sealed record MemoryScoreBreakdown(
    double Lexical,
    double Semantic,
    double Structural,
    double Authority,
    double Recency,
    double Confidence,
    double StalenessPenalty,
    double FinalScore)
{
    public string Explain() => $"lex {Lexical:0.00} · sem {Semantic:0.00} · graph {Structural:0.00} · authority {Authority:0.00} · recent {Recency:0.00} · confidence {Confidence:0.00} · stale -{StalenessPenalty:0.00}";
}

public sealed record MemorySearchHit(
    MemoryEntry Entry,
    double Score,
    MemoryScoreBreakdown Breakdown,
    bool IsStale = false,
    bool IsConflict = false,
    string? MatchKind = null)
{
    public string Explanation => Breakdown.Explain();
}

public sealed record MemoryConflict(
    string FactKey,
    IReadOnlyList<MemoryId> Entries,
    string Description);

public sealed record MemoryRetrievalResult(
    IReadOnlyList<MemorySearchHit> Hits,
    IReadOnlyList<MemoryConflict> Conflicts,
    TimeSpan Latency,
    bool CacheHit = false,
    string? Planner = null);

public sealed record MemoryStoreStatistics(
    long Entries,
    long Chunks,
    long Embeddings,
    long KnowledgeNodes,
    long KnowledgeEdges,
    long IndexedFiles,
    long PendingJobs = 0,
    long Conflicts = 0,
    long StaleEntries = 0,
    long DatabaseBytes = 0);

public sealed record MemoryRetrievalOptions(
    int LexicalLimit = 24,
    int SemanticLimit = 24,
    int SymbolLimit = 16,
    int RecentLimit = 12,
    int FinalLimit = 8,
    bool EnableSemantic = true,
    bool EnableGraph = true);

public sealed record EmbeddingVector(
    string Model,
    int Dimensions,
    float[] Values,
    DateTimeOffset CreatedAt)
{
    public ReadOnlyMemory<float> Memory => Values;
}

public interface IEmbeddingProvider
{
    string ProviderId { get; }
    string ModelId { get; }
    ValueTask<EmbeddingVector?> EmbedAsync(string text, CancellationToken cancellationToken = default);
}

public interface IMemoryStore : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask<MemoryEntry?> GetAsync(MemoryId id, CancellationToken cancellationToken = default);
    ValueTask UpsertAsync(MemoryEntry entry, CancellationToken cancellationToken = default);
    ValueTask SupersedeAsync(MemoryId oldId, MemoryId replacementId, CancellationToken cancellationToken = default);
    ValueTask ForgetAsync(MemoryId id, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MemoryEntry>> SearchLexicalAsync(MemorySearchQuery query, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MemoryEntry>> SearchSymbolAsync(MemorySearchQuery query, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MemoryEntry>> SearchGraphAsync(MemorySearchQuery query, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<(MemoryEntry Entry, double Score)>> SearchSemanticAsync(MemorySearchQuery query, EmbeddingVector vector, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MemoryEntry>> RecentAsync(MemorySearchQuery query, CancellationToken cancellationToken = default);
    ValueTask AddChunkAsync(MemoryChunk chunk, CancellationToken cancellationToken = default);
    ValueTask AddEmbeddingAsync(EmbeddingId id, MemoryId memoryId, EmbeddingVector vector, CancellationToken cancellationToken = default);
    ValueTask AddNodeAsync(KnowledgeNode node, CancellationToken cancellationToken = default);
    ValueTask AddEdgeAsync(KnowledgeEdge edge, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<KnowledgeEdge>> GetEdgesAsync(IEnumerable<KnowledgeNodeId> nodeIds, CancellationToken cancellationToken = default);
    ValueTask<IndexedFileRecord?> GetIndexedFileAsync(string projectKey, string relativePath, CancellationToken cancellationToken = default);
    ValueTask UpsertIndexedFileAsync(IndexedFileRecord record, CancellationToken cancellationToken = default);
    ValueTask<MemoryStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<MemoryEntry>> ExportAsync(string? projectKey = null, CancellationToken cancellationToken = default);
}

public interface IHybridMemoryRetriever
{
    ValueTask<MemoryRetrievalResult> RetrieveAsync(MemorySearchQuery query, CancellationToken cancellationToken = default);
}

public sealed record ContextCompilationRequest(
    string Objective,
    MemorySearchQuery Query,
    int ContextWindow = 32_000,
    int ReservedOutputTokens = 4_000,
    IReadOnlyList<string>? Constraints = null,
    IReadOnlyList<string>? CurrentState = null,
    IReadOnlyList<string>? PriorAttempts = null,
    bool IncludeEvidenceReferences = true);

public sealed record ContextSection(string Name, string Content, int Priority, int EstimatedTokens);

public sealed record MemoryContextPackage(
    string Text,
    IReadOnlyList<MemoryId> IncludedMemories,
    IReadOnlyList<EvidenceId> IncludedEvidence,
    IReadOnlyList<ContextSection> Sections,
    IReadOnlyList<MemoryConflict> Conflicts,
    int EstimatedTokens,
    string ContentHash,
    string? AxlProjection = null);

public static class MemoryIds
{
    public static MemoryId Stable(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new MemoryId(new Guid(bytes.AsSpan(0, 16)));
    }
}

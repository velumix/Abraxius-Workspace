using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Abraxius.Models;
using Abraxius.Protocol;

namespace Abraxius.Memory;

public sealed class HashEmbeddingProvider : IEmbeddingProvider
{
    public HashEmbeddingProvider(string modelId = "local-hash-embedding-v1", int dimensions = 256)
    {
        if (dimensions is < 32 or > 4096) throw new ArgumentOutOfRangeException(nameof(dimensions));
        ModelId = modelId;
        Dimensions = dimensions;
    }

    public string ProviderId => "local";
    public string ModelId { get; }
    public int Dimensions { get; }

    public ValueTask<EmbeddingVector?> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var vector = new float[Dimensions];
        var terms = InMemoryKnowledgeStore.Terms(text);
        foreach (var term in terms)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(term.ToLowerInvariant()));
            var index = BitConverter.ToUInt32(hash, 0) % (uint)Dimensions;
            var sign = (hash[4] & 1) == 0 ? 1f : -1f;
            vector[index] += sign;
        }

        var length = Math.Sqrt(vector.Sum(static value => value * value));
        if (length > 0)
        {
            for (var index = 0; index < vector.Length; index++) vector[index] = (float)(vector[index] / length);
        }

        return ValueTask.FromResult<EmbeddingVector?>(new EmbeddingVector(ModelId, Dimensions, vector, DateTimeOffset.UtcNow));
    }
}

public sealed class HybridMemoryRetriever(
    IMemoryStore store,
    IEmbeddingProvider? embeddings = null,
    MemoryRetrievalOptions? options = null) : IHybridMemoryRetriever
{
    private readonly IMemoryStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IEmbeddingProvider _embeddings = embeddings ?? new HashEmbeddingProvider();
    private readonly MemoryRetrievalOptions _options = options ?? new MemoryRetrievalOptions();

    public async ValueTask<MemoryRetrievalResult> RetrieveAsync(MemorySearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(query.Text) || query.Mode == MemoryRetrievalMode.Recent)
        {
            var recent = await _store.RecentAsync(query with { Limit = _options.FinalLimit }, cancellationToken).ConfigureAwait(false);
            return Finalize(recent.Select(entry => new Candidate(entry, 0, 0, 0, 0, 1)), [], started, "recent-fast-path", query);
        }

        var lexicalTask = query.Mode is MemoryRetrievalMode.Semantic or MemoryRetrievalMode.Recent
            ? Task.FromResult<IReadOnlyList<MemoryEntry>>([])
            : _store.SearchLexicalAsync(query with { Limit = _options.LexicalLimit }, cancellationToken).AsTask();
        var symbolTask = query.ExactSymbol is null && !LooksLikeSymbol(query.Text)
            ? Task.FromResult<IReadOnlyList<MemoryEntry>>([])
            : _store.SearchSymbolAsync(query with { Limit = _options.SymbolLimit, ExactSymbol = query.ExactSymbol ?? query.Text }, cancellationToken).AsTask();
        var graphTask = _options.EnableGraph && query.Mode is not MemoryRetrievalMode.Lexical and not MemoryRetrievalMode.Symbol
            ? _store.SearchGraphAsync(query with { Limit = _options.SymbolLimit }, cancellationToken).AsTask()
            : Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
        var recentTask = _store.RecentAsync(query with { Limit = _options.RecentLimit }, cancellationToken).AsTask();
        var embeddingTask = _options.EnableSemantic && query.Mode is not MemoryRetrievalMode.Lexical and not MemoryRetrievalMode.Symbol
            ? _embeddings.EmbedAsync(query.Text, cancellationToken).AsTask()
            : Task.FromResult<EmbeddingVector?>(null);
        await Task.WhenAll(lexicalTask, symbolTask, graphTask, recentTask, embeddingTask).ConfigureAwait(false);

        IReadOnlyList<(MemoryEntry Entry, double Score)> semantic = [];
        var embedding = await embeddingTask.ConfigureAwait(false);
        if (embedding is { } vector)
        {
            semantic = await _store.SearchSemanticAsync(query with { Limit = _options.SemanticLimit }, vector, cancellationToken).ConfigureAwait(false);
        }

        var lexical = await lexicalTask.ConfigureAwait(false);
        var symbols = await symbolTask.ConfigureAwait(false);
        var graph = await graphTask.ConfigureAwait(false);
        var recentEntries = await recentTask.ConfigureAwait(false);
        var candidates = new Dictionary<MemoryId, Candidate>();
        AddRanked(candidates, lexical, static (rank, count) => 0.52 / (1 + rank), CandidateMatch.Lexical);
        AddRanked(candidates, symbols, static (rank, count) => 0.90 / (1 + rank), CandidateMatch.Symbol);
        AddRanked(candidates, graph, static (rank, count) => 0.38 / (1 + rank), CandidateMatch.Graph);
        AddRanked(candidates, semantic.Select(static item => item.Entry).ToArray(), (rank, count) => semantic[rank].Score * 0.48, CandidateMatch.Semantic);
        AddRanked(candidates, recentEntries, static (rank, count) => 0.12 / (1 + rank), CandidateMatch.Recent);

        return Finalize(candidates.Values, FindConflicts(candidates.Values, cancellationToken), started, "hybrid-rff", query);
    }

    private static IReadOnlyList<MemoryConflict> FindConflicts(IEnumerable<Candidate> candidates, CancellationToken cancellationToken)
    {
        var groups = candidates.Where(static item => !string.IsNullOrWhiteSpace(item.Entry.Provenance.FactKey))
            .GroupBy(static item => item.Entry.Provenance.FactKey!, StringComparer.Ordinal)
            .Where(static group => group.Select(item => item.Entry.Provenance.FactValue ?? item.Entry.Content).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => new MemoryConflict(group.Key, group.Select(static item => item.Entry.Id).ToArray(), "Multiple active memories claim different values for the same fact key."))
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return groups;
    }

    private MemoryRetrievalResult Finalize(IEnumerable<Candidate> candidates, IReadOnlyList<MemoryConflict> conflicts, long started, string planner, MemorySearchQuery query)
    {
        var conflictIds = conflicts.SelectMany(static conflict => conflict.Entries).ToHashSet();
        var hits = candidates.Select(candidate =>
        {
            var stale = IsStale(candidate.Entry, query);
            var authority = Authority(candidate.Entry.Provenance.Source);
            var recency = Recency(candidate.Entry.LastObservedAt);
            var confidence = candidate.Entry.Provenance.ClampedConfidence;
            var stalePenalty = stale ? 0.30 : 0;
            var final = candidate.Score + authority * 0.22 + recency * 0.08 + confidence * 0.18 - stalePenalty;
            return new MemorySearchHit(candidate.Entry, final, new MemoryScoreBreakdown(candidate.Lexical, candidate.Semantic, candidate.Structural, authority, recency, confidence, stalePenalty, final), stale, conflictIds.Contains(candidate.Entry.Id), candidate.Match.ToString());
        }).Where(hit => query.IncludeStale || !hit.IsStale).OrderByDescending(static hit => hit.Score).Take(_options.FinalLimit).ToArray();
        return new MemoryRetrievalResult(hits, conflicts, Stopwatch.GetElapsedTime(started), Planner: planner);
    }

    private static bool IsStale(MemoryEntry entry, MemorySearchQuery query)
    {
        if (entry.Provenance.SourceHash is null || entry.Provenance.SourcePath is null) return false;
        if (query.CurrentSourceHashes is not null && query.CurrentSourceHashes.TryGetValue(entry.Provenance.SourcePath, out var currentHash))
        {
            return !string.Equals(currentHash, entry.Provenance.SourceHash, StringComparison.OrdinalIgnoreCase);
        }

        return entry.Metadata.TryGetValue("current_source_hash", out var current) && !string.Equals(current, entry.Provenance.SourceHash, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddRanked(Dictionary<MemoryId, Candidate> candidates, IReadOnlyList<MemoryEntry> entries, Func<int, int, double> score, CandidateMatch match)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!candidates.TryGetValue(entry.Id, out var candidate)) candidate = new Candidate(entry, 0, 0, 0, 0, 0);
            var contribution = score(index, entries.Count);
            candidate = match switch
            {
                CandidateMatch.Lexical => candidate with { Score = candidate.Score + contribution, Lexical = Math.Max(candidate.Lexical, contribution), Match = CandidateMatch.Lexical },
                CandidateMatch.Semantic => candidate with { Score = candidate.Score + contribution, Semantic = Math.Max(candidate.Semantic, contribution), Match = candidate.Match == CandidateMatch.None ? CandidateMatch.Semantic : candidate.Match },
                CandidateMatch.Symbol => candidate with { Score = candidate.Score + contribution, Structural = Math.Max(candidate.Structural, contribution), Match = CandidateMatch.Symbol },
                CandidateMatch.Graph => candidate with { Score = candidate.Score + contribution, Structural = Math.Max(candidate.Structural, contribution), Match = candidate.Match == CandidateMatch.None ? CandidateMatch.Graph : candidate.Match },
                _ => candidate with { Score = candidate.Score + contribution, Match = candidate.Match == CandidateMatch.None ? CandidateMatch.Recent : candidate.Match }
            };
            candidates[entry.Id] = candidate;
        }
    }

    private static bool LooksLikeSymbol(string text) => text.Any(char.IsUpper) || text.Contains('.') || text.Contains("::", StringComparison.Ordinal) || text.Contains('/', StringComparison.Ordinal);
    private static double Authority(MemorySourceKind source) => source switch
    {
        MemorySourceKind.VerifiedExecution => 1,
        MemorySourceKind.SourceCode => 0.98,
        MemorySourceKind.ToolResult => 0.92,
        MemorySourceKind.User => 0.90,
        MemorySourceKind.Documentation => 0.82,
        MemorySourceKind.ImportedKnowledge => 0.65,
        MemorySourceKind.ModelInference => 0.42,
        _ => 0.55
    };
    private static double Recency(DateTimeOffset observed) => Math.Exp(-Math.Max(0, (DateTimeOffset.UtcNow - observed).TotalDays) / 30d);

    private enum CandidateMatch { None, Lexical, Semantic, Symbol, Graph, Recent }
    private sealed record Candidate(MemoryEntry Entry, double Score, double Lexical, double Semantic, double Structural, double Confidence, CandidateMatch Match = CandidateMatch.None);
}

public sealed class PersistentMemoryProvider(IHybridMemoryRetriever retriever) : IMemoryProvider
{
    private readonly IHybridMemoryRetriever _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));

    public async ValueTask<MemoryResult> QueryAsync(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        var result = await _retriever.RetrieveAsync(new MemorySearchQuery(query.Text, query.Limit, query.ProjectKey, query.Scope, Mode: query.Mode), cancellationToken).ConfigureAwait(false);
        var hits = result.Hits.Select(hit => new MemoryHit(hit.Entry.Id.ToString(), hit.Entry.Content, hit.Score, hit.Entry.Evidence.Where(static evidence => evidence.EvidenceId.HasValue).Select(static evidence => evidence.EvidenceId!.Value).ToArray())
        {
            MemoryId = hit.Entry.Id,
            Kind = hit.Entry.Kind,
            Source = hit.Entry.Provenance.Source.ToString(),
            Explanation = hit.Explanation
        }).ToArray();
        return new MemoryResult(hits, Stopwatch.GetElapsedTime(started), "local-hybrid");
    }
}

public static class MemoryModelRequestExtensions
{
    public static ModelRequest WithMemoryContext(this ModelRequest request, MemoryContextPackage package)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(package);
        var metadata = request.Metadata is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new Dictionary<string, string>(request.Metadata, StringComparer.Ordinal);
        metadata["memory.context.hash"] = package.ContentHash;
        metadata["memory.context.count"] = package.IncludedMemories.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return request with
        {
            Prompt = $"{request.Prompt}\n\n{package.Text}",
            Evidence = request.Evidence.Concat(package.IncludedEvidence).Distinct().ToArray(),
            RequiredContextTokens = Math.Max(request.RequiredContextTokens ?? 0, package.EstimatedTokens),
            Metadata = metadata
        };
    }
}

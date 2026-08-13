using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Abraxius.Memory;

public sealed class InMemoryKnowledgeStore : IMemoryStore
{
    private readonly ConcurrentDictionary<MemoryId, MemoryEntry> _entries = new();
    private readonly ConcurrentDictionary<ChunkId, MemoryChunk> _chunks = new();
    private readonly ConcurrentDictionary<EmbeddingId, (MemoryId MemoryId, EmbeddingVector Vector)> _embeddings = new();
    private readonly ConcurrentDictionary<KnowledgeNodeId, KnowledgeNode> _nodes = new();
    private readonly ConcurrentDictionary<KnowledgeEdgeId, KnowledgeEdge> _edges = new();
    private readonly ConcurrentDictionary<string, IndexedFileRecord> _files = new(StringComparer.Ordinal);

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask<MemoryEntry?> GetAsync(MemoryId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_entries.TryGetValue(id, out var entry) ? entry : null);
    }

    public ValueTask UpsertAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(entry);
        _entries[entry.Id] = entry;
        return ValueTask.CompletedTask;
    }

    public async ValueTask SupersedeAsync(MemoryId oldId, MemoryId replacementId, CancellationToken cancellationToken = default)
    {
        var old = await GetAsync(oldId, cancellationToken).ConfigureAwait(false);
        if (old is not null)
        {
            _entries[oldId] = old with { State = MemoryLifecycleState.Superseded, SupersededAt = DateTimeOffset.UtcNow, Supersedes = replacementId };
        }
    }

    public ValueTask ForgetAsync(MemoryId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _entries.TryRemove(id, out _);
        foreach (var chunk in _chunks.Where(pair => pair.Value.MemoryId == id).Select(static pair => pair.Key).ToArray())
        {
            _chunks.TryRemove(chunk, out _);
        }

        foreach (var embedding in _embeddings.Where(pair => pair.Value.MemoryId == id).Select(static pair => pair.Key).ToArray())
        {
            _embeddings.TryRemove(embedding, out _);
        }

        var nodeIds = _nodes.Where(pair => pair.Value.MemoryId == id).Select(static pair => pair.Key).ToHashSet();
        foreach (var nodeId in nodeIds) _nodes.TryRemove(nodeId, out _);
        foreach (var edge in _edges.Where(pair => nodeIds.Contains(pair.Value.From) || nodeIds.Contains(pair.Value.To)).Select(static pair => pair.Key).ToArray())
        {
            _edges.TryRemove(edge, out _);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<MemoryEntry>> SearchLexicalAsync(MemorySearchQuery query, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(Search(query, static (entry, terms) => ScoreTerms(entry, terms), query.Limit));

    public ValueTask<IReadOnlyList<MemoryEntry>> SearchSymbolAsync(MemorySearchQuery query, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(Search(query, static (entry, terms) =>
            entry.Metadata.TryGetValue("symbol", out var symbol) && terms.Any(term => symbol.Contains(term, StringComparison.OrdinalIgnoreCase)) ? 1d : 0d, query.Limit));

    public ValueTask<IReadOnlyList<MemoryEntry>> SearchGraphAsync(MemorySearchQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var terms = Terms(query.Text);
        var matchingNodes = _nodes.Values
            .Where(node => terms.Any(term => node.Key.Contains(term, StringComparison.OrdinalIgnoreCase) || node.Label.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(static node => node.Id)
            .ToHashSet();
        var relatedNodes = _edges.Values
            .Where(edge => matchingNodes.Contains(edge.From) || matchingNodes.Contains(edge.To))
            .SelectMany(edge => new[] { edge.From, edge.To });
        matchingNodes.UnionWith(relatedNodes);
        var memoryIds = _nodes.Values.Where(node => matchingNodes.Contains(node.Id)).Select(static node => node.MemoryId).ToHashSet();
        var result = _entries.Values
            .Where(entry => memoryIds.Contains(entry.Id) && Matches(entry, query))
            .OrderByDescending(static entry => entry.Provenance.ClampedConfidence)
            .ThenByDescending(static entry => entry.LastObservedAt)
            .Take(Math.Max(1, query.Limit))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(result);
    }

    public ValueTask<IReadOnlyList<(MemoryEntry Entry, double Score)>> SearchSemanticAsync(MemorySearchQuery query, EmbeddingVector vector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var matches = _embeddings.Values
            .Select(item => (item.MemoryId, Score: Cosine(item.Vector.Values, vector.Values)))
            .Where(item => item.Score > 0)
            .OrderByDescending(static item => item.Score)
            .Take(Math.Max(1, query.Limit))
            .Select(item => (_entries.TryGetValue(item.MemoryId, out var entry) ? entry : null, item.Score))
            .Where(static item => item.Item1 is not null)
            .Select(static item => (item.Item1!, item.Score))
            .Where(item => Matches(item.Item1, query))
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<(MemoryEntry Entry, double Score)>>(matches);
    }

    public ValueTask<IReadOnlyList<MemoryEntry>> RecentAsync(MemorySearchQuery query, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(_entries.Values.Where(entry => Matches(entry, query)).OrderByDescending(static entry => entry.LastObservedAt).Take(query.Limit).ToArray());

    public ValueTask AddChunkAsync(MemoryChunk chunk, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _chunks[chunk.Id] = chunk;
        return ValueTask.CompletedTask;
    }

    public ValueTask AddEmbeddingAsync(EmbeddingId id, MemoryId memoryId, EmbeddingVector vector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _embeddings[id] = (memoryId, vector);
        return ValueTask.CompletedTask;
    }

    public ValueTask AddNodeAsync(KnowledgeNode node, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _nodes[node.Id] = node;
        return ValueTask.CompletedTask;
    }

    public ValueTask AddEdgeAsync(KnowledgeEdge edge, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _edges[edge.Id] = edge;
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<KnowledgeEdge>> GetEdgesAsync(IEnumerable<KnowledgeNodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ids = nodeIds.ToHashSet();
        return ValueTask.FromResult<IReadOnlyList<KnowledgeEdge>>(_edges.Values.Where(edge => ids.Contains(edge.From) || ids.Contains(edge.To)).ToArray());
    }

    public ValueTask<IndexedFileRecord?> GetIndexedFileAsync(string projectKey, string relativePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _files.TryGetValue(FileKey(projectKey, relativePath), out var record);
        return ValueTask.FromResult(record);
    }

    public ValueTask UpsertIndexedFileAsync(IndexedFileRecord record, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _files[FileKey(record.ProjectKey, record.RelativePath)] = record;
        return ValueTask.CompletedTask;
    }

    public ValueTask<MemoryStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new MemoryStoreStatistics(
            _entries.Count, _chunks.Count, _embeddings.Count, _nodes.Count, _edges.Count, _files.Count,
            Conflicts: _entries.Values.Count(static entry => entry.State == MemoryLifecycleState.Conflicted),
            StaleEntries: 0));
    }

    public ValueTask<IReadOnlyList<MemoryEntry>> ExportAsync(string? projectKey = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<MemoryEntry>>(_entries.Values.Where(entry => projectKey is null || entry.ScopeKey == projectKey).OrderBy(static entry => entry.CreatedAt).ToArray());

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private IReadOnlyList<MemoryEntry> Search(MemorySearchQuery query, Func<MemoryEntry, string[], double> scorer, int limit)
    {
        var terms = Terms(query.Text);
        return _entries.Values.Where(entry => Matches(entry, query))
            .Select(entry => (Entry: entry, Score: scorer(entry, terms)))
            .Where(static pair => pair.Score > 0)
            .OrderByDescending(static pair => pair.Score)
            .ThenByDescending(static pair => pair.Entry.Provenance.ClampedConfidence)
            .Take(Math.Max(1, limit))
            .Select(static pair => pair.Entry)
            .ToArray();
    }

    internal static bool Matches(MemoryEntry entry, MemorySearchQuery query) =>
        entry.State != MemoryLifecycleState.Deleted &&
        entry.Privacy <= query.MaximumPrivacy &&
        (query.ProjectKey is null || entry.ScopeKey == query.ProjectKey) &&
        (query.Scope is null || entry.Scope == query.Scope) &&
        (query.Kinds is null || query.Kinds.Contains(entry.Kind)) &&
        (!query.RequireEvidence || entry.IsEvidenceBacked) &&
        (!query.Since.HasValue || entry.LastObservedAt >= query.Since.Value) &&
        (query.Branch is null || entry.Metadata.TryGetValue("branch", out var branch) && string.Equals(branch, query.Branch, StringComparison.Ordinal));

    internal static double ScoreTerms(MemoryEntry entry, string[] terms)
    {
        if (terms.Length == 0) return 0.1;
        var haystack = $"{entry.Title} {entry.Content} {entry.Provenance.SourcePath} {entry.Provenance.FactKey}";
        var matches = terms.Count(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
        return (double)matches / terms.Length;
    }

    internal static string[] Terms(string? text) => (text ?? string.Empty).Split([' ', '\t', '\r', '\n', '.', ':', '/', '\\', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    internal static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length == 0 || left.Length != right.Length) return 0;
        double dot = 0, leftLength = 0, rightLength = 0;
        for (var index = 0; index < left.Length; index++)
        {
            dot += left[index] * right[index];
            leftLength += left[index] * left[index];
            rightLength += right[index] * right[index];
        }

        return leftLength == 0 || rightLength == 0 ? 0 : dot / (Math.Sqrt(leftLength) * Math.Sqrt(rightLength));
    }

    private static string FileKey(string projectKey, string path) => $"{projectKey}\0{path}";
}

public sealed class SqliteMemoryStore : IMemoryStore
{
    private const int SchemaVersion = 1;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Task _initialization;
    private int _disposed;

    public SqliteMemoryStore(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A database path is required.", nameof(databasePath));
        _databasePath = Path.GetFullPath(databasePath);
        if (!string.Equals(_databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = string.Equals(_databasePath, ":memory:", StringComparison.OrdinalIgnoreCase) ? SqliteOpenMode.Memory : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 5
        }.ToString();
        _initialization = InitializeCoreAsync(CancellationToken.None);
    }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => new(_initialization.WaitAsync(cancellationToken));

    public async ValueTask<MemoryEntry?> GetAsync(MemoryId id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = EntrySelect + " WHERE e.id = $id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadEntry(reader) : null;
    }

    public async ValueTask UpsertAsync(MemoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, """
                INSERT INTO memory_entries (id, kind, scope, scope_key, title, content, state, privacy, source, confidence, observed_at, source_model, source_commit, source_path, source_hash, fact_key, fact_value, evidence_json, metadata_json, created_at, last_verified_at, superseded_at, supersedes)
                VALUES ($id, $kind, $scope, $scope_key, $title, $content, $state, $privacy, $source, $confidence, $observed_at, $source_model, $source_commit, $source_path, $source_hash, $fact_key, $fact_value, $evidence_json, $metadata_json, $created_at, $last_verified_at, $superseded_at, $supersedes)
                ON CONFLICT(id) DO UPDATE SET kind=excluded.kind, scope=excluded.scope, scope_key=excluded.scope_key, title=excluded.title, content=excluded.content, state=excluded.state, privacy=excluded.privacy, source=excluded.source, confidence=excluded.confidence, observed_at=excluded.observed_at, source_model=excluded.source_model, source_commit=excluded.source_commit, source_path=excluded.source_path, source_hash=excluded.source_hash, fact_key=excluded.fact_key, fact_value=excluded.fact_value, evidence_json=excluded.evidence_json, metadata_json=excluded.metadata_json, created_at=excluded.created_at, last_verified_at=excluded.last_verified_at, superseded_at=excluded.superseded_at, supersedes=excluded.supersedes;
                """, new Dictionary<string, object?>
                {
                    ["$id"] = entry.Id.ToString(), ["$kind"] = (int)entry.Kind, ["$scope"] = (int)entry.Scope,
                    ["$scope_key"] = entry.ScopeKey, ["$title"] = entry.Title, ["$content"] = entry.Content,
                    ["$state"] = (int)entry.State, ["$privacy"] = (int)entry.Privacy, ["$source"] = (int)entry.Provenance.Source,
                    ["$confidence"] = entry.Provenance.ClampedConfidence, ["$observed_at"] = entry.LastObservedAt.ToString("O"),
                    ["$source_model"] = entry.Provenance.SourceModel, ["$source_commit"] = entry.Provenance.SourceCommit,
                    ["$source_path"] = entry.Provenance.SourcePath, ["$source_hash"] = entry.Provenance.SourceHash,
                    ["$fact_key"] = entry.Provenance.FactKey, ["$fact_value"] = entry.Provenance.FactValue,
                    ["$evidence_json"] = JsonSerializer.Serialize(entry.Evidence), ["$metadata_json"] = JsonSerializer.Serialize(entry.Metadata),
                    ["$created_at"] = entry.CreatedAt.ToString("O"), ["$last_verified_at"] = NullableDate(entry.LastVerifiedAt),
                    ["$superseded_at"] = NullableDate(entry.SupersededAt), ["$supersedes"] = entry.Supersedes?.ToString()
                }, cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM memory_fts WHERE memory_id = $id; INSERT INTO memory_fts(memory_id,title,content,path,symbol_name) VALUES($id,$title,$content,$path,$symbol);", new Dictionary<string, object?>
            {
                ["$id"] = entry.Id.ToString(), ["$title"] = entry.Title, ["$content"] = entry.Content,
                ["$path"] = entry.Provenance.SourcePath, ["$symbol"] = entry.Metadata.TryGetValue("symbol", out var symbol) ? symbol : null
            }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask SupersedeAsync(MemoryId oldId, MemoryId replacementId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE memory_entries SET state=$state, superseded_at=$at, supersedes=$replacement WHERE id=$id";
            command.Parameters.AddWithValue("$state", (int)MemoryLifecycleState.Superseded);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$replacement", replacementId.ToString());
            command.Parameters.AddWithValue("$id", oldId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask ForgetAsync(MemoryId id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, transaction, "DELETE FROM memory_fts WHERE memory_id=$id; DELETE FROM memory_embeddings WHERE memory_id=$id; DELETE FROM memory_chunks WHERE memory_id=$id; DELETE FROM knowledge_edges WHERE from_id IN (SELECT id FROM knowledge_nodes WHERE memory_id=$id) OR to_id IN (SELECT id FROM knowledge_nodes WHERE memory_id=$id); DELETE FROM knowledge_nodes WHERE memory_id=$id; DELETE FROM memory_entries WHERE id=$id;", new Dictionary<string, object?> { ["$id"] = id.ToString() }, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask<IReadOnlyList<MemoryEntry>> SearchLexicalAsync(MemorySearchQuery query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var terms = InMemoryKnowledgeStore.Terms(query.Text);
        if (terms.Length == 0) return await RecentAsync(query, cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder(" WHERE memory_fts MATCH $match");
        command.Parameters.AddWithValue("$match", string.Join(" AND ", terms.Select(static term => $"\"{term.Replace("\"", "\"\"")}\"")));
        AppendFilters(command, where, query, "e");
        command.CommandText = EntrySelect + " JOIN memory_fts ON memory_fts.memory_id = e.id" + where + " ORDER BY bm25(memory_fts) LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Max(1, query.Limit));
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<MemoryEntry>> SearchSymbolAsync(MemorySearchQuery query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder(" WHERE memory_fts.symbol_name IS NOT NULL AND memory_fts.symbol_name LIKE $symbol");
        command.Parameters.AddWithValue("$symbol", $"%{query.ExactSymbol ?? query.Text}%");
        AppendFilters(command, where, query, "e");
        command.CommandText = EntrySelect + " JOIN memory_fts ON memory_fts.memory_id = e.id" + where + " ORDER BY length(memory_fts.symbol_name) LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Max(1, query.Limit));
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<MemoryEntry>> SearchGraphAsync(MemorySearchQuery query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var terms = InMemoryKnowledgeStore.Terms(query.Text);
        if (terms.Length == 0) return [];
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var termConditions = new string[terms.Length];
        for (var index = 0; index < terms.Length; index++)
        {
            var parameter = $"$graph{index}";
            termConditions[index] = $"(n.node_key LIKE {parameter} OR n.label LIKE {parameter} OR related.node_key LIKE {parameter} OR related.label LIKE {parameter})";
            command.Parameters.AddWithValue(parameter, $"%{terms[index]}%");
        }

        var where = new StringBuilder($" WHERE ({string.Join(" OR ", termConditions)})");
        AppendFilters(command, where, query, "e");
        command.CommandText = EntrySelect.Replace("SELECT e.id", "SELECT DISTINCT e.id", StringComparison.Ordinal)
            + " JOIN knowledge_nodes n ON n.memory_id=e.id LEFT JOIN knowledge_edges graph_edge ON graph_edge.from_id=n.id OR graph_edge.to_id=n.id LEFT JOIN knowledge_nodes related ON related.id=CASE WHEN graph_edge.from_id=n.id THEN graph_edge.to_id ELSE graph_edge.from_id END"
            + where + " ORDER BY e.observed_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Max(1, query.Limit));
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<(MemoryEntry Entry, double Score)>> SearchSemanticAsync(MemorySearchQuery query, EmbeddingVector vector, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = EntrySelect.Replace(" FROM memory_entries e", ",memory_embeddings.embedding FROM memory_entries e", StringComparison.Ordinal) + " JOIN memory_embeddings ON memory_embeddings.memory_id = e.id";
        var where = new StringBuilder(" WHERE memory_embeddings.model = $model AND memory_embeddings.dimensions = $dimensions");
        command.Parameters.AddWithValue("$model", vector.Model);
        command.Parameters.AddWithValue("$dimensions", vector.Dimensions);
        AppendFilters(command, where, query, "e");
        command.CommandText += where;
        var values = new List<(MemoryEntry Entry, double Score)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var entry = ReadEntry(reader);
            var blob = (byte[])reader[reader.GetOrdinal("embedding")];
            var stored = new float[blob.Length / sizeof(float)];
            Buffer.BlockCopy(blob, 0, stored, 0, blob.Length);
            var score = InMemoryKnowledgeStore.Cosine(stored, vector.Values);
            if (score > 0) values.Add((entry, score));
        }

        return values.OrderByDescending(static value => value.Score).Take(Math.Max(1, query.Limit)).ToArray();
    }

    public async ValueTask<IReadOnlyList<MemoryEntry>> RecentAsync(MemorySearchQuery query, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder(" WHERE 1=1");
        AppendFilters(command, where, query, "e");
        command.CommandText = EntrySelect + where + " ORDER BY e.observed_at DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", Math.Max(1, query.Limit));
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask AddChunkAsync(MemoryChunk chunk, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO memory_chunks(id,memory_id,text,ordinal,file_path,symbol_name,symbol_kind,language,start_line,end_line,content_hash) VALUES($id,$memory_id,$text,$ordinal,$file_path,$symbol_name,$symbol_kind,$language,$start_line,$end_line,$content_hash) ON CONFLICT(id) DO UPDATE SET text=excluded.text, ordinal=excluded.ordinal, file_path=excluded.file_path, symbol_name=excluded.symbol_name, symbol_kind=excluded.symbol_kind, language=excluded.language, start_line=excluded.start_line, end_line=excluded.end_line, content_hash=excluded.content_hash";
            AddParameters(command, new Dictionary<string, object?>
            {
                ["$id"] = chunk.Id.ToString(), ["$memory_id"] = chunk.MemoryId.ToString(), ["$text"] = chunk.Text, ["$ordinal"] = chunk.Ordinal,
                ["$file_path"] = chunk.FilePath, ["$symbol_name"] = chunk.SymbolName, ["$symbol_kind"] = chunk.SymbolKind, ["$language"] = chunk.Language,
                ["$start_line"] = chunk.StartLine, ["$end_line"] = chunk.EndLine, ["$content_hash"] = chunk.ContentHash
            });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask AddEmbeddingAsync(EmbeddingId id, MemoryId memoryId, EmbeddingVector vector, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var values = new byte[vector.Values.Length * sizeof(float)];
        Buffer.BlockCopy(vector.Values, 0, values, 0, values.Length);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO memory_embeddings(id,memory_id,model,dimensions,embedding,created_at) VALUES($id,$memory_id,$model,$dimensions,$embedding,$created_at) ON CONFLICT(id) DO UPDATE SET memory_id=excluded.memory_id, model=excluded.model, dimensions=excluded.dimensions, embedding=excluded.embedding, created_at=excluded.created_at";
            AddParameters(command, new Dictionary<string, object?>
            {
                ["$id"] = id.ToString(), ["$memory_id"] = memoryId.ToString(), ["$model"] = vector.Model,
                ["$dimensions"] = vector.Dimensions, ["$embedding"] = values, ["$created_at"] = vector.CreatedAt.ToString("O")
            });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask AddNodeAsync(KnowledgeNode node, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO knowledge_nodes(id,memory_id,type,node_key,label,scope_key) VALUES($id,$memory_id,$type,$key,$label,$scope) ON CONFLICT(id) DO UPDATE SET memory_id=excluded.memory_id,type=excluded.type,node_key=excluded.node_key,label=excluded.label,scope_key=excluded.scope_key";
            AddParameters(command, new Dictionary<string, object?>
            {
                ["$id"] = node.Id.ToString(), ["$memory_id"] = node.MemoryId.ToString(), ["$type"] = node.Type,
                ["$key"] = node.Key, ["$label"] = node.Label, ["$scope"] = node.ScopeKey
            });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask AddEdgeAsync(KnowledgeEdge edge, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO knowledge_edges(id,from_id,relation,to_id,confidence,observed_at,metadata_json) VALUES($id,$from,$relation,$to,$confidence,$observed,$metadata) ON CONFLICT(id) DO UPDATE SET from_id=excluded.from_id, relation=excluded.relation, to_id=excluded.to_id, confidence=excluded.confidence, observed_at=excluded.observed_at, metadata_json=excluded.metadata_json";
            AddParameters(command, new Dictionary<string, object?>
            {
                ["$id"] = edge.Id.ToString(), ["$from"] = edge.From.ToString(), ["$relation"] = (int)edge.Relation, ["$to"] = edge.To.ToString(),
                ["$confidence"] = Math.Clamp(edge.Confidence, 0, 1), ["$observed"] = (edge.ObservedAt ?? DateTimeOffset.UtcNow).ToString("O"),
                ["$metadata"] = JsonSerializer.Serialize(edge.Metadata ?? new Dictionary<string, string>())
            });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask<IReadOnlyList<KnowledgeEdge>> GetEdgesAsync(IEnumerable<KnowledgeNodeId> nodeIds, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var ids = nodeIds.Select(static id => id.ToString()).ToArray();
        if (ids.Length == 0) return [];
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var placeholders = new string[ids.Length];
        for (var index = 0; index < ids.Length; index++)
        {
            placeholders[index] = $"$id{index}";
            command.Parameters.AddWithValue(placeholders[index], ids[index]);
        }

        command.CommandText = $"SELECT id,from_id,relation,to_id,confidence,observed_at,metadata_json FROM knowledge_edges WHERE from_id IN ({string.Join(',', placeholders)}) OR to_id IN ({string.Join(',', placeholders)})";
        var edges = new List<KnowledgeEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!KnowledgeNodeId.TryParse(reader.GetString(1), out var from) ||
                !KnowledgeNodeId.TryParse(reader.GetString(3), out var to) ||
                !KnowledgeEdgeId.TryParse(reader.GetString(0), out var id))
            {
                continue;
            }
            edges.Add(new(id, from, (KnowledgeRelationType)reader.GetInt32(2), to, reader.GetDouble(4), ParseDate(reader.GetString(5)), DeserializeDictionary(reader.GetString(6))));
        }

        return edges;
    }

    public async ValueTask<IndexedFileRecord?> GetIndexedFileAsync(string projectKey, string relativePath, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_key,relative_path,content_hash,size_bytes,indexed_at,memory_ids_json,branch,git_commit FROM indexed_files WHERE project_key=$project AND relative_path=$path";
        command.Parameters.AddWithValue("$project", projectKey);
        command.Parameters.AddWithValue("$path", relativePath);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new IndexedFileRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), ParseDate(reader.GetString(4)), JsonSerializer.Deserialize<List<MemoryId>>(reader.GetString(5)) ?? [], reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7));
    }

    public async ValueTask UpsertIndexedFileAsync(IndexedFileRecord record, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO indexed_files(project_key,relative_path,content_hash,size_bytes,indexed_at,memory_ids_json,branch,git_commit) VALUES($project,$path,$hash,$size,$indexed,$ids,$branch,$commit) ON CONFLICT(project_key,relative_path) DO UPDATE SET content_hash=excluded.content_hash,size_bytes=excluded.size_bytes,indexed_at=excluded.indexed_at,memory_ids_json=excluded.memory_ids_json,branch=excluded.branch,git_commit=excluded.git_commit";
            AddParameters(command, new Dictionary<string, object?>
            {
                ["$project"] = record.ProjectKey, ["$path"] = record.RelativePath, ["$hash"] = record.ContentHash, ["$size"] = record.SizeBytes,
                ["$indexed"] = record.IndexedAt.ToString("O"), ["$ids"] = JsonSerializer.Serialize(record.MemoryIds), ["$branch"] = record.Branch, ["$commit"] = record.Commit
            });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    public async ValueTask<MemoryStoreStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        async Task<long> CountAsync(string table, string? where = null)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM {table}{where ?? string.Empty}";
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }

        var entries = await CountAsync("memory_entries", " WHERE state <> 5").ConfigureAwait(false);
        var conflicts = await CountAsync("memory_entries", $" WHERE state = {(int)MemoryLifecycleState.Conflicted}").ConfigureAwait(false);
        // Staleness depends on the caller's current source-hash snapshot. It is
        // calculated during retrieval rather than guessed from persisted rows.
        const long stale = 0;
        return new MemoryStoreStatistics(entries, await CountAsync("memory_chunks").ConfigureAwait(false), await CountAsync("memory_embeddings").ConfigureAwait(false), await CountAsync("knowledge_nodes").ConfigureAwait(false), await CountAsync("knowledge_edges").ConfigureAwait(false), await CountAsync("indexed_files").ConfigureAwait(false), Conflicts: conflicts, StaleEntries: stale, DatabaseBytes: GetDatabaseBytes());
    }

    public async ValueTask<IReadOnlyList<MemoryEntry>> ExportAsync(string? projectKey = null, CancellationToken cancellationToken = default)
    {
        var query = new MemorySearchQuery(string.Empty, 1_000_000, projectKey, Mode: MemoryRetrievalMode.Recent);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var where = new StringBuilder(" WHERE e.state <> $deleted");
        command.Parameters.AddWithValue("$deleted", (int)MemoryLifecycleState.Deleted);
        AppendFilters(command, where, query, "e");
        command.CommandText = EntrySelect + where + " ORDER BY e.created_at LIMIT $limit";
        command.Parameters.AddWithValue("$limit", 1_000_000);
        return await ReadEntriesAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _writeGate.Dispose();
        }

        await ValueTask.CompletedTask;
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenRawAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS schema_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS memory_entries (
                id TEXT PRIMARY KEY, kind INTEGER NOT NULL, scope INTEGER NOT NULL, scope_key TEXT NOT NULL,
                title TEXT NOT NULL, content TEXT NOT NULL, state INTEGER NOT NULL, privacy INTEGER NOT NULL,
                source INTEGER NOT NULL, confidence REAL NOT NULL, observed_at TEXT NOT NULL, source_model TEXT,
                source_commit TEXT, source_path TEXT, source_hash TEXT, fact_key TEXT, fact_value TEXT,
                evidence_json TEXT NOT NULL, metadata_json TEXT NOT NULL, created_at TEXT NOT NULL,
                last_verified_at TEXT, superseded_at TEXT, supersedes TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_memory_scope ON memory_entries(scope,scope_key,state);
            CREATE INDEX IF NOT EXISTS idx_memory_source ON memory_entries(source_path,source_hash);
            CREATE INDEX IF NOT EXISTS idx_memory_fact ON memory_entries(fact_key,state);
            CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(memory_id UNINDEXED,title,content,path,symbol_name);
            CREATE TABLE IF NOT EXISTS memory_chunks (
                id TEXT PRIMARY KEY, memory_id TEXT NOT NULL, text TEXT NOT NULL, ordinal INTEGER NOT NULL,
                file_path TEXT, symbol_name TEXT, symbol_kind TEXT, language TEXT, start_line INTEGER, end_line INTEGER, content_hash TEXT,
                FOREIGN KEY(memory_id) REFERENCES memory_entries(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_chunks_symbol ON memory_chunks(symbol_name);
            CREATE TABLE IF NOT EXISTS memory_embeddings (
                id TEXT PRIMARY KEY, memory_id TEXT NOT NULL, model TEXT NOT NULL, dimensions INTEGER NOT NULL,
                embedding BLOB NOT NULL, created_at TEXT NOT NULL,
                FOREIGN KEY(memory_id) REFERENCES memory_entries(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_embeddings_model ON memory_embeddings(model,dimensions);
            CREATE TABLE IF NOT EXISTS knowledge_nodes (
                id TEXT PRIMARY KEY, memory_id TEXT NOT NULL, type TEXT NOT NULL, node_key TEXT NOT NULL, label TEXT NOT NULL, scope_key TEXT,
                FOREIGN KEY(memory_id) REFERENCES memory_entries(id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_nodes_key ON knowledge_nodes(type,node_key);
            CREATE TABLE IF NOT EXISTS knowledge_edges (
                id TEXT PRIMARY KEY, from_id TEXT NOT NULL, relation INTEGER NOT NULL, to_id TEXT NOT NULL,
                confidence REAL NOT NULL, observed_at TEXT NOT NULL, metadata_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_edges_from ON knowledge_edges(from_id,relation);
            CREATE INDEX IF NOT EXISTS idx_edges_to ON knowledge_edges(to_id,relation);
            CREATE TABLE IF NOT EXISTS indexed_files (
                project_key TEXT NOT NULL, relative_path TEXT NOT NULL, content_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL,
                indexed_at TEXT NOT NULL, memory_ids_json TEXT NOT NULL, branch TEXT, git_commit TEXT,
                PRIMARY KEY(project_key,relative_path)
            );
            INSERT INTO schema_meta(key,value) VALUES('memory.schema', $schema) ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """;
        command.Parameters.AddWithValue("$schema", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Older development databases may have been created while SQLite was still
        // executing the bootstrap batch.  Keep migrations additive and idempotent;
        // no user memory is discarded when a missing derived table is repaired.
        var repairStatements = new[]
        {
            "CREATE TABLE IF NOT EXISTS memory_chunks (id TEXT PRIMARY KEY, memory_id TEXT NOT NULL, text TEXT NOT NULL, ordinal INTEGER NOT NULL, file_path TEXT, symbol_name TEXT, symbol_kind TEXT, language TEXT, start_line INTEGER, end_line INTEGER, content_hash TEXT, FOREIGN KEY(memory_id) REFERENCES memory_entries(id) ON DELETE CASCADE);",
            "CREATE TABLE IF NOT EXISTS memory_embeddings (id TEXT PRIMARY KEY, memory_id TEXT NOT NULL, model TEXT NOT NULL, dimensions INTEGER NOT NULL, embedding BLOB NOT NULL, created_at TEXT NOT NULL, FOREIGN KEY(memory_id) REFERENCES memory_entries(id) ON DELETE CASCADE);",
            "CREATE TABLE IF NOT EXISTS knowledge_nodes (id TEXT PRIMARY KEY, memory_id TEXT NOT NULL, type TEXT NOT NULL, node_key TEXT NOT NULL, label TEXT NOT NULL, scope_key TEXT, FOREIGN KEY(memory_id) REFERENCES memory_entries(id) ON DELETE CASCADE);",
            "CREATE TABLE IF NOT EXISTS knowledge_edges (id TEXT PRIMARY KEY, from_id TEXT NOT NULL, relation INTEGER NOT NULL, to_id TEXT NOT NULL, confidence REAL NOT NULL, observed_at TEXT NOT NULL, metadata_json TEXT NOT NULL);",
            "CREATE TABLE IF NOT EXISTS indexed_files (project_key TEXT NOT NULL, relative_path TEXT NOT NULL, content_hash TEXT NOT NULL, size_bytes INTEGER NOT NULL, indexed_at TEXT NOT NULL, memory_ids_json TEXT NOT NULL, branch TEXT, git_commit TEXT, PRIMARY KEY(project_key,relative_path));",
            "CREATE INDEX IF NOT EXISTS idx_chunks_symbol ON memory_chunks(symbol_name);",
            "CREATE INDEX IF NOT EXISTS idx_embeddings_model ON memory_embeddings(model,dimensions);",
            "CREATE INDEX IF NOT EXISTS idx_nodes_key ON knowledge_nodes(type,node_key);",
            "CREATE INDEX IF NOT EXISTS idx_edges_from ON knowledge_edges(from_id,relation);",
            "CREATE INDEX IF NOT EXISTS idx_edges_to ON knowledge_edges(to_id,relation);"
        };
        foreach (var statement in repairStatements)
        {
            await using var repair = connection.CreateCommand();
            repair.CommandText = statement;
            await repair.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _initialization.ConfigureAwait(false);
        return await OpenRawAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenRawAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch { await connection.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private static readonly string EntrySelect = "SELECT e.id,e.kind,e.scope,e.scope_key,e.title,e.content,e.state,e.privacy,e.source,e.confidence,e.observed_at,e.source_model,e.source_commit,e.source_path,e.source_hash,e.fact_key,e.fact_value,e.evidence_json,e.metadata_json,e.created_at,e.last_verified_at,e.superseded_at,e.supersedes FROM memory_entries e";

    private static void AppendFilters(SqliteCommand command, StringBuilder where, MemorySearchQuery query, string alias)
    {
        if (query.ProjectKey is not null) { where.Append($" AND {alias}.scope_key=$project"); command.Parameters.AddWithValue("$project", query.ProjectKey); }
        if (query.Scope is not null) { where.Append($" AND {alias}.scope=$scope"); command.Parameters.AddWithValue("$scope", (int)query.Scope.Value); }
        if (query.Kinds is { Count: > 0 })
        {
            var values = query.Kinds.Select((kind, index) => { var name = $"$kind{index}"; command.Parameters.AddWithValue(name, (int)kind); return name; });
            where.Append($" AND {alias}.kind IN ({string.Join(',', values)})");
        }
        where.Append($" AND {alias}.state <> {(int)MemoryLifecycleState.Deleted}");
        where.Append($" AND {alias}.privacy <= {(int)query.MaximumPrivacy}");
        if (query.RequireEvidence) where.Append($" AND ({alias}.evidence_json <> '[]' OR {alias}.source IN ({(int)MemorySourceKind.SourceCode},{(int)MemorySourceKind.ToolResult},{(int)MemorySourceKind.VerifiedExecution}))");
        if (query.Since is not null) { where.Append($" AND {alias}.observed_at >= $since"); command.Parameters.AddWithValue("$since", query.Since.Value.ToString("O")); }
        if (query.Branch is not null)
        {
            where.Append($" AND {alias}.metadata_json LIKE $branch");
            var branch = query.Branch.Replace("\"", string.Empty, StringComparison.Ordinal);
            command.Parameters.AddWithValue("$branch", $"%\"branch\":\"{branch}\"%");
        }
    }

    private static async Task<IReadOnlyList<MemoryEntry>> ReadEntriesAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var entries = new List<MemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) entries.Add(ReadEntry(reader));
        return entries;
    }

    private static MemoryEntry ReadEntry(SqliteDataReader reader)
    {
        if (!MemoryId.TryParse(reader.GetString(reader.GetOrdinal("id")), out var id)) throw new InvalidDataException("Memory store contained an invalid memory ID.");
        return new MemoryEntry
        {
            Id = id, Kind = (MemoryKind)reader.GetInt32(reader.GetOrdinal("kind")), Scope = (MemoryScopeKind)reader.GetInt32(reader.GetOrdinal("scope")),
            ScopeKey = reader.GetString(reader.GetOrdinal("scope_key")), Title = reader.GetString(reader.GetOrdinal("title")), Content = reader.GetString(reader.GetOrdinal("content")),
            State = (MemoryLifecycleState)reader.GetInt32(reader.GetOrdinal("state")), Privacy = (MemoryPrivacyClass)reader.GetInt32(reader.GetOrdinal("privacy")),
            Provenance = new MemoryProvenance((MemorySourceKind)reader.GetInt32(reader.GetOrdinal("source")), reader.GetDouble(reader.GetOrdinal("confidence")), ParseDate(reader.GetString(reader.GetOrdinal("observed_at"))), NullableString(reader, "source_model"), NullableString(reader, "source_commit"), NullableString(reader, "source_path"), NullableString(reader, "source_hash"), NullableString(reader, "fact_key"), NullableString(reader, "fact_value")),
            Evidence = JsonSerializer.Deserialize<ImmutableArray<MemoryEvidenceLink>>(reader.GetString(reader.GetOrdinal("evidence_json"))),
            Metadata = JsonSerializer.Deserialize<ImmutableDictionary<string, string>>(reader.GetString(reader.GetOrdinal("metadata_json"))) ?? ImmutableDictionary<string, string>.Empty,
            CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("created_at"))), LastObservedAt = ParseDate(reader.GetString(reader.GetOrdinal("observed_at"))),
            LastVerifiedAt = NullableDate(reader, "last_verified_at"), SupersededAt = NullableDate(reader, "superseded_at"),
            Supersedes = NullableMemoryId(reader, "supersedes")
        };
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        AddParameters(command, values);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(SqliteCommand command, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var pair in values) command.Parameters.AddWithValue(pair.Key, pair.Value ?? DBNull.Value);
    }

    private static string? NullableDate(DateTimeOffset? value) => value?.ToString("O");
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : DateTimeOffset.MinValue;
    private static string? NullableString(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : reader.GetString(reader.GetOrdinal(name));
    private static DateTimeOffset? NullableDate(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : ParseDate(reader.GetString(reader.GetOrdinal(name)));
    private static MemoryId? NullableMemoryId(SqliteDataReader reader, string name) => reader.IsDBNull(reader.GetOrdinal(name)) ? null : MemoryId.TryParse(reader.GetString(reader.GetOrdinal(name)), out var id) ? id : null;
    private static Dictionary<string, string> DeserializeDictionary(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
    private long GetDatabaseBytes() => File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0;
}

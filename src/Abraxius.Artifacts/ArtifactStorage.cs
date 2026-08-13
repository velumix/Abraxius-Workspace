using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Abraxius.Artifacts;

public interface IArtifactContentStore
{
    ValueTask<ArtifactContentDescriptor> PutAsync(Stream content, string mediaType, string? fileName = null, CancellationToken cancellationToken = default);
    ValueTask<Stream> OpenReadAsync(ArtifactBlobId blobId, CancellationToken cancellationToken = default);
    ValueTask<bool> VerifyAsync(ArtifactContentDescriptor descriptor, CancellationToken cancellationToken = default);
    ValueTask<long> GetStoredBytesAsync(CancellationToken cancellationToken = default);
    ValueTask<int> CollectOrphansAsync(IReadOnlySet<ArtifactBlobId> retained, CancellationToken cancellationToken = default);
}

public sealed class FileArtifactContentStore : IArtifactContentStore
{
    private readonly string _root;
    private readonly string _temporary;

    public FileArtifactContentStore(string root)
    {
        _root = Path.GetFullPath(root);
        _temporary = Path.Combine(_root, ".tmp");
    }

    public async ValueTask<ArtifactContentDescriptor> PutAsync(Stream content, string mediaType, string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        Directory.CreateDirectory(_temporary);
        var temporary = Path.Combine(_temporary, $"{Guid.NewGuid():N}.part");
        long length = 0;
        string hash;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read);
                    length += read;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }

            var destination = BlobPath(hash);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Move(temporary, destination);
            else File.Delete(temporary);
            return new(new ArtifactBlobId(hash), hash, length, mediaType, fileName,
                new ArtifactLocation(ArtifactLocationKind.ContentStore, new Uri(destination).AbsoluteUri));
        }
        catch
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    public ValueTask<Stream> OpenReadAsync(ArtifactBlobId blobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(BlobPath(blobId.Value), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    public async ValueTask<bool> VerifyAsync(ArtifactContentDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var path = BlobPath(descriptor.BlobId.Value);
        if (!File.Exists(path) || new FileInfo(path).Length != descriptor.Length) return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(descriptor.ContentHash));
    }

    public ValueTask<long> GetStoredBytesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_root)) return ValueTask.FromResult(0L);
        return ValueTask.FromResult(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(static path => !path.EndsWith(".part", StringComparison.Ordinal)).Sum(path => new FileInfo(path).Length));
    }

    public ValueTask<int> CollectOrphansAsync(IReadOnlySet<ArtifactBlobId> retained, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(_root)) return ValueTask.FromResult(0);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.StartsWith(_temporary, StringComparison.Ordinal)) { if (Path.GetExtension(file) == ".part") { File.Delete(file); count++; } continue; }
            if (!retained.Contains(new ArtifactBlobId(Path.GetFileName(file)))) { File.Delete(file); count++; }
        }
        return ValueTask.FromResult(count);
    }

    private string BlobPath(string hash)
    {
        if (hash.Length != 64 || hash.Any(static c => !Uri.IsHexDigit(c))) throw new ArgumentException("Invalid content hash.", nameof(hash));
        return Path.Combine(_root, hash[..2], hash[2..4], hash);
    }
}

public sealed class InMemoryArtifactContentStore : IArtifactContentStore
{
    private readonly ConcurrentDictionary<ArtifactBlobId, byte[]> _blobs = new();
    public async ValueTask<ArtifactContentDescriptor> PutAsync(Stream content, string mediaType, string? fileName = null, CancellationToken cancellationToken = default)
    {
        using var output = new MemoryStream();
        await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        var bytes = output.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var id = new ArtifactBlobId(hash);
        _blobs.TryAdd(id, bytes);
        return new(id, hash, bytes.LongLength, mediaType, fileName, new(ArtifactLocationKind.ContentStore, $"memory://artifact/{hash}"));
    }
    public ValueTask<Stream> OpenReadAsync(ArtifactBlobId blobId, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<Stream>(new MemoryStream(_blobs[blobId], writable: false)); }
    public ValueTask<bool> VerifyAsync(ArtifactContentDescriptor descriptor, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_blobs.TryGetValue(descriptor.BlobId, out var bytes) && bytes.LongLength == descriptor.Length && Convert.ToHexString(SHA256.HashData(bytes)).Equals(descriptor.ContentHash, StringComparison.OrdinalIgnoreCase)); }
    public ValueTask<long> GetStoredBytesAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_blobs.Values.Sum(static bytes => (long)bytes.Length)); }
    public ValueTask<int> CollectOrphansAsync(IReadOnlySet<ArtifactBlobId> retained, CancellationToken cancellationToken = default) { var count = 0; foreach (var id in _blobs.Keys) if (!retained.Contains(id) && _blobs.TryRemove(id, out _)) count++; return ValueTask.FromResult(count); }
}

public interface IArtifactStore : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask<ArtifactAggregate?> GetAsync(ArtifactId id, CancellationToken cancellationToken = default);
    ValueTask<ArtifactAggregate?> GetByRevisionAsync(ArtifactRevisionId revisionId, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ArtifactDescriptor>> QueryAsync(ArtifactQuery query, CancellationToken cancellationToken = default);
    ValueTask InsertAsync(ArtifactAggregate artifact, CancellationToken cancellationToken = default);
    ValueTask UpdateAsync(ArtifactAggregate artifact, ArtifactRevisionId expectedCurrentRevision, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ArtifactAggregate> ReadAllAsync(CancellationToken cancellationToken = default);
}

public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly ConcurrentDictionary<ArtifactId, ArtifactAggregate> _artifacts = new();
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<ArtifactAggregate?> GetAsync(ArtifactId id, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _artifacts.TryGetValue(id, out var value); return ValueTask.FromResult(value); }
    public ValueTask<ArtifactAggregate?> GetByRevisionAsync(ArtifactRevisionId revisionId, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_artifacts.Values.FirstOrDefault(item => item.Revisions.Any(revision => revision.Id == revisionId))); }
    public ValueTask<IReadOnlyList<ArtifactDescriptor>> QueryAsync(ArtifactQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = _artifacts.Values.Select(static item => item.Descriptor)
            .Where(item => query.Text is null || item.Title.Contains(query.Text, StringComparison.OrdinalIgnoreCase))
            .Where(item => query.Kind is null || item.Kind == query.Kind)
            .Where(item => query.State is null || item.State == query.State)
            .Where(item => query.ProjectId is null || item.ProjectId == query.ProjectId)
            .Where(item => query.Producer is null || item.Producer.PrincipalId == query.Producer)
            .OrderByDescending(static item => item.UpdatedAt).Skip(query.Offset).Take(Math.Clamp(query.Limit, 1, 1000)).ToArray();
        return ValueTask.FromResult<IReadOnlyList<ArtifactDescriptor>>(result);
    }
    public ValueTask InsertAsync(ArtifactAggregate artifact, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); if (!_artifacts.TryAdd(artifact.Descriptor.Id, artifact)) throw new InvalidOperationException("Artifact already exists."); return ValueTask.CompletedTask; }
    public ValueTask UpdateAsync(ArtifactAggregate artifact, ArtifactRevisionId expectedCurrentRevision, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            if (!_artifacts.TryGetValue(artifact.Descriptor.Id, out var current)) throw new KeyNotFoundException("Artifact does not exist.");
            if (current.Descriptor.CurrentRevision != expectedCurrentRevision) throw new ArtifactConcurrencyException(current.Descriptor.CurrentRevision);
            if (_artifacts.TryUpdate(artifact.Descriptor.Id, artifact, current)) return ValueTask.CompletedTask;
        }
    }
    public async IAsyncEnumerable<ArtifactAggregate> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { foreach (var item in _artifacts.Values) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); } }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SqliteArtifactStore : IArtifactStore
{
    private readonly string _connectionString;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault
    };
    public SqliteArtifactStore(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS artifacts(
              artifact_id TEXT PRIMARY KEY,
              current_revision_id TEXT NOT NULL,
              kind TEXT NOT NULL,
              state INTEGER NOT NULL,
              project_id TEXT NULL,
              producer_id TEXT NOT NULL,
              title TEXT NOT NULL,
              updated_utc TEXT NOT NULL,
              payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_artifacts_query ON artifacts(state, kind, updated_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_artifacts_project ON artifacts(project_id, updated_utc DESC);
            CREATE TABLE IF NOT EXISTS artifact_revisions(revision_id TEXT PRIMARY KEY, artifact_id TEXT NOT NULL, revision_number INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_artifact_revisions_artifact ON artifact_revisions(artifact_id, revision_number);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ArtifactAggregate?> GetAsync(ArtifactId id, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload_json FROM artifacts WHERE artifact_id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? null : JsonSerializer.Deserialize<ArtifactAggregate>(value, _json);
    }

    public async ValueTask<ArtifactAggregate?> GetByRevisionAsync(ArtifactRevisionId revisionId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand(); command.CommandText = "SELECT a.payload_json FROM artifact_revisions r JOIN artifacts a ON a.artifact_id=r.artifact_id WHERE r.revision_id=$id"; command.Parameters.AddWithValue("$id", revisionId.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return value is null ? null : JsonSerializer.Deserialize<ArtifactAggregate>(value, _json);
    }

    public async ValueTask<IReadOnlyList<ArtifactDescriptor>> QueryAsync(ArtifactQuery query, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM artifacts
            WHERE ($text IS NULL OR title LIKE '%' || $text || '%')
              AND ($kind IS NULL OR kind=$kind) AND ($state IS NULL OR state=$state)
              AND ($project IS NULL OR project_id=$project) AND ($producer IS NULL OR producer_id=$producer)
            ORDER BY updated_utc DESC LIMIT $limit OFFSET $offset
            """;
        command.Parameters.AddWithValue("$text", (object?)query.Text ?? DBNull.Value); command.Parameters.AddWithValue("$kind", (object?)query.Kind?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$state", query.State is null ? DBNull.Value : (int)query.State.Value); command.Parameters.AddWithValue("$project", (object?)query.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("$producer", (object?)query.Producer?.Value ?? DBNull.Value); command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 1000)); command.Parameters.AddWithValue("$offset", Math.Max(0, query.Offset));
        var result = new List<ArtifactDescriptor>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) if (JsonSerializer.Deserialize<ArtifactAggregate>(reader.GetString(0), _json) is { } item) result.Add(item.Descriptor);
        return result;
    }

    public async ValueTask InsertAsync(ArtifactAggregate artifact, CancellationToken cancellationToken = default) => await WriteAsync(artifact, null, insert: true, cancellationToken).ConfigureAwait(false);
    public async ValueTask UpdateAsync(ArtifactAggregate artifact, ArtifactRevisionId expectedCurrentRevision, CancellationToken cancellationToken = default) => await WriteAsync(artifact, expectedCurrentRevision, insert: false, cancellationToken).ConfigureAwait(false);

    private async ValueTask WriteAsync(ArtifactAggregate artifact, ArtifactRevisionId? expected, bool insert, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        if (!insert)
        {
            await using var check = connection.CreateCommand(); check.Transaction = transaction; check.CommandText = "SELECT current_revision_id FROM artifacts WHERE artifact_id=$id"; check.Parameters.AddWithValue("$id", artifact.Descriptor.Id.ToString());
            var current = await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
            if (current is null) throw new KeyNotFoundException("Artifact does not exist.");
            if (!current.Equals(expected?.ToString(), StringComparison.Ordinal)) throw new ArtifactConcurrencyException(new ArtifactRevisionId(Guid.ParseExact(current, "N")));
        }
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = insert
                ? "INSERT INTO artifacts VALUES($id,$revision,$kind,$state,$project,$producer,$title,$updated,$payload)"
                : "UPDATE artifacts SET current_revision_id=$revision,kind=$kind,state=$state,project_id=$project,producer_id=$producer,title=$title,updated_utc=$updated,payload_json=$payload WHERE artifact_id=$id";
            command.Parameters.AddWithValue("$id", artifact.Descriptor.Id.ToString()); command.Parameters.AddWithValue("$revision", artifact.Descriptor.CurrentRevision.ToString()); command.Parameters.AddWithValue("$kind", artifact.Descriptor.Kind.Value);
            command.Parameters.AddWithValue("$state", (int)artifact.Descriptor.State); command.Parameters.AddWithValue("$project", (object?)artifact.Descriptor.ProjectId ?? DBNull.Value); command.Parameters.AddWithValue("$producer", artifact.Descriptor.Producer.PrincipalId.Value);
            command.Parameters.AddWithValue("$title", artifact.Descriptor.Title); command.Parameters.AddWithValue("$updated", artifact.Descriptor.UpdatedAt.ToString("O")); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(artifact, _json));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var revision in artifact.Revisions)
        {
            await using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "INSERT OR IGNORE INTO artifact_revisions VALUES($revision,$artifact,$number)";
            command.Parameters.AddWithValue("$revision", revision.Id.ToString()); command.Parameters.AddWithValue("$artifact", artifact.Descriptor.Id.ToString()); command.Parameters.AddWithValue("$number", revision.RevisionNumber);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ArtifactAggregate> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload_json FROM artifacts ORDER BY updated_utc";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) if (JsonSerializer.Deserialize<ArtifactAggregate>(reader.GetString(0), _json) is { } item) yield return item;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ArtifactConcurrencyException(ArtifactRevisionId currentRevision) : InvalidOperationException($"Artifact changed concurrently; current revision is {currentRevision}.")
{
    public ArtifactRevisionId CurrentRevision { get; } = currentRevision;
}

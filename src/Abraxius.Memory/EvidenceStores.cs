using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Abraxius.Core;
using Abraxius.Protocol;

namespace Abraxius.Memory;

public sealed class InMemoryEvidenceStore : IEvidenceStore
{
    private readonly ConcurrentDictionary<EvidenceId, EvidenceItem> _items = new();

    public ValueTask<EvidenceReference> StoreAsync(EvidenceInput input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(input);
        var bytes = input.Data.ToArray();
        var id = EvidenceId.New();
        var reference = new EvidenceReference(
            id,
            input.Kind,
            input.Name,
            input.ContentType,
            bytes.LongLength,
            Convert.ToHexString(SHA256.HashData(bytes)),
            DateTimeOffset.UtcNow,
            input.Metadata is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(input.Metadata));
        _items[id] = new EvidenceItem(reference, bytes);
        return ValueTask.FromResult(reference);
    }

    public ValueTask<EvidenceItem?> GetAsync(EvidenceId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_items.TryGetValue(id, out var item) ? item : null);
    }
}

public sealed class FileEvidenceStore : IEvidenceStore
{
    private readonly string _directory;
    private readonly InMemoryEvidenceStore _hot = new();

    public FileEvidenceStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public async ValueTask<EvidenceReference> StoreAsync(EvidenceInput input, CancellationToken cancellationToken = default)
    {
        var reference = await _hot.StoreAsync(input, cancellationToken).ConfigureAwait(false);
        var item = await _hot.GetAsync(reference.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Evidence was not available after storing it in the hot tier.");
        var path = GetPath(reference.Id);
        await File.WriteAllBytesAsync(path, item.Data.ToArray(), cancellationToken).ConfigureAwait(false);
        return reference;
    }

    public async ValueTask<EvidenceItem?> GetAsync(EvidenceId id, CancellationToken cancellationToken = default)
    {
        var hot = await _hot.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (hot is not null)
        {
            return hot;
        }

        var path = GetPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        var data = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var reference = new EvidenceReference(
            id,
            "artifact",
            Path.GetFileName(path),
            "application/octet-stream",
            data.LongLength,
            Convert.ToHexString(SHA256.HashData(data)),
            File.GetCreationTimeUtc(path),
            new Dictionary<string, string>());
        return new EvidenceItem(reference, data);
    }

    private string GetPath(EvidenceId id) => Path.Combine(_directory, $"{id}.bin");
}

public sealed record MemoryQuery(
    string Text,
    int Limit = 8,
    IReadOnlyList<string>? Scopes = null,
    ExecutionId? ExecutionId = null)
{
    public string? ProjectKey { get; init; }
    public MemoryScopeKind? Scope { get; init; }
    public MemoryRetrievalMode Mode { get; init; } = MemoryRetrievalMode.Hybrid;
}

public sealed record MemoryHit(
    string Key,
    string Text,
    double Score,
    IReadOnlyList<EvidenceId> Evidence)
{
    public MemoryId? MemoryId { get; init; }
    public MemoryKind? Kind { get; init; }
    public string? Source { get; init; }
    public string? Explanation { get; init; }
}

public sealed record MemoryResult(
    IReadOnlyList<MemoryHit> Hits,
    TimeSpan Latency,
    string? Provider = null);

public interface IMemoryProvider
{
    ValueTask<MemoryResult> QueryAsync(MemoryQuery query, CancellationToken cancellationToken = default);
}

public sealed class MockMemoryProvider : IMemoryProvider
{
    private readonly TimeSpan _latency;
    private readonly IReadOnlyList<MemoryHit> _hits;

    public MockMemoryProvider(TimeSpan? latency = null, IReadOnlyList<MemoryHit>? hits = null)
    {
        _latency = latency ?? TimeSpan.FromMilliseconds(500);
        _hits = hits ??
        [
            new MemoryHit("runtime-principles", "Independent work is scheduled from explicit dependency edges.", 0.98, []),
            new MemoryHit("ui-coalescing", "The UI consumes bounded snapshots instead of every runtime event.", 0.91, [])
        ];
    }

    public async ValueTask<MemoryResult> QueryAsync(MemoryQuery query, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        var hits = _hits.Take(Math.Max(0, query.Limit)).ToArray();
        return new MemoryResult(hits, Stopwatch.GetElapsedTime(started), "mock-memory");
    }
}

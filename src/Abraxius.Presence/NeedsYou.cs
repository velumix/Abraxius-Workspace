using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Abraxius.Protocol;
using Microsoft.Data.Sqlite;

namespace Abraxius.Presence;

public sealed class InMemoryNeedsYouStore : INeedsYouStore
{
    private readonly ConcurrentDictionary<NeedsYouId, NeedsYouItem> _items = new();
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask UpsertAsync(NeedsYouItem item, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _items[item.Id] = item; return ValueTask.CompletedTask; }
    public ValueTask<NeedsYouItem?> GetAsync(NeedsYouId id, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_items.TryGetValue(id, out var item) ? item : null); }
    public ValueTask<IReadOnlyList<NeedsYouItem>> ListAsync(bool includeResolved = false, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); IReadOnlyList<NeedsYouItem> result = _items.Values.Where(item => includeResolved || item.State is NeedsYouState.Pending or NeedsYouState.Viewed).OrderByDescending(static item => item.Priority).ThenBy(static item => item.Created).ToArray(); return ValueTask.FromResult(result); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SqliteNeedsYouStore : INeedsYouStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    public SqliteNeedsYouStore(string path)
    {
        var fullPath = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
    }
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE IF NOT EXISTS needs_you(id TEXT PRIMARY KEY, state INTEGER NOT NULL, created TEXT NOT NULL, payload TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_needs_you_state_created ON needs_you(state,created);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask UpsertAsync(NeedsYouItem item, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO needs_you(id,state,created,payload) VALUES($id,$state,$created,$payload) ON CONFLICT(id) DO UPDATE SET state=excluded.state,payload=excluded.payload";
        command.Parameters.AddWithValue("$id", item.Id.ToString()); command.Parameters.AddWithValue("$state", (int)item.State); command.Parameters.AddWithValue("$created", item.Created.ToString("O")); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<NeedsYouItem?> GetAsync(NeedsYouId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM needs_you WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string; return payload is null ? null : JsonSerializer.Deserialize<NeedsYouItem>(payload, JsonOptions);
    }
    public async ValueTask<IReadOnlyList<NeedsYouItem>> ListAsync(bool includeResolved = false, CancellationToken cancellationToken = default)
    {
        var items = new List<NeedsYouItem>(); await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand();
        command.CommandText = includeResolved ? "SELECT payload FROM needs_you ORDER BY created DESC" : $"SELECT payload FROM needs_you WHERE state IN ({(int)NeedsYouState.Pending},{(int)NeedsYouState.Viewed}) ORDER BY created DESC";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { var item = JsonSerializer.Deserialize<NeedsYouItem>(reader.GetString(0), JsonOptions); if (item is not null) items.Add(item); }
        return items;
    }
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken token) { var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(token).ConfigureAwait(false); return connection; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class NeedsYouService(INeedsYouStore store, INotificationHub notifications) : INeedsYouService
{
    public event EventHandler? Changed;
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => store.InitializeAsync(cancellationToken);
    public async ValueTask<NeedsYouItem> CreateAsync(NeedsYouItem item, AttentionContext context, CancellationToken cancellationToken = default)
    {
        await store.UpsertAsync(item, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        await notifications.PublishAsync(new AbraxiusNotification(NotificationId.New(), NotificationCategory.NeedsYou, item.Priority,
            "Abraxius needs you", item.ContextSummary, new NotificationTarget(item.MissionId, item.AssignmentId, NeedsYouId: item.Id),
            [new(new NotificationActionId("needs-you.review"), "Review", true)], item.Source, item.Created,
            item.Deadline, $"needs-you:{item.Id}", NotificationPrivacy.Redacted, item.SourceEventId), context, cancellationToken).ConfigureAwait(false);
        return item;
    }
    public ValueTask<NeedsYouItem?> MarkViewedAsync(NeedsYouId id, CancellationToken cancellationToken = default) => UpdateAsync(id, item => item with { State = NeedsYouState.Viewed }, cancellationToken);
    public ValueTask<NeedsYouItem?> SnoozeAsync(NeedsYouId id, DateTimeOffset until, CancellationToken cancellationToken = default) => UpdateAsync(id, item => item with { SnoozedUntil = until }, cancellationToken);
    public ValueTask<NeedsYouItem?> ResolveAsync(NeedsYouId id, NeedsYouResolution resolution, string? note = null, CancellationToken cancellationToken = default) => UpdateAsync(id, item => item with { State = NeedsYouState.Resolved, Resolution = resolution, ResolutionNote = note }, cancellationToken);
    public ValueTask<IReadOnlyList<NeedsYouItem>> ListAsync(bool includeResolved = false, CancellationToken cancellationToken = default) => store.ListAsync(includeResolved, cancellationToken);
    private async ValueTask<NeedsYouItem?> UpdateAsync(NeedsYouId id, Func<NeedsYouItem, NeedsYouItem> update, CancellationToken token)
    {
        var item = await store.GetAsync(id, token).ConfigureAwait(false); if (item is null) return null; var changed = update(item); await store.UpsertAsync(changed, token).ConfigureAwait(false); Changed?.Invoke(this, EventArgs.Empty); return changed;
    }
}

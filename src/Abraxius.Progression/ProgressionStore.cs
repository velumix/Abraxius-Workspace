using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Abraxius.Progression;

public interface IProgressionStore : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask<ProgressionSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken = default);
    ValueTask<bool> TryCommitRewardAsync(MissionRewardRecord reward, IReadOnlyList<ProgressionEvent> events, ProgressionSnapshot snapshot, CancellationToken cancellationToken = default);
    IAsyncEnumerable<MissionRewardRecord> ReadRewardsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<ProgressionEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
    ValueTask CommitStandaloneEventAsync(ProgressionEvent progressionEvent, ProgressionSnapshot snapshot, CancellationToken cancellationToken = default);
    ValueTask SaveSnapshotAsync(ProgressionSnapshot snapshot, CancellationToken cancellationToken = default);
}

public sealed class InMemoryProgressionStore : IProgressionStore
{
    private readonly ConcurrentDictionary<string, MissionRewardRecord> _rewards = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<ProgressionEvent> _events = new();
    private ProgressionSnapshot? _snapshot;
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask<ProgressionSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_snapshot); }
    public ValueTask<bool> TryCommitRewardAsync(MissionRewardRecord reward, IReadOnlyList<ProgressionEvent> events, ProgressionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_rewards.TryAdd(reward.Id.Value, reward)) return ValueTask.FromResult(false);
        foreach (var progressionEvent in events) _events.Enqueue(progressionEvent);
        _snapshot = snapshot;
        return ValueTask.FromResult(true);
    }
    public async IAsyncEnumerable<ProgressionEvent> ReadEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in _events.OrderBy(static item => item.Timestamp)) { cancellationToken.ThrowIfCancellationRequested(); yield return item; }
        await Task.CompletedTask.ConfigureAwait(false);
    }
    public ValueTask CommitStandaloneEventAsync(ProgressionEvent progressionEvent, ProgressionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); _events.Enqueue(progressionEvent); _snapshot = snapshot; return ValueTask.CompletedTask;
    }
    public async IAsyncEnumerable<MissionRewardRecord> ReadRewardsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var reward in _rewards.Values.OrderBy(static item => item.Timestamp)) { cancellationToken.ThrowIfCancellationRequested(); yield return reward; }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask SaveSnapshotAsync(ProgressionSnapshot snapshot, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _snapshot = snapshot; return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SqliteProgressionStore : IProgressionStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;

    public SqliteProgressionStore(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS progression_schema(version INTEGER NOT NULL);
            INSERT INTO progression_schema(version) SELECT $version WHERE NOT EXISTS(SELECT 1 FROM progression_schema);
            CREATE TABLE IF NOT EXISTS mission_rewards(
                reward_id TEXT PRIMARY KEY,
                trajectory_id TEXT NOT NULL,
                mission_id TEXT NOT NULL,
                rules_version INTEGER NOT NULL,
                completed_at TEXT NOT NULL,
                payload TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS ix_rewards_trajectory_rules ON mission_rewards(trajectory_id, rules_version);
            CREATE TABLE IF NOT EXISTS progression_events(
                event_id TEXT PRIMARY KEY,
                reward_id TEXT NULL,
                trajectory_id TEXT NULL,
                occurred_at TEXT NOT NULL,
                kind INTEGER NOT NULL,
                payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_progression_events_time ON progression_events(occurred_at);
            CREATE TABLE IF NOT EXISTS progression_snapshot(
                snapshot_id INTEGER PRIMARY KEY CHECK(snapshot_id = 1),
                sequence INTEGER NOT NULL,
                payload TEXT NOT NULL);
            """;
        command.Parameters.AddWithValue("$version", SchemaVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<ProgressionSnapshot?> LoadSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM progression_snapshot WHERE snapshot_id=1";
        var payload = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        return payload is null ? null : JsonSerializer.Deserialize<ProgressionSnapshot>(payload, JsonOptions);
    }

    public async ValueTask<bool> TryCommitRewardAsync(MissionRewardRecord reward, IReadOnlyList<ProgressionEvent> events, ProgressionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (var rewardCommand = connection.CreateCommand())
            {
                rewardCommand.Transaction = transaction;
                rewardCommand.CommandText = "INSERT INTO mission_rewards(reward_id,trajectory_id,mission_id,rules_version,completed_at,payload) VALUES($id,$trajectory,$mission,$rules,$at,$payload)";
                rewardCommand.Parameters.AddWithValue("$id", reward.Id.Value);
                rewardCommand.Parameters.AddWithValue("$trajectory", reward.TrajectoryId.ToString());
                rewardCommand.Parameters.AddWithValue("$mission", reward.MissionId.ToString());
                rewardCommand.Parameters.AddWithValue("$rules", reward.RulesVersion.Value);
                rewardCommand.Parameters.AddWithValue("$at", reward.Timestamp.ToString("O"));
                rewardCommand.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(reward, JsonOptions));
                await rewardCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            foreach (var item in events)
            {
                await using var eventCommand = connection.CreateCommand();
                eventCommand.Transaction = transaction;
                eventCommand.CommandText = "INSERT INTO progression_events(event_id,reward_id,trajectory_id,occurred_at,kind,payload) VALUES($id,$reward,$trajectory,$at,$kind,$payload)";
                eventCommand.Parameters.AddWithValue("$id", item.Id.ToString());
                eventCommand.Parameters.AddWithValue("$reward", (object?)item.RewardId?.Value ?? DBNull.Value);
                eventCommand.Parameters.AddWithValue("$trajectory", (object?)item.SourceTrajectoryId?.ToString() ?? DBNull.Value);
                eventCommand.Parameters.AddWithValue("$at", item.Timestamp.ToString("O"));
                eventCommand.Parameters.AddWithValue("$kind", (int)item.Kind);
                eventCommand.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(item, JsonOptions));
                await eventCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await UpsertSnapshotAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    public async IAsyncEnumerable<MissionRewardRecord> ReadRewardsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM mission_rewards ORDER BY completed_at,reward_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var reward = JsonSerializer.Deserialize<MissionRewardRecord>(reader.GetString(0), JsonOptions);
            if (reward is not null) yield return reward;
        }
    }

    public async IAsyncEnumerable<ProgressionEvent> ReadEventsAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM progression_events ORDER BY occurred_at,event_id";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = JsonSerializer.Deserialize<ProgressionEvent>(reader.GetString(0), JsonOptions);
            if (item is not null) yield return item;
        }
    }

    public async ValueTask CommitStandaloneEventAsync(ProgressionEvent progressionEvent, ProgressionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var eventCommand = connection.CreateCommand())
        {
            eventCommand.Transaction = transaction;
            eventCommand.CommandText = "INSERT INTO progression_events(event_id,reward_id,trajectory_id,occurred_at,kind,payload) VALUES($id,NULL,NULL,$at,$kind,$payload)";
            eventCommand.Parameters.AddWithValue("$id", progressionEvent.Id.ToString());
            eventCommand.Parameters.AddWithValue("$at", progressionEvent.Timestamp.ToString("O"));
            eventCommand.Parameters.AddWithValue("$kind", (int)progressionEvent.Kind);
            eventCommand.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(progressionEvent, JsonOptions));
            await eventCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await UpsertSnapshotAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveSnapshotAsync(ProgressionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await UpsertSnapshotAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task UpsertSnapshotAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, ProgressionSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO progression_snapshot(snapshot_id,sequence,payload) VALUES(1,$sequence,$payload) ON CONFLICT(snapshot_id) DO UPDATE SET sequence=excluded.sequence,payload=excluded.payload";
        command.Parameters.AddWithValue("$sequence", snapshot.Sequence);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(snapshot, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

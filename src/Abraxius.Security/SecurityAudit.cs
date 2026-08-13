using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Abraxius.Security;

public enum SecurityAuditEventType
{
    AuthorizationRequested, AuthorizationAllowed, AuthorizationDenied, ApprovalRequested, ApprovalGranted,
    ApprovalRejected, GrantIssued, GrantRevoked, SecretUsed, PolicyChanged, PluginPermissionChanged,
    SandboxViolation, ExecutionSucceeded, ExecutionFailed
}

public sealed record SecurityAuditEvent(
    SecurityAuditEventId Id,
    SecurityAuditEventType Type,
    DateTimeOffset Timestamp,
    PrincipalId Principal,
    string Action,
    string Resource,
    string? MissionId = null,
    string? AssignmentId = null,
    string? AgentInstanceId = null,
    string? SkillExecutionId = null,
    string? DecisionId = null,
    string? GrantId = null,
    string? ReasonCode = null,
    string? ResultCode = null,
    string SchemaVersion = "security.audit/1")
{
    public static SecurityAuditEvent Requested(AuthorizationRequest request) => From(SecurityAuditEventType.AuthorizationRequested, request);
    public static SecurityAuditEvent Decided(AuthorizationRequest request, AuthorizationDecision decision) => From(decision.IsAllowed ? SecurityAuditEventType.AuthorizationAllowed : SecurityAuditEventType.AuthorizationDenied,
        request, decision.DecisionId.ToString(), decision.Grant?.GrantId.ToString(), decision.ReasonCode.ToString());
    public static SecurityAuditEvent Executed(AuthorizationRequest request, AuthorizationDecision decision, bool succeeded, string? resultCode) => From(
        succeeded ? SecurityAuditEventType.ExecutionSucceeded : SecurityAuditEventType.ExecutionFailed, request, decision.DecisionId.ToString(), decision.Grant?.GrantId.ToString(), decision.ReasonCode.ToString(), resultCode);
    private static SecurityAuditEvent From(SecurityAuditEventType type, AuthorizationRequest request, string? decision = null, string? grant = null, string? reason = null, string? result = null) =>
        new(SecurityAuditEventId.New(), type, DateTimeOffset.UtcNow, request.Subject.PrincipalId, request.Action.Operation, request.Action.Resource.CanonicalUri,
            request.Subject.MissionId?.ToString(), request.Subject.AssignmentId?.ToString(), request.Subject.AgentInstanceId?.ToString(), request.Subject.SkillExecutionId?.ToString(), decision, grant, reason, result);
}

public interface ISecurityAuditStore : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default);
    IAsyncEnumerable<SecurityAuditEvent> QueryAsync(int limit = 200, CancellationToken cancellationToken = default);
}

public sealed class InMemorySecurityAuditStore : ISecurityAuditStore
{
    private readonly ConcurrentQueue<SecurityAuditEvent> _events = new();
    private readonly int _limit;
    public InMemorySecurityAuditStore(int limit = 10_000) => _limit = Math.Max(100, limit);
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); _events.Enqueue(auditEvent); while (_events.Count > _limit) _events.TryDequeue(out _); return ValueTask.CompletedTask;
    }
    public async IAsyncEnumerable<SecurityAuditEvent> QueryAsync(int limit = 200, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in _events.Reverse().Take(Math.Max(1, limit))) { cancellationToken.ThrowIfCancellationRequested(); yield return item; await Task.Yield(); }
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class SqliteSecurityAuditStore : ISecurityAuditStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    public SqliteSecurityAuditStore(string path)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared }.ToString();
    }
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE IF NOT EXISTS security_audit(sequence INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,type INTEGER NOT NULL,timestamp TEXT NOT NULL,mission_id TEXT,payload TEXT NOT NULL); CREATE INDEX IF NOT EXISTS ix_security_audit_time ON security_audit(timestamp); CREATE INDEX IF NOT EXISTS ix_security_audit_mission ON security_audit(mission_id);";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask AppendAsync(SecurityAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO security_audit(id,type,timestamp,mission_id,payload) VALUES($id,$type,$time,$mission,$payload)";
        command.Parameters.AddWithValue("$id", auditEvent.Id.ToString()); command.Parameters.AddWithValue("$type", (int)auditEvent.Type); command.Parameters.AddWithValue("$time", auditEvent.Timestamp.ToString("O"));
        command.Parameters.AddWithValue("$mission", (object?)auditEvent.MissionId ?? DBNull.Value); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(auditEvent, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
    public async IAsyncEnumerable<SecurityAuditEvent> QueryAsync(int limit = 200, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM security_audit ORDER BY sequence DESC LIMIT $limit"; command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 10_000));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false); while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { var item = JsonSerializer.Deserialize<SecurityAuditEvent>(reader.GetString(0), JsonOptions); if (item is not null) yield return item; }
    }
    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken token) { var connection = new SqliteConnection(_connectionString); await connection.OpenAsync(token).ConfigureAwait(false); return connection; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

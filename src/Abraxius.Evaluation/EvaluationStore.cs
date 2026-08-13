using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Abraxius.Evaluation;

public interface IEvalStore : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask SaveSuiteAsync(EvalSuite suite, CancellationToken cancellationToken = default);
    ValueTask<EvalSuite?> GetSuiteAsync(EvalSuiteId id, string? version = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<EvalSuite>> ListSuitesAsync(CancellationToken cancellationToken = default);
    ValueTask SaveDatasetAsync(EvalDataset dataset, CancellationToken cancellationToken = default);
    ValueTask SaveRunAsync(EvalRun run, CancellationToken cancellationToken = default);
    ValueTask SaveCaseResultAsync(EvalCaseResult result, CancellationToken cancellationToken = default);
    ValueTask<EvalRun?> GetRunAsync(EvalRunId id, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default);
    ValueTask SaveComparisonAsync(EvalComparison comparison, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<EvalRegression>> ListRegressionsAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default);
}

public sealed class InMemoryEvalStore : IEvalStore
{
    private readonly ConcurrentDictionary<(EvalSuiteId, string), EvalSuite> _suites = new();
    private readonly ConcurrentDictionary<(EvalDatasetId, string), EvalDataset> _datasets = new();
    private readonly ConcurrentDictionary<EvalRunId, EvalRun> _runs = new();
    private readonly ConcurrentDictionary<(EvalRunId, EvalExecutionId), EvalCaseResult> _results = new();
    private readonly ConcurrentDictionary<EvalComparisonId, EvalComparison> _comparisons = new();
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask SaveSuiteAsync(EvalSuite suite, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _suites[(suite.Id, suite.Version)] = suite; return ValueTask.CompletedTask; }
    public ValueTask<EvalSuite?> GetSuiteAsync(EvalSuiteId id, string? version = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var suite = version is null ? _suites.Where(item => item.Key.Item1 == id).OrderByDescending(static item => item.Key.Item2, StringComparer.Ordinal).Select(static item => item.Value).FirstOrDefault() : _suites.GetValueOrDefault((id, version));
        return ValueTask.FromResult(suite);
    }
    public ValueTask<IReadOnlyList<EvalSuite>> ListSuitesAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<IReadOnlyList<EvalSuite>>(_suites.Values.OrderBy(static item => item.Name, StringComparer.Ordinal).ToArray()); }
    public ValueTask SaveDatasetAsync(EvalDataset dataset, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _datasets[(dataset.Id, dataset.Version)] = dataset; return ValueTask.CompletedTask; }
    public ValueTask SaveRunAsync(EvalRun run, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _runs[run.Id] = run with { CaseResults = [] }; return ValueTask.CompletedTask; }
    public ValueTask SaveCaseResultAsync(EvalCaseResult result, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _results[(result.RunId, result.ExecutionId)] = result; return ValueTask.CompletedTask; }
    public ValueTask<EvalRun?> GetRunAsync(EvalRunId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_runs.TryGetValue(id, out var run)) return ValueTask.FromResult<EvalRun?>(null);
        var results = _results.Values.Where(item => item.RunId == id).OrderBy(static item => item.CaseId.Value).ThenBy(static item => item.Repeat).ToImmutableArray();
        return ValueTask.FromResult<EvalRun?>(run with { CaseResults = results });
    }
    public ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var summaries = _runs.Values.OrderByDescending(static item => item.StartedAt).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 1000)).Select(run => Summary(run, _results.Values.Where(item => item.RunId == run.Id))).ToArray();
        return ValueTask.FromResult<IReadOnlyList<EvalRunSummary>>(summaries);
    }
    public ValueTask SaveComparisonAsync(EvalComparison comparison, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _comparisons[comparison.Id] = comparison; return ValueTask.CompletedTask; }
    public ValueTask<IReadOnlyList<EvalRegression>> ListRegressionsAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<EvalRegression>>(_comparisons.Values.OrderByDescending(static item => item.CreatedAt).SelectMany(static item => item.Regressions).Skip(Math.Max(0, offset)).Take(Math.Clamp(limit, 1, 1000)).ToArray());
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    internal static EvalRunSummary Summary(EvalRun run, IEnumerable<EvalCaseResult> results)
    {
        var list = results.ToArray();
        return new(run.Id, run.SuiteId, run.SuiteVersion, run.Candidate.Name, run.Status, run.StartedAt, run.CompletedAt,
            list.Count(static item => item.Status == EvalCaseStatus.Passed), list.Count(static item => item.Status == EvalCaseStatus.Failed),
            list.Count(static item => item.Status == EvalCaseStatus.Inconclusive), list.Count(static item => item.Status == EvalCaseStatus.InfrastructureFailure));
    }
}

public sealed class SqliteEvalStore(string path) : IEvalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly string _path = Path.GetFullPath(path);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS eval_schema(version INTEGER NOT NULL);
            INSERT INTO eval_schema(version) SELECT 1 WHERE NOT EXISTS(SELECT 1 FROM eval_schema);
            CREATE TABLE IF NOT EXISTS eval_suites(id TEXT NOT NULL, version TEXT NOT NULL, name TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(id,version));
            CREATE TABLE IF NOT EXISTS eval_datasets(id TEXT NOT NULL, version TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(id,version));
            CREATE TABLE IF NOT EXISTS eval_runs(id TEXT PRIMARY KEY, suite_id TEXT NOT NULL, suite_version TEXT NOT NULL, candidate TEXT NOT NULL, status INTEGER NOT NULL, started TEXT NOT NULL, completed TEXT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_eval_runs_started ON eval_runs(started DESC);
            CREATE TABLE IF NOT EXISTS eval_case_results(run_id TEXT NOT NULL, execution_id TEXT NOT NULL, case_id TEXT NOT NULL, repeat_no INTEGER NOT NULL, status INTEGER NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(run_id,execution_id));
            CREATE INDEX IF NOT EXISTS ix_eval_results_run_case ON eval_case_results(run_id,case_id,repeat_no);
            CREATE TABLE IF NOT EXISTS eval_metric_samples(run_id TEXT NOT NULL, execution_id TEXT NOT NULL, metric_id TEXT NOT NULL, value REAL NOT NULL, unit TEXT NOT NULL, repeat_no INTEGER NOT NULL, seed INTEGER NULL);
            CREATE INDEX IF NOT EXISTS ix_eval_metrics_run_metric ON eval_metric_samples(run_id,metric_id);
            CREATE TABLE IF NOT EXISTS eval_comparisons(id TEXT PRIMARY KEY, created TEXT NOT NULL, payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS eval_regressions(id TEXT PRIMARY KEY, comparison_id TEXT NOT NULL, suite_id TEXT NOT NULL, severity INTEGER NOT NULL, state INTEGER NOT NULL, payload TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_eval_regressions_state ON eval_regressions(state,severity);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveSuiteAsync(EvalSuite suite, CancellationToken cancellationToken = default)
    {
        var normalized = Normalize(suite);
        await UpsertAsync("eval_suites", "id,version,name,payload", "$id,$version,$name,$payload", new() { ["$id"] = suite.Id.Value, ["$version"] = suite.Version, ["$name"] = suite.Name, ["$payload"] = JsonSerializer.Serialize(normalized, JsonOptions) }, "id,version", cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<EvalSuite?> GetSuiteAsync(EvalSuiteId id, string? version = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand();
        command.CommandText = version is null ? "SELECT payload FROM eval_suites WHERE id=$id ORDER BY version DESC LIMIT 1" : "SELECT payload FROM eval_suites WHERE id=$id AND version=$version";
        command.Parameters.AddWithValue("$id", id.Value); if (version is not null) command.Parameters.AddWithValue("$version", version);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string payload ? JsonSerializer.Deserialize<EvalSuite>(payload, JsonOptions) : null;
    }
    public async ValueTask<IReadOnlyList<EvalSuite>> ListSuitesAsync(CancellationToken cancellationToken = default) => await ReadPayloadsAsync<EvalSuite>("SELECT payload FROM eval_suites ORDER BY name,version DESC", cancellationToken).ConfigureAwait(false);
    public async ValueTask SaveDatasetAsync(EvalDataset dataset, CancellationToken cancellationToken = default) => await UpsertAsync("eval_datasets", "id,version,payload", "$id,$version,$payload", new() { ["$id"] = dataset.Id.Value, ["$version"] = dataset.Version, ["$payload"] = JsonSerializer.Serialize(dataset, JsonOptions) }, "id,version", cancellationToken).ConfigureAwait(false);

    public async ValueTask SaveRunAsync(EvalRun run, CancellationToken cancellationToken = default)
    {
        var metadata = run with { CaseResults = [] };
        await UpsertAsync("eval_runs", "id,suite_id,suite_version,candidate,status,started,completed,payload", "$id,$suite,$version,$candidate,$status,$started,$completed,$payload",
            new() { ["$id"] = run.Id.ToString(), ["$suite"] = run.SuiteId.Value, ["$version"] = run.SuiteVersion, ["$candidate"] = run.Candidate.Name, ["$status"] = (int)run.Status, ["$started"] = run.StartedAt.ToString("O"), ["$completed"] = (object?)run.CompletedAt?.ToString("O") ?? DBNull.Value, ["$payload"] = JsonSerializer.Serialize(metadata, JsonOptions) }, "id", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveCaseResultAsync(EvalCaseResult result, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction; command.CommandText = "INSERT INTO eval_case_results(run_id,execution_id,case_id,repeat_no,status,payload) VALUES($run,$execution,$case,$repeat,$status,$payload) ON CONFLICT(run_id,execution_id) DO UPDATE SET status=excluded.status,payload=excluded.payload";
            command.Parameters.AddWithValue("$run", result.RunId.ToString()); command.Parameters.AddWithValue("$execution", result.ExecutionId.ToString()); command.Parameters.AddWithValue("$case", result.CaseId.Value); command.Parameters.AddWithValue("$repeat", result.Repeat); command.Parameters.AddWithValue("$status", (int)result.Status); command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(result, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var delete = connection.CreateCommand()) { delete.Transaction = transaction; delete.CommandText = "DELETE FROM eval_metric_samples WHERE run_id=$run AND execution_id=$execution"; delete.Parameters.AddWithValue("$run", result.RunId.ToString()); delete.Parameters.AddWithValue("$execution", result.ExecutionId.ToString()); await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false); }
        foreach (var sample in result.Metrics)
        {
            await using var metric = connection.CreateCommand(); metric.Transaction = transaction; metric.CommandText = "INSERT INTO eval_metric_samples(run_id,execution_id,metric_id,value,unit,repeat_no,seed) VALUES($run,$execution,$metric,$value,$unit,$repeat,$seed)";
            metric.Parameters.AddWithValue("$run", result.RunId.ToString()); metric.Parameters.AddWithValue("$execution", result.ExecutionId.ToString()); metric.Parameters.AddWithValue("$metric", sample.MetricId.Value); metric.Parameters.AddWithValue("$value", sample.Value); metric.Parameters.AddWithValue("$unit", sample.Unit); metric.Parameters.AddWithValue("$repeat", sample.Repeat); metric.Parameters.AddWithValue("$seed", sample.Seed ?? (object)DBNull.Value); await metric.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EvalRun?> GetRunAsync(EvalRunId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand(); command.CommandText = "SELECT payload FROM eval_runs WHERE id=$id"; command.Parameters.AddWithValue("$id", id.ToString());
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not string payload) return null;
        var run = JsonSerializer.Deserialize<EvalRun>(payload, JsonOptions)!; var results = await ReadPayloadsAsync<EvalCaseResult>("SELECT payload FROM eval_case_results WHERE run_id=$id ORDER BY case_id,repeat_no", cancellationToken, ("$id", id.ToString())).ConfigureAwait(false);
        return run with { CaseResults = results.ToImmutableArray() };
    }

    public async ValueTask<IReadOnlyList<EvalRunSummary>> ListRunsAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM eval_runs ORDER BY started DESC LIMIT $limit OFFSET $offset"; command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000)); command.Parameters.AddWithValue("$offset", Math.Max(0, offset));
        var result = new List<EvalRunSummary>(); await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) { var run = JsonSerializer.Deserialize<EvalRun>(reader.GetString(0), JsonOptions)!; var full = await GetRunAsync(run.Id, cancellationToken).ConfigureAwait(false); result.Add(InMemoryEvalStore.Summary(run, full?.CaseResults ?? [])); }
        return result;
    }

    public async ValueTask SaveComparisonAsync(EvalComparison comparison, CancellationToken cancellationToken = default)
    {
        await UpsertAsync("eval_comparisons", "id,created,payload", "$id,$created,$payload", new() { ["$id"] = comparison.Id.ToString(), ["$created"] = comparison.CreatedAt.ToString("O"), ["$payload"] = JsonSerializer.Serialize(comparison, JsonOptions) }, "id", cancellationToken).ConfigureAwait(false);
        foreach (var regression in comparison.Regressions) await UpsertAsync("eval_regressions", "id,comparison_id,suite_id,severity,state,payload", "$id,$comparison,$suite,$severity,$state,$payload", new() { ["$id"] = regression.Id.ToString(), ["$comparison"] = comparison.Id.ToString(), ["$suite"] = regression.SuiteId.Value, ["$severity"] = (int)regression.Severity, ["$state"] = (int)regression.State, ["$payload"] = JsonSerializer.Serialize(regression, JsonOptions) }, "id", cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<IReadOnlyList<EvalRegression>> ListRegressionsAsync(int offset = 0, int limit = 100, CancellationToken cancellationToken = default) => await ReadPayloadsAsync<EvalRegression>("SELECT payload FROM eval_regressions ORDER BY severity DESC,rowid DESC LIMIT $limit OFFSET $offset", cancellationToken, ("$limit", Math.Clamp(limit, 1, 1000)), ("$offset", Math.Max(0, offset))).ConfigureAwait(false);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken token) { var connection = new SqliteConnection($"Data Source={_path}"); await connection.OpenAsync(token).ConfigureAwait(false); return connection; }
    private async ValueTask UpsertAsync(string table, string columns, string values, Dictionary<string, object> parameters, string conflict, CancellationToken token)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false); await using var command = connection.CreateCommand(); command.CommandText = $"INSERT INTO {table}({columns}) VALUES({values}) ON CONFLICT({conflict}) DO UPDATE SET payload=excluded.payload"; foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Key, pair.Value); await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
    private async ValueTask<IReadOnlyList<T>> ReadPayloadsAsync<T>(string sql, CancellationToken token, params (string, object)[] parameters)
    {
        await using var connection = await OpenAsync(token).ConfigureAwait(false); await using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var pair in parameters) command.Parameters.AddWithValue(pair.Item1, pair.Item2); var result = new List<T>(); await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false); while (await reader.ReadAsync(token).ConfigureAwait(false)) if (JsonSerializer.Deserialize<T>(reader.GetString(0), JsonOptions) is { } item) result.Add(item); return result;
    }
    private static EvalSuite Normalize(EvalSuite suite) => suite with
    {
        Cases = suite.Cases.Select(item => item with
        {
            Input = item.Input with { ArtifactRevisionIds = item.Input.ArtifactRevisionIds.IsDefault ? [] : item.Input.ArtifactRevisionIds, DatasetRefs = item.Input.DatasetRefs.IsDefault ? [] : item.Input.DatasetRefs },
            Environment = item.Environment with { Platforms = item.Environment.Platforms.IsDefault ? [] : item.Environment.Platforms },
            ExpectedOutcome = item.ExpectedOutcome with { Invariants = item.ExpectedOutcome.Invariants.IsDefault ? [] : item.ExpectedOutcome.Invariants, RequiredEvidence = item.ExpectedOutcome.RequiredEvidence.IsDefault ? [] : item.ExpectedOutcome.RequiredEvidence, RequiredArtifacts = item.ExpectedOutcome.RequiredArtifacts.IsDefault ? [] : item.ExpectedOutcome.RequiredArtifacts },
            VerificationPlan = item.VerificationPlan with { Checks = item.VerificationPlan.Checks.IsDefault ? [] : item.VerificationPlan.Checks },
            Tags = item.Tags.IsDefault ? [] : item.Tags,
            Seeds = item.Seeds.IsDefault ? [] : item.Seeds
        }).ToImmutableArray(),
        Metrics = suite.Metrics.IsDefault ? [] : suite.Metrics,
        Gates = suite.Gates.IsDefault ? [] : suite.Gates
    };
}

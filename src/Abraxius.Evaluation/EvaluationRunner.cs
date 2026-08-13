using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Abraxius.Artifacts;
using Abraxius.Core;
using Abraxius.Platform;
using Abraxius.Scheduler;
using Abraxius.Security;

namespace Abraxius.Evaluation;

public sealed record EvalCaseContext(EvalRunId RunId, EvalCandidate Candidate, EvalEnvironmentSnapshot Environment, EvalCase Case, int Repeat, int? Seed, string Workspace, CancellationToken CancellationToken);

public interface IEvalCaseExecutor
{
    ValueTask<EvalCaseExecution> ExecuteAsync(EvalCaseContext context);
}

public interface IEvalVerifier
{
    EvalVerificationResult Verify(EvalCase evalCase, EvalCaseExecution execution);
}

public interface IEvalRunner
{
    ValueTask<EvalRun> RunAsync(EvalRunRequest request, CancellationToken cancellationToken = default);
}

public sealed class DeterministicEvalVerifier : IEvalVerifier
{
    public EvalVerificationResult Verify(EvalCase evalCase, EvalCaseExecution execution)
    {
        var passed = ImmutableArray.CreateBuilder<string>(); var failed = ImmutableArray.CreateBuilder<string>();
        foreach (var check in evalCase.VerificationPlan.Checks)
        {
            var observed = check.Property is null ? execution.Observed : execution.Observations.GetValueOrDefault(check.Property);
            var ok = check.Kind switch
            {
                EvalVerificationKind.ExactResult => string.Equals(observed, check.Expected, StringComparison.Ordinal),
                EvalVerificationKind.Invariant or EvalVerificationKind.SchemaValid or EvalVerificationKind.TestsPass or EvalVerificationKind.AxlParses or EvalVerificationKind.SecurityDenied or EvalVerificationKind.ArtifactHash => string.Equals(observed, check.Expected, StringComparison.OrdinalIgnoreCase),
                EvalVerificationKind.RequiredEvidence => execution.Evidence.Length >= (check.Threshold ?? 1),
                EvalVerificationKind.RequiredArtifact => execution.Artifacts.Length >= (check.Threshold ?? 1),
                EvalVerificationKind.LatencyThreshold => execution.Metrics.Where(item => item.MetricId == EvalMetricIds.LatencyMilliseconds).Any(item => item.Value <= (check.Threshold ?? double.MaxValue)),
                EvalVerificationKind.Custom => string.Equals(observed, check.Expected, StringComparison.Ordinal),
                _ => false
            };
            (ok ? passed : failed).Add($"{check.Kind}: expected {check.Expected}, observed {observed ?? "<missing>"}");
        }
        var verified = evalCase.VerificationPlan.RequireAll ? failed.Count == 0 : passed.Count > 0;
        return new(verified, passed.ToImmutable(), failed.ToImmutable(), execution.Evidence.IsDefault ? [] : execution.Evidence);
    }
}

public sealed class EvalRunner(
    DagScheduler scheduler,
    IEvidenceStore evidenceStore,
    IEvalStore store,
    IEvalCaseExecutor executor,
    IEvalVerifier verifier,
    IArtifactService? artifacts = null) : IEvalRunner
{
    public async ValueTask<EvalRun> RunAsync(EvalRunRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var suite = request.Suite;
        if (suite.Cases.IsDefaultOrEmpty) throw new InvalidOperationException("An evaluation suite must contain at least one case.");
        var started = DateTimeOffset.UtcNow; var runId = request.ResumeRunId ?? EvalRunId.New();
        var existing = request.ResumeRunId is { } resume ? await store.GetRunAsync(resume, cancellationToken).ConfigureAwait(false) : null;
        if (existing is not null && (existing.SuiteId != suite.Id || existing.SuiteVersion != suite.Version)) throw new InvalidOperationException("A partial run can only resume the same suite version.");
        var completed = existing?.CaseResults.ToList() ?? [];
        var run = new EvalRun(runId, suite.Id, suite.Version, request.Candidate, request.BaselineId, request.Environment, EvalRunStatus.Running, existing?.StartedAt ?? started, null, [], [], RewardEligible: false);
        await store.SaveSuiteAsync(suite, cancellationToken).ConfigureAwait(false); await store.SaveRunAsync(run, cancellationToken).ConfigureAwait(false);

        var preset = request.Preset ?? suite.ExecutionPolicy.Preset;
        var caseLimit = suite.ExecutionPolicy.MaxCases ?? preset switch { EvalSamplingPreset.Smoke => 50, EvalSamplingPreset.Standard => 1000, _ => int.MaxValue };
        var work = suite.Cases.Take(caseLimit).SelectMany(@case => Enumerable.Range(0, @case.EffectiveRepeatCount).Select(repeat => (@case, repeat, seed: @case.EffectiveSeeds[repeat % @case.EffectiveSeeds.Length])))
            .Where(item => !completed.Any(done => done.CaseId == item.@case.Id && done.Repeat == item.repeat && done.Seed == item.seed)).ToArray();
        var concurrency = Math.Clamp(suite.ExecutionPolicy.MaxConcurrency, 1, 256);
        var cancelled = false;

        foreach (var batch in work.Chunk(concurrency))
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            var executionId = ExecutionId.New(); var nodes = new List<WorkNode>(batch.Length); var map = new Dictionary<TaskId, (EvalCase Case, int Repeat, int Seed)>();
            for (var index = 0; index < batch.Length; index++)
            {
                var item = batch[index]; var taskId = TaskId.New(); map[taskId] = (item.@case, item.repeat, item.seed);
                nodes.Add(new WorkNode(taskId, executionId, $"Eval {item.@case.Id} repeat {item.repeat}", ExecutorKind.Cpu,
                    context => ExecuteOneAsync(runId, request.Candidate, request.Environment, item.@case, item.repeat, item.seed, context.CancellationToken),
                    timeout: item.@case.Timeout, retryPolicy: new RetryPolicy(1), creationOrder: index));
            }
            ExecutionResult result;
            try { result = await scheduler.ExecuteAsync(new ExecutionPlan(executionId, CorrelationId.New(), nodes), evidenceStore, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled = true; break; }
            foreach (var pair in map)
            {
                if (result.Results.TryGetValue(pair.Key, out var workResult) && workResult.Value is { } value)
                {
                    var item = value.Deserialize<EvalCaseResult>() ?? throw new InvalidDataException("Evaluation result payload was invalid.");
                    completed.Add(item);
                }
                else if (result.Tasks.TryGetValue(pair.Key, out var state) && state.State == WorkState.Cancelled)
                {
                    cancelled = true;
                }
                else if (!completed.Any(item => item.CaseId == pair.Value.Case.Id && item.Repeat == pair.Value.Repeat && item.Seed == pair.Value.Seed))
                {
                    var error = result.Errors.GetValueOrDefault(pair.Key);
                    var infrastructure = FailureResult(runId, pair.Value.Case.Id, pair.Value.Repeat, pair.Value.Seed, error?.Message ?? "Evaluation infrastructure failed.");
                    await store.SaveCaseResultAsync(infrastructure, CancellationToken.None).ConfigureAwait(false); completed.Add(infrastructure);
                }
            }
        }

        var metrics = EvalMetricMath.AggregateSuite(suite, completed);
        var status = cancelled ? (completed.Count == 0 ? EvalRunStatus.Cancelled : EvalRunStatus.Partial) : DetermineStatus(completed);
        run = run with { Status = status, CompletedAt = DateTimeOffset.UtcNow, CaseResults = completed.OrderBy(static item => item.CaseId.Value).ThenBy(static item => item.Repeat).ToImmutableArray(), Metrics = metrics };
        if (artifacts is not null)
        {
            var revision = await CreateReportArtifactAsync(run, cancellationToken).ConfigureAwait(false);
            run = run with { ReportArtifactRevision = revision };
        }
        await store.SaveRunAsync(run, CancellationToken.None).ConfigureAwait(false);
        return run;
    }

    private async ValueTask<WorkResult> ExecuteOneAsync(EvalRunId runId, EvalCandidate candidate, EvalEnvironmentSnapshot environment, EvalCase @case, int repeat, int seed, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        await using var isolation = await EvalIsolationLease.CreateAsync(@case.Environment.Isolation, token).ConfigureAwait(false);
        EvalCaseExecution execution;
        try { execution = await executor.ExecuteAsync(new(runId, candidate, environment, @case, repeat, seed, isolation.Workspace, token)).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { execution = new(EvalCaseStatus.InfrastructureFailure, null, ImmutableDictionary<string, string>.Empty, [], Errors: [exception.GetType().Name + ": " + exception.Message]); }
        var elapsed = Stopwatch.GetElapsedTime(started);
        var samples = (execution.Metrics.IsDefault ? ImmutableArray<EvalMetricSample>.Empty : execution.Metrics).Add(new(EvalMetricIds.LatencyMilliseconds, elapsed.TotalMilliseconds, "ms", repeat, seed, DateTimeOffset.UtcNow));
        execution = execution with { Metrics = samples };
        var verification = verifier.Verify(@case, execution);
        var status = execution.Status == EvalCaseStatus.InfrastructureFailure ? EvalCaseStatus.InfrastructureFailure : execution.Status == EvalCaseStatus.Cancelled ? EvalCaseStatus.Cancelled : verification.Verified ? EvalCaseStatus.Passed : EvalCaseStatus.Failed;
        var result = new EvalCaseResult(EvalExecutionId.New(), runId, @case.Id, repeat, seed, status, verification, samples,
            execution.Artifacts.IsDefault ? [] : execution.Artifacts, execution.TrajectoryId, execution.Errors.IsDefault ? [] : execution.Errors,
            elapsed, execution.Cost, execution.CostPrecision, execution.ProviderCalls.IsDefault ? [] : execution.ProviderCalls,
            execution.SpecialistContributions.IsDefault ? [] : execution.SpecialistContributions, DateTimeOffset.UtcNow);
        await store.SaveCaseResultAsync(result, CancellationToken.None).ConfigureAwait(false);
        return new WorkResult(ResultId.New(), $"{@case.Id}: {status}", result.Verification.EvidenceRefs, JsonSerializer.SerializeToElement(result));
    }

    private async ValueTask<ArtifactRevisionId> CreateReportArtifactAsync(EvalRun run, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(run);
        await using var stream = new MemoryStream(bytes, writable: false);
        var producer = new ArtifactProducer(new PrincipalId("system:evaluation"), ArtifactProducerKind.System, "Evaluation Lab");
        var metadata = ImmutableDictionary<string, string>.Empty.Add("suite", run.SuiteId.Value).Add("suiteVersion", run.SuiteVersion).Add("candidate", run.Candidate.Reference).Add("environment", run.Environment.Fingerprint).Add("rewardEligibility", "ineligible");
        var artifact = await artifacts!.CreateAsync(new(new ArtifactKind("evaluation-report"), $"{run.SuiteId} · {run.Candidate.Name}", producer,
            new ArtifactProvenance(TrajectoryId: $"eval:{run.Id}"), new ArtifactClassification(DataClassification.Internal), "application/json", $"eval-{run.Id}.json", ArtifactState.Verified, ArtifactRetention.Persistent, TypedMetadata: metadata), stream, token).ConfigureAwait(false);
        return artifact.CurrentRevision.Id;
    }

    private static EvalRunStatus DetermineStatus(List<EvalCaseResult> results)
    {
        if (results.Count == 0) return EvalRunStatus.Inconclusive;
        if (results.All(static item => item.Status == EvalCaseStatus.InfrastructureFailure)) return EvalRunStatus.InfrastructureFailure;
        if (results.Any(static item => item.Status == EvalCaseStatus.Failed)) return EvalRunStatus.Failed;
        if (results.Any(static item => item.Status is EvalCaseStatus.Inconclusive or EvalCaseStatus.InfrastructureFailure)) return EvalRunStatus.Inconclusive;
        return EvalRunStatus.Passed;
    }
    private static EvalCaseResult FailureResult(EvalRunId run, EvalCaseId @case, int repeat, int seed, string error) => new(EvalExecutionId.New(), run, @case, repeat, seed, EvalCaseStatus.InfrastructureFailure, new(false, [], [error], []), [], [], null, [error], TimeSpan.Zero, null, EvalCostPrecision.Unknown, [], [], DateTimeOffset.UtcNow);
}

public sealed class EvalIsolationLease : IAsyncDisposable
{
    private readonly bool _owns;
    private EvalIsolationLease(string workspace, bool owns) { Workspace = workspace; _owns = owns; }
    public string Workspace { get; }
    public static ValueTask<EvalIsolationLease> CreateAsync(EvalIsolationKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (kind == EvalIsolationKind.InMemory) return ValueTask.FromResult(new EvalIsolationLease(string.Empty, false));
        if (kind is EvalIsolationKind.Sandbox or EvalIsolationKind.Remote) throw new InvalidOperationException($"Required evaluation isolation '{kind}' is unavailable on this runner.");
        var path = Path.Combine(Path.GetTempPath(), $"abraxius-eval-{Guid.NewGuid():N}"); Directory.CreateDirectory(path); return ValueTask.FromResult(new EvalIsolationLease(path, true));
    }
    public ValueTask DisposeAsync() { if (_owns && Directory.Exists(Workspace)) Directory.Delete(Workspace, recursive: true); return ValueTask.CompletedTask; }
}

public static class EvalEnvironmentCapture
{
    public static EvalEnvironmentSnapshot Capture(string abraxiusVersion, string gitCommit, string avaloniaVersion, string axlVersion, string securityPolicyVersion, ImmutableDictionary<string, string>? models = null, ImmutableDictionary<string, string>? skills = null, int? seed = null, string executionMode = "Release") =>
        new(abraxiusVersion, gitCommit, System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription, avaloniaVersion, $"{Environment.ProcessorCount} logical processors", "unknown", GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            models ?? ImmutableDictionary<string, string>.Empty, skills ?? ImmutableDictionary<string, string>.Empty, axlVersion, "runtime-default", "runtime-default", securityPolicyVersion, seed, executionMode, DateTimeOffset.UtcNow);
}

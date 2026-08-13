using System.Collections.Immutable;
using Abraxius.Artifacts;
using Abraxius.Core;
using Abraxius.Evaluation;
using Abraxius.Memory;
using Abraxius.Platform;
using Abraxius.Protocol;
using Abraxius.Scheduler;
using Abraxius.Security;
using Xunit;

namespace Abraxius.Evaluation.Tests;

public sealed class EvaluationTests
{
    [Fact]
    public void RetrievalMetricsUseKnownGroundTruth()
    {
        string[] relevant = ["a", "c"]; string[] ranked = ["x", "a", "c"];
        Assert.Equal(1, EvalMetricMath.RecallAtK(relevant, ranked, 3));
        Assert.Equal(2d / 3, EvalMetricMath.PrecisionAtK(relevant, ranked, 3), 8);
        Assert.Equal(.5, EvalMetricMath.MeanReciprocalRank(relevant, ranked));
    }

    [Fact]
    public void MissingMetricRemainsUnknownInsteadOfZero()
    {
        var value = EvalMetricMath.Aggregate(new(new("missing"), "Missing", "count", EvalMetricDirection.HigherIsBetter, EvalMetricAggregation.Mean), []);
        Assert.Null(value.Value); Assert.Equal(EvalMetricAvailability.Unknown, value.Availability);
    }

    [Fact]
    public async Task BasicSuiteRecordsVerifiedPassAndFail()
    {
        await using var fixture = await Fixture.CreateAsync();
        var suite = Suite([Case("pass", "deterministic", "pass"), Case("fail", "deterministic", "expected", Props(("observed", "wrong")))]);
        var run = await fixture.Runner.RunAsync(new(suite, Candidate("candidate"), Env()));
        Assert.Single(run.CaseResults, static item => item.Status == EvalCaseStatus.Passed);
        Assert.Single(run.CaseResults, static item => item.Status == EvalCaseStatus.Failed);
        Assert.Equal(EvalRunStatus.Failed, run.Status); Assert.False(run.RewardEligible);
    }

    [Fact]
    public async Task InfrastructureFailureIsNotCountedAsProductFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        var suite = Suite([Case("provider", "deterministic", "pass", Props(("infrastructureFailure", "true")))]);
        var run = await fixture.Runner.RunAsync(new(suite, Candidate("candidate"), Env()));
        Assert.Equal(EvalCaseStatus.InfrastructureFailure, Assert.Single(run.CaseResults).Status);
        Assert.Equal(EvalRunStatus.InfrastructureFailure, run.Status);
    }

    [Fact]
    public async Task RunnerUsesBoundedPhaseFourBatches()
    {
        var tracking = new TrackingExecutor(); await using var fixture = await Fixture.CreateAsync(tracking, cpuConcurrency: 8);
        var cases = Enumerable.Range(0, 8).Select(index => Case($"c{index}", "tracked", "pass")).ToImmutableArray();
        var suite = Suite(cases) with { ExecutionPolicy = new(2) };
        var run = await fixture.Runner.RunAsync(new(suite, Candidate("candidate"), Env()));
        Assert.Equal(8, run.CaseResults.Length); Assert.InRange(tracking.MaximumConcurrency, 1, 2);
    }

    [Fact]
    public async Task ResumeDoesNotDuplicateCompletedCase()
    {
        var tracking = new TrackingExecutor(); await using var fixture = await Fixture.CreateAsync(tracking);
        var suite = Suite([Case("one", "tracked", "pass"), Case("two", "tracked", "pass")]); var runId = EvalRunId.New();
        var partial = new EvalRun(runId, suite.Id, suite.Version, Candidate("candidate"), null, Env(), EvalRunStatus.Partial, DateTimeOffset.UtcNow, null, [], [], RewardEligible: false);
        await fixture.Store.SaveRunAsync(partial); var done = Result(runId, new("one")); await fixture.Store.SaveCaseResultAsync(done);
        var resumed = await fixture.Runner.RunAsync(new(suite, partial.Candidate, partial.Environment, ResumeRunId: runId));
        Assert.Equal(2, resumed.CaseResults.Length); Assert.Equal(1, tracking.Calls);
    }

    [Fact]
    public void ComparisonDetectsRegressionAndFailsGate()
    {
        var suite = Suite([Case("one", "deterministic", "pass")]) with { Gates = [new(new("verified"), EvalMetricIds.VerifiedSuccessRate, EvalGateMode.RelativeMaximumRegression, .05, EvalGateSeverity.ReleaseBlocking)] };
        var baseline = Run(suite, .95); var candidate = Run(suite, .80);
        var comparison = EvalComparisonEngine.Compare(suite, baseline, candidate);
        Assert.True(comparison.ReleaseBlocked); Assert.Single(comparison.Regressions); Assert.Equal(EvalChangeClassification.Regression, comparison.Deltas.Single(item => item.MetricId == EvalMetricIds.VerifiedSuccessRate).Classification);
    }

    [Fact]
    public void LowerIsBetterRelativeRegressionUsesMetricDirection()
    {
        var metric = new EvalMetricDefinition(EvalMetricIds.LatencyMilliseconds, "Latency", "ms", EvalMetricDirection.LowerIsBetter, EvalMetricAggregation.Median);
        var suite = Suite([Case("one", "deterministic", "pass")]) with
        {
            Metrics = [metric],
            Gates = [new(new("latency"), metric.Id, EvalGateMode.RelativeMaximumRegression, .10, EvalGateSeverity.ReleaseBlocking)]
        };
        var baseline = Run(suite, 100, metric: metric.Id);
        var candidate = Run(suite, 120, metric: metric.Id);

        var comparison = EvalComparisonEngine.Compare(suite, baseline, candidate);

        Assert.Equal(EvalGateStatus.Failed, Assert.Single(comparison.Gates).Status);
    }

    [Fact]
    public void DifferentWorkloadsCannotBeCompared()
    {
        var suite = Suite([Case("one", "deterministic", "pass")]); var baseline = Run(suite, 1);
        var candidate = Run(suite, 1) with { CaseResults = [Result(EvalRunId.New(), new("different"))] };
        Assert.Throws<InvalidOperationException>(() => EvalComparisonEngine.Compare(suite, baseline, candidate));
    }

    [Fact]
    public void EnvironmentMismatchMakesPerformanceMetricInconclusive()
    {
        var latency = new EvalMetricDefinition(EvalMetricIds.LatencyMilliseconds, "Latency", "ms", EvalMetricDirection.LowerIsBetter, EvalMetricAggregation.Median);
        var suite = Suite([Case("one", "deterministic", "pass")]) with { Metrics = [latency] };
        var baseline = Run(suite, 10, metric: EvalMetricIds.LatencyMilliseconds); var candidate = Run(suite, 5, metric: EvalMetricIds.LatencyMilliseconds) with { Environment = Env() with { Cpu = "other" } };
        var comparison = EvalComparisonEngine.Compare(suite, baseline, candidate);
        Assert.False(comparison.EnvironmentCompatible); Assert.Equal(EvalChangeClassification.Inconclusive, Assert.Single(comparison.Deltas).Classification);
    }

    [Fact]
    public void SmallSampleGateIsInconclusive()
    {
        var suite = Suite([Case("one", "deterministic", "pass")]) with { Gates = [new(new("sample"), EvalMetricIds.VerifiedSuccessRate, EvalGateMode.AbsoluteMinimum, .9, EvalGateSeverity.Required, 10)] };
        var comparison = EvalComparisonEngine.Compare(suite, Run(suite, 1), Run(suite, 1));
        Assert.Equal(EvalGateStatus.Inconclusive, Assert.Single(comparison.Gates).Status);
    }

    [Fact]
    public void CriticalSecurityGateCannotBeCasuallyOverridden()
    {
        var result = new EvalGateResult(new("security"), EvalGateStatus.Failed, EvalGateSeverity.SecurityCritical, EvalMetricIds.SecurityCriticalEscapes, 1, 0, 0, 1, "escape");
        Assert.Throws<InvalidOperationException>(() => EvalComparisonEngine.Override(result, "user", "ship anyway"));
        Assert.Equal(EvalGateStatus.Passed, EvalComparisonEngine.Override(result, "security-admin", "fixture exception", securityOverrideAllowed: true).Status);
    }

    [Fact]
    public async Task LocalOnlyCloudEgressIsDeniedByRealPolicy()
    {
        await using var fixture = await Fixture.CreateAsync(); var suite = BuiltInEvalSuites.Find("security.adversarial")!;
        var run = await fixture.Runner.RunAsync(new(suite, Candidate("candidate"), Env()));
        var item = run.CaseResults.Single(result => result.CaseId.Value == "localonly-egress");
        Assert.Equal(EvalCaseStatus.Passed, item.Status); Assert.True(item.Verification.Verified);
        Assert.Equal(0, run.Metrics.Single(metric => metric.MetricId == EvalMetricIds.SecurityCriticalEscapes).Value);
    }

    [Fact]
    public async Task ArtifactSuiteProvesVerificationIsRevisionPinned()
    {
        await using var fixture = await Fixture.CreateAsync(); var suite = BuiltInEvalSuites.Find("artifacts.integrity")!;
        var run = await fixture.Runner.RunAsync(new(suite, Candidate("candidate"), Env()));
        Assert.Equal(EvalCaseStatus.Passed, Assert.Single(run.CaseResults).Status); Assert.NotEmpty(run.CaseResults[0].Artifacts);
    }

    [Fact]
    public async Task EvalReportIsAnImmutableArtifactAndNeverRewardEligible()
    {
        await using var fixture = await Fixture.CreateAsync(createReports: true); var run = await fixture.Runner.RunAsync(new(Suite([Case("one", "deterministic", "pass")]), Candidate("candidate"), Env()));
        Assert.NotNull(run.ReportArtifactRevision); Assert.False(run.RewardEligible);
        Assert.NotNull(await fixture.Artifacts.GetRevisionAsync(run.ReportArtifactRevision!.Value));
    }

    [Fact]
    public async Task SqliteStoreRoundTripsNormalizedCaseResults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"abraxius-eval-test-{Guid.NewGuid():N}"); Directory.CreateDirectory(root);
        try
        {
            await using var store = new SqliteEvalStore(Path.Combine(root, "eval.db")); await store.InitializeAsync(); var suite = Suite([Case("one", "deterministic", "pass")]); var run = Run(suite, 1); await store.SaveSuiteAsync(suite); await store.SaveRunAsync(run); foreach (var result in run.CaseResults) await store.SaveCaseResultAsync(result);
            var loaded = await store.GetRunAsync(run.Id); Assert.NotNull(loaded); Assert.Single(loaded!.CaseResults); Assert.Single(await store.ListRunsAsync());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RequiredBuiltInSuitesArePresent()
    {
        var ids = BuiltInEvalSuites.CreateAll().Select(static item => item.Id.Value).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(ids, new HashSet<string>(["core.mission-smoke", "axl.core", "memory.retrieval", "scheduler.parallelism", "security.adversarial", "skills.effectiveness", "artifacts.integrity"]));
    }

    [Fact]
    public void RegressionConvertsToStructuredMission()
    {
        var suite = Suite([Case("one", "deterministic", "pass")]); var baseline = Run(suite, 1); var candidate = Run(suite, .5); var regression = new EvalRegression(EvalRegressionId.New(), EvalComparisonId.New(), suite.Id, new("one"), EvalMetricIds.VerifiedSuccessRate, 1, .5, -.5, EvalRegressionSeverity.Major, "evidence", []);
        var mission = EvalRegressionMissionFactory.Create(regression, baseline, candidate);
        Assert.Equal(regression.Id, mission.RegressionId); Assert.Equal(candidate.Id.ToString(), mission.References["candidateRunId"]);
    }

    private static EvalSuite Suite(ImmutableArray<EvalCase> cases) => new(new("test.suite"), "1.0.0", "Test", "Test suite", EvalDomain.Reliability, cases,
        [new(EvalMetricIds.VerifiedSuccessRate, "Verified", "ratio", EvalMetricDirection.HigherIsBetter, EvalMetricAggregation.Mean), new(EvalMetricIds.LatencyMilliseconds, "Latency", "ms", EvalMetricDirection.LowerIsBetter, EvalMetricAggregation.Median)], [], new(4));
    private static EvalCase Case(string id, string operation, string expected, ImmutableDictionary<string, string>? properties = null) => new(new(id), id, new(operation, Properties: properties), new(), new(ExactResult: expected), new([new(EvalVerificationKind.ExactResult, expected)]), [], 1, EvalDeterminism.Deterministic);
    private static ImmutableDictionary<string, string> Props(params (string Key, string Value)[] values) => values.ToImmutableDictionary(static item => item.Key, static item => item.Value);
    private static EvalCandidate Candidate(string name) => new(EvalCandidateId.New(), name, "commit", name);
    private static EvalEnvironmentSnapshot Env() => EvalEnvironmentCapture.Capture("19", "commit", "11", "1", "1");
    private static EvalCaseResult Result(EvalRunId run, EvalCaseId id) => new(EvalExecutionId.New(), run, id, 0, 0, EvalCaseStatus.Passed, new(true, ["passed"], [], []), [], [], null, [], TimeSpan.Zero, null, EvalCostPrecision.Unknown, [], [], DateTimeOffset.UtcNow);
    private static EvalRun Run(EvalSuite suite, double value, EvalMetricId? metric = null)
    {
        var id = EvalRunId.New(); var caseResults = suite.Cases.Select(item => Result(id, item.Id)).ToImmutableArray(); var metricId = metric ?? EvalMetricIds.VerifiedSuccessRate;
        return new(id, suite.Id, suite.Version, Candidate("candidate"), null, Env(), EvalRunStatus.Passed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, caseResults, [new(metricId, value, metricId == EvalMetricIds.LatencyMilliseconds ? "ms" : "ratio", EvalMetricAggregation.Mean, caseResults.Length)], RewardEligible: false);
    }

    private sealed class TrackingExecutor : IEvalCaseExecutor
    {
        private int _active; private int _maximum; private int _calls;
        public int MaximumConcurrency => Volatile.Read(ref _maximum); public int Calls => Volatile.Read(ref _calls);
        public async ValueTask<EvalCaseExecution> ExecuteAsync(EvalCaseContext context)
        {
            Interlocked.Increment(ref _calls); var active = Interlocked.Increment(ref _active); int current;
            do { current = _maximum; if (current >= active) break; } while (Interlocked.CompareExchange(ref _maximum, active, current) != current);
            try { await Task.Delay(20, context.CancellationToken); return new(EvalCaseStatus.Passed, "pass", ImmutableDictionary<string, string>.Empty, []); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DagScheduler _scheduler; private readonly InMemoryEvalStore _store; private readonly InMemorySecurityAuditStore _audit;
        private Fixture(DagScheduler scheduler, InMemoryEvalStore store, InMemorySecurityAuditStore audit, ArtifactService artifacts, EvalRunner runner) { _scheduler = scheduler; _store = store; _audit = audit; Artifacts = artifacts; Runner = runner; }
        public EvalRunner Runner { get; } public InMemoryEvalStore Store => _store; public ArtifactService Artifacts { get; }
        public static async ValueTask<Fixture> CreateAsync(IEvalCaseExecutor? custom = null, int cpuConcurrency = 4, bool createReports = false)
        {
            var store = new InMemoryEvalStore(); await store.InitializeAsync(); var audit = new InMemorySecurityAuditStore(); await audit.InitializeAsync();
            var kernel = new SecurityKernel(new DeterministicPolicyEngine(), new DeterministicRiskClassifier(), new InMemoryAuthorizationGrantStore(), audit, new ResourceCanonicalizer());
            var artifactStore = new InMemoryArtifactStore(); var content = new InMemoryArtifactContentStore(); var artifacts = new ArtifactService(artifactStore, content); await artifacts.InitializeAsync();
            var executor = custom ?? new BuiltInEvalCaseExecutor(new ModelEgressPolicy(), kernel, artifacts);
            var limits = SchedulerOptions.CreateDefaults().ToDictionary(); limits[ExecutorKind.Cpu] = cpuConcurrency;
            var scheduler = new DagScheduler(new SchedulerOptions { ConcurrencyLimits = limits });
            var runner = new EvalRunner(scheduler, new InMemoryEvidenceStore(), store, executor, new DeterministicEvalVerifier(), createReports ? artifacts : null);
            return new(scheduler, store, audit, artifacts, runner);
        }
        public async ValueTask DisposeAsync() { await _scheduler.DisposeAsync(); await _store.DisposeAsync(); await _audit.DisposeAsync(); }
    }
}

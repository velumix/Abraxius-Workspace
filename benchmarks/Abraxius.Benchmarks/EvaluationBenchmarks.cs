using System.Collections.Immutable;
using Abraxius.Evaluation;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class EvaluationBenchmarks : IAsyncDisposable
{
    private EvalSuite _suite = null!; private EvalRun _baseline = null!; private EvalRun _candidate = null!; private EvalMetricDefinition _metric = null!; private EvalMetricSample[] _samples = null!; private InMemoryEvalStore _store = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _metric = new(EvalMetricIds.LatencyMilliseconds, "Latency", "ms", EvalMetricDirection.LowerIsBetter, EvalMetricAggregation.P95);
        var cases = Enumerable.Range(0, 10_000).Select(index => Case(index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToImmutableArray();
        _suite = new(new("benchmark.evaluation"), "1", "Evaluation benchmark", "Comparison benchmark", EvalDomain.Performance, cases,
            [_metric], [new(new("latency"), EvalMetricIds.LatencyMilliseconds, EvalGateMode.RelativeMaximumRegression, .1, EvalGateSeverity.ReleaseBlocking, 100)], new(8));
        _baseline = Run(cases, 10); _candidate = Run(cases, 11.5);
        _samples = Enumerable.Range(0, 100_000).Select(index => new EvalMetricSample(EvalMetricIds.LatencyMilliseconds, index % 1000, "ms", index)).ToArray();
        _store = new InMemoryEvalStore(); await _store.InitializeAsync();
        for (var index = 0; index < 10_000; index++) await _store.SaveRunAsync(Run([Case($"store-{index}")], index));
    }

    [Benchmark]
    public EvalComparison CompareTenThousandCases() => EvalComparisonEngine.Compare(_suite, _baseline, _candidate);

    [Benchmark]
    public EvalMetricValue AggregateOneHundredThousandSamples() => EvalMetricMath.Aggregate(_metric, _samples);

    [Benchmark]
    public ValueTask<IReadOnlyList<EvalRunSummary>> QueryTenThousandRunStore() => _store.ListRunsAsync(limit: 100);

    [GlobalCleanup]
    public async Task Cleanup() => await DisposeAsync();

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private EvalRun Run(ImmutableArray<EvalCase> cases, double latency)
    {
        var id = EvalRunId.New(); var results = cases.Select(item => new EvalCaseResult(EvalExecutionId.New(), id, item.Id, 0, 0, EvalCaseStatus.Passed,
            new(true, ["passed"], [], []), [new(EvalMetricIds.LatencyMilliseconds, latency, "ms", 0)], [], null, [], TimeSpan.FromMilliseconds(latency), null, EvalCostPrecision.Unknown, [], [], DateTimeOffset.UtcNow)).ToImmutableArray();
        return new(id, _suite?.Id ?? new EvalSuiteId("benchmark.evaluation"), "1", new(EvalCandidateId.New(), "candidate", "benchmark", "candidate"), null,
            EvalEnvironmentCapture.Capture("19", "benchmark", "headless", "1", "1"), EvalRunStatus.Passed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, results,
            [new(EvalMetricIds.LatencyMilliseconds, latency, "ms", EvalMetricAggregation.P95, results.Length)], RewardEligible: false);
    }

    private static EvalCase Case(string id) => new(new(id), id, new("deterministic"), new(), new(ExactResult: "pass"), new([new(EvalVerificationKind.ExactResult, "pass")]), [], 1, EvalDeterminism.Deterministic);
}

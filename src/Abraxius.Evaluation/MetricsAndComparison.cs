namespace Abraxius.Evaluation;

public static class EvalMetricMath
{
    public static EvalMetricValue Aggregate(EvalMetricDefinition definition, IEnumerable<EvalMetricSample> samples)
    {
        var values = samples.Where(item => item.MetricId == definition.Id).Select(static item => item.Value).Order().ToArray();
        if (values.Length == 0) return EvalMetricValue.Unknown(definition.Id, definition.Unit, definition.Aggregation);
        var value = definition.Aggregation switch
        {
            EvalMetricAggregation.Count => values.Length,
            EvalMetricAggregation.Rate or EvalMetricAggregation.Mean => values.Average(),
            EvalMetricAggregation.Sum => values.Sum(),
            EvalMetricAggregation.Median => Percentile(values, .5),
            EvalMetricAggregation.P90 => Percentile(values, .90),
            EvalMetricAggregation.P95 => Percentile(values, .95),
            EvalMetricAggregation.P99 => Percentile(values, .99),
            EvalMetricAggregation.Minimum => values[0],
            EvalMetricAggregation.Maximum => values[^1],
            _ => throw new ArgumentOutOfRangeException(nameof(definition))
        };
        return new(definition.Id, value, definition.Unit, definition.Aggregation, values.Length);
    }

    public static ImmutableArray<EvalMetricValue> AggregateSuite(EvalSuite suite, IReadOnlyList<EvalCaseResult> results)
    {
        var samples = results.SelectMany(static item => item.Metrics).ToList();
        var productResults = results.Where(static item => item.Status is not EvalCaseStatus.InfrastructureFailure and not EvalCaseStatus.Skipped and not EvalCaseStatus.Cancelled).ToArray();
        samples.Add(new(EvalMetricIds.SuccessRate, productResults.Length == 0 ? double.NaN : productResults.Count(static item => item.Status == EvalCaseStatus.Passed) / (double)productResults.Length, "ratio", 0));
        samples.Add(new(EvalMetricIds.VerifiedSuccessRate, productResults.Length == 0 ? double.NaN : productResults.Count(static item => item.Status == EvalCaseStatus.Passed && item.Verification.Verified) / (double)productResults.Length, "ratio", 0));
        var definitions = suite.Metrics;
        return definitions.Select(definition =>
        {
            var selected = samples.Where(item => item.MetricId == definition.Id && !double.IsNaN(item.Value));
            return Aggregate(definition, selected);
        }).ToImmutableArray();
    }

    public static double RecallAtK(IReadOnlyCollection<string> relevant, IReadOnlyList<string> ranked, int k)
    {
        if (relevant.Count == 0) return 1;
        var hits = ranked.Take(Math.Max(0, k)).Distinct(StringComparer.Ordinal).Count(relevant.Contains);
        return hits / (double)relevant.Count;
    }

    public static double PrecisionAtK(IReadOnlyCollection<string> relevant, IReadOnlyList<string> ranked, int k)
    {
        var selected = ranked.Take(Math.Max(0, k)).ToArray();
        return selected.Length == 0 ? 0 : selected.Distinct(StringComparer.Ordinal).Count(relevant.Contains) / (double)selected.Length;
    }

    public static double MeanReciprocalRank(IReadOnlyCollection<string> relevant, IReadOnlyList<string> ranked)
    {
        for (var index = 0; index < ranked.Count; index++) if (relevant.Contains(ranked[index])) return 1d / (index + 1);
        return 0;
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 1) return sorted[0];
        var position = percentile * (sorted.Length - 1);
        var lower = (int)Math.Floor(position); var upper = (int)Math.Ceiling(position);
        return lower == upper ? sorted[lower] : sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
    }
}

public static class EvalComparisonEngine
{
    public static EvalComparison Compare(EvalSuite suite, EvalRun baseline, EvalRun candidate)
    {
        ArgumentNullException.ThrowIfNull(suite); ArgumentNullException.ThrowIfNull(baseline); ArgumentNullException.ThrowIfNull(candidate);
        var sameWorkload = baseline.SuiteId == candidate.SuiteId && baseline.SuiteVersion == candidate.SuiteVersion &&
            baseline.CaseResults.Select(static item => item.CaseId).Distinct().OrderBy(static item => item.Value)
                .SequenceEqual(candidate.CaseResults.Select(static item => item.CaseId).Distinct().OrderBy(static item => item.Value));
        if (!sameWorkload) throw new InvalidOperationException("Baseline and candidate must use the same suite version and case set.");

        var environmentCompatible = baseline.Environment.Fingerprint == candidate.Environment.Fingerprint;
        var warnings = environmentCompatible ? ImmutableArray<string>.Empty : ["Environment mismatch: performance and resource metrics are not directly comparable."];
        var comparisonId = EvalComparisonId.New();
        var deltas = ImmutableArray.CreateBuilder<EvalMetricDelta>();
        var regressions = ImmutableArray.CreateBuilder<EvalRegression>();
        var improvements = ImmutableArray.CreateBuilder<EvalImprovement>();

        foreach (var definition in suite.Metrics)
        {
            var before = baseline.Metrics.FirstOrDefault(item => item.MetricId == definition.Id) ?? EvalMetricValue.Unknown(definition.Id, definition.Unit, definition.Aggregation);
            var after = candidate.Metrics.FirstOrDefault(item => item.MetricId == definition.Id) ?? EvalMetricValue.Unknown(definition.Id, definition.Unit, definition.Aggregation);
            var comparable = before.Availability == EvalMetricAvailability.Known && after.Availability == EvalMetricAvailability.Known && before.Value.HasValue && after.Value.HasValue &&
                (environmentCompatible || !IsPerformanceMetric(definition.Id));
            if (!comparable)
            {
                deltas.Add(new(definition.Id, before, after, null, null, EvalChangeClassification.Inconclusive, environmentCompatible ? "Metric is unavailable." : "Environment mismatch prevents a controlled comparison."));
                continue;
            }
            var absolute = after.Value!.Value - before.Value!.Value;
            double? relative = Math.Abs(before.Value.Value) < double.Epsilon ? null : absolute / Math.Abs(before.Value.Value);
            var classification = Math.Abs(absolute) < 1e-12 ? EvalChangeClassification.Neutral : definition.Direction switch
            {
                EvalMetricDirection.HigherIsBetter => absolute > 0 ? EvalChangeClassification.Improvement : EvalChangeClassification.Regression,
                EvalMetricDirection.LowerIsBetter => absolute < 0 ? EvalChangeClassification.Improvement : EvalChangeClassification.Regression,
                _ => EvalChangeClassification.Neutral
            };
            deltas.Add(new(definition.Id, before, after, absolute, relative, classification, $"{definition.Name}: {before.Value:g5} → {after.Value:g5} {definition.Unit}."));
            if (classification == EvalChangeClassification.Improvement) improvements.Add(new(definition.Id, before.Value.Value, after.Value.Value, absolute, "Paired suite aggregate improved."));
        }

        var immutableDeltas = deltas.ToImmutable();
        var gates = suite.Gates.Select(gate => EvaluateGate(gate, immutableDeltas, candidate)).ToImmutableArray();
        foreach (var gate in gates.Where(static item => item.Status == EvalGateStatus.Failed))
        {
            var delta = deltas.First(item => item.MetricId == gate.MetricId);
            regressions.Add(new(EvalRegressionId.New(), comparisonId, suite.Id, null, gate.MetricId, delta.Baseline.Value ?? double.NaN,
                delta.Candidate.Value ?? double.NaN, delta.AbsoluteDelta ?? double.NaN,
                gate.Severity is EvalGateSeverity.SecurityCritical ? EvalRegressionSeverity.Critical : gate.Severity == EvalGateSeverity.ReleaseBlocking ? EvalRegressionSeverity.Major : EvalRegressionSeverity.Minor,
                gate.Explanation, [], null));
        }
        return new(comparisonId, baseline.Id, candidate.Id, DateTimeOffset.UtcNow, true, environmentCompatible, warnings, immutableDeltas, regressions.ToImmutable(), improvements.ToImmutable(), gates);
    }

    public static EvalGateResult Override(EvalGateResult result, string user, string reason, bool securityOverrideAllowed = false)
    {
        if (result.Severity == EvalGateSeverity.SecurityCritical && !securityOverrideAllowed) throw new InvalidOperationException("Critical security gates cannot be casually overridden.");
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A gate override requires identity and rationale.");
        return result with { Status = EvalGateStatus.Passed, OverrideUser = user.Trim(), OverrideReason = reason.Trim(), Explanation = $"Override recorded: {reason.Trim()}" };
    }

    private static EvalGateResult EvaluateGate(EvalGateDefinition gate, ImmutableArray<EvalMetricDelta> deltas, EvalRun candidate)
    {
        var delta = deltas.FirstOrDefault(item => item.MetricId == gate.MetricId);
        if (delta is null || delta.Candidate.Value is null || delta.Candidate.SampleCount < gate.RequiredSampleSize)
            return new(gate.Id, EvalGateStatus.Inconclusive, gate.Severity, gate.MetricId, delta?.Candidate.Value, delta?.Baseline.Value, gate.Threshold, delta?.Candidate.SampleCount ?? 0, "Insufficient comparable samples.");
        var observed = delta.Candidate.Value.Value; var baseline = delta.Baseline.Value;
        var passed = gate.Mode switch
        {
            EvalGateMode.AbsoluteMinimum => observed >= gate.Threshold,
            EvalGateMode.AbsoluteMaximum => observed <= gate.Threshold,
            EvalGateMode.ZeroTolerance => Math.Abs(observed) < double.Epsilon,
            EvalGateMode.RelativeMaximumRegression => delta.RelativeDelta is { } relative &&
                (delta.Classification != EvalChangeClassification.Regression || Math.Abs(relative) <= Math.Abs(gate.Threshold)),
            EvalGateMode.RelativeMinimumImprovement => delta.RelativeDelta is { } improvement &&
                delta.Classification == EvalChangeClassification.Improvement && Math.Abs(improvement) >= Math.Abs(gate.Threshold),
            _ => false
        };
        var explanation = passed ? $"Gate passed: observed {observed:g5}." : $"Gate failed: observed {observed:g5}; threshold {gate.Mode} {gate.Threshold:g5}.";
        return new(gate.Id, passed ? EvalGateStatus.Passed : EvalGateStatus.Failed, gate.Severity, gate.MetricId, observed, baseline, gate.Threshold, delta.Candidate.SampleCount, explanation);
    }

    private static bool IsPerformanceMetric(EvalMetricId id) => id == EvalMetricIds.LatencyMilliseconds || id.Value.Contains("memory", StringComparison.OrdinalIgnoreCase) || id.Value.Contains("throughput", StringComparison.OrdinalIgnoreCase) || id.Value.Contains("cpu", StringComparison.OrdinalIgnoreCase);
}

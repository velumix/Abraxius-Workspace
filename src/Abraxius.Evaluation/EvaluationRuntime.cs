namespace Abraxius.Evaluation;

public sealed class EvaluationRuntime(
    IEvalStore store,
    IEvalRunner runner) : IAsyncDisposable
{
    public IEvalStore Store { get; } = store;
    public IEvalRunner Runner { get; } = runner;
    public ImmutableArray<EvalSuite> BuiltInSuites { get; } = BuiltInEvalSuites.CreateAll();

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        foreach (var suite in BuiltInSuites) await Store.SaveSuiteAsync(suite, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<EvalComparison> CompareAsync(EvalRunId baselineId, EvalRunId candidateId, CancellationToken cancellationToken = default)
    {
        var baseline = await Store.GetRunAsync(baselineId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Eval run {baselineId} was not found.");
        var candidate = await Store.GetRunAsync(candidateId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Eval run {candidateId} was not found.");
        var suite = await Store.GetSuiteAsync(candidate.SuiteId, candidate.SuiteVersion, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Eval suite {candidate.SuiteId}/{candidate.SuiteVersion} was not found.");
        var comparison = EvalComparisonEngine.Compare(suite, baseline, candidate); await Store.SaveComparisonAsync(comparison, cancellationToken).ConfigureAwait(false); return comparison;
    }

    public ValueTask DisposeAsync() => Store.DisposeAsync();
}

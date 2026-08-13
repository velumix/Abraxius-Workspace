using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Abraxius.Protocol;

namespace Abraxius.Models;

/// <summary>Routes each request once through the Abraxius policy engine and then delegates to one peer fabric.</summary>
public sealed class RoutedModelProvider : IModelProvider, IAsyncDisposable
{
    private readonly IIntelligenceRouteEngine _routes;
    private readonly IReadOnlyDictionary<IntelligenceGateway, IModelProvider> _providers;
    private readonly IReadOnlyDictionary<IntelligenceGateway, IIntelligenceGatewayProvider> _gatewayProviders;
    private readonly IntelligenceBudgetLedger? _budget;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _routeGates = new(StringComparer.Ordinal);

    public RoutedModelProvider(
        IIntelligenceRouteEngine routes,
        IEnumerable<IModelProvider> providers,
        IntelligenceBudgetLedger? budget = null)
    {
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _budget = budget;
        ArgumentNullException.ThrowIfNull(providers);
        var providerArray = providers.ToArray();
        _providers = providerArray
            .Select((provider, index) => (provider, index, gateway: GetGateway(provider, index)))
            .GroupBy(static value => value.gateway)
            .ToDictionary(static group => group.Key, static group => group.First().provider);
        _gatewayProviders = providerArray
            .OfType<IIntelligenceGatewayProvider>()
            .GroupBy(static provider => provider.Gateway)
            .ToDictionary(static group => group.Key, static group => group.First());
    }

    public IntelligenceFabricSnapshot Snapshot => _routes.Snapshot;
    public RouteDecision? LastDecision => _routes.Snapshot.LastDecision;
    public IntelligenceBudgetLedger? Budget => _budget;

    public async ValueTask<ModelResult> InferAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        RouteDecision? lastDecision = null;
        Exception? lastException = null;
        var escalationCount = 0;
        var allowTierEscalation = false;

        for (var attempt = 1; attempt <= Math.Max(1, _routes.Snapshot.CandidateCount); attempt++)
        {
            RouteDecision decision;
            try
            {
                decision = SelectWithSameTierPreference(request, excluded, lastDecision, allowTierEscalation);
            }
            catch (IntelligenceRoutingException exception)
            {
                throw lastException is null ? exception : new IntelligenceRoutingException(
                    new RuntimeError(ErrorCategory.Model, "all_model_routes_failed", "All eligible model routes failed.", lastException.Message, IsTransient: false),
                    lastDecision);
            }

            lastDecision = decision;
            if (!_providers.TryGetValue(decision.Gateway, out var provider))
            {
                excluded.Add(decision.SelectedCandidateKey());
                lastException = new IntelligenceRoutingException(new RuntimeError(
                    ErrorCategory.Configuration,
                    "provider_not_registered",
                    $"No provider adapter is registered for gateway '{decision.Gateway}'."), decision);
                continue;
            }

            try
            {
                ReserveBudget(request, decision);
                var routeGate = await AcquireRouteGateAsync(decision, cancellationToken).ConfigureAwait(false);
                ModelResult result;
                try
                {
                    var routedRequest = request with { Model = decision.Route };
                    result = await provider.InferAsync(routedRequest, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseRouteGate(routeGate);
                }
                if (!IsStructuredResponseValid(request, result))
                {
                    var qualityOutcome = new ModelOutcome(
                        Succeeded: false,
                        Verified: false,
                        Transient: false,
                        TimeSpan.Zero,
                        Detail: "structured_output_invalid");
                    _routes.RecordOutcome(decision, qualityOutcome);
                    var escalation = _routes.DecideEscalation(decision, qualityOutcome, escalationCount);
                    if (escalation.Action is EscalationAction.Abort or EscalationAction.RequestUserApproval)
                    {
                        throw new IntelligenceRoutingException(new RuntimeError(
                            ErrorCategory.Verification,
                            "structured_output_invalid",
                            "The selected route did not produce valid structured output and the escalation policy stopped further attempts.",
                            IsTransient: false), decision);
                    }

                    if (escalation.Action is EscalationAction.EscalateOneTier or EscalationAction.EscalateToSpecialist)
                    {
                        escalationCount++;
                        allowTierEscalation = true;
                    }

                    excluded.Add(decision.SelectedCandidateKey());
                    lastDecision = decision;
                    lastException = new IntelligenceRoutingException(new RuntimeError(
                        ErrorCategory.Verification,
                        "structured_output_invalid",
                        "The selected route returned invalid structured output."), decision);
                    continue;
                }

                _routes.RecordOutcome(decision, new ModelOutcome(
                    Succeeded: true,
                    Verified: true,
                    Transient: false,
                    result.Latency,
                    result.Usage?.InputTokens,
                    result.Usage?.OutputTokens,
                    result.Usage?.EstimatedCost));
                return result with
                {
                    Model = result.Model == "unknown" ? decision.SelectedCandidate.ModelId : result.Model,
                    Provider = result.Provider ?? decision.SelectedCandidate.Provider,
                    Route = decision
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IntelligenceRoutingException exception) when (exception.Error.Category == ErrorCategory.Policy)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastException = exception;
                excluded.Add(decision.SelectedCandidateKey());
                _routes.RecordOutcome(decision, new ModelOutcome(
                    Succeeded: false,
                    Verified: false,
                    Transient: IsTransient(exception),
                    TimeSpan.Zero,
                    Detail: exception.Message));
            }
        }

        var error = lastException switch
        {
            ModelProviderException modelProviderException => modelProviderException.Error,
            IntelligenceRoutingException routingException => routingException.Error,
            _ => new RuntimeError(ErrorCategory.Model, "all_model_routes_failed", "All eligible model routes failed.", lastException?.Message, IsTransient: IsTransient(lastException))
        };
        throw new IntelligenceRoutingException(error, lastDecision);
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var decision = _routes.SelectRoute(request);
        if (!_providers.TryGetValue(decision.Gateway, out var provider))
        {
            throw new IntelligenceRoutingException(new RuntimeError(
                ErrorCategory.Configuration,
                "provider_not_registered",
                $"No provider adapter is registered for gateway '{decision.Gateway}'."), decision);
        }

        ReserveBudget(request, decision);
        var routeGate = await AcquireRouteGateAsync(decision, cancellationToken).ConfigureAwait(false);
        try
        {
            await foreach (var item in provider.StreamAsync(request with { Model = decision.Route }, cancellationToken).ConfigureAwait(false))
            {
                if (item is ModelStreamEvent.Completed completed)
                {
                    _routes.RecordOutcome(decision, new ModelOutcome(
                        Succeeded: true,
                        Verified: true,
                        Transient: false,
                        completed.Result.Latency,
                        completed.Result.Usage?.InputTokens,
                        completed.Result.Usage?.OutputTokens,
                        completed.Result.Usage?.EstimatedCost));
                    yield return completed with { Result = completed.Result with { Route = decision } };
                }
                else
                {
                    yield return item;
                }
            }
        }
        finally
        {
            ReleaseRouteGate(routeGate);
        }
    }

    public async ValueTask<IReadOnlyList<ProviderHealth>> RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = new List<ProviderHealth>(_gatewayProviders.Count);
        foreach (var provider in _gatewayProviders.Values)
        {
            health.Add(await provider.CheckHealthAsync(cancellationToken).ConfigureAwait(false));
        }

        return health;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers.Values.Distinct())
        {
            switch (provider)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }

        foreach (var gate in _routeGates.Values)
        {
            gate.Dispose();
        }

        _routeGates.Clear();
    }

    private RouteDecision SelectWithSameTierPreference(ModelRequest request, HashSet<string> excluded, RouteDecision? previous, bool allowTierEscalation)
    {
        if (previous is null)
        {
            return _routes.SelectRoute(request, excluded);
        }

        try
        {
            var sameTierRequest = request with
            {
                Policy = request.Policy with { MaximumTier = previous.Tier }
            };
            return _routes.SelectRoute(sameTierRequest, excluded);
        }
        catch (IntelligenceRoutingException) when (allowTierEscalation)
        {
            return _routes.SelectRoute(request, excluded);
        }
    }

    private static bool IsStructuredResponseValid(ModelRequest request, ModelResult result)
    {
        if (request.ExpectedJsonSchema is null)
        {
            return true;
        }

        var json = result.StructuredJson ?? result.Text;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void ReserveBudget(ModelRequest request, RouteDecision decision)
    {
        if (_budget is not null && request.ExecutionId is { } executionId &&
            !_budget.TryReserve(
                executionId,
                request.ExecutionMaximumCalls,
                request.ExecutionMaximumCost,
                request.Policy.MaximumPremiumTokens,
                decision.EstimatedCost ?? 0,
                decision.Tier >= IntelligenceTier.Frontier ? request.MaxOutputTokens ?? 0 : 0,
                out var budgetError))
        {
            throw new IntelligenceRoutingException(budgetError!, decision);
        }
    }

    private async ValueTask<SemaphoreSlim?> AcquireRouteGateAsync(RouteDecision decision, CancellationToken cancellationToken)
    {
        var maximum = decision.SelectedCandidate.Capacity?.MaxConcurrentRequests;
        if (maximum is not > 0)
        {
            return null;
        }

        var gate = _routeGates.GetOrAdd(
            decision.SelectedCandidateKey(),
            _ => new SemaphoreSlim(maximum.Value, maximum.Value));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return gate;
    }

    private static void ReleaseRouteGate(SemaphoreSlim? gate) => gate?.Release();

    private static IntelligenceGateway GetGateway(IModelProvider provider, int index) =>
        provider is IIntelligenceGatewayProvider gatewayProvider
            ? gatewayProvider.Gateway
            : index == 0 ? IntelligenceGateway.Mock : IntelligenceGateway.Local;

    private static bool IsTransient(Exception? exception) => exception switch
    {
        ModelProviderException providerException => providerException.Error.IsTransient,
        HttpRequestException => true,
        TimeoutException => true,
        _ => false
    };
}

internal static class RouteDecisionExtensions
{
    public static string SelectedCandidateKey(this RouteDecision decision) =>
        $"{decision.Gateway}:{decision.SelectedCandidate.Provider}:{decision.SelectedCandidate.ModelId}:{decision.SelectedCandidate.Route}";
}

/// <summary>Deterministic provider double used by routing and escalation tests.</summary>
public class FakeIntelligenceProvider : IIntelligenceGatewayProvider
{
    private readonly Func<ModelRequest, CancellationToken, ValueTask<ModelResult>> _handler;
    private readonly ModelDescriptor[] _models;

    public FakeIntelligenceProvider(
        IntelligenceGateway gateway,
        string providerKey,
        IEnumerable<ModelDescriptor> models,
        Func<ModelRequest, CancellationToken, ValueTask<ModelResult>>? handler = null)
    {
        Gateway = gateway;
        ProviderKey = providerKey;
        _models = models.ToArray();
        _handler = handler ?? (Func<ModelRequest, CancellationToken, ValueTask<ModelResult>>)((request, _) => ValueTask.FromResult(new ModelResult(
            $"fake:{providerKey}:{request.Prompt}",
            null,
            request.Model ?? (_models.Length > 0 ? _models[0].ModelId : "fake"),
            new ModelUsage(1, 1, 0),
            TimeSpan.Zero,
            providerKey)));
    }

    public IntelligenceGateway Gateway { get; }
    public string ProviderKey { get; }
    public Uri Endpoint { get; } = new("https://fake.invalid/v1");

    public ValueTask<ModelResult> InferAsync(ModelRequest request, CancellationToken cancellationToken = default) => _handler(request, cancellationToken);

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await InferAsync(request, cancellationToken).ConfigureAwait(false);
        yield return new ModelStreamEvent.Started(DateTimeOffset.UtcNow, result.Model);
        yield return new ModelStreamEvent.Token(DateTimeOffset.UtcNow, result.Text);
        yield return new ModelStreamEvent.Completed(DateTimeOffset.UtcNow, result);
    }

    public ValueTask<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ProviderHealth { Status = ProviderHealthStatus.Healthy, ObservedAt = DateTimeOffset.UtcNow });

    public ValueTask<IReadOnlyList<ModelDescriptor>> DiscoverModelsAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<ModelDescriptor>>(_models);
}

public sealed class FakeOmniRouteModelProvider : FakeIntelligenceProvider
{
    public FakeOmniRouteModelProvider(IEnumerable<ModelDescriptor> models, Func<ModelRequest, CancellationToken, ValueTask<ModelResult>>? handler = null)
        : base(IntelligenceGateway.OmniRoute, "omniroute", models, handler)
    {
    }
}

public sealed class FakeLiteLlmModelProvider : FakeIntelligenceProvider
{
    public FakeLiteLlmModelProvider(IEnumerable<ModelDescriptor> models, Func<ModelRequest, CancellationToken, ValueTask<ModelResult>>? handler = null)
        : base(IntelligenceGateway.LiteLlm, "litellm", models, handler)
    {
    }
}

public sealed class FakeFrontierModelProvider : FakeIntelligenceProvider
{
    public FakeFrontierModelProvider(IEnumerable<ModelDescriptor> models, Func<ModelRequest, CancellationToken, ValueTask<ModelResult>>? handler = null)
        : base(IntelligenceGateway.Frontier, "frontier", models, handler)
    {
    }
}

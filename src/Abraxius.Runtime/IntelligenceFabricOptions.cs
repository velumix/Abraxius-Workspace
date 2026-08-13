using Abraxius.Models;
using Abraxius.Security;

namespace Abraxius.Runtime;

public sealed record GatewayConnectionOptions
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string DefaultModel { get; init; } = "default";
    public string? ApiKeyEnvironmentVariable { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed record IntelligenceFabricOptions
{
    public IntelligenceRoutingPolicy Policy { get; init; } = new();
    public GatewayConnectionOptions OmniRoute { get; init; } = new()
    {
        Endpoint = "http://localhost:20128/v1/chat/completions",
        DefaultModel = "auto/coding:free"
    };
    public GatewayConnectionOptions LiteLlm { get; init; } = new()
    {
        Endpoint = "http://localhost:4000/v1/chat/completions",
        DefaultModel = "general-low-cost"
    };
    public GatewayConnectionOptions Frontier { get; init; } = new();
    public IReadOnlyList<ModelRouteCandidate> Candidates { get; init; } = [];
}

public sealed class IntelligenceFabric : IAsyncDisposable
{
    private readonly IReadOnlyList<IModelProvider> _providers;

    internal IntelligenceFabric(
        IntelligenceRouteEngine routeEngine,
        RoutedModelProvider provider,
        IReadOnlyList<IModelProvider> providers,
        IntelligenceBudgetLedger budget)
    {
        RouteEngine = routeEngine;
        Provider = provider;
        _providers = providers;
        Budget = budget;
    }

    public IntelligenceRouteEngine RouteEngine { get; }
    public RoutedModelProvider Provider { get; }
    public IntelligenceBudgetLedger Budget { get; }
    public IntelligenceFabricSnapshot Snapshot => RouteEngine.Snapshot;

    public async ValueTask<IReadOnlyList<ProviderHealth>> RefreshHealthAsync(CancellationToken cancellationToken = default)
    {
        var providers = _providers.OfType<IIntelligenceGatewayProvider>().ToArray();
        var healthTasks = providers.Select(async provider =>
        {
            var health = await provider.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            RouteEngine.UpdateGatewayHealth(provider.Gateway, health);
            if (health.Status is ProviderHealthStatus.Healthy or ProviderHealthStatus.Degraded)
            {
                try
                {
                    var discovered = await provider.DiscoverModelsAsync(cancellationToken).ConfigureAwait(false);
                    RouteEngine.MergeDiscoveredModels(discovered);
                }
                catch (ModelProviderException)
                {
                    // Health is still useful when a gateway's optional model catalog endpoint is unavailable.
                }
            }

            return health;
        }).ToArray();
        var health = await Task.WhenAll(healthTasks).ConfigureAwait(false);

        return health;
    }

    public ValueTask DisposeAsync() => Provider.DisposeAsync();
}

internal static class IntelligenceFabricFactory
{
    public static IntelligenceFabric Create(
        IntelligenceFabricOptions options,
        ISecretBroker? secrets = null,
        ISecretRedactor? redactor = null,
        IReadOnlyDictionary<IntelligenceGateway, SecretReference>? secretReferences = null,
        SecuritySubject? transportSubject = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var candidates = options.Candidates.ToList();
        var providers = new List<IModelProvider>();
        var configuredExternalGateway = false;

        AddGateway(
            options.OmniRoute,
            IntelligenceGateway.OmniRoute,
            static (httpClient, endpoint, model, apiKey) => new OmniRouteModelProvider(httpClient, endpoint, model, apiKey, ownsHttpClient: true),
            providers,
            candidates,
            ref configuredExternalGateway,
            secrets,
            redactor,
            secretReferences,
            transportSubject);
        AddGateway(
            options.LiteLlm,
            IntelligenceGateway.LiteLlm,
            static (httpClient, endpoint, model, apiKey) => new LiteLlmModelProvider(httpClient, endpoint, model, apiKey, ownsHttpClient: true),
            providers,
            candidates,
            ref configuredExternalGateway,
            secrets,
            redactor,
            secretReferences,
            transportSubject);
        AddGateway(
            options.Frontier,
            IntelligenceGateway.Frontier,
            static (httpClient, endpoint, model, apiKey) => new FrontierModelProvider(httpClient, endpoint, model, apiKey, ownsHttpClient: true),
            providers,
            candidates,
            ref configuredExternalGateway,
            secrets,
            redactor,
            secretReferences,
            transportSubject);

        if (!configuredExternalGateway || candidates.Count == 0)
        {
            var mock = new MockModelProvider();
            providers.Add(mock);
            candidates.Add(new ModelRouteCandidate(new ModelDescriptor
            {
                ModelId = "mock-reasoner",
                DisplayName = "Deterministic mock reasoner",
                Provider = "mock",
                Gateway = IntelligenceGateway.Mock,
                Route = "mock-reasoner",
                Tier = IntelligenceTier.Free,
                CostClass = ModelCostClass.Zero,
                Capabilities = new ModelCapabilities
                {
                    Streaming = true,
                    ToolCalling = true,
                    StructuredOutput = true,
                    Reasoning = true,
                    Coding = true,
                    ContextWindow = 128_000,
                    MaximumOutputTokens = 8_192
                },
                Health = new ProviderHealth { Status = ProviderHealthStatus.Healthy, ObservedAt = DateTimeOffset.UtcNow }
            }));
        }

        var engine = new IntelligenceRouteEngine(candidates, options.Policy);
        var budget = new IntelligenceBudgetLedger();
        var routed = new RoutedModelProvider(engine, providers, budget);
        return new IntelligenceFabric(engine, routed, providers, budget);
    }

    private static void AddGateway(
        GatewayConnectionOptions configuration,
        IntelligenceGateway gateway,
        Func<HttpClient, Uri, string, string?, IModelProvider> create,
        List<IModelProvider> providers,
        List<ModelRouteCandidate> candidates,
        ref bool configuredExternalGateway,
        ISecretBroker? secrets,
        ISecretRedactor? redactor,
        IReadOnlyDictionary<IntelligenceGateway, SecretReference>? secretReferences,
        SecuritySubject? transportSubject)
    {
        if (!configuration.Enabled || !Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out var endpoint))
        {
            return;
        }

        HttpMessageHandler? handler = null;
        if (secretReferences is not null && secretReferences.TryGetValue(gateway, out var reference))
        {
            if (secrets is null || redactor is null || transportSubject is null)
                throw new InvalidOperationException("Configured model credentials require the Security secret broker.");
            handler = new BrokeredBearerAuthenticationHandler(secrets, redactor, reference, transportSubject);
        }
        var httpClient = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, configuration.TimeoutSeconds));
        providers.Add(create(httpClient, endpoint, configuration.DefaultModel, null));
        configuredExternalGateway = true;

        if (!candidates.Any(candidate => candidate.Model.Gateway == gateway))
        {
            candidates.Add(new ModelRouteCandidate(new ModelDescriptor
            {
                ModelId = configuration.DefaultModel,
                DisplayName = configuration.DefaultModel,
                Provider = gateway.ToString(),
                Gateway = gateway,
                Route = configuration.DefaultModel,
                Tier = gateway == IntelligenceGateway.OmniRoute ? IntelligenceTier.Free : IntelligenceTier.Standard,
                CostClass = ModelCostClass.Unknown,
                Capabilities = new ModelCapabilities { Streaming = true, StructuredOutput = true },
                Health = new ProviderHealth { Status = ProviderHealthStatus.Unknown }
            }));
        }
    }
}

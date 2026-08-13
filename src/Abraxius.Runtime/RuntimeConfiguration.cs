using Abraxius.Protocol;
using Abraxius.Models;
using Abraxius.Scheduler;
using Microsoft.Extensions.Configuration;

namespace Abraxius.Runtime;

public sealed class AbraxiusConfiguration
{
    public SchedulerConfiguration Scheduler { get; init; } = new();
    public ModelsConfiguration Models { get; init; } = new();
    public LatticeConfiguration Lattice { get; init; } = new();
    public MemoryConfiguration Memory { get; init; } = new();
    public LedgerConfiguration Ledger { get; init; } = new();
    public UiConfiguration Ui { get; init; } = new();
    public TelemetryConfiguration Telemetry { get; init; } = new();
    public IntelligenceConfiguration Intelligence { get; init; } = new();
}

public sealed class SchedulerConfiguration
{
    public int DefaultQueueCapacity { get; init; } = 128;
    public int DefaultTimeoutSeconds { get; init; } = 30;
    public Dictionary<string, int> ConcurrencyLimits { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelsConfiguration
{
    public string Endpoint { get; init; } = "http://localhost:4000/v1/chat/completions";
    public string DefaultModel { get; init; } = "default";
    public int MaxConcurrentRequests { get; init; } = 4;
}

public sealed class LatticeConfiguration
{
    public string RootPath { get; init; } = ".";
    public bool AllowMutations { get; init; }
}

public sealed class MemoryConfiguration
{
    public string? EvidencePath { get; init; }
    public string? DatabasePath { get; init; }
    public bool UseFileEvidence { get; init; } = true;
}

public sealed class LedgerConfiguration
{
    public string? Path { get; init; }
    public bool UseFileLedger { get; init; } = true;
    public int BufferCapacity { get; init; } = 8192;
}

public sealed class UiConfiguration
{
    public string PerformanceMode { get; init; } = "Balanced";
    public int GraphRefreshRate { get; init; } = 60;
    public bool ReducedMotion { get; init; }
}

public sealed class TelemetryConfiguration
{
    public int RetentionEvents { get; init; } = 10000;
}

public sealed class IntelligenceConfiguration
{
    public string RoutingMode { get; init; } = "FreeFirst";
    public string MaximumTier { get; init; } = "Frontier";
    public bool AllowPaidInference { get; init; }
    public decimal? MaximumEstimatedCost { get; init; }
    public int MaximumEscalations { get; init; } = 2;
    public string Privacy { get; init; } = "AnyAllowed";
    public bool StrictUnknownCost { get; init; } = true;
    public GatewayConfiguration OmniRoute { get; init; } = new()
    {
        Endpoint = "http://localhost:20128/v1/chat/completions",
        DefaultModel = "auto/coding:free"
    };
    public GatewayConfiguration LiteLlm { get; init; } = new()
    {
        Endpoint = "http://localhost:4000/v1/chat/completions",
        DefaultModel = "general-low-cost"
    };
    public GatewayConfiguration Frontier { get; init; } = new();
    public List<ModelConfiguration> Models { get; init; } = [];
}

public sealed class GatewayConfiguration
{
    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string DefaultModel { get; init; } = "default";
    public string? ApiKeyEnvironmentVariable { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
}

public sealed class ModelConfiguration
{
    public string ModelId { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Gateway { get; init; } = "OmniRoute";
    public string? Route { get; init; }
    public string Tier { get; init; } = "Free";
    public string CostClass { get; init; } = "Unknown";
    public decimal? EstimatedCostPerRequest { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Streaming { get; init; } = true;
    public bool ToolCalling { get; init; }
    public bool StructuredOutput { get; init; }
    public bool Vision { get; init; }
    public bool Reasoning { get; init; }
    public bool Coding { get; init; }
    public int? ContextWindow { get; init; }
    public int? MaximumOutputTokens { get; init; }
    public int? MaxConcurrentRequests { get; init; }
}

public static class RuntimeConfigurationLoader
{
    public static IConfiguration Load(string? basePath = null) => new ConfigurationBuilder()
        .SetBasePath(basePath ?? AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddEnvironmentVariables("ABRAXIUS_")
        .Build();

    public static RuntimeHostOptions ToHostOptions(IConfiguration configuration)
    {
        var root = configuration.GetSection("Abraxius").Get<AbraxiusConfiguration>() ?? new AbraxiusConfiguration();
        var limits = SchedulerOptions.CreateDefaults().ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (var pair in root.Scheduler.ConcurrencyLimits)
        {
            if (Enum.TryParse<ExecutorKind>(pair.Key, ignoreCase: true, out var kind))
            {
                limits[kind] = Math.Max(1, pair.Value);
            }
        }

        return new RuntimeHostOptions(
            LedgerPath: root.Ledger.Path,
            EvidencePath: root.Memory.EvidencePath,
            MemoryDatabasePath: root.Memory.DatabasePath,
            UseFileEvidence: root.Memory.UseFileEvidence,
            UseFileLedger: root.Ledger.UseFileLedger,
            EventBufferCapacity: Math.Max(1, root.Ledger.BufferCapacity),
            Scheduler: new SchedulerOptions
            {
                DefaultQueueCapacity = Math.Max(1, root.Scheduler.DefaultQueueCapacity),
                DefaultTimeout = TimeSpan.FromSeconds(Math.Max(1, root.Scheduler.DefaultTimeoutSeconds)),
                ConcurrencyLimits = limits
            },
            Intelligence: CreateIntelligenceOptions(root.Intelligence));
    }

    private static IntelligenceFabricOptions CreateIntelligenceOptions(IntelligenceConfiguration configuration)
    {
        var mode = Enum.TryParse<IntelligenceRoutingMode>(configuration.RoutingMode, true, out var parsedMode)
            ? parsedMode
            : IntelligenceRoutingMode.FreeFirst;
        var maximumTier = Enum.TryParse<IntelligenceTier>(configuration.MaximumTier, true, out var parsedTier)
            ? parsedTier
            : IntelligenceTier.Frontier;
        var privacy = Enum.TryParse<PrivacyRoutePolicy>(configuration.Privacy, true, out var parsedPrivacy)
            ? parsedPrivacy
            : PrivacyRoutePolicy.AnyAllowed;
        var candidates = configuration.Models
            .Where(static model => !string.IsNullOrWhiteSpace(model.ModelId) && Enum.TryParse<IntelligenceGateway>(model.Gateway, true, out _))
            .Select(model => new ModelRouteCandidate(new ModelDescriptor
            {
                ModelId = model.ModelId,
                DisplayName = model.ModelId,
                Provider = string.IsNullOrWhiteSpace(model.Provider) ? model.Gateway : model.Provider,
                Gateway = Enum.Parse<IntelligenceGateway>(model.Gateway, true),
                Route = model.Route,
                Tier = Enum.TryParse<IntelligenceTier>(model.Tier, true, out var tier) ? tier : IntelligenceTier.Standard,
                CostClass = Enum.TryParse<ModelCostClass>(model.CostClass, true, out var cost) ? cost : ModelCostClass.Unknown,
                EstimatedCostPerRequest = model.EstimatedCostPerRequest,
                Enabled = model.Enabled,
                Capabilities = new ModelCapabilities
                {
                    Streaming = model.Streaming,
                    ToolCalling = model.ToolCalling,
                    StructuredOutput = model.StructuredOutput,
                    Vision = model.Vision,
                    Reasoning = model.Reasoning,
                    Coding = model.Coding,
                    ContextWindow = model.ContextWindow,
                    MaximumOutputTokens = model.MaximumOutputTokens
                },
                Health = new ProviderHealth { Status = ProviderHealthStatus.Unknown },
                Capacity = model.MaxConcurrentRequests is { } maxConcurrentRequests
                    ? new RouteCapacity { MaxConcurrentRequests = maxConcurrentRequests }
                    : null
            }))
            .ToArray();

        return new IntelligenceFabricOptions
        {
            Policy = new IntelligenceRoutingPolicy
            {
                StrictUnknownCost = configuration.StrictUnknownCost,
                DefaultRequest = new IntelligenceRequestPolicy
                {
                    Mode = mode,
                    MaximumTier = maximumTier,
                    AllowPaidInference = configuration.AllowPaidInference,
                    MaximumEstimatedCost = configuration.MaximumEstimatedCost,
                    MaximumEscalations = Math.Max(0, configuration.MaximumEscalations),
                    Privacy = privacy
                }
            },
            OmniRoute = ToGatewayOptions(configuration.OmniRoute, "http://localhost:20128/v1/chat/completions", "auto/coding:free"),
            LiteLlm = ToGatewayOptions(configuration.LiteLlm, "http://localhost:4000/v1/chat/completions", "general-low-cost"),
            Frontier = ToGatewayOptions(configuration.Frontier, string.Empty, "frontier"),
            Candidates = candidates
        };
    }

    private static GatewayConnectionOptions ToGatewayOptions(GatewayConfiguration configuration, string endpoint, string model) => new()
    {
        Enabled = configuration.Enabled,
        Endpoint = string.IsNullOrWhiteSpace(configuration.Endpoint) ? endpoint : configuration.Endpoint,
        DefaultModel = string.IsNullOrWhiteSpace(configuration.DefaultModel) ? model : configuration.DefaultModel,
        ApiKeyEnvironmentVariable = configuration.ApiKeyEnvironmentVariable,
        TimeoutSeconds = Math.Max(1, configuration.TimeoutSeconds)
    };
}

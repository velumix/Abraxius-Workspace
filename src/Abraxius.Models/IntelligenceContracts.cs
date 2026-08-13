using System.Collections.Immutable;
using Abraxius.Protocol;

namespace Abraxius.Models;

/// <summary>Provider fabric selected by Abraxius for one model request.</summary>
public enum IntelligenceGateway
{
    Local,
    OmniRoute,
    LiteLlm,
    Frontier,
    Mock
}

/// <summary>Ordered intelligence tiers. Higher tiers are more capable and normally more expensive.</summary>
public enum IntelligenceTier
{
    Deterministic = 0,
    Free = 1,
    Included = 2,
    UltraLow = 3,
    Standard = 4,
    Frontier = 5,
    Specialist = 6
}

public enum IntelligenceRoutingMode
{
    FreeFirst,
    FreeFirstStrict,
    Balanced,
    QualityFirst,
    Manual
}

public enum ModelCostClass
{
    Zero,
    Included,
    UltraLow,
    Low,
    Standard,
    Premium,
    Unknown
}

public enum ModelCapability
{
    Streaming,
    ToolCalling,
    StructuredOutput,
    Vision,
    Reasoning,
    Coding,
    LongContext
}

public enum IntelligenceTaskClass
{
    General,
    SimpleQuestion,
    CodeSearch,
    CodeGeneration,
    CodeReview,
    Debugging,
    Architecture,
    Planning,
    Summarization,
    Extraction,
    ToolSelection,
    Vision,
    LongContext,
    Verification
}

public enum IntelligenceComplexity
{
    Trivial,
    Simple,
    Moderate,
    Complex,
    Extreme
}

public enum PrivacyRoutePolicy
{
    AnyAllowed,
    TrustedProvidersOnly,
    LocalOnly
}

public enum DataClassification
{
    Public,
    Internal,
    Sensitive,
    Confidential,
    Secret,
    LocalOnly
}

public enum ProviderHealthStatus
{
    Healthy,
    Degraded,
    RateLimited,
    Unavailable,
    Unknown
}

public enum QuotaPeriod
{
    Unknown,
    Minute,
    Hour,
    Day,
    Week,
    Month,
    ProviderDefined
}

public enum QuotaSource
{
    UserConfiguration,
    Gateway,
    Provider,
    Estimated
}

public enum RouteRejectionReason
{
    Disabled,
    TierAboveMaximum,
    CapabilityMissing,
    ContextTooSmall,
    PrivacyPolicy,
    Unhealthy,
    QuotaExhausted,
    CostAboveBudget,
    PaidInferenceDisabled,
    ManualPolicy,
    UnknownCost,
    ExcludedAfterFailure
}

public enum EscalationAction
{
    Stay,
    Retry,
    SameTierFailover,
    EscalateOneTier,
    EscalateToSpecialist,
    Abort,
    RequestUserApproval
}

/// <summary>Capabilities advertised by a model independently of any provider SDK.</summary>
public sealed record ModelCapabilities
{
    public bool Streaming { get; init; }
    public bool ToolCalling { get; init; }
    public bool StructuredOutput { get; init; }
    public bool Vision { get; init; }
    public bool Reasoning { get; init; }
    public bool Coding { get; init; }
    public int? ContextWindow { get; init; }
    public int? MaximumOutputTokens { get; init; }

    public bool Supports(ModelCapability capability) => capability switch
    {
        ModelCapability.Streaming => Streaming,
        ModelCapability.ToolCalling => ToolCalling,
        ModelCapability.StructuredOutput => StructuredOutput,
        ModelCapability.Vision => Vision,
        ModelCapability.Reasoning => Reasoning,
        ModelCapability.Coding => Coding,
        ModelCapability.LongContext => ContextWindow is >= 100_000,
        _ => false
    };
}

public sealed record ModelDescriptor
{
    public required string ModelId { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public required string Provider { get; init; }
    public required IntelligenceGateway Gateway { get; init; }
    public string? Route { get; init; }
    public IntelligenceTier Tier { get; init; } = IntelligenceTier.Standard;
    public ModelCostClass CostClass { get; init; } = ModelCostClass.Unknown;
    /// <summary>Optional configured estimate for one representative request; null means unknown.</summary>
    public decimal? EstimatedCostPerRequest { get; init; }
    public ModelCapabilities Capabilities { get; init; } = new();
    public ProviderHealth Health { get; init; } = new();
    public ModelQualityProfile Quality { get; init; } = new();
    public RouteCapacity? Capacity { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsLocal => Gateway is IntelligenceGateway.Local or IntelligenceGateway.Mock;
}

/// <summary>Optional gateway-advertised admission limits for one model route.</summary>
public sealed record RouteCapacity
{
    public int? MaxConcurrentRequests { get; init; }
    public int? RequestsPerMinuteRemaining { get; init; }
    public long? TokensPerMinuteRemaining { get; init; }
    public DateTimeOffset? ObservedAt { get; init; }
}

public sealed record ProviderHealth
{
    public ProviderHealthStatus Status { get; init; } = ProviderHealthStatus.Unknown;
    public DateTimeOffset? ObservedAt { get; init; }
    public TimeSpan? EstimatedLatency { get; init; }
    public string? Detail { get; init; }
}

public sealed record QuotaState
{
    public long? RemainingTokens { get; init; }
    public long? LimitTokens { get; init; }
    public DateTimeOffset? ResetAt { get; init; }
    public QuotaPeriod Period { get; init; } = QuotaPeriod.Unknown;
    public QuotaSource Source { get; init; } = QuotaSource.Estimated;
    public double Confidence { get; init; }
    public DateTimeOffset? RefreshedAt { get; init; }

    public bool IsExhausted => RemainingTokens is <= 0;
    public bool IsKnown => RemainingTokens is not null || LimitTokens is not null || ResetAt is not null;
}

public sealed record ModelQualityProfile
{
    public double QualityScore { get; init; } = 50;
    public double SuccessRate { get; init; } = 0.5;
    public double VerificationPassRate { get; init; } = 0.5;
    public double ToolValidityRate { get; init; } = 0.5;
    public int Observations { get; init; }
}

public sealed record ModelRouteCandidate(
    ModelDescriptor Model,
    QuotaState? Quota = null,
    string? CandidateKey = null)
{
    public string Key => CandidateKey ?? $"{Model.Gateway}:{Model.Provider}:{Model.ModelId}:{Model.Route}";
}

public sealed record IntelligenceRequestPolicy
{
    public IntelligenceRoutingMode Mode { get; init; } = IntelligenceRoutingMode.FreeFirst;
    public IntelligenceTier MaximumTier { get; init; } = IntelligenceTier.Frontier;
    public IntelligenceTier? PreferredTier { get; init; }
    public bool AllowPaidInference { get; init; }
    public decimal? MaximumEstimatedCost { get; init; }
    public int? MaximumPremiumTokens { get; init; }
    public int MaximumEscalations { get; init; } = 2;
    public PrivacyRoutePolicy Privacy { get; init; } = PrivacyRoutePolicy.AnyAllowed;
    public DataClassification DataClassification { get; init; } = DataClassification.Internal;
    public ImmutableArray<string> TrustedProviders { get; init; } = ImmutableArray<string>.Empty;
    public string? ManualModelId { get; init; }
    public IntelligenceTier MinimumQualityTier { get; init; } = IntelligenceTier.Deterministic;
}

public sealed record RouteScoringWeights
{
    public double Capability { get; init; } = 100;
    public double Quality { get; init; } = 0.8;
    public double FreeQuota { get; init; } = 0.35;
    public double QuotaExpiry { get; init; } = 0.1;
    public double Health { get; init; } = 10;
    public double Latency { get; init; } = 0.02;
    public double Affinity { get; init; } = 8;
    public double Cost { get; init; } = 12;
    public double Reliability { get; init; } = 15;
}

public sealed record IntelligenceRoutingPolicy
{
    public IntelligenceRequestPolicy DefaultRequest { get; init; } = new();
    public RouteScoringWeights Weights { get; init; } = new();
    public bool EnableExploration { get; init; }
    public bool StrictUnknownCost { get; init; } = true;
}

public sealed record RouteCandidateEvaluation(
    ModelRouteCandidate Candidate,
    bool Eligible,
    double Score,
    RouteRejectionReason? Rejection = null,
    string? Detail = null);

public sealed record RouteDecision
{
    public required ModelRequestId RequestId { get; init; }
    public required IntelligenceTier Tier { get; init; }
    public required IntelligenceGateway Gateway { get; init; }
    public required string Route { get; init; }
    public required ModelDescriptor SelectedCandidate { get; init; }
    public required IReadOnlyList<RouteCandidateEvaluation> Candidates { get; init; }
    public string Reason { get; init; } = string.Empty;
    public decimal? EstimatedCost { get; init; }
    public QuotaState? Quota { get; init; }
    public string? EscalationReason { get; init; }
    public int Attempt { get; init; } = 1;
    public DateTimeOffset SelectedAt { get; init; } = DateTimeOffset.UtcNow;

    public override string ToString() => $"Tier {Tier} / {Gateway} / {Route} / {SelectedCandidate.ModelId}";
}

public sealed record ModelOutcome(
    bool Succeeded,
    bool Verified,
    bool Transient,
    TimeSpan Latency,
    int? InputTokens = null,
    int? OutputTokens = null,
    decimal? EstimatedCost = null,
    string? Detail = null);

public sealed record EscalationDecision(
    EscalationAction Action,
    IntelligenceTier CurrentTier,
    IntelligenceTier? NextTier,
    string Reason,
    int Attempt,
    decimal? EstimatedAdditionalCost = null);

public sealed record IntelligenceFabricSnapshot(
    IntelligenceRoutingMode Mode,
    int CandidateCount,
    int HealthyGatewayCount,
    int FreeCandidateCount,
    int IncludedCandidateCount,
    int PaidCandidateCount,
    int FrontierCandidateCount,
    RouteDecision? LastDecision,
    DateTimeOffset RefreshedAt)
{
    public string StatusText => $"INTELLIGENCE {Mode} · FREE {FreeCandidateCount} · FRONTIER {FrontierCandidateCount}";
}

public interface IIntelligenceRouteEngine
{
    IntelligenceFabricSnapshot Snapshot { get; }
    IReadOnlyList<ModelRouteCandidate> Candidates { get; }
    RouteDecision SelectRoute(ModelRequest request, IReadOnlySet<string>? excludedCandidateKeys = null);
    EscalationDecision DecideEscalation(RouteDecision decision, ModelOutcome outcome, int escalationCount);
    void RecordOutcome(RouteDecision decision, ModelOutcome outcome);
    void UpdateGatewayHealth(IntelligenceGateway gateway, ProviderHealth health);
    void MergeDiscoveredModels(IEnumerable<ModelDescriptor> models);
}

public interface IIntelligenceGatewayProvider : IModelProvider
{
    IntelligenceGateway Gateway { get; }
    string ProviderKey { get; }
    Uri Endpoint { get; }
    ValueTask<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ModelDescriptor>> DiscoverModelsAsync(CancellationToken cancellationToken = default);
}

public sealed class IntelligenceRoutingException : Exception
{
    public IntelligenceRoutingException(RuntimeError error, RouteDecision? decision = null)
        : base(error.Message)
    {
        Error = error;
        Decision = decision;
    }

    public RuntimeError Error { get; }
    public RouteDecision? Decision { get; }
}

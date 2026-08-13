using System.Collections.Concurrent;
using System.Collections.Immutable;
using Abraxius.Protocol;

namespace Abraxius.Models;

/// <summary>
/// Transparent, deterministic route selection. Gateway-internal routing is deliberately opaque to
/// this class: it chooses the peer fabric and route policy, while the selected gateway chooses its
/// own provider deployment.
/// </summary>
public sealed class IntelligenceRouteEngine : IIntelligenceRouteEngine
{
    private readonly RouteScoringWeights _weights;
    private readonly IntelligenceRoutingPolicy _policy;
    private ModelRouteCandidate[] _candidates;
    private readonly ConcurrentDictionary<string, ModelQualityProfile> _quality = new(StringComparer.Ordinal);
    private readonly object _snapshotGate = new();
    private IntelligenceFabricSnapshot _snapshot;

    public IntelligenceRouteEngine(
        IEnumerable<ModelRouteCandidate> candidates,
        IntelligenceRoutingPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _policy = policy ?? new IntelligenceRoutingPolicy();
        _weights = _policy.Weights;
        _candidates = candidates
            .Where(static candidate => candidate.Model is not null)
            .GroupBy(static candidate => candidate.Key, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        _snapshot = CreateSnapshot(null);
    }

    public IntelligenceFabricSnapshot Snapshot
    {
        get
        {
            lock (_snapshotGate)
            {
                return _snapshot;
            }
        }
    }

    public IReadOnlyList<ModelRouteCandidate> Candidates => Volatile.Read(ref _candidates);

    public RouteDecision SelectRoute(ModelRequest request, IReadOnlySet<string>? excludedCandidateKeys = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestPolicy = MergePolicy(_policy.DefaultRequest, request.Policy);
        var evaluations = new List<RouteCandidateEvaluation>(_candidates.Length);
        var eligible = new List<(ModelRouteCandidate Candidate, double Score, decimal? Cost)>(_candidates.Length);

        foreach (var candidate in _candidates)
        {
            var rejection = GetRejection(candidate, request, requestPolicy, excludedCandidateKeys);
            if (rejection is not null)
            {
                evaluations.Add(new RouteCandidateEvaluation(candidate, false, double.NegativeInfinity, rejection.Value, RejectionText(rejection.Value)));
                continue;
            }

            var score = Score(candidate, request, requestPolicy, out var cost);
            evaluations.Add(new RouteCandidateEvaluation(candidate, true, score, Detail: ScoreText(candidate, request, cost)));
            eligible.Add((candidate, score, cost));
        }

        if (eligible.Count == 0)
        {
            var error = new RuntimeError(
                ErrorCategory.Model,
                requestPolicy.Mode == IntelligenceRoutingMode.FreeFirstStrict ? "free_route_unavailable" : "no_eligible_model_route",
                BuildNoRouteMessage(request, requestPolicy, evaluations));
            throw new IntelligenceRoutingException(error);
        }

        var selected = eligible
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Candidate.Key, StringComparer.Ordinal)
            .First();
        var selectedModel = selected.Candidate.Model;
        var decision = new RouteDecision
        {
            RequestId = request.RequestId,
            Tier = selectedModel.Tier,
            Gateway = selectedModel.Gateway,
            Route = selectedModel.Route ?? selectedModel.ModelId,
            SelectedCandidate = selectedModel,
            Candidates = evaluations,
            Reason = BuildReason(request, requestPolicy, selected.Candidate, selected.Score),
            EstimatedCost = selected.Cost,
            Quota = selected.Candidate.Quota,
            Attempt = 1
        };
        lock (_snapshotGate)
        {
            _snapshot = CreateSnapshot(decision);
        }

        return decision;
    }

    public EscalationDecision DecideEscalation(RouteDecision decision, ModelOutcome outcome, int escalationCount)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(outcome);
        var requestPolicy = _policy.DefaultRequest;
        if (outcome.Succeeded && outcome.Verified)
        {
            return new EscalationDecision(EscalationAction.Stay, decision.Tier, null, "verified_success", escalationCount);
        }

        if (outcome.Transient)
        {
            return new EscalationDecision(EscalationAction.SameTierFailover, decision.Tier, decision.Tier, "transient_provider_failure", escalationCount);
        }

        if (escalationCount >= Math.Max(0, requestPolicy.MaximumEscalations))
        {
            return new EscalationDecision(EscalationAction.Abort, decision.Tier, null, "escalation_budget_exhausted", escalationCount);
        }

        var nextTier = NextTier(decision.Tier, requestPolicy.MaximumTier);
        if (nextTier is null)
        {
            return new EscalationDecision(EscalationAction.Abort, decision.Tier, null, "maximum_tier_reached", escalationCount);
        }

        var action = nextTier.Value >= IntelligenceTier.Frontier
            ? EscalationAction.EscalateToSpecialist
            : EscalationAction.EscalateOneTier;
        return new EscalationDecision(action, decision.Tier, nextTier, outcome.Detail ?? "verification_failed", escalationCount + 1);
    }

    public void RecordOutcome(RouteDecision decision, ModelOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(outcome);
        var key = decision.SelectedCandidate.ModelId;
        _quality.AddOrUpdate(
            key,
            _ => UpdateQuality(decision.SelectedCandidate.Quality, outcome),
            (_, previous) => UpdateQuality(previous, outcome));
    }

    public void UpdateGatewayHealth(IntelligenceGateway gateway, ProviderHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        var current = Volatile.Read(ref _candidates);
        var updated = new ModelRouteCandidate[current.Length];
        for (var index = 0; index < current.Length; index++)
        {
            updated[index] = current[index].Model.Gateway == gateway
                ? current[index] with { Model = current[index].Model with { Health = health } }
                : current[index];
        }

        Volatile.Write(ref _candidates, updated);
        lock (_snapshotGate)
        {
            _snapshot = CreateSnapshot(_snapshot.LastDecision);
        }
    }

    public void MergeDiscoveredModels(IEnumerable<ModelDescriptor> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var discovered = models.ToArray();
        if (discovered.Length == 0)
        {
            return;
        }

        var current = Volatile.Read(ref _candidates);
        var keys = new HashSet<string>(current.Select(static candidate => candidate.Key), StringComparer.Ordinal);
        var additions = discovered
            .Select(static model => new ModelRouteCandidate(model))
            .Where(candidate => keys.Add(candidate.Key))
            .ToArray();
        if (additions.Length == 0)
        {
            return;
        }

        var updated = new ModelRouteCandidate[current.Length + additions.Length];
        current.CopyTo(updated, 0);
        additions.CopyTo(updated, current.Length);
        Volatile.Write(ref _candidates, updated);
        lock (_snapshotGate)
        {
            _snapshot = CreateSnapshot(_snapshot.LastDecision);
        }
    }

    private double Score(ModelRouteCandidate candidate, ModelRequest request, IntelligenceRequestPolicy policy, out decimal? estimatedCost)
    {
        var model = candidate.Model;
        estimatedCost = model.EstimatedCostPerRequest;
        var quality = _quality.TryGetValue(model.ModelId, out var observed) ? observed : model.Quality;
        var health = model.Health.Status switch
        {
            ProviderHealthStatus.Healthy => 1.0,
            ProviderHealthStatus.Degraded => 0.45,
            ProviderHealthStatus.Unknown => 0.25,
            _ => 0.0
        };
        var quota = candidate.Quota;
        var quotaScore = quota?.RemainingTokens is { } remaining && quota.LimitTokens is { } limit && limit > 0
            ? Math.Clamp((double)remaining / limit * 100, 0, 100)
            : model.CostClass is ModelCostClass.Zero or ModelCostClass.Included ? 50 : 0;
        var expiryScore = quota?.ResetAt is { } reset
            ? Math.Clamp(100 - Math.Max(0, (reset - DateTimeOffset.UtcNow).TotalHours), 0, 100)
            : 0;
        var latencyPenalty = model.Health.EstimatedLatency?.TotalMilliseconds ?? 0;
        var costPenalty = model.CostClass switch
        {
            ModelCostClass.Zero => 0,
            ModelCostClass.Included => 0.5,
            ModelCostClass.UltraLow => 2,
            ModelCostClass.Low => 4,
            ModelCostClass.Standard => 8,
            ModelCostClass.Premium => 16,
            _ => 10
        };
        var affinity = !string.IsNullOrWhiteSpace(request.SessionKey) &&
            (!string.IsNullOrWhiteSpace(request.Model) &&
             (string.Equals(request.Model, model.ModelId, StringComparison.OrdinalIgnoreCase) ||
              string.Equals(request.Model, model.Route, StringComparison.OrdinalIgnoreCase)))
            ? 1.0
            : 0;
        var taskFit = TaskFit(request.TaskClass, model);
        var tierFit = policy.PreferredTier is { } preferred
            ? 100 - Math.Abs((int)model.Tier - (int)preferred) * 10
            : 0;

        return taskFit * _weights.Capability
            + quality.QualityScore * _weights.Quality
            + quotaScore * _weights.FreeQuota
            + expiryScore * _weights.QuotaExpiry
            + health * _weights.Health
            + tierFit
            + affinity * _weights.Affinity
            + quality.VerificationPassRate * 100 * _weights.Reliability
            - latencyPenalty * _weights.Latency
            - costPenalty * _weights.Cost;
    }

    private static double TaskFit(IntelligenceTaskClass taskClass, ModelDescriptor model)
    {
        return taskClass switch
        {
            IntelligenceTaskClass.CodeGeneration or IntelligenceTaskClass.CodeReview or IntelligenceTaskClass.Debugging
                => model.Capabilities.Coding ? 1 : 0.25,
            IntelligenceTaskClass.Vision => model.Capabilities.Vision ? 1 : 0,
            IntelligenceTaskClass.Architecture or IntelligenceTaskClass.Planning
                => model.Capabilities.Reasoning ? 1 : 0.45,
            IntelligenceTaskClass.LongContext => model.Capabilities.ContextWindow is >= 100_000 ? 1 : 0,
            _ => 0.75
        };
    }

    private RouteRejectionReason? GetRejection(
        ModelRouteCandidate candidate,
        ModelRequest request,
        IntelligenceRequestPolicy policy,
        IReadOnlySet<string>? excluded)
    {
        var model = candidate.Model;
        if (excluded?.Contains(candidate.Key) == true)
        {
            return RouteRejectionReason.ExcludedAfterFailure;
        }

        if (!model.Enabled)
        {
            return RouteRejectionReason.Disabled;
        }

        if (model.Tier > policy.MaximumTier || model.Tier < policy.MinimumQualityTier)
        {
            return RouteRejectionReason.TierAboveMaximum;
        }

        if (policy.Mode == IntelligenceRoutingMode.FreeFirstStrict && model.CostClass is not (ModelCostClass.Zero or ModelCostClass.Included))
        {
            return RouteRejectionReason.PaidInferenceDisabled;
        }

        if (!policy.AllowPaidInference && model.CostClass is not (ModelCostClass.Zero or ModelCostClass.Included))
        {
            return RouteRejectionReason.PaidInferenceDisabled;
        }

        if (_policy.StrictUnknownCost && model.CostClass == ModelCostClass.Unknown && policy.Mode != IntelligenceRoutingMode.Manual)
        {
            return RouteRejectionReason.UnknownCost;
        }

        if (policy.Privacy == PrivacyRoutePolicy.LocalOnly && !model.IsLocal)
        {
            return RouteRejectionReason.PrivacyPolicy;
        }

        if ((request.DataClassification is DataClassification.Secret or DataClassification.LocalOnly) && !model.IsLocal)
        {
            return RouteRejectionReason.PrivacyPolicy;
        }

        if (policy.Privacy == PrivacyRoutePolicy.TrustedProvidersOnly &&
            !policy.TrustedProviders.Contains(model.Provider, StringComparer.OrdinalIgnoreCase))
        {
            return RouteRejectionReason.PrivacyPolicy;
        }

        foreach (var required in request.RequiredCapabilities)
        {
            if (!model.Capabilities.Supports(required))
            {
                return RouteRejectionReason.CapabilityMissing;
            }
        }

        if (request.RequiredContextTokens is { } requiredContext &&
            model.Capabilities.ContextWindow is { } contextWindow && contextWindow < requiredContext)
        {
            return RouteRejectionReason.ContextTooSmall;
        }

        if (request.RequiredContextTokens is { } contextRequired && model.Capabilities.ContextWindow is null)
        {
            return RouteRejectionReason.ContextTooSmall;
        }

        if (model.Health.Status is ProviderHealthStatus.Unavailable or ProviderHealthStatus.RateLimited)
        {
            return RouteRejectionReason.Unhealthy;
        }

        if (candidate.Quota?.IsExhausted == true)
        {
            return RouteRejectionReason.QuotaExhausted;
        }

        var estimatedCost = model.EstimatedCostPerRequest;
        if (policy.MaximumEstimatedCost is { } maximum && estimatedCost is { } cost && cost > maximum)
        {
            return RouteRejectionReason.CostAboveBudget;
        }

        if (policy.MaximumEstimatedCost is not null && estimatedCost is null && model.CostClass is not (ModelCostClass.Zero or ModelCostClass.Included))
        {
            return RouteRejectionReason.UnknownCost;
        }

        if (policy.Mode == IntelligenceRoutingMode.Manual)
        {
            var target = policy.ManualModelId ?? request.Model;
            if (!string.IsNullOrWhiteSpace(target) &&
                !string.Equals(target, model.ModelId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target, model.Route, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(target, candidate.Key, StringComparison.OrdinalIgnoreCase))
            {
                return RouteRejectionReason.ManualPolicy;
            }
        }

        return null;
    }

    private static IntelligenceRequestPolicy MergePolicy(IntelligenceRequestPolicy defaults, IntelligenceRequestPolicy request)
    {
        // Request policies are already concrete records. The default is used only when a caller
        // submits the default policy object; explicit request values remain authoritative.
        return request == new IntelligenceRequestPolicy() ? defaults : request;
    }

    private IntelligenceFabricSnapshot CreateSnapshot(RouteDecision? lastDecision)
    {
        var healthyGateways = _candidates
            .Where(static candidate => candidate.Model.Health.Status is ProviderHealthStatus.Healthy or ProviderHealthStatus.Unknown)
            .Select(static candidate => candidate.Model.Gateway)
            .Distinct()
            .Count();
        return new IntelligenceFabricSnapshot(
            _policy.DefaultRequest.Mode,
            _candidates.Length,
            healthyGateways,
            _candidates.Count(static candidate => candidate.Model.CostClass == ModelCostClass.Zero),
            _candidates.Count(static candidate => candidate.Model.CostClass == ModelCostClass.Included),
            _candidates.Count(static candidate => candidate.Model.CostClass is ModelCostClass.UltraLow or ModelCostClass.Low or ModelCostClass.Standard or ModelCostClass.Premium),
            _candidates.Count(static candidate => candidate.Model.Tier >= IntelligenceTier.Frontier),
            lastDecision,
            DateTimeOffset.UtcNow);
    }

    private static ModelQualityProfile UpdateQuality(ModelQualityProfile previous, ModelOutcome outcome)
    {
        var observations = previous.Observations + 1;
        var alpha = 1d / observations;
        return previous with
        {
            Observations = observations,
            SuccessRate = previous.SuccessRate + ((outcome.Succeeded ? 1 : 0) - previous.SuccessRate) * alpha,
            VerificationPassRate = previous.VerificationPassRate + ((outcome.Verified ? 1 : 0) - previous.VerificationPassRate) * alpha,
            QualityScore = previous.QualityScore + (((outcome.Succeeded && outcome.Verified ? 100 : 20)) - previous.QualityScore) * alpha
        };
    }

    private static IntelligenceTier? NextTier(IntelligenceTier current, IntelligenceTier maximum)
    {
        var next = current switch
        {
            IntelligenceTier.Deterministic => IntelligenceTier.Free,
            IntelligenceTier.Free => IntelligenceTier.Included,
            IntelligenceTier.Included => IntelligenceTier.UltraLow,
            IntelligenceTier.UltraLow => IntelligenceTier.Standard,
            IntelligenceTier.Standard => IntelligenceTier.Frontier,
            IntelligenceTier.Frontier => IntelligenceTier.Specialist,
            _ => (IntelligenceTier?)null
        };
        return next is { } value && value <= maximum ? value : null;
    }

    private static string BuildNoRouteMessage(ModelRequest request, IntelligenceRequestPolicy policy, IReadOnlyList<RouteCandidateEvaluation> evaluations)
    {
        var reasons = evaluations
            .Where(static evaluation => evaluation.Rejection is not null)
            .GroupBy(static evaluation => evaluation.Rejection!.Value)
            .OrderByDescending(static group => group.Count())
            .Take(3)
            .Select(static group => $"{group.Key}={group.Count()}");
        return $"No eligible intelligence route for '{request.TaskClass}' under {policy.Mode} (max tier {policy.MaximumTier}). Rejections: {string.Join(", ", reasons)}.";
    }

    private static string BuildReason(ModelRequest request, IntelligenceRequestPolicy policy, ModelRouteCandidate candidate, double score) =>
        $"{policy.Mode} selected {candidate.Model.Gateway}/{candidate.Model.Route ?? candidate.Model.ModelId}; score={score:F2}; task={request.TaskClass}; tier={candidate.Model.Tier}; cost={candidate.Model.CostClass}.";

    private static string ScoreText(ModelRouteCandidate candidate, ModelRequest request, decimal? cost) =>
        $"eligible; gateway={candidate.Model.Gateway}; tier={candidate.Model.Tier}; cost={candidate.Model.CostClass}; estimated={cost?.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}; task={request.TaskClass}";

    private static string RejectionText(RouteRejectionReason reason) => reason switch
    {
        RouteRejectionReason.PaidInferenceDisabled => "paid inference is disabled by policy",
        RouteRejectionReason.ContextTooSmall => "context window is insufficient",
        RouteRejectionReason.CapabilityMissing => "required capability is not advertised",
        RouteRejectionReason.QuotaExhausted => "configured quota is exhausted",
        RouteRejectionReason.Unhealthy => "gateway/provider is unavailable or rate limited",
        RouteRejectionReason.PrivacyPolicy => "privacy route policy excludes this provider",
        _ => reason.ToString()
    };
}

/// <summary>Policy state machine used by callers that have verification feedback.</summary>
public sealed class EscalationController : IEscalationController
{
    private readonly IIntelligenceRouteEngine _engine;

    public EscalationController(IIntelligenceRouteEngine engine) => _engine = engine;

    public EscalationDecision Decide(RouteDecision decision, ModelOutcome outcome, int escalationCount) =>
        _engine.DecideEscalation(decision, outcome, escalationCount);
}

public interface IEscalationController
{
    EscalationDecision Decide(RouteDecision decision, ModelOutcome outcome, int escalationCount);
}

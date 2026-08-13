using Abraxius.Models;
using Abraxius.Protocol;
using Xunit;
using ModelUsage = Abraxius.Models.ModelUsage;

namespace Abraxius.Runtime.Tests;

public sealed class IntelligenceRoutingTests
{
    [Fact]
    public async Task FreeRouteSucceedsWithoutFrontierCall()
    {
        var free = Descriptor("free-coder", IntelligenceGateway.OmniRoute, IntelligenceTier.Free, ModelCostClass.Zero, coding: true);
        var frontier = Descriptor("frontier-coder", IntelligenceGateway.Frontier, IntelligenceTier.Frontier, ModelCostClass.Premium, coding: true, reasoning: true);
        var calls = 0;
        var freeProvider = new FakeOmniRouteModelProvider([free], (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return ValueTask.FromResult(new ModelResult("free result", null, free.ModelId, new ModelUsage(2, 3, 0), TimeSpan.FromMilliseconds(2), "omniroute"));
        });
        var frontierProvider = new FakeFrontierModelProvider([frontier], (_, _) =>
            ValueTask.FromException<ModelResult>(new InvalidOperationException("frontier must not be called")));
        await using var routed = new RoutedModelProvider(
            new IntelligenceRouteEngine([
                new ModelRouteCandidate(free),
                new ModelRouteCandidate(frontier)
            ], new IntelligenceRoutingPolicy
            {
                DefaultRequest = new IntelligenceRequestPolicy
                {
                    Mode = IntelligenceRoutingMode.FreeFirstStrict,
                    MaximumTier = IntelligenceTier.Frontier
                }
            }),
            [freeProvider, frontierProvider]);

        var result = await routed.InferAsync(new ModelRequest("find the error")
        {
            TaskClass = IntelligenceTaskClass.CodeSearch,
            RequiredCapabilities = [ModelCapability.Coding]
        });

        Assert.Equal("free result", result.Text);
        Assert.Equal(1, calls);
        Assert.Equal(IntelligenceTier.Free, result.Route!.Tier);
    }

    [Fact]
    public void CapabilityAndContextFiltersRejectImpossibleCandidatesBeforeScoring()
    {
        var shortModel = Descriptor("short", IntelligenceGateway.OmniRoute, IntelligenceTier.Free, ModelCostClass.Zero, contextWindow: 32_000);
        var longModel = Descriptor("long", IntelligenceGateway.LiteLlm, IntelligenceTier.Standard, ModelCostClass.Low, contextWindow: 128_000, reasoning: true);
        var engine = new IntelligenceRouteEngine([
            new ModelRouteCandidate(shortModel),
            new ModelRouteCandidate(longModel)
        ], new IntelligenceRoutingPolicy
        {
            DefaultRequest = new IntelligenceRequestPolicy { AllowPaidInference = true }
        });

        var decision = engine.SelectRoute(new ModelRequest("architecture")
        {
            TaskClass = IntelligenceTaskClass.Architecture,
            RequiredCapabilities = [ModelCapability.Reasoning],
            RequiredContextTokens = 80_000
        });

        Assert.Equal("long", decision.SelectedCandidate.ModelId);
        Assert.Contains(decision.Candidates, candidate => candidate.Candidate.Model.ModelId == "short" && candidate.Rejection == RouteRejectionReason.CapabilityMissing);
    }

    [Fact]
    public void StrictFreePolicyReturnsStructuredNoRouteWhenQuotaIsExhausted()
    {
        var free = Descriptor("free", IntelligenceGateway.OmniRoute, IntelligenceTier.Free, ModelCostClass.Zero);
        var engine = new IntelligenceRouteEngine([
            new ModelRouteCandidate(free, new QuotaState { RemainingTokens = 0, LimitTokens = 1_000, Source = QuotaSource.Gateway })
        ], new IntelligenceRoutingPolicy
        {
            DefaultRequest = new IntelligenceRequestPolicy
            {
                Mode = IntelligenceRoutingMode.FreeFirstStrict,
                MaximumTier = IntelligenceTier.Free
            }
        });

        var exception = Assert.Throws<IntelligenceRoutingException>(() => engine.SelectRoute(new ModelRequest("summarize")));

        Assert.Equal("free_route_unavailable", exception.Error.Code);
        Assert.Contains("QuotaExhausted", exception.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PaidInferenceRequiresExplicitPermissionAndCeiling()
    {
        var cheap = Descriptor("cheap", IntelligenceGateway.LiteLlm, IntelligenceTier.UltraLow, ModelCostClass.UltraLow, costPerRequest: 0.02m);
        var engine = new IntelligenceRouteEngine([new ModelRouteCandidate(cheap)]);

        var denied = Assert.Throws<IntelligenceRoutingException>(() => engine.SelectRoute(new ModelRequest("repair")
        {
            Policy = new IntelligenceRequestPolicy { MaximumTier = IntelligenceTier.UltraLow }
        }));
        Assert.Contains("PaidInferenceDisabled", denied.Error.Message, StringComparison.Ordinal);

        var allowed = engine.SelectRoute(new ModelRequest("repair")
        {
            Policy = new IntelligenceRequestPolicy
            {
                MaximumTier = IntelligenceTier.UltraLow,
                AllowPaidInference = true,
                MaximumEstimatedCost = 0.05m
            }
        });
        Assert.Equal("cheap", allowed.SelectedCandidate.ModelId);
    }

    [Fact]
    public void PrivacyAndManualSelectionAreHardConstraints()
    {
        var local = Descriptor("local", IntelligenceGateway.Local, IntelligenceTier.Free, ModelCostClass.Zero);
        var remote = Descriptor("remote", IntelligenceGateway.OmniRoute, IntelligenceTier.Free, ModelCostClass.Zero);
        var engine = new IntelligenceRouteEngine([
            new ModelRouteCandidate(local),
            new ModelRouteCandidate(remote)
        ]);

        var localDecision = engine.SelectRoute(new ModelRequest("private")
        {
            Policy = new IntelligenceRequestPolicy { Privacy = PrivacyRoutePolicy.LocalOnly }
        });
        Assert.Equal("local", localDecision.SelectedCandidate.ModelId);

        var manual = engine.SelectRoute(new ModelRequest("manual")
        {
            Policy = new IntelligenceRequestPolicy
            {
                Mode = IntelligenceRoutingMode.Manual,
                ManualModelId = "remote"
            }
        });
        Assert.Equal("remote", manual.SelectedCandidate.ModelId);
    }

    [Fact]
    public void TransientFailureUsesSameTierFailoverBeforeEscalation()
    {
        var engine = new IntelligenceRouteEngine([], new IntelligenceRoutingPolicy
        {
            DefaultRequest = new IntelligenceRequestPolicy { MaximumTier = IntelligenceTier.Frontier }
        });
        var decision = new RouteDecision
        {
            RequestId = ModelRequestId.New(),
            Tier = IntelligenceTier.Free,
            Gateway = IntelligenceGateway.OmniRoute,
            Route = "free",
            SelectedCandidate = Descriptor("free", IntelligenceGateway.OmniRoute, IntelligenceTier.Free, ModelCostClass.Zero),
            Candidates = []
        };

        var failover = engine.DecideEscalation(decision, new ModelOutcome(false, false, true, TimeSpan.Zero), 0);
        var quality = engine.DecideEscalation(decision, new ModelOutcome(false, false, false, TimeSpan.Zero, Detail: "verification_failed"), 0);

        Assert.Equal(EscalationAction.SameTierFailover, failover.Action);
        Assert.Equal(EscalationAction.EscalateOneTier, quality.Action);
        Assert.Equal(IntelligenceTier.Included, quality.NextTier);
    }

    [Fact]
    public async Task StreamingCarriesRouteAndCanBeCancelled()
    {
        var model = Descriptor("free-stream", IntelligenceGateway.OmniRoute, IntelligenceTier.Free, ModelCostClass.Zero);
        await using var routed = new RoutedModelProvider(
            new IntelligenceRouteEngine([new ModelRouteCandidate(model)]),
            [new FakeOmniRouteModelProvider([model])]);
        using var cancellation = new CancellationTokenSource();
        var events = new List<ModelStreamEvent>();

        await foreach (var item in routed.StreamAsync(new ModelRequest("stream"), cancellation.Token))
        {
            events.Add(item);
        }

        var completed = Assert.IsType<ModelStreamEvent.Completed>(events[^1]);
        Assert.Equal("free-stream", completed.Result.Route!.SelectedCandidate.ModelId);
        Assert.Contains(events, item => item is ModelStreamEvent.Started);
    }

    [Fact]
    public async Task InvalidStructuredOutputUsesBoundedQualityEscalation()
    {
        var free = Descriptor("free-json", IntelligenceGateway.OmniRoute, IntelligenceTier.Free, ModelCostClass.Zero);
        var cheap = Descriptor("cheap-json", IntelligenceGateway.LiteLlm, IntelligenceTier.UltraLow, ModelCostClass.UltraLow, costPerRequest: 0.01m);
        var freeCalls = 0;
        var freeProvider = new FakeOmniRouteModelProvider([free], (_, _) =>
        {
            Interlocked.Increment(ref freeCalls);
            return ValueTask.FromResult(new ModelResult("not json", "not json", free.ModelId, null, TimeSpan.Zero, "omniroute"));
        });
        var cheapProvider = new FakeLiteLlmModelProvider([cheap], (_, _) =>
            ValueTask.FromResult(new ModelResult("{\"ok\":true}", "{\"ok\":true}", cheap.ModelId, null, TimeSpan.Zero, "litellm")));
        await using var routed = new RoutedModelProvider(
            new IntelligenceRouteEngine([
                new ModelRouteCandidate(free),
                new ModelRouteCandidate(cheap)
            ], new IntelligenceRoutingPolicy
            {
                DefaultRequest = new IntelligenceRequestPolicy
                {
                    Mode = IntelligenceRoutingMode.FreeFirst,
                    AllowPaidInference = true,
                    MaximumTier = IntelligenceTier.UltraLow,
                    MaximumEscalations = 1
                }
            }),
            [freeProvider, cheapProvider]);

        var result = await routed.InferAsync(new ModelRequest("structured")
        {
            ExpectedJsonSchema = "{\"type\":\"object\"}"
        });

        Assert.Equal("cheap-json", result.Model);
        Assert.Equal(1, freeCalls);
        Assert.Equal(IntelligenceGateway.LiteLlm, result.Route!.Gateway);
    }

    [Fact]
    public void ExecutionModelBudgetRejectsTheNextReservation()
    {
        var executionId = ExecutionId.New();
        var ledger = new IntelligenceBudgetLedger();

        Assert.True(ledger.TryReserve(
            executionId,
            maximumCalls: 1,
            maximumCost: 0.01m,
            maximumPremiumTokens: null,
            estimatedCost: 0.01m,
            estimatedPremiumTokens: 0,
            out var firstError));
        Assert.Null(firstError);

        Assert.False(ledger.TryReserve(
            executionId,
            maximumCalls: 1,
            maximumCost: 0.01m,
            maximumPremiumTokens: null,
            estimatedCost: 0,
            estimatedPremiumTokens: 0,
            out var error));
        Assert.NotNull(error);
        Assert.Equal("model_call_budget_exhausted", error!.Code);
        Assert.Equal(1, ledger.Get(executionId).ModelCalls);
    }

    [Fact]
    public async Task RouteCapacityLimitsConcurrentProviderCalls()
    {
        var model = Descriptor("capacity-one", IntelligenceGateway.OmniRoute, IntelligenceTier.Free, ModelCostClass.Zero) with
        {
            Capacity = new RouteCapacity { MaxConcurrentRequests = 1 }
        };
        var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;
        var provider = new FakeOmniRouteModelProvider([model], async (_, cancellationToken) =>
        {
            var current = Interlocked.Increment(ref active);
            while (true)
            {
                var observed = Volatile.Read(ref maximumActive);
                if (current <= observed || Interlocked.CompareExchange(ref maximumActive, current, observed) == observed)
                {
                    break;
                }
            }
            firstStarted.TrySetResult(true);
            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref active);
            return new ModelResult("ok", null, model.ModelId, null, TimeSpan.Zero, "omniroute");
        });
        await using var routed = new RoutedModelProvider(
            new IntelligenceRouteEngine([new ModelRouteCandidate(model)]),
            [provider]);

        var first = routed.InferAsync(new ModelRequest("first")).AsTask();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var second = routed.InferAsync(new ModelRequest("second")).AsTask();
        await Task.Delay(20);

        Assert.False(second.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref maximumActive));

        release.TrySetResult(true);
        await Task.WhenAll(first, second);
        Assert.Equal(1, Volatile.Read(ref maximumActive));
    }

    [Fact]
    public void ContextBudgeterPrefersHighPriorityEvidenceAndProducesStableIdentity()
    {
        var first = EvidenceId.New();
        var second = EvidenceId.New();
        var budgeter = new DefaultContextBudgeter();

        var package = budgeter.Build(new ContextBudgetRequest(
            "diagnose",
            [
                new ContextEvidenceItem(first, new string('a', 200), Priority: 10),
                new ContextEvidenceItem(second, new string('b', 4_000), Priority: 1)
            ],
            ContextWindow: 600,
            ReservedOutputTokens: 100));

        Assert.Contains(first, package.IncludedEvidence);
        Assert.DoesNotContain(second, package.IncludedEvidence);
        Assert.Equal(package.ContentHash, budgeter.Build(new ContextBudgetRequest(
            "diagnose",
            [new ContextEvidenceItem(first, new string('a', 200), Priority: 10)],
            ContextWindow: 600,
            ReservedOutputTokens: 100)).ContentHash);
    }

    private static ModelDescriptor Descriptor(
        string modelId,
        IntelligenceGateway gateway,
        IntelligenceTier tier,
        ModelCostClass cost,
        bool coding = false,
        bool reasoning = false,
        int? contextWindow = 128_000,
        decimal? costPerRequest = null) => new()
        {
            ModelId = modelId,
            Provider = gateway.ToString(),
            Gateway = gateway,
            Route = modelId,
            Tier = tier,
            CostClass = cost,
            EstimatedCostPerRequest = costPerRequest,
            Capabilities = new ModelCapabilities
            {
                Streaming = true,
                StructuredOutput = true,
                Coding = coding,
                Reasoning = reasoning,
                ContextWindow = contextWindow
            },
            Health = new ProviderHealth { Status = ProviderHealthStatus.Healthy }
        };
}

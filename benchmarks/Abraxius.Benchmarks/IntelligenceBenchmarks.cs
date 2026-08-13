using Abraxius.Models;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class IntelligenceBenchmarks
{
    [Params(10, 100, 500)]
    public int CandidateCount { get; set; }

    private IntelligenceRouteEngine _engine = null!;
    private ModelRequest _request = null!;

    [GlobalSetup]
    public void Setup()
    {
        var candidates = new List<ModelRouteCandidate>(CandidateCount);
        for (var index = 0; index < CandidateCount; index++)
        {
            candidates.Add(new ModelRouteCandidate(new ModelDescriptor
            {
                ModelId = $"free-{index}",
                Provider = "benchmark",
                Gateway = index % 2 == 0 ? IntelligenceGateway.OmniRoute : IntelligenceGateway.LiteLlm,
                Route = $"free-{index}",
                Tier = IntelligenceTier.Free,
                CostClass = ModelCostClass.Zero,
                Capabilities = new ModelCapabilities
                {
                    Streaming = true,
                    StructuredOutput = true,
                    Coding = true,
                    ContextWindow = 128_000
                },
                Health = new ProviderHealth { Status = ProviderHealthStatus.Healthy }
            }));
        }

        _engine = new IntelligenceRouteEngine(candidates);
        _request = new ModelRequest("find the source")
        {
            TaskClass = IntelligenceTaskClass.CodeSearch,
            RequiredCapabilities = [ModelCapability.Coding]
        };
    }

    [Benchmark]
    public RouteDecision SelectRoute() => _engine.SelectRoute(_request);
}


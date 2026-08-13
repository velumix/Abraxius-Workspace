using Abraxius.Security;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class SecurityBenchmarks
{
    private readonly DeterministicPolicyEngine _policy = new();
    private readonly DeterministicRiskClassifier _risk = new();
    private readonly AuthorizationRequest _read;
    private readonly AuthorizationRequest _network;

    public SecurityBenchmarks()
    {
        var subject = SecuritySubject.System("benchmark");
        var root = Path.GetFullPath(Environment.CurrentDirectory);
        var file = new SecurityResource(ResourceKind.File, new Uri(Path.Combine(root, "benchmark.cs")).AbsoluteUri, Path.Combine(root, "benchmark.cs"));
        _read = new AuthorizationRequest(subject, new ProposedAction(ActionId.New(), subject, "filesystem", SecurityActions.FileRead, file),
            new AuthorizationContext(WorkspaceRoot: root), DateTimeOffset.UtcNow);
        var endpoint = new SecurityResource(ResourceKind.Network, "https://api.github.com/repos", Host: "api.github.com", Port: 443);
        _network = new AuthorizationRequest(subject, new ProposedAction(ActionId.New(), subject, "network", SecurityActions.NetworkHttpGet, endpoint, ExternalEffect: true),
            new AuthorizationContext(), DateTimeOffset.UtcNow);
    }

    [Benchmark(Baseline = true)]
    public AuthorizationOutcome WorkspaceRead() => _policy.Evaluate(_read, _risk.Classify(_read.Action)).Decision.Outcome;

    [Benchmark]
    public AuthorizationOutcome ExternalNetwork() => _policy.Evaluate(_network, _risk.Classify(_network.Action)).Decision.Outcome;
}

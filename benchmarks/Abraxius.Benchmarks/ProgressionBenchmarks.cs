using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Progression;
using Abraxius.Protocol;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class ProgressionBenchmarks
{
    private readonly ProgressionRulesV1 _rules = new();
    private readonly ProgressionTrajectory _trajectory;
    private readonly ProgressionSnapshot _snapshot;
    private readonly RewardEvaluator _evaluator;
    private readonly AchievementEngine _achievements = new();

    public ProgressionBenchmarks()
    {
        _trajectory = new ProgressionTrajectory(TrajectoryId.New(), MissionId.New(), "verify concurrent scheduler", "benchmark-state", DateTimeOffset.UtcNow,
            MissionState.Succeeded, RewardEligibility.Ineligible, VerificationStrength.IndependentlyVerified, 24, 10, 8, 3, 3, 3, 0, 0,
            false, true, false, true, false, true,
            [new SpecialistContributionFacts(SpecialistRole.Investigator, 1, UsefulEvidence: 4, RootCauseConfirmed: true), new SpecialistContributionFacts(SpecialistRole.Verifier, 1, IndependentVerification: true)],
            [], [EvidenceId.New()], ["scheduler", "runtime", "tests"]);
        _snapshot = ProgressionSnapshot.Empty(_rules);
        _evaluator = new RewardEvaluator(_rules);
    }

    [Benchmark(Baseline = true)]
    public MissionRewardRecord EvaluateReward() => _evaluator.Evaluate(_trajectory, _snapshot);

    [Benchmark]
    public ImmutableArray<AchievementId> EvaluateAchievements()
    {
        var reward = _evaluator.Evaluate(_trajectory, _snapshot);
        return _achievements.Evaluate(_trajectory, reward, _snapshot);
    }
}

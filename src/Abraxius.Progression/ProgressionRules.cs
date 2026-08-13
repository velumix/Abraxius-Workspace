using System.Collections.Immutable;
using Abraxius.Agents;

namespace Abraxius.Progression;

public interface IProgressionRules
{
    ProgressionRulesVersion Version { get; }
    int MaximumOperatorLevel { get; }
    long ExperienceRequiredForLevel(int level);
    int LevelForExperience(long experience);
    int SpecialistLevelForExperience(long experience);
    int BaseExperience(ProgressionTrajectory trajectory);
    int TrustedSkillUsesForMastery { get; }
    double SkillReliabilityForMastery { get; }
}

public sealed class ProgressionRulesV1 : IProgressionRules
{
    public ProgressionRulesVersion Version => new(1);
    public int MaximumOperatorLevel => 100;
    public int TrustedSkillUsesForMastery => 12;
    public double SkillReliabilityForMastery => .85;

    public long ExperienceRequiredForLevel(int level)
    {
        level = Math.Clamp(level, 1, MaximumOperatorLevel);
        return 100L + (35L * (level - 1)) + (2L * (level - 1) * (level - 1));
    }

    public int LevelForExperience(long experience)
    {
        var remaining = Math.Max(0, experience);
        for (var level = 1; level < MaximumOperatorLevel; level++)
        {
            var required = ExperienceRequiredForLevel(level);
            if (remaining < required) return level;
            remaining -= required;
        }
        return MaximumOperatorLevel;
    }

    public int SpecialistLevelForExperience(long experience)
    {
        var remaining = Math.Max(0, experience);
        for (var level = 1; level < 100; level++)
        {
            var required = 75L + (30L * (level - 1)) + ((level - 1L) * (level - 1L));
            if (remaining < required) return level;
            remaining -= required;
        }
        return 100;
    }

    public int BaseExperience(ProgressionTrajectory trajectory)
    {
        if (trajectory.IsReplay || trajectory.IsBenchmarkOrTest || trajectory.Eligibility == RewardEligibility.Ineligible) return 0;
        if (trajectory.Succeeded) return 80 + Math.Min(220, trajectory.MeaningfulNodes * 8);
        return trajectory.SafeEvidenceRefs.Length > 0 ? 20 : 0;
    }
}

public interface IMissionDifficultyEvaluator
{
    DifficultyEvaluation Evaluate(ProgressionTrajectory trajectory);
}

public sealed record DifficultyEvaluation(double Score, MissionDifficultyClass Class, double RiskScore, ImmutableArray<RewardFactor> Factors);

public sealed class MissionDifficultyEvaluator : IMissionDifficultyEvaluator
{
    public DifficultyEvaluation Evaluate(ProgressionTrajectory trajectory)
    {
        var node = Math.Min(1, trajectory.MeaningfulNodes / 24d);
        var depth = Math.Min(1, trajectory.CriticalPathDepth / 12d);
        var branches = Math.Min(1, trajectory.MeaningfulParallelBranches / 10d);
        var domains = Math.Min(1, Math.Max(trajectory.DomainCount, trajectory.SafeDomains.Length) / 4d);
        var mutationRisk = Math.Min(1, trajectory.MutationNodes / 6d);
        var verification = Math.Min(1, trajectory.VerificationNodes / 4d);
        var score = Math.Clamp((node * .20) + (depth * .22) + (branches * .16) + (domains * .18) + (mutationRisk * .14) + (verification * .10), 0, 1);
        var risk = Math.Clamp((mutationRisk * .65) + (domains * .20) + (depth * .15), 0, 1);
        var kind = score switch { < .18 => MissionDifficultyClass.Routine, < .38 => MissionDifficultyClass.Moderate, < .60 => MissionDifficultyClass.Advanced, < .82 => MissionDifficultyClass.Expert, _ => MissionDifficultyClass.Extreme };
        var factors = ImmutableArray.Create(
            new RewardFactor("meaningful graph", node, $"{trajectory.MeaningfulNodes} meaningful nodes; trivial fan-out is excluded"),
            new RewardFactor("critical path", depth, $"dependency depth {trajectory.CriticalPathDepth}"),
            new RewardFactor("parallel structure", branches, $"{trajectory.MeaningfulParallelBranches} independently necessary branches"),
            new RewardFactor("domains", domains, $"{Math.Max(trajectory.DomainCount, trajectory.SafeDomains.Length)} technical domains"),
            new RewardFactor("mutation risk", mutationRisk, $"{trajectory.MutationNodes} mutation nodes"),
            new RewardFactor("verification", verification, $"{trajectory.VerificationNodes} verification nodes"));
        return new(score, kind, risk, factors);
    }
}

public interface ISpecialistContributionEvaluator
{
    IReadOnlyDictionary<SpecialistRole, int> Evaluate(ProgressionTrajectory trajectory, int operatorXp);
}

public sealed class SpecialistContributionEvaluator : ISpecialistContributionEvaluator
{
    public IReadOnlyDictionary<SpecialistRole, int> Evaluate(ProgressionTrajectory trajectory, int operatorXp)
    {
        if (operatorXp <= 0) return ImmutableDictionary<SpecialistRole, int>.Empty;
        var weights = new Dictionary<SpecialistRole, double>();
        foreach (var contribution in trajectory.SafeSpecialistContributions)
        {
            if (contribution.SuccessfulAssignments + contribution.FailedAssignments == 0) continue;
            double weight = contribution.SuccessfulAssignments;
            weight += Math.Min(3, contribution.UsefulEvidence) * .20;
            if (contribution.RootCauseConfirmed) weight += .8;
            if (contribution.SelectedImplementation) weight += 1;
            if (contribution.IndependentVerification) weight += .8;
            weight += Math.Min(2, contribution.DefectsCaught) * .45;
            weight += Math.Min(2, contribution.Recoveries) * .35;
            weights[contribution.Role] = weights.GetValueOrDefault(contribution.Role) + weight;
        }
        var total = weights.Values.Sum();
        if (total <= 0) return ImmutableDictionary<SpecialistRole, int>.Empty;
        return weights.ToDictionary(static item => item.Key, item => Math.Max(1, (int)Math.Round(operatorXp * .65 * item.Value / total, MidpointRounding.AwayFromZero)));
    }
}

public interface IRewardEvaluator
{
    MissionRewardRecord Evaluate(ProgressionTrajectory trajectory, ProgressionSnapshot current);
}

public sealed class RewardEvaluator : IRewardEvaluator
{
    private readonly IProgressionRules _rules;
    private readonly IMissionDifficultyEvaluator _difficulty;
    private readonly ISpecialistContributionEvaluator _contributions;

    public RewardEvaluator(IProgressionRules rules, IMissionDifficultyEvaluator? difficulty = null, ISpecialistContributionEvaluator? contributions = null)
    {
        _rules = rules;
        _difficulty = difficulty ?? new MissionDifficultyEvaluator();
        _contributions = contributions ?? new SpecialistContributionEvaluator();
    }

    public MissionRewardRecord Evaluate(ProgressionTrajectory trajectory, ProgressionSnapshot current)
    {
        var difficulty = _difficulty.Evaluate(trajectory);
        var baseXp = _rules.BaseExperience(trajectory);
        var verification = trajectory.Verification switch
        {
            VerificationStrength.Unverified => .15,
            VerificationStrength.Inconclusive => .35,
            VerificationStrength.Verified => 1,
            VerificationStrength.RegressionCovered => 1.12,
            VerificationStrength.IndependentlyVerified => 1.20,
            _ => 0
        };
        var efficiency = Math.Clamp(1.10 - (trajectory.Replans * .06) - (Math.Max(0, trajectory.DistinctFailedApproaches - 2) * .03) + (trajectory.UsedTrustedSkill ? .08 : 0), .65, 1.18);
        var novelty = trajectory.UsedTrustedSkill ? 1 : 1.05;
        var duplicateCount = current.RecentEvents.Count(item => item.Kind == ProgressionEventKind.MissionRewarded && item.Subject == trajectory.StateFingerprint);
        var duplicate = duplicateCount switch { 0 => 1, 1 => .15, _ => 0 };
        var eligibility = trajectory.IsReplay || trajectory.IsBenchmarkOrTest ? RewardEligibility.Ineligible : trajectory.Eligibility;
        var eligibilityFactor = eligibility switch { RewardEligibility.Eligible => 1, RewardEligibility.Reduced => .25, _ => 0 };
        var difficultyMultiplier = .85 + difficulty.Score;
        var finalXp = (int)Math.Round(baseXp * difficultyMultiplier * verification * efficiency * novelty * duplicate * eligibilityFactor, MidpointRounding.AwayFromZero);
        var additionalFactors = ImmutableArray.Create(
            new RewardFactor("verification", verification, trajectory.Verification.ToString()),
            new RewardFactor("efficiency", efficiency, trajectory.UsedTrustedSkill ? "validated procedure reused; replans remain bounded" : $"{trajectory.Replans} replans"),
            new RewardFactor("novelty", novelty, trajectory.UsedTrustedSkill ? "known procedure correctly reused" : "no trusted procedure matched"),
            new RewardFactor("duplicate", duplicate, duplicate == 1 ? "new project-state outcome" : duplicate == 0 ? "repeated unchanged outcome is ineligible" : "one reduced repeat"),
            new RewardFactor("eligibility", eligibilityFactor, eligibility.ToString()));
        var factors = difficulty.Factors.AddRange(additionalFactors);
        var specialistXp = _contributions.Evaluate(trajectory, finalXp);
        var skillXp = trajectory.SafeSkillUses.Where(static use => use.Verified && use.Meaningful)
            .GroupBy(static use => $"{use.SkillId}/{use.Version}", StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, group => Math.Max(1, (int)Math.Round(finalXp * .12 / Math.Max(1, trajectory.SafeSkillUses.Length))));
        return new(RewardRecordId.For(trajectory.Id, _rules.Version), trajectory.MissionId, trajectory.Id, _rules.Version, baseXp,
            difficulty.Score, difficulty.Class, verification, efficiency, novelty, difficulty.RiskScore, eligibility, Math.Max(0, finalXp), specialistXp,
            skillXp, [], factors, trajectory.CompletedAt, trajectory.SafeEvidenceRefs, trajectory);
    }
}

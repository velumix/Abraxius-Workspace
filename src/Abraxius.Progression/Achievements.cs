using System.Collections.Immutable;
using Abraxius.Agents;

namespace Abraxius.Progression;

public abstract record AchievementCriterion
{
    public abstract bool IsSatisfied(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot);
    public virtual long Progress(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => IsSatisfied(trajectory, reward, snapshot) ? 1 : 0;
}

public sealed record AllCriterion(ImmutableArray<AchievementCriterion> Criteria) : AchievementCriterion
{
    public override bool IsSatisfied(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => Criteria.All(item => item.IsSatisfied(trajectory, reward, snapshot));
}
public sealed record VerifiedMissionCriterion : AchievementCriterion
{
    public override bool IsSatisfied(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => trajectory.Succeeded && trajectory.Verification >= VerificationStrength.Verified && reward.OperatorXp > 0;
}
public sealed record MissionThresholdCriterion(Func<ProgressionTrajectory, long> Selector, long Threshold) : AchievementCriterion
{
    public override bool IsSatisfied(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => Selector(trajectory) >= Threshold;
    public override long Progress(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => Math.Min(Threshold, Selector(trajectory));
}
public sealed record CareerThresholdCriterion(Func<CareerStatistics, long> Selector, long Threshold) : AchievementCriterion
{
    public override bool IsSatisfied(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => Selector(snapshot.Career) >= Threshold;
    public override long Progress(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => Math.Min(Threshold, Selector(snapshot.Career));
}
public sealed record SpecialistFactCriterion(SpecialistRole Role, Func<SpecialistContributionFacts, bool> Predicate) : AchievementCriterion
{
    public override bool IsSatisfied(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => trajectory.SafeSpecialistContributions.Any(item => item.Role == Role && Predicate(item));
}
public sealed record TrajectoryPredicateCriterion(Func<ProgressionTrajectory, bool> Predicate) : AchievementCriterion
{
    public override bool IsSatisfied(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => Predicate(trajectory);
}
public sealed record SnapshotPredicateCriterion(Func<ProgressionSnapshot, bool> Predicate, Func<ProgressionSnapshot, long>? Counter = null) : AchievementCriterion
{
    public override bool IsSatisfied(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => Predicate(snapshot);
    public override long Progress(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot snapshot) => Counter?.Invoke(snapshot) ?? base.Progress(trajectory, reward, snapshot);
}

public sealed record AchievementDefinition(
    AchievementId Id, string Name, string Description, AchievementCategory Category, AchievementRarity Rarity,
    AchievementVisibility Visibility, AchievementCriterion Criteria, long Target = 1, bool Retroactive = true, string? CosmeticReward = null);

public interface IAchievementEngine
{
    IReadOnlyList<AchievementDefinition> Definitions { get; }
    ImmutableArray<AchievementId> Evaluate(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot projectedSnapshot);
}

public sealed class AchievementEngine : IAchievementEngine
{
    public AchievementEngine(IReadOnlyList<AchievementDefinition>? definitions = null) => Definitions = definitions ?? BuiltInAchievements.Create();
    public IReadOnlyList<AchievementDefinition> Definitions { get; }

    public ImmutableArray<AchievementId> Evaluate(ProgressionTrajectory trajectory, MissionRewardRecord reward, ProgressionSnapshot projectedSnapshot) =>
        Definitions.Where(item => projectedSnapshot.Achievements.GetValueOrDefault(item.Id.Value)?.Unlocked != true)
            .Where(item => item.Criteria.IsSatisfied(trajectory, reward, projectedSnapshot)).Select(static item => item.Id).ToImmutableArray();
}

public static class BuiltInAchievements
{
    private static AchievementDefinition Define(string id, string name, string description, AchievementCategory category, AchievementRarity rarity, AchievementCriterion criterion, long target = 1, string? reward = null) =>
        new(new AchievementId($"achievement.{id}"), name, description, category, rarity, AchievementVisibility.Visible, criterion, target, CosmeticReward: reward);

    public static IReadOnlyList<AchievementDefinition> Create() =>
    [
        Define("first-light", "First Light", "Complete the first verified mission.", AchievementCategory.Missions, AchievementRarity.Common, new AllCriterion([new VerifiedMissionCriterion(), new CareerThresholdCriterion(static c => c.VerifiedMissions, 1)]), reward: "Verified Operator emblem"),
        Define("parallel-mind", "Parallel Mind", "Verify an Advanced mission with eight meaningful concurrent branches.", AchievementCategory.Efficiency, AchievementRarity.Rare, new AllCriterion([new VerifiedMissionCriterion(), new MissionThresholdCriterion(static t => t.MeaningfulParallelBranches, 8), new TrajectoryPredicateCriterion(static t => t.MeaningfulNodes >= 12)]), 8, "Execution-path visualization"),
        Define("no-stone-unturned", "No Stone Unturned", "Orion combines useful source, Git, and memory evidence.", AchievementCategory.Investigation, AchievementRarity.Uncommon, new AllCriterion([new VerifiedMissionCriterion(), new SpecialistFactCriterion(SpecialistRole.Investigator, static f => f.UsefulEvidence >= 3 && f.RootCauseConfirmed), new TrajectoryPredicateCriterion(static t => t.UsedUsefulLongTermMemory && t.SafeDomains.Length >= 2)])),
        Define("measure-twice", "Measure Twice", "Argus rejects an incomplete implementation before a verified repair.", AchievementCategory.Verification, AchievementRarity.Rare, new AllCriterion([new VerifiedMissionCriterion(), new SpecialistFactCriterion(SpecialistRole.Verifier, static f => f.DefectsCaught > 0), new SpecialistFactCriterion(SpecialistRole.Builder, static f => f.Recoveries > 0)]), reward: "Argus inspection insignia"),
        Define("master-craftsman", "Master Craftsman", "Reach Builder mastery level 50.", AchievementCategory.Engineering, AchievementRarity.Epic, new SnapshotPredicateCriterion(static s => s.Specialists.GetValueOrDefault(SpecialistRole.Builder)?.Level >= 50)),
        Define("the-watcher", "The Watcher", "Catch 50 real defects before integration.", AchievementCategory.Verification, AchievementRarity.Epic, new CareerThresholdCriterion(static c => c.DefectsCaught, 50), 50, "Argus icon variant"),
        Define("procedural", "Procedural", "Complete a verified mission using a trusted Skill.", AchievementCategory.Skills, AchievementRarity.Uncommon, new AllCriterion([new VerifiedMissionCriterion(), new TrajectoryPredicateCriterion(static t => t.UsedTrustedSkill)])),
        Define("zero-frontier", "Zero Frontier", "Verify an Advanced mission without frontier inference.", AchievementCategory.Intelligence, AchievementRarity.Rare, new AllCriterion([new VerifiedMissionCriterion(), new TrajectoryPredicateCriterion(static t => !t.UsedFrontierInference && t.UsedFreeOrIncludedInference && t.MeaningfulNodes >= 10)]), reward: "Intelligence-efficiency emblem"),
        Define("against-the-odds", "Against the Odds", "Recover after three genuinely distinct failed approaches.", AchievementCategory.Missions, AchievementRarity.Epic, new AllCriterion([new VerifiedMissionCriterion(), new MissionThresholdCriterion(static t => t.DistinctFailedApproaches, 3)]), 3),
        Define("old-memory", "Old Memory", "Resolve a verified mission using useful long-term memory.", AchievementCategory.Intelligence, AchievementRarity.Uncommon, new AllCriterion([new VerifiedMissionCriterion(), new TrajectoryPredicateCriterion(static t => t.UsedUsefulLongTermMemory)])),
        Define("verified-ten", "Reliable Start", "Complete ten verified missions.", AchievementCategory.Missions, AchievementRarity.Uncommon, new CareerThresholdCriterion(static c => c.VerifiedMissions, 10), 10),
        Define("verified-hundred", "Proven Operator", "Complete one hundred verified missions.", AchievementCategory.Legacy, AchievementRarity.Epic, new CareerThresholdCriterion(static c => c.VerifiedMissions, 100), 100),
        Define("deep-path", "Deep Path", "Verify a mission with a critical path depth of twelve.", AchievementCategory.Engineering, AchievementRarity.Rare, new AllCriterion([new VerifiedMissionCriterion(), new MissionThresholdCriterion(static t => t.CriticalPathDepth, 12)]), 12),
        Define("multi-domain", "Systems Thinking", "Verify work spanning four technical domains.", AchievementCategory.Engineering, AchievementRarity.Rare, new AllCriterion([new VerifiedMissionCriterion(), new MissionThresholdCriterion(static t => Math.Max(t.DomainCount, t.SafeDomains.Length), 4)]), 4),
        Define("root-cause", "Root Cause", "Confirm a root cause with evidence.", AchievementCategory.Investigation, AchievementRarity.Common, new AllCriterion([new VerifiedMissionCriterion(), new SpecialistFactCriterion(SpecialistRole.Investigator, static f => f.RootCauseConfirmed)])),
        Define("independent-proof", "Independent Proof", "Complete strong independent verification.", AchievementCategory.Verification, AchievementRarity.Common, new AllCriterion([new VerifiedMissionCriterion(), new TrajectoryPredicateCriterion(static t => t.Verification == VerificationStrength.IndependentlyVerified)])),
        Define("regression-covered", "Regression Covered", "Complete a mission with regression coverage.", AchievementCategory.Verification, AchievementRarity.Uncommon, new AllCriterion([new VerifiedMissionCriterion(), new TrajectoryPredicateCriterion(static t => t.Verification >= VerificationStrength.RegressionCovered)])),
        Define("skillful-ten", "Procedural Fluency", "Use trusted Skills in ten verified missions.", AchievementCategory.Skills, AchievementRarity.Rare, new CareerThresholdCriterion(static c => c.TrustedSkillUses, 10), 10),
        Define("free-fifty", "Efficient Intelligence", "Resolve fifty missions with free or included intelligence.", AchievementCategory.Intelligence, AchievementRarity.Rare, new CareerThresholdCriterion(static c => c.FreeOrIncludedMissions, 50), 50),
        Define("guardian-recovery", "Guarded Recovery", "Argus finds a defect and the mission still verifies.", AchievementCategory.Verification, AchievementRarity.Uncommon, new AllCriterion([new VerifiedMissionCriterion(), new SpecialistFactCriterion(SpecialistRole.Verifier, static f => f.DefectsCaught > 0)])),
        Define("extreme", "Edge of Complexity", "Complete an Extreme verified mission.", AchievementCategory.Rare, AchievementRarity.Legendary, new AllCriterion([new VerifiedMissionCriterion(), new TrajectoryPredicateCriterion(static t => t.MeaningfulNodes >= 24 && t.CriticalPathDepth >= 10 && t.DomainCount >= 3)]), reward: "Extreme mission insignia")
    ];
}

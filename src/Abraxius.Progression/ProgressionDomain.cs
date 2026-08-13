using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Abraxius.Agents;
using Abraxius.Protocol;
using Abraxius.Skills;

namespace Abraxius.Progression;

public readonly record struct TrajectoryId(Guid Value)
{
    public static TrajectoryId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct RewardRecordId(string Value)
{
    public static RewardRecordId For(TrajectoryId trajectory, ProgressionRulesVersion rules) =>
        new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{trajectory}:{rules.Value}"))).ToLowerInvariant()[..32]);
    public override string ToString() => Value;
}

public readonly record struct ProgressionEventId(Guid Value)
{
    public static ProgressionEventId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct AchievementId(string Value) { public override string ToString() => Value; }
public readonly record struct ProgressionRulesVersion(int Value) { public override string ToString() => $"v{Value}"; }
public readonly record struct PrestigeRank(int Value) { public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture); }

public enum RewardEligibility { Eligible, Reduced, Ineligible }
public enum VerificationStrength { Unverified, Inconclusive, Verified, RegressionCovered, IndependentlyVerified }
public enum MissionDifficultyClass { Routine, Moderate, Advanced, Expert, Extreme }
public enum ProgressionEventKind
{
    MissionRewarded, SpecialistExperienceAwarded, SkillMasteryAwarded, AchievementUnlocked,
    OperatorLeveled, SpecialistLeveled, SkillMastered, PrestigeUnlocked, PrestigeActivated
}
public enum AchievementCategory { Engineering, Investigation, Verification, Intelligence, Efficiency, Skills, Missions, Legacy, Rare }
public enum AchievementRarity { Common, Uncommon, Rare, Epic, Legendary }
public enum AchievementVisibility { Visible, Secret }
public enum MasteryCategory
{
    MissionPlanning, Delegation, Recovery, ResourceEfficiency,
    SourceRecon, GitArchaeology, EvidenceFusion, RootCause,
    Implementation, Refactoring, Repair, Architecture,
    Tests, Regression, Requirements, Invariants
}

public sealed record SpecialistContributionFacts(
    SpecialistRole Role,
    int SuccessfulAssignments,
    int FailedAssignments = 0,
    int UsefulEvidence = 0,
    bool RootCauseConfirmed = false,
    bool SelectedImplementation = false,
    bool IndependentVerification = false,
    int DefectsCaught = 0,
    int Recoveries = 0,
    ImmutableArray<MasteryCategory> Categories = default)
{
    public ImmutableArray<MasteryCategory> SafeCategories => Categories.IsDefault ? [] : Categories;
}

public sealed record SkillUseFacts(
    SkillId SkillId,
    SkillVersion Version,
    bool Verified,
    bool Meaningful,
    double Reliability,
    string? Environment = null);

/// <summary>Immutable Phase 14 projection consumed by Phase 15. It contains measurements, never model ratings.</summary>
public sealed record ProgressionTrajectory(
    TrajectoryId Id,
    MissionId MissionId,
    string Objective,
    string StateFingerprint,
    DateTimeOffset CompletedAt,
    MissionState Outcome,
    RewardEligibility Eligibility,
    VerificationStrength Verification,
    int MeaningfulNodes,
    int CriticalPathDepth,
    int MeaningfulParallelBranches,
    int DomainCount,
    int MutationNodes,
    int VerificationNodes,
    int Replans,
    int DistinctFailedApproaches,
    bool UsedTrustedSkill,
    bool UsedUsefulLongTermMemory,
    bool UsedFrontierInference,
    bool UsedFreeOrIncludedInference,
    bool IsReplay,
    bool IsBenchmarkOrTest,
    ImmutableArray<SpecialistContributionFacts> SpecialistContributions,
    ImmutableArray<SkillUseFacts> SkillUses,
    ImmutableArray<EvidenceId> EvidenceRefs,
    ImmutableArray<string> Domains = default)
{
    public bool Succeeded => Outcome == MissionState.Succeeded;
    public ImmutableArray<SpecialistContributionFacts> SafeSpecialistContributions => SpecialistContributions.IsDefault ? [] : SpecialistContributions;
    public ImmutableArray<SkillUseFacts> SafeSkillUses => SkillUses.IsDefault ? [] : SkillUses;
    public ImmutableArray<EvidenceId> SafeEvidenceRefs => EvidenceRefs.IsDefault ? [] : EvidenceRefs;
    public ImmutableArray<string> SafeDomains => Domains.IsDefault ? [] : Domains;
}

public sealed record RewardFactor(string Name, double Value, string Explanation);

public sealed record MissionRewardRecord(
    RewardRecordId Id,
    MissionId MissionId,
    TrajectoryId TrajectoryId,
    ProgressionRulesVersion RulesVersion,
    int BaseExperience,
    double DifficultyScore,
    MissionDifficultyClass Difficulty,
    double VerificationScore,
    double EfficiencyScore,
    double NoveltyScore,
    double RiskScore,
    RewardEligibility Eligibility,
    int OperatorXp,
    IReadOnlyDictionary<SpecialistRole, int> SpecialistXp,
    IReadOnlyDictionary<string, int> SkillMastery,
    ImmutableArray<AchievementId> AchievementsUnlocked,
    ImmutableArray<RewardFactor> Factors,
    DateTimeOffset Timestamp,
    ImmutableArray<EvidenceId> EvidenceRefs,
    ProgressionTrajectory SourceTrajectory);

public sealed record OperatorProgression(int CurrentLevel = 1, long CycleExperience = 0, long LifetimeExperience = 0, long ExperienceIntoLevel = 0, long ExperienceRequired = 100);
public sealed record SpecialistProgression(SpecialistRole Role, int Level = 1, long Experience = 0, string Title = "Initiate", IReadOnlyDictionary<MasteryCategory, long>? Categories = null)
{
    public IReadOnlyDictionary<MasteryCategory, long> SafeCategories => Categories ?? ImmutableDictionary<MasteryCategory, long>.Empty;
}
public sealed record SkillMastery(string SkillKey, long Experience = 0, int VerifiedUses = 0, int Environments = 0, bool Mastered = false);
public sealed record AchievementProgress(AchievementId Id, bool Unlocked = false, long Current = 0, long Target = 1, DateTimeOffset? UnlockedAt = null);
public sealed record PrestigeState(PrestigeRank Rank, bool Available = false, DateTimeOffset? LastActivatedAt = null, ImmutableArray<string> UnmetRequirements = default)
{
    public ImmutableArray<string> SafeUnmetRequirements => UnmetRequirements.IsDefault ? [] : UnmetRequirements;
}

public sealed record CareerStatistics(
    long Missions = 0, long VerifiedMissions = 0, long FailedMissions = 0, long CancelledMissions = 0,
    long MeaningfulParallelBranches = 0, int PeakMeaningfulConcurrency = 0, long DefectsCaught = 0, long ExtremeMissions = 0,
    long TrustedSkillUses = 0, long MasteredSkills = 0, long FrontierMissions = 0, long FreeOrIncludedMissions = 0,
    DateTimeOffset? FirstMissionAt = null, DateTimeOffset? LastMissionAt = null)
{
    public double VerificationRate => Missions == 0 ? 0 : (double)VerifiedMissions / Missions;
    public double FreeOrIncludedRate => Missions == 0 ? 0 : (double)FreeOrIncludedMissions / Missions;
}

public sealed record ProgressionEvent(
    ProgressionEventId Id,
    ProgressionEventKind Kind,
    RewardRecordId? RewardId,
    TrajectoryId? SourceTrajectoryId,
    MissionId? SourceMissionId,
    ProgressionRulesVersion RulesVersion,
    DateTimeOffset Timestamp,
    string Summary,
    string Subject,
    long Amount = 0);

public sealed record ProgressionSnapshot(
    long Sequence,
    OperatorProgression Operator,
    PrestigeState Prestige,
    IReadOnlyDictionary<SpecialistRole, SpecialistProgression> Specialists,
    IReadOnlyDictionary<string, SkillMastery> Skills,
    IReadOnlyDictionary<string, AchievementProgress> Achievements,
    CareerStatistics Career,
    ImmutableArray<ProgressionEvent> RecentEvents,
    DateTimeOffset UpdatedAt)
{
    public static ProgressionSnapshot Empty(IProgressionRules rules)
    {
        var specialists = Enum.GetValues<SpecialistRole>().Where(static role => role != SpecialistRole.DomainExpert)
            .ToDictionary(static role => role, role => new SpecialistProgression(role, Title: SpecialistTitles.For(role, 1)));
        return new(0, new OperatorProgression(ExperienceRequired: rules.ExperienceRequiredForLevel(1)), new PrestigeState(new PrestigeRank(0)), specialists,
            ImmutableDictionary<string, SkillMastery>.Empty, ImmutableDictionary<string, AchievementProgress>.Empty,
            new CareerStatistics(), [], DateTimeOffset.UtcNow);
    }
}

public static class SpecialistTitles
{
    public static string For(SpecialistRole role, int level) => role switch
    {
        SpecialistRole.Coordinator => Pick(level, "Initiate", "Tactician", "Strategist", "Architect", "Grand Strategist"),
        SpecialistRole.Investigator => Pick(level, "Tracker", "Pathfinder", "Investigator", "Seeker", "Master Hunter"),
        SpecialistRole.Builder => Pick(level, "Apprentice", "Craftsman", "Engineer", "Architect", "Master Builder"),
        SpecialistRole.Verifier => Pick(level, "Watcher", "Sentinel", "Examiner", "Guardian", "All-Seeing"),
        _ => "Domain Specialist"
    };

    private static string Pick(int level, params string[] values) => values[Math.Clamp((level - 1) / 10, 0, values.Length - 1)];
}

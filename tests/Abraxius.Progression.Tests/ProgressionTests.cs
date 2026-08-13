using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Progression;
using Abraxius.Protocol;
using Abraxius.Skills;
using Xunit;

namespace Abraxius.Progression.Tests;

public sealed class ProgressionTests
{
    [Fact]
    public async Task VerifiedMissionProducesOneIdempotentReward()
    {
        await using var service = CreateService();
        var trajectory = CreateTrajectory();
        var first = await service.ProcessAsync(trajectory);
        var second = await service.ProcessAsync(trajectory);
        Assert.NotNull(first);
        Assert.True(first.OperatorXp > 0);
        Assert.Null(second);
        Assert.Equal(first.OperatorXp, service.Snapshot.Operator.LifetimeExperience);
        Assert.Equal(1, service.Snapshot.Career.Missions);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ReplayAndBenchmarksNeverReward(bool replay, bool benchmark)
    {
        await using var service = CreateService();
        var reward = await service.ProcessAsync(CreateTrajectory() with { Id = TrajectoryId.New(), IsReplay = replay, IsBenchmarkOrTest = benchmark });
        Assert.NotNull(reward);
        Assert.Equal(0, reward.OperatorXp);
        Assert.Equal(0, service.Snapshot.Career.Missions);
    }

    [Fact]
    public async Task DuplicateUnchangedWorkRapidlyDiminishes()
    {
        await using var service = CreateService();
        var first = await service.ProcessAsync(CreateTrajectory());
        var second = await service.ProcessAsync(CreateTrajectory() with { Id = TrajectoryId.New(), MissionId = MissionId.New() });
        var third = await service.ProcessAsync(CreateTrajectory() with { Id = TrajectoryId.New(), MissionId = MissionId.New() });
        Assert.True(second!.OperatorXp < first!.OperatorXp);
        Assert.Equal(0, third!.OperatorXp);
    }

    [Fact]
    public async Task OnlyParticipatingSpecialistsGainExperience()
    {
        await using var service = CreateService();
        await service.ProcessAsync(CreateTrajectory() with
        {
            SpecialistContributions = [new SpecialistContributionFacts(SpecialistRole.Investigator, 1, UsefulEvidence: 3, RootCauseConfirmed: true, Categories: [MasteryCategory.RootCause])]
        });
        Assert.True(service.Snapshot.Specialists[SpecialistRole.Investigator].Experience > 0);
        Assert.Equal(0, service.Snapshot.Specialists[SpecialistRole.Builder].Experience);
    }

    [Fact]
    public async Task TrustedSkillGainsMasteryWithoutChangingTrust()
    {
        await using var service = CreateService();
        await service.ProcessAsync(CreateTrajectory() with
        {
            UsedTrustedSkill = true,
            SkillUses = [new SkillUseFacts(new SkillId("git.regression-investigation"), SkillVersion.Initial, true, true, .95, "linux")]
        });
        var mastery = Assert.Single(service.Snapshot.Skills.Values);
        Assert.True(mastery.Experience > 0);
        Assert.False(mastery.Mastered);
    }

    [Fact]
    public async Task ParallelMindRequiresMeaningfulVerifiedWork()
    {
        await using var service = CreateService();
        await service.ProcessAsync(CreateTrajectory() with { MeaningfulParallelBranches = 8, MeaningfulNodes = 14 });
        Assert.True(service.Snapshot.Achievements["achievement.parallel-mind"].Unlocked);

        await using var noOpService = CreateService();
        await noOpService.ProcessAsync(CreateTrajectory() with { MeaningfulParallelBranches = 8, MeaningfulNodes = 2 });
        Assert.False(noOpService.Snapshot.Achievements["achievement.parallel-mind"].Unlocked);
    }

    [Fact]
    public async Task MeasureTwiceRequiresRealDefectAndRepair()
    {
        await using var service = CreateService();
        await service.ProcessAsync(CreateTrajectory() with
        {
            SpecialistContributions =
            [
                new SpecialistContributionFacts(SpecialistRole.Builder, 1, Recoveries: 1),
                new SpecialistContributionFacts(SpecialistRole.Verifier, 1, IndependentVerification: true, DefectsCaught: 1)
            ]
        });
        Assert.True(service.Snapshot.Achievements["achievement.measure-twice"].Unlocked);
        Assert.Equal(1, service.Snapshot.Career.DefectsCaught);
    }

    [Fact]
    public async Task SnapshotRebuildIsEquivalent()
    {
        var store = new InMemoryProgressionStore();
        await using var service = new ProgressionService(store);
        await service.InitializeAsync();
        await service.ProcessAsync(CreateTrajectory());
        var expected = service.Snapshot;
        await service.RebuildSnapshotAsync();
        Assert.Equal(expected.Operator, service.Snapshot.Operator);
        Assert.Equal(expected.Career, service.Snapshot.Career);
        var expectedOrion = expected.Specialists[SpecialistRole.Investigator];
        var rebuiltOrion = service.Snapshot.Specialists[SpecialistRole.Investigator];
        Assert.Equal(expectedOrion.Level, rebuiltOrion.Level);
        Assert.Equal(expectedOrion.Experience, rebuiltOrion.Experience);
        Assert.Equal(expectedOrion.SafeCategories, rebuiltOrion.SafeCategories);
    }

    [Fact]
    public void RuleVersionMakesRewardIdentityStable()
    {
        var trajectory = CreateTrajectory();
        Assert.Equal(RewardRecordId.For(trajectory.Id, new ProgressionRulesVersion(1)), RewardRecordId.For(trajectory.Id, new ProgressionRulesVersion(1)));
        Assert.NotEqual(RewardRecordId.For(trajectory.Id, new ProgressionRulesVersion(1)), RewardRecordId.For(trajectory.Id, new ProgressionRulesVersion(2)));
    }

    [Fact]
    public async Task PrestigePreservesCareerButRefusesEarlyActivation()
    {
        await using var service = CreateService();
        await service.ProcessAsync(CreateTrajectory());
        var result = await service.ActivatePrestigeAsync();
        Assert.False(result.Activated);
        Assert.NotEmpty(result.UnmetRequirements);
        Assert.Equal(1, service.Snapshot.Career.VerifiedMissions);
    }

    [Fact]
    public async Task PrestigeActivationIsAtomicAndRebuildable()
    {
        var rules = new ProgressionRulesV1();
        var store = new InMemoryProgressionStore();
        var specialists = Enum.GetValues<SpecialistRole>().Where(static role => role != SpecialistRole.DomainExpert)
            .ToDictionary(static role => role, static role => new SpecialistProgression(role, 25, 50_000, SpecialistTitles.For(role, 25)));
        var seed = ProgressionSnapshot.Empty(rules) with
        {
            Operator = new OperatorProgression(100, 500_000, 500_000, 0, rules.ExperienceRequiredForLevel(100)),
            Specialists = specialists,
            Skills = new Dictionary<string, SkillMastery> { ["git.regression-investigation/1.0.0"] = new("git.regression-investigation/1.0.0", 500, 15, 2, true) },
            Achievements = new Dictionary<string, AchievementProgress> { ["achievement.first-light"] = new(new AchievementId("achievement.first-light"), true, 1, 1, DateTimeOffset.UtcNow) },
            Career = new CareerStatistics(Missions: 110, VerifiedMissions: 100, ExtremeMissions: 1)
        };
        await store.SaveSnapshotAsync(seed);
        await using var service = new ProgressionService(store, rules);
        await service.InitializeAsync();
        var activated = await service.ActivatePrestigeAsync();
        Assert.True(activated.Activated);
        Assert.Equal(1, service.Snapshot.Prestige.Rank.Value);
        Assert.Equal(1, service.Snapshot.Operator.CurrentLevel);
        Assert.Equal(500_000, service.Snapshot.Operator.LifetimeExperience);
        Assert.True(service.Snapshot.Skills["git.regression-investigation/1.0.0"].Mastered);
        Assert.True(service.Snapshot.Achievements["achievement.first-light"].Unlocked);
        Assert.Equal(100, service.Snapshot.Career.VerifiedMissions);
        await service.RebuildSnapshotAsync();
        Assert.Equal(1, service.Snapshot.Prestige.Rank.Value);
    }

    [Fact]
    public async Task ConcurrentDuplicateProcessingCommitsOnce()
    {
        await using var service = CreateService();
        var trajectory = CreateTrajectory();
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => service.ProcessAsync(trajectory).AsTask()));
        Assert.Single(results, static item => item is not null);
        Assert.Equal(1, service.Snapshot.Career.Missions);
    }

    [Fact]
    public async Task AchievementUnlockIsEmittedOnce()
    {
        await using var service = CreateService();
        await service.ProcessAsync(CreateTrajectory());
        await service.ProcessAsync(CreateTrajectory() with { Id = TrajectoryId.New(), MissionId = MissionId.New(), StateFingerprint = "changed-state" });
        Assert.Single(service.Snapshot.RecentEvents.Where(static item => item.Kind == ProgressionEventKind.AchievementUnlocked && item.Subject == "achievement.first-light"));
    }

    [Fact]
    public async Task SqliteCommitIsAtomicAndIdempotent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abraxius-progression-{Guid.NewGuid():N}.db");
        try
        {
            await using var service = new ProgressionService(new SqliteProgressionStore(path));
            await service.InitializeAsync();
            var trajectory = CreateTrajectory();
            Assert.NotNull(await service.ProcessAsync(trajectory));
            Assert.Null(await service.ProcessAsync(trajectory));
            Assert.Equal(1, service.Snapshot.Career.Missions);
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix);
        }
    }

    private static ProgressionService CreateService()
        => new(new InMemoryProgressionStore());

    private static ProgressionTrajectory CreateTrajectory() => new(
        TrajectoryId.New(), MissionId.New(), "repair scheduler race", "state-a", DateTimeOffset.UtcNow,
        MissionState.Succeeded, RewardEligibility.Eligible, VerificationStrength.IndependentlyVerified,
        12, 6, 3, 2, 2, 2, 0, 0, false, false, false, true, false, false,
        [
            new SpecialistContributionFacts(SpecialistRole.Investigator, 1, UsefulEvidence: 3, RootCauseConfirmed: true, Categories: [MasteryCategory.RootCause]),
            new SpecialistContributionFacts(SpecialistRole.Builder, 1, SelectedImplementation: true, Categories: [MasteryCategory.Implementation]),
            new SpecialistContributionFacts(SpecialistRole.Verifier, 1, IndependentVerification: true, Categories: [MasteryCategory.Tests])
        ], [], [EvidenceId.New()], ["scheduler", "concurrency"]);
}

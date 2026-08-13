using System.Collections.Immutable;
using Abraxius.Agents;

namespace Abraxius.Progression;

public sealed record PrestigeActivationResult(bool Activated, PrestigeState State, ImmutableArray<string> UnmetRequirements);

public sealed class ProgressionService : IAsyncDisposable
{
    private readonly IProgressionStore _store;
    private readonly IProgressionRules _rules;
    private readonly IRewardEvaluator _rewards;
    private readonly IAchievementEngine _achievements;
    private readonly SemaphoreSlim _writer = new(1, 1);
    private ProgressionSnapshot? _snapshot;
    public event Action<ProgressionSnapshot, MissionRewardRecord>? RewardCommitted;

    public ProgressionService(IProgressionStore store, IProgressionRules? rules = null, IRewardEvaluator? rewards = null, IAchievementEngine? achievements = null)
    {
        _store = store;
        _rules = rules ?? new ProgressionRulesV1();
        _rewards = rewards ?? new RewardEvaluator(_rules);
        _achievements = achievements ?? new AchievementEngine();
    }

    public ProgressionSnapshot Snapshot => Volatile.Read(ref _snapshot) ?? ProgressionSnapshot.Empty(_rules);
    public IProgressionRules Rules => _rules;
    public IAchievementEngine Achievements => _achievements;
    public IAsyncEnumerable<MissionRewardRecord> ReadRewardsAsync(CancellationToken cancellationToken = default) => _store.ReadRewardsAsync(cancellationToken);

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _snapshot = await _store.LoadSnapshotAsync(cancellationToken).ConfigureAwait(false) ?? ProgressionSnapshot.Empty(_rules);
    }

    public async ValueTask<MissionRewardRecord?> ProcessAsync(ProgressionTrajectory trajectory, CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot;
            var reward = _rewards.Evaluate(trajectory, current);
            var projected = ApplyReward(current, trajectory, reward, []);
            var unlocked = _achievements.Evaluate(trajectory, reward, projected);
            reward = reward with { AchievementsUnlocked = unlocked };
            var events = CreateEvents(current, projected, trajectory, reward, unlocked);
            var final = ApplyReward(current, trajectory, reward, events);
            if (!await _store.TryCommitRewardAsync(reward, events, final, cancellationToken).ConfigureAwait(false)) return null;
            Volatile.Write(ref _snapshot, final);
            RewardCommitted?.Invoke(final, reward);
            return reward;
        }
        finally { _writer.Release(); }
    }

    public async ValueTask RebuildSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rewards = new Dictionary<string, MissionRewardRecord>(StringComparer.Ordinal);
            await foreach (var reward in _store.ReadRewardsAsync(cancellationToken).ConfigureAwait(false)) rewards[reward.Id.Value] = reward;
            var events = new List<ProgressionEvent>();
            await foreach (var progressionEvent in _store.ReadEventsAsync(cancellationToken).ConfigureAwait(false)) events.Add(progressionEvent);
            var rebuilt = ProgressionSnapshot.Empty(_rules);
            foreach (var progressionEvent in events.OrderBy(static item => item.Timestamp).ThenBy(static item => item.Id.Value))
            {
                if (progressionEvent.Kind == ProgressionEventKind.MissionRewarded && progressionEvent.RewardId is { } rewardId && rewards.TryGetValue(rewardId.Value, out var reward)) rebuilt = ApplyHistoricalReward(rebuilt, reward);
                else if (progressionEvent.Kind == ProgressionEventKind.PrestigeActivated) rebuilt = ApplyHistoricalPrestige(rebuilt, progressionEvent);
            }
            rebuilt = rebuilt with { RecentEvents = events.OrderByDescending(static item => item.Timestamp).Take(128).ToImmutableArray() };
            await _store.SaveSnapshotAsync(rebuilt, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, rebuilt);
        }
        finally { _writer.Release(); }
    }

    public async ValueTask<PrestigeActivationResult> ActivatePrestigeAsync(CancellationToken cancellationToken = default)
    {
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Snapshot;
            var unmet = PrestigeRequirements(current);
            if (unmet.Length > 0) return new(false, current.Prestige with { Available = false, UnmetRequirements = unmet }, unmet);
            var prestige = new PrestigeState(new PrestigeRank(current.Prestige.Rank.Value + 1), false, DateTimeOffset.UtcNow);
            var prestigeEvent = new ProgressionEvent(ProgressionEventId.New(), ProgressionEventKind.PrestigeActivated, null, null, null, _rules.Version, DateTimeOffset.UtcNow, $"Prestige {prestige.Rank.Value} activated.", prestige.Rank.ToString(), prestige.Rank.Value);
            var next = current with
            {
                Sequence = current.Sequence + 1,
                Operator = new OperatorProgression(1, 0, current.Operator.LifetimeExperience, 0, _rules.ExperienceRequiredForLevel(1)),
                Prestige = prestige,
                RecentEvents = current.RecentEvents.Insert(0, prestigeEvent).Take(128).ToImmutableArray(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await _store.CommitStandaloneEventAsync(prestigeEvent, next, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _snapshot, next);
            return new(true, prestige, []);
        }
        finally { _writer.Release(); }
    }

    private ProgressionSnapshot ApplyReward(ProgressionSnapshot current, ProgressionTrajectory trajectory, MissionRewardRecord reward, IReadOnlyList<ProgressionEvent> events)
    {
        var lifetime = current.Operator.LifetimeExperience + reward.OperatorXp;
        var cycle = current.Operator.CycleExperience + reward.OperatorXp;
        var level = _rules.LevelForExperience(cycle);
        var beforeLevelXp = 0L;
        for (var i = 1; i < level; i++) beforeLevelXp += _rules.ExperienceRequiredForLevel(i);
        var operatorProgression = new OperatorProgression(level, cycle, lifetime, Math.Max(0, cycle - beforeLevelXp), _rules.ExperienceRequiredForLevel(level));
        var specialists = current.Specialists.ToDictionary(static item => item.Key, static item => item.Value);
        foreach (var award in reward.SpecialistXp)
        {
            var previous = specialists.GetValueOrDefault(award.Key) ?? new SpecialistProgression(award.Key);
            var experience = previous.Experience + award.Value;
            var categories = previous.SafeCategories.ToDictionary(static item => item.Key, static item => item.Value);
            var facts = trajectory.SafeSpecialistContributions.FirstOrDefault(item => item.Role == award.Key);
            if (facts is not null)
            {
                foreach (var category in facts.SafeCategories) categories[category] = categories.GetValueOrDefault(category) + award.Value;
            }
            var specialistLevel = _rules.SpecialistLevelForExperience(experience);
            specialists[award.Key] = new SpecialistProgression(award.Key, specialistLevel, experience, SpecialistTitles.For(award.Key, specialistLevel), categories);
        }
        var skills = current.Skills.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        foreach (var award in reward.SkillMastery)
        {
            var old = skills.GetValueOrDefault(award.Key) ?? new SkillMastery(award.Key);
            var use = trajectory.SafeSkillUses.First(item => $"{item.SkillId}/{item.Version}" == award.Key);
            var uses = old.VerifiedUses + 1;
            var environments = old.Environments + (string.IsNullOrWhiteSpace(use.Environment) ? 0 : 1);
            skills[award.Key] = old with { Experience = old.Experience + award.Value, VerifiedUses = uses, Environments = environments, Mastered = uses >= _rules.TrustedSkillUsesForMastery && use.Reliability >= _rules.SkillReliabilityForMastery };
        }
        var achievements = current.Achievements.ToDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
        foreach (var definition in _achievements.Definitions)
        {
            var old = achievements.GetValueOrDefault(definition.Id.Value) ?? new AchievementProgress(definition.Id, Target: definition.Target);
            var progress = definition.Criteria.Progress(trajectory, reward, current);
            achievements[definition.Id.Value] = old with { Current = Math.Max(old.Current, progress), Target = definition.Target };
        }
        foreach (var id in reward.AchievementsUnlocked)
        {
            var old = achievements.GetValueOrDefault(id.Value) ?? new AchievementProgress(id);
            achievements[id.Value] = old with { Unlocked = true, Current = Math.Max(old.Target, old.Current), UnlockedAt = reward.Timestamp };
        }
        var verified = trajectory.Succeeded && trajectory.Verification >= VerificationStrength.Verified;
        var defects = trajectory.SafeSpecialistContributions.Sum(static item => item.DefectsCaught);
        var career = current.Career with
        {
            Missions = current.Career.Missions + (reward.Eligibility == RewardEligibility.Ineligible ? 0 : 1),
            VerifiedMissions = current.Career.VerifiedMissions + (verified && reward.Eligibility != RewardEligibility.Ineligible ? 1 : 0),
            FailedMissions = current.Career.FailedMissions + (trajectory.Outcome == MissionState.Failed && reward.Eligibility != RewardEligibility.Ineligible ? 1 : 0),
            CancelledMissions = current.Career.CancelledMissions + (trajectory.Outcome == MissionState.Cancelled && reward.Eligibility != RewardEligibility.Ineligible ? 1 : 0),
            MeaningfulParallelBranches = current.Career.MeaningfulParallelBranches + trajectory.MeaningfulParallelBranches,
            PeakMeaningfulConcurrency = Math.Max(current.Career.PeakMeaningfulConcurrency, trajectory.MeaningfulParallelBranches),
            DefectsCaught = current.Career.DefectsCaught + defects,
            ExtremeMissions = current.Career.ExtremeMissions + (reward.Difficulty == MissionDifficultyClass.Extreme && verified ? 1 : 0),
            TrustedSkillUses = current.Career.TrustedSkillUses + (trajectory.UsedTrustedSkill && verified ? 1 : 0),
            MasteredSkills = skills.Values.Count(static item => item.Mastered),
            FrontierMissions = current.Career.FrontierMissions + (trajectory.UsedFrontierInference ? 1 : 0),
            FreeOrIncludedMissions = current.Career.FreeOrIncludedMissions + (trajectory.UsedFreeOrIncludedInference && !trajectory.UsedFrontierInference ? 1 : 0),
            FirstMissionAt = current.Career.FirstMissionAt ?? trajectory.CompletedAt,
            LastMissionAt = trajectory.CompletedAt
        };
        var preliminary = current with { Operator = operatorProgression, Specialists = specialists, Skills = skills, Achievements = achievements, Career = career };
        var unmet = PrestigeRequirements(preliminary);
        return preliminary with
        {
            Sequence = current.Sequence + 1,
            Prestige = preliminary.Prestige with { Available = unmet.Length == 0, UnmetRequirements = unmet },
            RecentEvents = events.Concat(current.RecentEvents).Take(128).ToImmutableArray(),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private ProgressionSnapshot ApplyHistoricalReward(ProgressionSnapshot current, MissionRewardRecord reward)
    {
        return ApplyReward(current, reward.SourceTrajectory, reward, []);
    }

    private ProgressionSnapshot ApplyHistoricalPrestige(ProgressionSnapshot current, ProgressionEvent progressionEvent) => current with
    {
        Sequence = current.Sequence + 1,
        Operator = new OperatorProgression(1, 0, current.Operator.LifetimeExperience, 0, _rules.ExperienceRequiredForLevel(1)),
        Prestige = new PrestigeState(new PrestigeRank((int)progressionEvent.Amount), false, progressionEvent.Timestamp),
        UpdatedAt = progressionEvent.Timestamp
    };

    private List<ProgressionEvent> CreateEvents(ProgressionSnapshot before, ProgressionSnapshot after, ProgressionTrajectory trajectory, MissionRewardRecord reward, ImmutableArray<AchievementId> unlocked)
    {
        var events = new List<ProgressionEvent>
        {
            new(ProgressionEventId.New(), ProgressionEventKind.MissionRewarded, reward.Id, trajectory.Id, trajectory.MissionId, reward.RulesVersion, reward.Timestamp, trajectory.Objective, trajectory.StateFingerprint, reward.OperatorXp)
        };
        foreach (var item in reward.SpecialistXp) events.Add(new(ProgressionEventId.New(), ProgressionEventKind.SpecialistExperienceAwarded, reward.Id, trajectory.Id, trajectory.MissionId, reward.RulesVersion, reward.Timestamp, $"{item.Key} contribution", item.Key.ToString(), item.Value));
        foreach (var item in reward.SkillMastery) events.Add(new(ProgressionEventId.New(), ProgressionEventKind.SkillMasteryAwarded, reward.Id, trajectory.Id, trajectory.MissionId, reward.RulesVersion, reward.Timestamp, "Verified Skill reuse", item.Key, item.Value));
        foreach (var id in unlocked) events.Add(new(ProgressionEventId.New(), ProgressionEventKind.AchievementUnlocked, reward.Id, trajectory.Id, trajectory.MissionId, reward.RulesVersion, reward.Timestamp, _achievements.Definitions.First(item => item.Id == id).Name, id.Value));
        if (after.Operator.CurrentLevel > before.Operator.CurrentLevel) events.Add(new(ProgressionEventId.New(), ProgressionEventKind.OperatorLeveled, reward.Id, trajectory.Id, trajectory.MissionId, reward.RulesVersion, reward.Timestamp, $"Operator level {after.Operator.CurrentLevel}", "operator", after.Operator.CurrentLevel));
        return events;
    }

    private ImmutableArray<string> PrestigeRequirements(ProgressionSnapshot snapshot)
    {
        var unmet = ImmutableArray.CreateBuilder<string>();
        if (snapshot.Operator.CurrentLevel < _rules.MaximumOperatorLevel) unmet.Add($"Operator Level {_rules.MaximumOperatorLevel}");
        if (snapshot.Career.VerifiedMissions < 100) unmet.Add("100 verified missions");
        if (snapshot.Specialists.Where(static item => item.Key != SpecialistRole.DomainExpert).Any(static item => item.Value.Level < 25)) unmet.Add("All core specialists Level 25");
        if (snapshot.Career.ExtremeMissions < 1) unmet.Add("One Extreme verified mission");
        return unmet.ToImmutable();
    }

    public async ValueTask DisposeAsync() { _writer.Dispose(); await _store.DisposeAsync().ConfigureAwait(false); }
}

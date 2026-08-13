using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Abraxius.Agents;
using Abraxius.Progression;

namespace Abraxius.App;

public enum ProgressionSection { Overview, Specialists, Skills, Achievements, Prestige, Career }
public sealed record SpecialistMasteryRow(string Name, string Role, int Level, string Title, long Experience, string Categories);
public sealed record SkillMasteryRow(string Skill, long Experience, int VerifiedUses, bool Mastered, string Status);
public sealed record AchievementRow(string Name, string Description, string Category, string Rarity, string Progress, bool Unlocked, string Reward);
public sealed record ProgressionFeedRow(string Time, string Kind, string Summary, string Amount);

public sealed class ProgressionViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ProgressionService _service;
    private readonly IUiDispatcher _dispatcher;
    private ProgressionSection _section;
    private string _status = "CAREER READY";

    public ProgressionViewModel(ProgressionService service, IUiDispatcher dispatcher)
    {
        _service = service;
        _dispatcher = dispatcher;
        SelectOverviewCommand = new RelayCommand(() => Section = ProgressionSection.Overview);
        SelectSpecialistsCommand = new RelayCommand(() => Section = ProgressionSection.Specialists);
        SelectSkillsCommand = new RelayCommand(() => Section = ProgressionSection.Skills);
        SelectAchievementsCommand = new RelayCommand(() => Section = ProgressionSection.Achievements);
        SelectPrestigeCommand = new RelayCommand(() => Section = ProgressionSection.Prestige);
        SelectCareerCommand = new RelayCommand(() => Section = ProgressionSection.Career);
        RefreshCommand = new RelayCommand(Refresh);
        ActivatePrestigeCommand = new AsyncRelayCommand(ActivatePrestigeAsync);
        _service.RewardCommitted += OnRewardCommitted;
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<SpecialistMasteryRow> Specialists { get; } = [];
    public ObservableCollection<SkillMasteryRow> Skills { get; } = [];
    public ObservableCollection<AchievementRow> Achievements { get; } = [];
    public ObservableCollection<ProgressionFeedRow> Recent { get; } = [];
    public ICommand SelectOverviewCommand { get; }
    public ICommand SelectSpecialistsCommand { get; }
    public ICommand SelectSkillsCommand { get; }
    public ICommand SelectAchievementsCommand { get; }
    public ICommand SelectPrestigeCommand { get; }
    public ICommand SelectCareerCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ActivatePrestigeCommand { get; }
    public ProgressionSection Section { get => _section; private set { if (_section == value) return; _section = value; NotifySections(); } }
    public bool IsOverview => Section == ProgressionSection.Overview;
    public bool IsSpecialists => Section == ProgressionSection.Specialists;
    public bool IsSkills => Section == ProgressionSection.Skills;
    public bool IsAchievements => Section == ProgressionSection.Achievements;
    public bool IsPrestige => Section == ProgressionSection.Prestige;
    public bool IsCareer => Section == ProgressionSection.Career;
    public string Status { get => _status; private set { if (_status == value) return; _status = value; OnPropertyChanged(); } }
    public string PrestigeText { get; private set; } = "Legacy begins here";
    public string LevelText { get; private set; } = "Operator Level 1";
    public string ExperienceText { get; private set; } = "0 / 100 XP";
    public double LevelProgress { get; private set; }
    public string CareerSummary { get; private set; } = string.Empty;
    public string IntelligenceSummary { get; private set; } = string.Empty;
    public string SkillSummary { get; private set; } = string.Empty;
    public string AchievementSummary { get; private set; } = string.Empty;
    public string PrestigeRequirements { get; private set; } = string.Empty;
    public bool PrestigeAvailable { get; private set; }

    public void Refresh()
    {
        var snapshot = _service.Snapshot;
        PrestigeText = snapshot.Prestige.Rank.Value == 0 ? "Legacy begins here" : $"Prestige {ToRoman(snapshot.Prestige.Rank.Value)}";
        LevelText = $"Operator Level {snapshot.Operator.CurrentLevel}";
        ExperienceText = $"{snapshot.Operator.ExperienceIntoLevel:N0} / {snapshot.Operator.ExperienceRequired:N0} XP · {snapshot.Operator.LifetimeExperience:N0} lifetime";
        LevelProgress = snapshot.Operator.ExperienceRequired == 0 ? 1 : Math.Clamp((double)snapshot.Operator.ExperienceIntoLevel / snapshot.Operator.ExperienceRequired, 0, 1);
        CareerSummary = $"{snapshot.Career.Missions:N0} missions · {snapshot.Career.VerifiedMissions:N0} verified · {snapshot.Career.VerificationRate:P0} verification";
        IntelligenceSummary = $"{snapshot.Career.FreeOrIncludedRate:P0} resolved with free/included intelligence · {snapshot.Career.FrontierMissions:N0} frontier missions";
        SkillSummary = $"{snapshot.Skills.Count:N0} practiced · {snapshot.Skills.Values.Count(static item => item.Mastered):N0} mastered · {snapshot.Career.TrustedSkillUses:N0} trusted uses";
        AchievementSummary = $"{snapshot.Achievements.Values.Count(static item => item.Unlocked):N0} / {_service.Achievements.Definitions.Count:N0} milestones";
        PrestigeAvailable = snapshot.Prestige.Available;
        PrestigeRequirements = snapshot.Prestige.Available ? "All requirements satisfied. Activation preserves career, specialist, Skill, memory, and achievement progress."
            : string.Join(Environment.NewLine, snapshot.Prestige.SafeUnmetRequirements.Select(static item => $"○ {item}"));
        Specialists.Clear();
        foreach (var item in snapshot.Specialists.Where(static item => item.Key != SpecialistRole.DomainExpert).OrderBy(static item => item.Key))
        {
            Specialists.Add(new SpecialistMasteryRow(DisplayName(item.Key), item.Key.ToString(), item.Value.Level, item.Value.Title, item.Value.Experience,
                string.Join(" · ", item.Value.SafeCategories.OrderByDescending(static category => category.Value).Take(4).Select(static category => $"{Split(category.Key.ToString())} {category.Value:N0}"))));
        }
        Skills.Clear();
        foreach (var item in snapshot.Skills.Values.OrderByDescending(static item => item.Experience)) Skills.Add(new SkillMasteryRow(item.SkillKey, item.Experience, item.VerifiedUses, item.Mastered, item.Mastered ? "MASTERED" : "IN PROGRESS"));
        Achievements.Clear();
        foreach (var definition in _service.Achievements.Definitions)
        {
            var progress = snapshot.Achievements.GetValueOrDefault(definition.Id.Value) ?? new AchievementProgress(definition.Id, Target: definition.Target);
            Achievements.Add(new AchievementRow(definition.Name, definition.Description, definition.Category.ToString(), definition.Rarity.ToString(), $"{progress.Current:N0} / {progress.Target:N0}", progress.Unlocked, definition.CosmeticReward ?? "Career milestone"));
        }
        Recent.Clear();
        foreach (var item in snapshot.RecentEvents.Take(100)) Recent.Add(new ProgressionFeedRow(item.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Split(item.Kind.ToString()), item.Summary, item.Amount == 0 ? string.Empty : $"+{item.Amount:N0}"));
        Status = $"RULES {_service.Rules.Version} · LEDGER {snapshot.Sequence:N0}";
        NotifySummary();
    }

    private async Task ActivatePrestigeAsync()
    {
        var result = await _service.ActivatePrestigeAsync().ConfigureAwait(false);
        _dispatcher.Post(() => { Status = result.Activated ? "PRESTIGE ACTIVATED" : "PRESTIGE REQUIREMENTS INCOMPLETE"; Refresh(); });
    }

    private void OnRewardCommitted(ProgressionSnapshot snapshot, MissionRewardRecord reward) => _dispatcher.Post(Refresh);
    private void NotifySections() { OnPropertyChanged(nameof(Section)); OnPropertyChanged(nameof(IsOverview)); OnPropertyChanged(nameof(IsSpecialists)); OnPropertyChanged(nameof(IsSkills)); OnPropertyChanged(nameof(IsAchievements)); OnPropertyChanged(nameof(IsPrestige)); OnPropertyChanged(nameof(IsCareer)); }
    private void NotifySummary() { OnPropertyChanged(nameof(PrestigeText)); OnPropertyChanged(nameof(LevelText)); OnPropertyChanged(nameof(ExperienceText)); OnPropertyChanged(nameof(LevelProgress)); OnPropertyChanged(nameof(CareerSummary)); OnPropertyChanged(nameof(IntelligenceSummary)); OnPropertyChanged(nameof(SkillSummary)); OnPropertyChanged(nameof(AchievementSummary)); OnPropertyChanged(nameof(PrestigeRequirements)); OnPropertyChanged(nameof(PrestigeAvailable)); }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    private static string DisplayName(SpecialistRole role) => role switch { SpecialistRole.Coordinator => "Athena", SpecialistRole.Investigator => "Orion", SpecialistRole.Builder => "Daedalus", SpecialistRole.Verifier => "Argus", _ => "Specialist" };
    private static string Split(string value) => string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
    private static string ToRoman(int value)
    {
        if (value <= 0) return "0";
        var pairs = new[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var text = new System.Text.StringBuilder();
        foreach (var (number, token) in pairs) while (value >= number) { text.Append(token); value -= number; }
        return text.ToString();
    }

    public void Dispose() => _service.RewardCommitted -= OnRewardCommitted;
}

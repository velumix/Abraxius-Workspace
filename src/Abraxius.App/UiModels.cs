using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Input;
using Abraxius.Axl;
using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Debrief;
using Abraxius.Distribution;
using Abraxius.Memory;
using Abraxius.Platform;
using Abraxius.Presence;
using Abraxius.Protocol;
using Abraxius.Runtime;
using Abraxius.Security;
using Abraxius.Skills;
using Abraxius.Voice;

namespace Abraxius.App;

public sealed record UiTaskSnapshot(
    TaskId TaskId,
    string Label,
    ExecutorKind Executor,
    WorkState State,
    WorkPriority Priority,
    int Attempt,
    IReadOnlyList<TaskId> Dependencies,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    TaskTiming? Timing,
    string? Error,
    IReadOnlyList<TaskId>? Dependents = null,
    IReadOnlyList<EvidenceId>? Evidence = null,
    ResultId? ResultId = null,
    string? Source = null,
    double Progress = 0);

public sealed record UiEventLine(long Sequence, DateTimeOffset Timestamp, RuntimeEventKind Kind, string Text);

public sealed record UiAgentCard(
    string Name,
    ExecutorKind Executor,
    WorkState State,
    string Detail,
    int ActiveTasks,
    int TotalTasks,
    double Progress);

public sealed record UiSpecialistCard(
    SpecialistInstanceId InstanceId,
    string Name,
    SpecialistRole Role,
    SpecialistState State,
    string Detail,
    int ActiveAssignments,
    int EvidenceCount);

public sealed record UiSpecialistTraceItem(
    DateTimeOffset Timestamp,
    AgentEventKind Kind,
    string Specialist,
    string Detail,
    MissionId? MissionId,
    AssignmentId? AssignmentId);

public sealed record UiDebriefChapter(ChapterId Id, int Ordinal, string Title, string Objective, bool Ready);

public sealed record UiDebriefTurn(
    DialogueTurnId Id,
    string Speaker,
    string Text,
    string Sources,
    bool IsCurrent);

public sealed record UiSkillRow(
    string Id,
    string Version,
    string State,
    string Reliability,
    string Usage,
    string Scope,
    string Description);

public sealed record UiNeedsYouRow(NeedsYouItem Item, string Specialist, string Reason, string Summary, string Created, string Deadline, string State);
public sealed record UiSecurityGrantRow(string Id, string Subject, string Capabilities, string Scope, string Expires, string Uses);
public sealed record UiSecurityAuditRow(string Time, string Type, string Principal, string Action, string Resource, string Reason);

public sealed record UiGraphSnapshot(
    ExecutionId? ExecutionId,
    IReadOnlyList<UiTaskSnapshot> Tasks,
    IReadOnlyList<UiEventLine> Events,
    RuntimeMetricsSnapshot Metrics)
{
    public IReadOnlyList<ActivityBlock> Blocks { get; init; } = [];
    public IReadOnlyList<UiAgentCard> Agents { get; init; } = [];
    public string? MissionSummary { get; init; }
    public static UiGraphSnapshot Empty { get; } = new(null, [], [], new RuntimeMetricsSnapshot(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow));
}

public interface IUiDispatcher
{
    void Post(Action action);
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute)
    {
    }

    public RelayCommand(Action<object?> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => _execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private int _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => Volatile.Read(ref _running) == 0 && (_canExecute?.Invoke() ?? true);

    public void Execute(object? parameter)
    {
        _ = ExecuteAsync();
    }

    private async Task ExecuteAsync()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            return;
        }

        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Command failures are surfaced by the owning view model's status state.
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class MainViewModel : INotifyPropertyChanged, IAsyncDisposable, IUpdateShutdownParticipant
{
    private readonly AbraxiusRuntimeHost _runtime;
    private readonly bool _ownsRuntime;
    private readonly IPlatformEnvironment _environment;
    private readonly RuntimeUiStateAggregator _aggregator;
    private readonly IUiDispatcher _dispatcher;
    private readonly CommandRegistry _commands = new();
    private readonly LayoutPreferencesStore _layoutStore;
    private readonly VoiceEventHub _voiceEvents;
    private readonly VoiceMetricsCollector _voiceMetrics = new();
    private readonly VoiceConversationOrchestrator _voice;
    private readonly IUpdateService _updates;
    private IUpdateCoordinator _updateCoordinator;
    private readonly CancellationTokenSource _voiceLifetime = new();
    private readonly CancellationTokenSource _updateLifetime = new();
    private CancellationTokenSource? _voiceSessionCancellation;
    private readonly TerminalViewModel _terminal;
    private readonly MemoryExplorerViewModel _memoryExplorer;
    private readonly ProgressionViewModel _progression;
    private readonly ArtifactViewModel _artifacts;
    private readonly EvaluationViewModel _evaluation;
    private readonly FabricViewModel _fabric;
    private readonly ComputeViewModel _compute;
    private readonly ExtensionsViewModel _extensions;
    private readonly ChatViewModel _chat;
    private readonly CancellationTokenSource _agentLifetime = new();
    private readonly CancellationTokenSource _debriefLifetime = new();
    private AgentEventSubscription? _agentSubscription;
    private DebriefEventSubscription? _debriefSubscription;
    private DebriefSession? _debriefSession;
    private readonly ObservableCollection<UiDebriefChapter> _debriefChapters = [];
    private readonly ObservableCollection<UiDebriefTurn> _debriefTurns = [];
    private readonly ObservableCollection<UiSkillRow> _skills = [];
    private readonly ObservableCollection<UiNeedsYouRow> _needsYou = [];
    private readonly ObservableCollection<UiSecurityGrantRow> _securityGrants = [];
    private readonly ObservableCollection<UiSecurityAuditRow> _securityAudit = [];
    private readonly ObservableCollection<string> _inAppNotifications = [];
    private readonly ObservableCollection<UiSpecialistTraceItem> _specialistTrace = [];
    private readonly ObservableCollection<UiNavigationGroup> _navigationGroups = [];
    private readonly List<UiNavigationItem> _navigationItems = [];
    private IReadOnlyList<UiSpecialistCard> _specialistCards = [];
    private UiLayoutPreferences _layout;
    private string _commandText = "Analyze the repository";
    private string _commandSearchText = string.Empty;
    private UiGraphSnapshot _graph = UiGraphSnapshot.Empty;
    private UiTaskSnapshot? _selectedTask;
    private string _status = "INITIALIZING";
    private RailDestination _selectedRail = RailDestination.Mission;
    private ViewportClass _viewportClass = ViewportClass.Expanded;
    private bool _performanceOverlayVisible;
    private ActivityFilter _activityFilter;
    private IReadOnlyList<CommandItemViewModel> _commandResults = [];
    private int _disposed;
    private VoiceTurnState _voiceState = VoiceTurnState.Idle;
    private string _voiceTranscript = string.Empty;
    private string _voiceStatus = "VOICE IDLE";
    private VoiceSettings _voiceSettings = VoiceSettings.Default;
    private UpdateProgress? _updateProgress;
    private string _axlInputText = "axl/1 find code q=\"ExecutionGraph\" lim=20";
    private string _axlFormattedText = string.Empty;
    private string _axlDiagnosticsText = "Press VALIDATE to inspect AXL.";
    private string _axlStatusText = "NOT PARSED";
    private string _axlHash = "--";
    private int _axlCommandCount;
    private string _skillQuery = string.Empty;
    private string _skillStatus = "READY";

    public MainViewModel(
        AbraxiusRuntimeHost runtime,
        IPlatformEnvironment? environment = null,
        IProcessExecutionService? processService = null,
        IUiDispatcher? dispatcher = null,
        IAudioCaptureService? audioCapture = null,
        IAudioPlaybackService? audioPlayback = null,
        IUpdateService? updateService = null,
        IUpdateCoordinator? updateCoordinator = null,
        bool ownsRuntime = true)
    {
        _runtime = runtime;
        _ownsRuntime = ownsRuntime;
        _environment = environment ?? PlatformEnvironmentFactory.CreateCurrent();
        _dispatcher = dispatcher ?? new AvaloniaUiDispatcher();
        _updates = updateService ?? new UnavailableUpdateService();
        _updateCoordinator = updateCoordinator ?? new UpdateCoordinator(_updates, [this]);
        _updates.StateChanged += OnUpdateStateChanged;
        _layoutStore = new LayoutPreferencesStore(_environment);
        _layout = _layoutStore.Load();
        _memoryExplorer = new MemoryExplorerViewModel(new HybridMemoryRetriever(runtime.Memory, new HashEmbeddingProvider()));
        _progression = new ProgressionViewModel(runtime.Progression, _dispatcher);
        _artifacts = new ArtifactViewModel(runtime.Artifacts, _dispatcher);
        _evaluation = new EvaluationViewModel(runtime, _dispatcher);
        _fabric = new FabricViewModel(runtime.Fabric, _dispatcher);
        _compute = new ComputeViewModel(runtime.Compute, _dispatcher);
        _extensions = new ExtensionsViewModel(runtime.Plugins, _dispatcher);
        _chat = new ChatViewModel(
            runtime.Model,
            _dispatcher,
            runtime.Agents.Registry.Definitions.Select(static definition => definition.DisplayName),
            runtime.Agents.Registry.Definitions.Select(static definition => new ChatSpecialistProfile(
                definition.DisplayName,
                definition.Role.ToString(),
                definition.Mission.Summary,
                definition.PlanningPolicy.AllowDelegation)));
        _terminal = new TerminalViewModel(_dispatcher, new ProcessTerminalSurface(processService));
        _aggregator = new RuntimeUiStateAggregator(runtime.Events, _dispatcher, ApplySnapshot, runtime.Metrics);
        _voiceEvents = new VoiceEventHub();
        var configuredTts = SpeechProviderFactory.CreateConfiguredTts(runtime.VoiceCredentials);
        var configuredPlayback = audioPlayback ?? new UnavailableAudioPlaybackService();
        _voice = new VoiceConversationOrchestrator(
            audioCapture ?? new UnavailableAudioCaptureService(),
            new EnergyVoiceActivityDetector(),
            SpeechProviderFactory.CreateConfiguredStt(runtime.VoiceCredentials),
            configuredTts,
            configuredPlayback,
            new StaticSpeechVocabularyProvider(["Abraxius", "Athena", "Orion", "Daedalus", "Argus", "Avalonia", "ExecutionGraph", "CancellationToken", "OmniRoute", "LiteLLM", "Lattice"]),
            new ModelVoiceResponseGenerator(runtime.Model),
            _voiceEvents,
            intentSink: new RuntimeVoiceIntentSink(runtime));
        _runtime.ConfigureDebriefAudio(configuredTts, audioPlayback);

        SubmitCommand = new AsyncRelayCommand(SubmitAsync);
        CancelCommand = new RelayCommand(runtime.CancelActiveExecution);
        OpenCommandPaletteCommand = new RelayCommand(OpenCommandPalette);
        CloseCommandPaletteCommand = new RelayCommand(CloseCommandPalette);
        ExecutePaletteCommand = new RelayCommand(parameter =>
        {
            if (parameter is CommandItemViewModel item)
            {
                CloseCommandPalette();
                item.Command.Execute(null);
            }
        });
        SelectMissionCommand = new RelayCommand(_ => SelectRail(RailDestination.Mission));
        SelectChatCommand = new RelayCommand(_ => SelectRail(RailDestination.Chat));
        SelectAgentsCommand = new RelayCommand(_ => SelectRail(RailDestination.Agents));
        SelectTerminalCommand = new RelayCommand(_ => SelectRail(RailDestination.Terminal));
        SelectDiagnosticsCommand = new RelayCommand(_ => SelectRail(RailDestination.Diagnostics));
        SelectMemoryCommand = new RelayCommand(_ => SelectRail(RailDestination.Memory));
        SelectDebriefCommand = new RelayCommand(_ => SelectRail(RailDestination.Debrief));
        SelectSkillsCommand = new RelayCommand(_ => SelectRail(RailDestination.Skills));
        SelectProgressionCommand = new RelayCommand(_ => { SelectRail(RailDestination.Progression); _progression.Refresh(); });
        SelectArtifactsCommand = new RelayCommand(_ => { SelectRail(RailDestination.Artifacts); Forget(_artifacts.RefreshAsync()); });
        SelectEvaluationCommand = new RelayCommand(_ => { SelectRail(RailDestination.Evaluation); Forget(_evaluation.RefreshAsync()); });
        SelectFabricCommand = new RelayCommand(_ => { SelectRail(RailDestination.Fabric); _fabric.Refresh(); });
        SelectComputeCommand = new RelayCommand(_ => { SelectRail(RailDestination.Compute); Forget(_compute.RefreshAsync()); });
        SelectExtensionsCommand = new RelayCommand(_ => { SelectRail(RailDestination.Extensions); Forget(_extensions.RefreshAsync()); });
        SelectNeedsYouCommand = new RelayCommand(_ => { SelectRail(RailDestination.NeedsYou); Forget(RefreshNeedsYouAsync()); });
        SelectSecurityCommand = new RelayCommand(_ => { SelectRail(RailDestination.Security); Forget(RefreshSecurityAsync()); });
        SelectSettingsCommand = new RelayCommand(_ => SelectRail(RailDestination.Settings));
        ToggleRailCommand = new RelayCommand(_ => IsRailExpanded = !IsRailExpanded);
        RefreshSecurityCommand = new RelayCommand(_ => Forget(RefreshSecurityAsync()));
        ToggleLockdownCommand = new RelayCommand(_ => { _runtime.Security.Kernel.Lockdown = !_runtime.Security.Kernel.Lockdown; Forget(RefreshSecurityAsync()); });
        CycleCloseBehaviorCommand = new RelayCommand(_ => Forget(UpdatePresenceSettingsAsync(_runtime.Presence.Settings with { CloseButton = _runtime.Presence.Settings.CloseButton switch { CloseButtonBehavior.HideToTray => CloseButtonBehavior.Quit, CloseButtonBehavior.Quit => CloseButtonBehavior.Ask, _ => CloseButtonBehavior.HideToTray } })));
        CycleBackgroundModeCommand = new RelayCommand(_ => Forget(UpdatePresenceSettingsAsync(_runtime.Presence.Settings with { BackgroundMode = _runtime.Presence.Settings.BackgroundMode switch { BackgroundExecutionMode.ContinueNormally => BackgroundExecutionMode.ReduceBackgroundIntensity, BackgroundExecutionMode.ReduceBackgroundIntensity => BackgroundExecutionMode.PauseNonCritical, BackgroundExecutionMode.PauseNonCritical => BackgroundExecutionMode.PauseAll, _ => BackgroundExecutionMode.ContinueNormally } })));
        ToggleQuietHoursCommand = new RelayCommand(_ => Forget(UpdatePresenceSettingsAsync(_runtime.Presence.Settings with { QuietHours = _runtime.Presence.Settings.EffectiveQuietHours with { Enabled = !_runtime.Presence.Settings.EffectiveQuietHours.Enabled } })));
        ApproveNeedsYouCommand = new RelayCommand(parameter => Forget(ResolveNeedsYouAsync(parameter as UiNeedsYouRow, NeedsYouResolution.Approved)));
        RejectNeedsYouCommand = new RelayCommand(parameter => Forget(ResolveNeedsYouAsync(parameter as UiNeedsYouRow, NeedsYouResolution.Rejected)));
        SnoozeNeedsYouCommand = new RelayCommand(parameter => Forget(SnoozeNeedsYouAsync(parameter as UiNeedsYouRow)));
        RefreshSkillsCommand = new RelayCommand(_ => RefreshSkillsSurface());
        MatchSkillsCommand = new RelayCommand(_ => MatchSkillsSurface());
        CreateDebriefCommand = new AsyncRelayCommand(CreateDebriefAsync);
        PauseDebriefCommand = new AsyncRelayCommand(PauseDebriefAsync);
        ResumeDebriefCommand = new AsyncRelayCommand(ResumeDebriefAsync);
        AskDebriefCommand = new AsyncRelayCommand(AskDebriefAsync);
        TurnDebriefIntoMissionCommand = new AsyncRelayCommand(TurnDebriefIntoMissionAsync);
        OpenDebriefSourcesCommand = new RelayCommand(parameter => OpenDebriefSources(parameter as string));
        SkipDebriefChapterCommand = new RelayCommand(parameter => Forget(SkipDebriefChapterAsync(parameter as UiDebriefChapter)));
        ToggleInspectorCommand = new RelayCommand(_ => IsInspectorVisible = !IsInspectorVisible);
        ToggleActivityCommand = new RelayCommand(_ => IsActivityVisible = !IsActivityVisible);
        SelectGraphCommand = new RelayCommand(_ => MissionView = MissionViewMode.Graph);
        SelectLanesCommand = new RelayCommand(_ => MissionView = MissionViewMode.Lanes);
        SelectAgentGridCommand = new RelayCommand(_ => MissionView = MissionViewMode.Agents);
        SelectActivityCommand = new RelayCommand(_ => MissionView = MissionViewMode.Activity);
        TogglePerformanceCommand = new RelayCommand(_ => IsPerformanceOverlayVisible = !IsPerformanceOverlayVisible);
        ToggleReducedMotionCommand = new RelayCommand(_ => ReducedMotion = !ReducedMotion);
        CycleActivityFilterCommand = new RelayCommand(_ => ActivityFilter = ActivityFilter switch
        {
            ActivityFilter.All => ActivityFilter.Agents,
            ActivityFilter.Agents => ActivityFilter.Tools,
            ActivityFilter.Tools => ActivityFilter.Terminal,
            ActivityFilter.Terminal => ActivityFilter.Changes,
            ActivityFilter.Changes => ActivityFilter.Verification,
            ActivityFilter.Verification => ActivityFilter.Warnings,
            ActivityFilter.Warnings => ActivityFilter.Errors,
            _ => ActivityFilter.All
        });
        OpenEvidenceCommand = new RelayCommand(_ => SelectRail(RailDestination.Memory), () => SelectedTask?.Evidence?.Count > 0);
        ToggleVoiceCommand = new RelayCommand(_ => _ = ToggleVoiceAsync());
        InterruptVoiceCommand = new RelayCommand(_context => Forget(_voice.InterruptAsync("user requested stop")));
        CheckUpdatesCommand = new AsyncRelayCommand(() => CheckForUpdatesAsync(download: true));
        DownloadUpdateCommand = new AsyncRelayCommand(DownloadUpdateAsync, () => _updates.AvailableUpdate is not null);
        RestartUpdateCommand = new AsyncRelayCommand(RestartToUpdateAsync, () => _updates.DownloadedUpdate is not null);
        ParseAxlCommand = new RelayCommand(_ => ParseAxl());
        // The chat buttons bind their own enabled state to Chat.CanSendToMission. Keep
        // the command itself callable so Avalonia does not retain an initial false
        // CanExecute result before the first user message arrives.
        RunChatAsMissionCommand = new AsyncRelayCommand(RunChatAsMissionAsync);

        RegisterCommands();
        BuildNavigation();
        _chat.SetCommandSearch(query => _commands.Search(query)
            .Select(command => new ChatSuggestion("/", command.Id, command.Title, command.Description))
            .ToArray());
        RefreshCommandResults();
        ParseAxl();
        _runtime.Presence.NeedsYou.Changed += OnNeedsYouChanged;
        _runtime.Presence.InApp.Received += OnInAppNotification;
        RefreshSkillsSurface();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CommandText
    {
        get => _commandText;
        set => SetProperty(ref _commandText, value);
    }

    public string CommandSearchText
    {
        get => _commandSearchText;
        set
        {
            if (!SetProperty(ref _commandSearchText, value))
            {
                return;
            }

            RefreshCommandResults();
        }
    }

    public UiGraphSnapshot Graph { get => _graph; private set => SetProperty(ref _graph, value); }
    public UiTaskSnapshot? SelectedTask
    {
        get => _selectedTask;
        private set
        {
            if (!SetProperty(ref _selectedTask, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTaskId));
        }
    }

    public TaskId? SelectedTaskId => SelectedTask?.TaskId;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public VoiceTurnState VoiceState { get => _voiceState; private set => SetProperty(ref _voiceState, value); }
    public string VoiceTranscript { get => _voiceTranscript; private set => SetProperty(ref _voiceTranscript, value); }
    public string VoiceStatus { get => _voiceStatus; private set => SetProperty(ref _voiceStatus, value); }
    public string VoiceDiagnosticsText
    {
        get
        {
            var metrics = _voiceMetrics.Snapshot();
            return $"Voice starts {metrics.SpeechStarts} / ends {metrics.SpeechEnds} · partials {metrics.PartialTranscripts} · barge-ins {metrics.BargeIns} · input {metrics.LastInputLevel:0.000}";
        }
    }
    public string VoiceSettingsText => $"{VoiceSettings.Mode} · {VoiceSettings.RoutingMode} · {(VoiceSettings.PrivateMode ? "private" : "network allowed")} · barge-in {(VoiceSettings.BargeInEnabled ? "on" : "off")}";
    public string DebriefQuestion { get; set; } = string.Empty;
    public string DebriefTitle { get; private set; } = "No Debrief selected";
    public string DebriefStatus { get; private set; } = "READY";
    public string DebriefCurrentSpeaker { get; private set; } = "--";
    public string DebriefSourceText { get; private set; } = "No source snapshot";
    public ObservableCollection<UiDebriefChapter> DebriefChapters => _debriefChapters;
    public ObservableCollection<UiDebriefTurn> DebriefTurns => _debriefTurns;
    public ObservableCollection<UiSkillRow> Skills => _skills;
    public ObservableCollection<UiNeedsYouRow> NeedsYouItems => _needsYou;
    public ObservableCollection<UiSecurityGrantRow> SecurityGrants => _securityGrants;
    public ObservableCollection<UiSecurityAuditRow> SecurityAudit => _securityAudit;
    public IReadOnlyList<UiNavigationGroup> NavigationGroups => _navigationGroups;
    public string SecurityStatusText { get; private set; } = "SECURITY INITIALIZING";
    public string SecurityOverviewText { get; private set; } = "No security snapshot.";
    public string SecurityPolicyText { get; private set; } = "Balanced · default deny";
    public ObservableCollection<string> InAppNotifications => _inAppNotifications;
    public int NeedsYouCount => _needsYou.Count;
    public bool HasNeedsYou => NeedsYouCount > 0;
    public string SkillQuery { get => _skillQuery; set => SetProperty(ref _skillQuery, value); }
    public string SkillStatus { get => _skillStatus; private set => SetProperty(ref _skillStatus, value); }
    public string AxlInputText { get => _axlInputText; set => SetProperty(ref _axlInputText, value); }
    public string AxlFormattedText { get => _axlFormattedText; private set => SetProperty(ref _axlFormattedText, value); }
    public string AxlDiagnosticsText { get => _axlDiagnosticsText; private set => SetProperty(ref _axlDiagnosticsText, value); }
    public string AxlStatusText { get => _axlStatusText; private set => SetProperty(ref _axlStatusText, value); }
    public string AxlHash { get => _axlHash; private set => SetProperty(ref _axlHash, value); }
    public int AxlCommandCount { get => _axlCommandCount; private set => SetProperty(ref _axlCommandCount, value); }
    public MemoryExplorerViewModel Memory => _memoryExplorer;
    public ProgressionViewModel Progression => _progression;
    public ArtifactViewModel Artifacts => _artifacts;
    public EvaluationViewModel Evaluation => _evaluation;
    public FabricViewModel Fabric => _fabric;
    public ComputeViewModel Compute => _compute;
    public ExtensionsViewModel Extensions => _extensions;
    public ChatViewModel Chat => _chat;
    public ICommand SelectMemoryCommand { get; }
    public ICommand SelectSkillsCommand { get; }
    public ICommand SelectChatCommand { get; }
    public ICommand SelectProgressionCommand { get; }
    public ICommand SelectArtifactsCommand { get; }
    public ICommand SelectEvaluationCommand { get; }
    public ICommand SelectFabricCommand { get; }
    public ICommand SelectComputeCommand { get; }
    public ICommand SelectExtensionsCommand { get; }
    public ICommand SelectNeedsYouCommand { get; }
    public ICommand SelectSecurityCommand { get; }
    public ICommand RefreshSecurityCommand { get; }
    public ICommand ToggleLockdownCommand { get; }
    public ICommand ApproveNeedsYouCommand { get; }
    public ICommand RejectNeedsYouCommand { get; }
    public ICommand SnoozeNeedsYouCommand { get; }
    public ICommand SelectSettingsCommand { get; }
    public ICommand ToggleRailCommand { get; }
    public ICommand RunChatAsMissionCommand { get; }
    public ICommand CycleCloseBehaviorCommand { get; }
    public ICommand CycleBackgroundModeCommand { get; }
    public ICommand ToggleQuietHoursCommand { get; }
    public string PresenceSettingsSummary => $"Close → {_runtime.Presence.Settings.CloseButton} · Minimize → {_runtime.Presence.Settings.Minimize} · Background {_runtime.Presence.Settings.BackgroundMode}";
    public string NotificationSettingsSummary => $"Needs You {(_runtime.Presence.Settings.NativeNeedsYou ? "on" : "off")} · Completion {(_runtime.Presence.Settings.NativeMissionCompletion ? "on" : "off")} · Preview {_runtime.Presence.Settings.PreviewPrivacy} · Quiet hours {(_runtime.Presence.Settings.EffectiveQuietHours.Enabled ? $"{_runtime.Presence.Settings.EffectiveQuietHours.Start}–{_runtime.Presence.Settings.EffectiveQuietHours.End}" : "off")}";
    public ICommand RefreshSkillsCommand { get; }
    public ICommand MatchSkillsCommand { get; }
    public VoiceSettings VoiceSettings { get => _voiceSettings; private set => SetProperty(ref _voiceSettings, value); }
    public bool VoiceIsActive => VoiceState is not VoiceTurnState.Idle and not VoiceTurnState.Error;
    public string UpdateStatusText => _updateProgress is { Percent: var percent }
        ? $"UPDATE {percent ?? 0}%"
        : _updates.State switch
        {
            UpdateState.Available => $"UPDATE { _updates.AvailableUpdate?.Version ?? "READY" }",
            UpdateState.Downloading => "UPDATE DOWNLOADING",
            UpdateState.Downloaded or UpdateState.RestartRequired => "UPDATE READY",
            UpdateState.UpToDate => "UP TO DATE",
            UpdateState.Unavailable => "UPDATES UNAVAILABLE",
            UpdateState.Failed => $"UPDATE {_updates.LastError?.Code ?? UpdateErrorCode.Unknown}",
            _ => "UPDATES"
        };
    public string VersionText => $"{_updates.Build.ProductName} {_updates.Build.ProductVersion}";
    public string UpdateDetailsText => _updates.AvailableUpdate is { } update
        ? $"{_updates.Channel} · {update.Version} · {update.PackageSize?.ToString(CultureInfo.InvariantCulture) ?? "size unknown"} bytes\n{update.ReleaseNotes}"
        : $"{_updates.InstallationKind} · {_updates.Channel} · {_updates.LastCheckedAt?.ToString("u") ?? "not checked"}";
    public bool IsUpdateReady => _updates.DownloadedUpdate is not null;
    public UpdateInfo? AvailableUpdate => _updates.AvailableUpdate;
    public RailDestination SelectedRail { get => _selectedRail; private set => SetProperty(ref _selectedRail, value); }
    public double RailWidth => IsRailExpanded ? 216 : 56;
    public ViewportClass ViewportClass { get => _viewportClass; private set => SetProperty(ref _viewportClass, value); }
    public MissionViewMode MissionView
    {
        get => _layout.MissionView;
        private set
        {
            if (_layout.MissionView == value)
            {
                return;
            }

            _layout = _layout with { MissionView = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsGraphView));
            OnPropertyChanged(nameof(IsLanesView));
            OnPropertyChanged(nameof(IsAgentsView));
            OnPropertyChanged(nameof(IsActivityView));
            SaveLayout();
        }
    }
    public UiDensity Density { get => _layout.Density; private set { if (_layout.Density == value) return; _layout = _layout with { Density = value }; OnPropertyChanged(); SaveLayout(); } }
    public bool ReducedMotion { get => _layout.ReducedMotion; private set { if (_layout.ReducedMotion == value) return; _layout = _layout with { ReducedMotion = value }; OnPropertyChanged(); SaveLayout(); } }
    public bool IsRailExpanded
    {
        get => _layout.RailExpanded && ViewportClass != ViewportClass.Compact;
        private set
        {
            if (_layout.RailExpanded == value) return;
            _layout = _layout with { RailExpanded = value };
            OnPropertyChanged();
            OnPropertyChanged(nameof(RailWidth));
            SaveLayout();
        }
    }
    public bool IsInspectorVisible { get => _layout.InspectorVisible && ViewportClass != ViewportClass.Compact; private set { _layout = _layout with { InspectorVisible = value }; OnPropertyChanged(); SaveLayout(); } }
    public bool IsActivityVisible { get => _layout.ActivityVisible && ViewportClass != ViewportClass.Compact; private set { _layout = _layout with { ActivityVisible = value }; OnPropertyChanged(); OnPropertyChanged(nameof(IsActivityDeckVisible)); SaveLayout(); } }
    public bool IsCommandPaletteOpen { get; private set; }
    public bool IsPerformanceOverlayVisible { get => _performanceOverlayVisible; private set => SetProperty(ref _performanceOverlayVisible, value); }
    public ActivityFilter ActivityFilter { get => _activityFilter; private set { if (!SetProperty(ref _activityFilter, value)) return; OnPropertyChanged(nameof(ActivityBlocks)); } }
    public bool IsTerminalVisible => SelectedRail == RailDestination.Terminal;
    public bool IsChatVisible => SelectedRail == RailDestination.Chat;
    public bool IsDiagnosticsVisible => SelectedRail == RailDestination.Diagnostics;
    public bool IsMemoryVisible => SelectedRail == RailDestination.Memory;
    public bool IsDebriefVisible => SelectedRail == RailDestination.Debrief;
    public bool IsSkillsVisible => SelectedRail == RailDestination.Skills;
    public bool IsProgressionVisible => SelectedRail == RailDestination.Progression;
    public bool IsArtifactsVisible => SelectedRail == RailDestination.Artifacts;
    public bool IsEvaluationVisible => SelectedRail == RailDestination.Evaluation;
    public bool IsFabricVisible => SelectedRail == RailDestination.Fabric;
    public bool IsComputeVisible => SelectedRail == RailDestination.Compute;
    public bool IsExtensionsVisible => SelectedRail == RailDestination.Extensions;
    public bool IsNeedsYouVisible => SelectedRail == RailDestination.NeedsYou;
    public bool IsSecurityVisible => SelectedRail == RailDestination.Security;
    public bool IsSettingsVisible => SelectedRail == RailDestination.Settings;
    public bool IsMissionVisible => SelectedRail is not RailDestination.Terminal and not RailDestination.Chat and not RailDestination.Diagnostics and not RailDestination.Memory and not RailDestination.Debrief and not RailDestination.Skills and not RailDestination.Progression and not RailDestination.Artifacts and not RailDestination.Evaluation and not RailDestination.Fabric and not RailDestination.Compute and not RailDestination.Extensions and not RailDestination.NeedsYou and not RailDestination.Security and not RailDestination.Settings;
    public bool IsCommandBarVisible => !IsChatVisible;
    public bool IsActivityDeckVisible => IsActivityVisible && !IsChatVisible;
    public bool IsGraphView => MissionView == MissionViewMode.Graph;
    public bool IsLanesView => MissionView == MissionViewMode.Lanes;
    public bool IsAgentsView => MissionView == MissionViewMode.Agents;
    public bool IsActivityView => MissionView == MissionViewMode.Activity;
    public string PlatformText => $"{_environment.Platform.Family} / {_environment.Platform.Architecture} / {_environment.ExecutionMode}";
    public string ProjectText => $"{_environment.Platform.Family} workspace / main";
    public string CapabilityText => $"LOCAL {_environment.Capabilities.Values.Count(static capability => capability.Availability == CapabilityAvailability.Available)}  {(_environment.Budget.PreferRemote ? "REMOTE PREFERRED" : "LOCAL READY")}";
    public string IntelligenceText => _runtime.Intelligence.Snapshot.StatusText;
    public string IntelligenceRouteText => _runtime.Intelligence.Snapshot.LastDecision is { } decision
        ? $"ROUTE   {decision.Tier} / {decision.Gateway} / {decision.Route}"
        : "ROUTE   NONE SELECTED";
    public string IntelligenceReasonText => _runtime.Intelligence.Snapshot.LastDecision?.Reason ?? "No model route has been selected.";
    public string CpuText => $"TASKS {Graph.Tasks.Count,2}  RUN {Graph.Metrics.RunningTasks,2}  MAX {Graph.Metrics.MaxObservedConcurrency,2}";
    public string QueueText => $"READY {Graph.Metrics.ReadyTasks,2}  QUEUED {Graph.Metrics.QueuedTasks,2}";
    public string LatencyText => Graph.Tasks.LastOrDefault(static task => task.Timing is not null)?.Timing is { } timing
        ? $"LAT {timing.TotalLatency.TotalMilliseconds:F0}ms"
        : "LAT --";
    public string UiPipelineText => $"UI {_aggregator.Metrics.ConsumedEvents} events · {_aggregator.Metrics.AppliedFrames} frames · {_aggregator.Metrics.CoalescedEvents} coalesced";
    public string MissionText => Graph.ExecutionId is { } execution ? $"EXECUTION {execution}"[..Math.Min(20, $"EXECUTION {execution}".Length)] : "NO ACTIVE MISSION";
    public string AttentionText => Graph.Metrics.FailedTasks > 0 || Graph.Metrics.TimedOutTasks > 0 ? "ATTENTION REQUIRED" : Graph.Metrics.RunningTasks > 0 ? "LIVE EXECUTION" : "SYSTEM READY";
    public IReadOnlyList<CommandItemViewModel> CommandResults { get => _commandResults; private set => SetProperty(ref _commandResults, value); }
    public IReadOnlyList<ActivityBlock> ActivityBlocks => ActivityFilter == ActivityFilter.All
        ? Graph.Blocks
        : Graph.Blocks.Where(block => ActivityFilter switch
        {
            ActivityFilter.Agents => block.Kind == ActivityBlockKind.Agent,
            ActivityFilter.Tools => block.Kind == ActivityBlockKind.Tool,
            ActivityFilter.Terminal => block.Kind == ActivityBlockKind.Terminal,
            ActivityFilter.Changes => block.Kind is ActivityBlockKind.Evidence or ActivityBlockKind.Result or ActivityBlockKind.Artifact,
            ActivityFilter.Verification => block.Kind == ActivityBlockKind.Verification,
            ActivityFilter.Warnings => block.Kind == ActivityBlockKind.Warning,
            ActivityFilter.Errors => block.Kind == ActivityBlockKind.Error,
            _ => true
        }).ToArray();
    public IReadOnlyList<UiAgentCard> AgentCards => Graph.Agents;
    public IReadOnlyList<UiSpecialistCard> SpecialistCards => _specialistCards;
    public ObservableCollection<UiSpecialistTraceItem> SpecialistTrace => _specialistTrace;
    public ObservableCollection<UiEventLine> Events { get; } = [];
    public TerminalViewModel Terminal => _terminal;
    public ICommand SubmitCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand OpenCommandPaletteCommand { get; }
    public ICommand CloseCommandPaletteCommand { get; }
    public ICommand ExecutePaletteCommand { get; }
    public ICommand SelectMissionCommand { get; }
    public ICommand SelectAgentsCommand { get; }
    public ICommand SelectTerminalCommand { get; }
    public ICommand SelectDiagnosticsCommand { get; }
    public ICommand SelectDebriefCommand { get; }
    public ICommand CreateDebriefCommand { get; }
    public ICommand PauseDebriefCommand { get; }
    public ICommand ResumeDebriefCommand { get; }
    public ICommand AskDebriefCommand { get; }
    public ICommand TurnDebriefIntoMissionCommand { get; }
    public ICommand OpenDebriefSourcesCommand { get; }
    public ICommand SkipDebriefChapterCommand { get; }
    public ICommand ToggleInspectorCommand { get; }
    public ICommand ToggleActivityCommand { get; }
    public ICommand SelectGraphCommand { get; }
    public ICommand SelectLanesCommand { get; }
    public ICommand SelectAgentGridCommand { get; }
    public ICommand SelectActivityCommand { get; }
    public ICommand TogglePerformanceCommand { get; }
    public ICommand ToggleReducedMotionCommand { get; }
    public ICommand CycleActivityFilterCommand { get; }
    public ICommand OpenEvidenceCommand { get; }
    public ICommand ToggleVoiceCommand { get; }
    public ICommand InterruptVoiceCommand { get; }
    public ICommand CheckUpdatesCommand { get; }
    public ICommand DownloadUpdateCommand { get; }
    public ICommand RestartUpdateCommand { get; }
    public ICommand ParseAxlCommand { get; }

    public async Task StartAsync()
    {
        await _runtime.StartAsync();
        await RefreshSecurityAsync().ConfigureAwait(false);
        await _aggregator.StartAsync();
        _agentSubscription = _runtime.Agents.Events.Subscribe();
        _debriefSubscription = _runtime.Debrief.Events.Subscribe();
        _ = ObserveAgentEventsAsync();
        _ = ObserveDebriefEventsAsync();
        RefreshSpecialistCards();
        await RefreshNeedsYouAsync();
        RefreshSkillsSurface();
        _ = ObserveVoiceEventsAsync();
        _ = ObserveVoiceTelemetryAsync();
        SetStatus("READY / DESCRIBE A MISSION");
        _ = CheckForUpdatesAsync(download: true);
        _ = MonitorUpdatesAsync();
    }

    public void ReportStatus(string status) => SetStatus(status);

    public void ConfigureUpdateCoordinator(IUpdateCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _updateCoordinator = coordinator;
    }

    public async ValueTask PrepareForUpdateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveLayout();
        _voiceSessionCancellation?.Cancel();
        await _voice.InterruptAsync("application update", cancellationToken).ConfigureAwait(false);
    }

    public async Task SubmitAsync()
    {
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            return;
        }

        SetStatus("COMPILING / EXECUTING");
        try
        {
            var result = await _runtime.RunMissionAsync(new Intent(CommandText, CorrelationId.New()));
            SetStatus(result.Succeeded ? "MISSION VERIFIED" : $"MISSION {result.Mission.State.ToString().ToUpperInvariant()}");
        }
        catch (Exception exception)
        {
            SetStatus($"ERROR {exception.Message}");
        }
    }

    private async Task RunChatAsMissionAsync()
    {
        var objective = _chat.TakeMissionObjective();
        if (string.IsNullOrWhiteSpace(objective)) return;

        CommandText = objective;
        _chat.SetJobQueued(objective);
        SetStatus("MISSION CREATED FROM CHAT");
        try
        {
            var result = await _runtime.RunMissionAsync(new Intent(objective, CorrelationId.New()));
            _chat.SetJobFinished(result.Succeeded, result.Succeeded
                ? "Verified mission result is ready. Open Mission or Artifacts for the full evidence."
                : $"Mission ended as {result.Mission.State}. Open Mission for task errors and evidence.");
            SetStatus(result.Succeeded ? "MISSION VERIFIED" : $"MISSION {result.Mission.State.ToString().ToUpperInvariant()}");
        }
        catch (Exception exception)
        {
            _chat.SetJobFinished(false, $"Mission could not start: {exception.Message}");
            SetStatus($"ERROR {exception.Message}");
        }
    }

    public void SelectTask(TaskId taskId)
    {
        SelectedTask = Graph.Tasks.FirstOrDefault(task => task.TaskId == taskId);
        (OpenEvidenceCommand as RelayCommand)?.RaiseCanExecuteChanged();
        RefreshCommandResults();
    }

    public void SelectRail(RailDestination destination)
    {
        SelectedRail = destination;
        foreach (var item in _navigationItems)
        {
            item.SetSelected(item.Destination == destination);
        }
        OnPropertyChanged(nameof(IsMissionVisible));
        OnPropertyChanged(nameof(IsChatVisible));
        OnPropertyChanged(nameof(IsCommandBarVisible));
        OnPropertyChanged(nameof(IsActivityDeckVisible));
        OnPropertyChanged(nameof(IsTerminalVisible));
        OnPropertyChanged(nameof(IsDiagnosticsVisible));
        OnPropertyChanged(nameof(IsMemoryVisible));
        OnPropertyChanged(nameof(IsDebriefVisible));
        OnPropertyChanged(nameof(IsSkillsVisible));
        OnPropertyChanged(nameof(IsProgressionVisible));
        OnPropertyChanged(nameof(IsArtifactsVisible));
        OnPropertyChanged(nameof(IsEvaluationVisible));
        OnPropertyChanged(nameof(IsFabricVisible));
        OnPropertyChanged(nameof(IsComputeVisible));
        OnPropertyChanged(nameof(IsExtensionsVisible));
        OnPropertyChanged(nameof(IsNeedsYouVisible));
        OnPropertyChanged(nameof(IsSecurityVisible));
        OnPropertyChanged(nameof(IsSettingsVisible));
        if (destination == RailDestination.Agents)
        {
            MissionView = MissionViewMode.Agents;
        }
        else if (destination == RailDestination.Diagnostics)
        {
            // Diagnostics now has a dedicated AXL inspector surface; avoid covering it with the transient performance overlay.
            IsPerformanceOverlayVisible = false;
        }
    }

    public void UpdateViewport(double width, double height, double scale = 1)
    {
        var profile = ViewportProfile.From(width, height, scale, _environment.Device.TouchPrimary, ReducedMotion);
        ViewportClass = profile.Class;
        OnPropertyChanged(nameof(IsRailExpanded));
        OnPropertyChanged(nameof(RailWidth));
        OnPropertyChanged(nameof(IsInspectorVisible));
        OnPropertyChanged(nameof(IsActivityVisible));
    }

    public void OpenCommandPalette()
    {
        IsCommandPaletteOpen = true;
        CommandSearchText = string.Empty;
        OnPropertyChanged(nameof(IsCommandPaletteOpen));
    }

    public void CloseCommandPalette()
    {
        if (!IsCommandPaletteOpen)
        {
            return;
        }

        IsCommandPaletteOpen = false;
        OnPropertyChanged(nameof(IsCommandPaletteOpen));
    }

    public void SetMissionView(MissionViewMode view) => MissionView = view;

    private void ParseAxl()
    {
        var result = AxlPipeline.ParseAndValidate(AxlInputText);
        AxlStatusText = result.Status.ToString().ToUpperInvariant();
        AxlCommandCount = result.Document?.Commands.Length ?? 0;
        AxlHash = result.Document is { } hashDocument ? hashDocument.SemanticHash() : "--";
        AxlFormattedText = result.Document is { } document ? AxlFormatter.Pretty(document) : string.Empty;
        AxlDiagnosticsText = result.Diagnostics.Length == 0
            ? "No diagnostics. Parsing and validation completed; nothing was executed."
            : string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.ToString()));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _terminal.DisposeAsync();
        await _chat.DisposeAsync();
        _progression.Dispose();
        _voiceSessionCancellation?.Cancel();
        _voiceLifetime.Cancel();
        _agentLifetime.Cancel();
        _debriefLifetime.Cancel();
        _updateLifetime.Cancel();
        await _voice.DisposeAsync();
        await _voiceEvents.DisposeAsync();
        _updates.StateChanged -= OnUpdateStateChanged;
        await _updates.DisposeAsync();
        _voiceLifetime.Dispose();
        _updateLifetime.Dispose();
        await _aggregator.DisposeAsync();
        if (_agentSubscription is not null)
        {
            await _agentSubscription.DisposeAsync();
        }
        if (_debriefSubscription is not null)
        {
            await _debriefSubscription.DisposeAsync();
        }
        _agentLifetime.Dispose();
        _debriefLifetime.Dispose();
        _runtime.Presence.NeedsYou.Changed -= OnNeedsYouChanged;
        _runtime.Presence.InApp.Received -= OnInAppNotification;
        if (_ownsRuntime) await _runtime.DisposeAsync();
    }

    private async Task RunDemoInBackgroundAsync()
    {
        try
        {
            var result = await _runtime.RunDemoAsync();
            SetStatus(result.Succeeded ? "DEMO COMPLETE / READY" : "DEMO NEEDS ATTENTION");
        }
        catch (Exception exception)
        {
            SetStatus($"ERROR {exception.Message}");
        }
    }

    private void ApplySnapshot(UiGraphSnapshot snapshot)
    {
        Graph = snapshot;
        _chat.UpdateMission(snapshot);
        OnPropertyChanged(nameof(ActivityBlocks));
        OnPropertyChanged(nameof(AgentCards));
        OnPropertyChanged(nameof(CpuText));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(LatencyText));
        OnPropertyChanged(nameof(MissionText));
        OnPropertyChanged(nameof(AttentionText));
        OnPropertyChanged(nameof(UiPipelineText));
        OnPropertyChanged(nameof(IntelligenceText));
        OnPropertyChanged(nameof(IntelligenceRouteText));
        OnPropertyChanged(nameof(IntelligenceReasonText));

        Events.Clear();
        foreach (var line in snapshot.Events.TakeLast(160))
        {
            Events.Add(line);
        }

        if (SelectedTask is not null)
        {
            SelectedTask = snapshot.Tasks.FirstOrDefault(task => task.TaskId == SelectedTask.TaskId);
        }

        if (snapshot.Metrics.FailedTasks > 0 || snapshot.Metrics.TimedOutTasks > 0)
        {
            SetStatus("EXECUTION NEEDS ATTENTION");
        }
    }

    private async Task ObserveAgentEventsAsync()
    {
        if (_agentSubscription is null) return;
        try
        {
            await foreach (var item in _agentSubscription.ReadAllAsync(_agentLifetime.Token).ConfigureAwait(false))
            {
                _dispatcher.Post(() =>
                {
                    var specialist = item.InstanceId is { } instanceId && _runtime.Agents.Registry.TryGetInstance(instanceId, out var instance)
                        ? instance.DisplayName
                        : item.Kind == AgentEventKind.MissionStateChanged ? "Athena" : "Agent Kernel";
                    _specialistTrace.Add(new UiSpecialistTraceItem(item.Timestamp, item.Kind, specialist, item.Detail ?? string.Empty, item.MissionId, item.AssignmentId));
                    while (_specialistTrace.Count > 256) _specialistTrace.RemoveAt(0);
                    RefreshSpecialistCards();
                    OnPropertyChanged(nameof(SpecialistTrace));
                });
            }
        }
        catch (OperationCanceledException) when (_agentLifetime.IsCancellationRequested)
        {
        }
    }

    private void RefreshSpecialistCards()
    {
        _specialistCards = _runtime.Agents.Registry.Instances
            .GroupBy(static instance => instance.DefinitionId)
            .Select(group =>
            {
                var latest = group.OrderByDescending(static instance => instance.UpdatedAt).First();
                var active = group.Count(static instance => instance.State is SpecialistState.Assigned or SpecialistState.Running or SpecialistState.Waiting);
                return new UiSpecialistCard(latest.Id, latest.DisplayName, latest.Role, latest.State, active == 0 ? "Ready for assignment" : $"{active} active instance(s)", active, 0);
            })
            .Concat(_runtime.Agents.Registry.Definitions.Where(definition => !_runtime.Agents.Registry.Instances.Any(instance => instance.DefinitionId == definition.Id)).Select(definition => new UiSpecialistCard(SpecialistInstanceId.New(), definition.DisplayName, definition.Role, SpecialistState.Idle, "Ready · no active instance", 0, 0)))
            .OrderBy(static card => card.Role)
            .ToArray();
        OnPropertyChanged(nameof(SpecialistCards));
    }

    private async Task CreateDebriefAsync()
    {
        SelectRail(RailDestination.Debrief);
        DebriefStatus = "PLANNING";
        OnPropertyChanged(nameof(DebriefStatus));
        try
        {
            var objective = string.IsNullOrWhiteSpace(CommandText) ? "Explain the current Abraxius project state." : CommandText.Trim();
            var executionIds = _runtime.LastExecution is { } lastExecution ? new[] { lastExecution.ExecutionId } : null;
            _debriefSession = await _runtime.CreateDebriefAsync(new DebriefRequest(
                new DebriefSourceSet(ProjectKey: CurrentProjectKey(), ExecutionIds: executionIds, Query: objective),
                Mode: DebriefMode.Briefing,
                Objective: objective,
                TargetDuration: TimeSpan.FromMinutes(10),
                GenerateAudio: true,
                VoiceLanguage: VoiceSettings.Language,
                PrivateMode: VoiceSettings.PrivateMode));
            RefreshDebriefSurface();
            _ = PlayDebriefAsync(_debriefSession);
        }
        catch (Exception exception)
        {
            DebriefStatus = $"FAILED · {exception.GetType().Name}";
            OnPropertyChanged(nameof(DebriefStatus));
        }
    }

    private async Task PlayDebriefAsync(DebriefSession session)
    {
        var result = await _runtime.Debrief.PlayAsync(session).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            DebriefStatus = result.Succeeded ? "COMPLETED" : result.Summary.ToUpperInvariant();
            RefreshDebriefSurface();
            OnPropertyChanged(nameof(DebriefStatus));
        });
    }

    private async Task PauseDebriefAsync()
    {
        if (_debriefSession is null) return;
        await _runtime.Debrief.PauseAsync(_debriefSession).ConfigureAwait(false);
        DebriefStatus = "PAUSED";
        OnPropertyChanged(nameof(DebriefStatus));
    }

    private async Task ResumeDebriefAsync()
    {
        if (_debriefSession is null) return;
        DebriefStatus = "RESUMING";
        OnPropertyChanged(nameof(DebriefStatus));
        await _runtime.Debrief.ResumeAsync(_debriefSession).ConfigureAwait(false);
    }

    private async Task AskDebriefAsync()
    {
        if (_debriefSession is null || string.IsNullOrWhiteSpace(DebriefQuestion)) return;
        var question = DebriefQuestion.Trim();
        DebriefQuestion = string.Empty;
        OnPropertyChanged(nameof(DebriefQuestion));
        DebriefStatus = "ANSWERING";
        OnPropertyChanged(nameof(DebriefStatus));
        var answer = await _runtime.Debrief.AskAsync(_debriefSession, new DebriefLiveQuestion(question)).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            DebriefCurrentSpeaker = answer.Turn.SpeakerName;
            DebriefStatus = answer.Resumed ? "RESUMING" : "INTERRUPTED";
            RefreshDebriefSurface();
            OnPropertyChanged(nameof(DebriefCurrentSpeaker));
            OnPropertyChanged(nameof(DebriefStatus));
        });
    }

    private async Task TurnDebriefIntoMissionAsync()
    {
        if (_debriefSession is null) return;
        var objective = $"{_debriefSession.Plan.Objective} Continue from the Debrief findings: {string.Join(" | ", _debriefSession.Plan.Claims.Where(static claim => claim.IsSpeakable).Take(8).Select(static claim => claim.Statement))}";
        SetStatus("MISSION CREATED FROM DEBRIEF");
        var result = await _runtime.RunMissionAsync(new Intent(objective, CorrelationId.New()), cancellationToken: _debriefLifetime.Token).ConfigureAwait(false);
        _dispatcher.Post(() => SetStatus(result.Succeeded ? "MISSION VERIFIED" : $"MISSION {result.Mission.State.ToString().ToUpperInvariant()}"));
    }

    private void OpenDebriefSources(string? sources)
    {
        if (string.IsNullOrWhiteSpace(sources)) return;
        SelectRail(RailDestination.Memory);
        Memory.Query = sources;
        Forget(Memory.SearchAsync());
    }

    private async Task SkipDebriefChapterAsync(UiDebriefChapter? selected)
    {
        if (_debriefSession is null || _debriefChapters.Count == 0) return;
        var chapter = selected ?? _debriefChapters.FirstOrDefault(item => !item.Ready) ?? _debriefChapters[^1];
        await _runtime.Debrief.SkipToChapterAsync(_debriefSession, chapter.Id).ConfigureAwait(false);
        DebriefStatus = $"CHAPTER {chapter.Ordinal:00}";
        OnPropertyChanged(nameof(DebriefStatus));
    }

    private async Task ObserveDebriefEventsAsync()
    {
        if (_debriefSubscription is null) return;
        try
        {
            await foreach (var item in _debriefSubscription.ReadAllAsync(_debriefLifetime.Token).ConfigureAwait(false))
            {
                _dispatcher.Post(() =>
                {
                    if (_debriefSession is null || item.DebriefId != _debriefSession.Id) return;
                    if (item.Turn is { } turn) DebriefCurrentSpeaker = turn.SpeakerName;
                    DebriefStatus = item.Kind switch
                    {
                        DebriefEventKind.PlaybackStarted => "PLAYING",
                        DebriefEventKind.Paused => "PAUSED",
                        DebriefEventKind.Interrupted => "INTERRUPTED",
                        DebriefEventKind.Completed => "COMPLETED",
                        DebriefEventKind.Failed => "FAILED",
                        DebriefEventKind.ClaimRejected => "GROUNDING REJECTED",
                        _ => DebriefStatus
                    };
                    RefreshDebriefSurface();
                    OnPropertyChanged(nameof(DebriefStatus));
                    OnPropertyChanged(nameof(DebriefCurrentSpeaker));
                });
            }
        }
        catch (OperationCanceledException) when (_debriefLifetime.IsCancellationRequested)
        {
        }
    }

    private void RefreshDebriefSurface()
    {
        var session = _debriefSession;
        if (session is null) return;
        DebriefTitle = session.Plan.Title;
        DebriefSourceText = $"{session.Plan.Mode} · snapshot {session.Plan.SourceSnapshot.ContentHash[..Math.Min(12, session.Plan.SourceSnapshot.ContentHash.Length)]} · {session.Plan.Claims.Count} claims";
        _debriefChapters.Clear();
        foreach (var chapter in session.Plan.Chapters)
        {
            _debriefChapters.Add(new UiDebriefChapter(chapter.Id, chapter.Ordinal, chapter.Title, chapter.Objective, session.Turns.Any(turn => turn.ChapterId == chapter.Id)));
        }
        _debriefTurns.Clear();
        foreach (var turn in session.Turns)
        {
            _debriefTurns.Add(new UiDebriefTurn(turn.Id, turn.SpeakerName, turn.Text, string.Join(", ", turn.SafeSourceRefs), turn.Id == session.Turns.ElementAtOrDefault(session.CurrentTurnIndex)?.Id));
        }
        OnPropertyChanged(nameof(DebriefTitle));
        OnPropertyChanged(nameof(DebriefSourceText));
    }

    private static string CurrentProjectKey() => Path.GetFileName(Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public void OpenPresenceTarget(ActivationResult activation)
    {
        if (!activation.Accepted) return;
        if (activation.Surface == "needs-you")
        {
            SelectRail(RailDestination.NeedsYou);
            Forget(RefreshNeedsYouAsync());
        }
        else if (activation.Surface == "skills") SelectRail(RailDestination.Skills);
        else if (activation.Surface.StartsWith("settings", StringComparison.Ordinal)) SelectRail(RailDestination.Settings);
        else SelectRail(RailDestination.Mission);
    }

    private void OnNeedsYouChanged(object? sender, EventArgs e) => Forget(RefreshNeedsYouAsync());

    private void OnInAppNotification(object? sender, AbraxiusNotification notification) => _dispatcher.Post(() =>
    {
        _inAppNotifications.Insert(0, $"{notification.Title} · {notification.Body}");
        while (_inAppNotifications.Count > 16) _inAppNotifications.RemoveAt(_inAppNotifications.Count - 1);
        OnPropertyChanged(nameof(InAppNotifications));
    });

    private async Task RefreshNeedsYouAsync()
    {
        var items = await _runtime.Presence.NeedsYou.ListAsync().ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            _needsYou.Clear();
            foreach (var item in items)
            {
                var specialist = item.Source;
                var deadline = item.Deadline is { } value ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "No deadline";
                _needsYou.Add(new UiNeedsYouRow(item, specialist, item.Reason.ToString(), item.ContextSummary, item.Created.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), deadline, item.State.ToString()));
            }
            OnPropertyChanged(nameof(NeedsYouItems));
            OnPropertyChanged(nameof(NeedsYouCount));
            OnPropertyChanged(nameof(HasNeedsYou));
            UpdateNavigationBadge();
        });
    }

    private async Task ResolveNeedsYouAsync(UiNeedsYouRow? row, NeedsYouResolution resolution)
    {
        if (row is null) return;
        if (row.Item.Source.Equals("security", StringComparison.OrdinalIgnoreCase))
        {
            if (resolution == NeedsYouResolution.Approved) await _runtime.Security.Approvals.ApproveAsync(row.Item.Id, GrantScope.Once).ConfigureAwait(false);
            else await _runtime.Security.Approvals.RejectAsync(row.Item.Id, "Rejected by user.").ConfigureAwait(false);
            await RefreshSecurityAsync().ConfigureAwait(false);
        }
        else
        {
            await _runtime.Presence.NeedsYou.ResolveAsync(row.Item.Id, resolution, resolution == NeedsYouResolution.Approved ? "Approved by user." : "Rejected by user.").ConfigureAwait(false);
        }
        await RefreshNeedsYouAsync().ConfigureAwait(false);
    }

    private async Task RefreshSecurityAsync()
    {
        var status = await _runtime.Security.GetStatusAsync().ConfigureAwait(false);
        var grants = _runtime.Security.Grants.ListActive(DateTimeOffset.UtcNow);
        var audit = new List<SecurityAuditEvent>();
        await foreach (var item in _runtime.Security.Audit.QueryAsync(100).ConfigureAwait(false)) audit.Add(item);
        _dispatcher.Post(() =>
        {
            SecurityStatusText = status.Lockdown ? "LOCKDOWN" : status.Preset.ToString().ToUpperInvariant();
            SecurityOverviewText = $"Active grants {status.ActiveGrants} · Pending approvals {status.PendingApprovals} · Secrets {status.StoredSecrets} · Recent denials {status.RecentDenials}";
            SecurityPolicyText = $"{status.Preset} · default deny · raw secrets denied · external effects require scoped authority";
            _securityGrants.Clear();
            foreach (var grant in grants) _securityGrants.Add(new UiSecurityGrantRow(grant.GrantId.ToString(), grant.Subject.PrincipalId.Value,
                string.Join(", ", grant.Capabilities), grant.Scope.ToString(), grant.ExpiresAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), $"{grant.Uses}/{grant.MaximumUses?.ToString(CultureInfo.InvariantCulture) ?? "∞"}"));
            _securityAudit.Clear();
            foreach (var item in audit) _securityAudit.Add(new UiSecurityAuditRow(item.Timestamp.ToLocalTime().ToString("T", CultureInfo.CurrentCulture), item.Type.ToString(),
                item.Principal.Value, item.Action, item.Resource, item.ReasonCode ?? item.ResultCode ?? string.Empty));
            OnPropertyChanged(nameof(SecurityStatusText)); OnPropertyChanged(nameof(SecurityOverviewText)); OnPropertyChanged(nameof(SecurityPolicyText));
            OnPropertyChanged(nameof(SecurityGrants)); OnPropertyChanged(nameof(SecurityAudit));
        });
    }

    private async Task SnoozeNeedsYouAsync(UiNeedsYouRow? row)
    {
        if (row is null) return;
        await _runtime.Presence.NeedsYou.SnoozeAsync(row.Item.Id, DateTimeOffset.UtcNow.AddHours(1)).ConfigureAwait(false);
        await RefreshNeedsYouAsync().ConfigureAwait(false);
    }

    private async Task UpdatePresenceSettingsAsync(PresenceSettings settings)
    {
        await _runtime.Presence.UpdateSettingsAsync(settings).ConfigureAwait(false);
        _dispatcher.Post(() => { OnPropertyChanged(nameof(PresenceSettingsSummary)); OnPropertyChanged(nameof(NotificationSettingsSummary)); });
    }

    private void RefreshSkillsSurface()
    {
        _skills.Clear();
        foreach (var skill in _runtime.Skills.Registry.List(includeDisabled: true))
        {
            var scope = skill.Preconditions.Scope is { } value ? $"{value.Kind}:{value.Key}" : "Global";
            _skills.Add(new UiSkillRow(
                skill.Id.Value,
                skill.Version.ToString(),
                skill.State.ToString(),
                $"{skill.Statistics.Reliability:P0}",
                $"{skill.Statistics.VerifiedSuccesses}/{skill.Statistics.Executions} verified",
                scope,
                skill.Description));
        }
        SkillStatus = $"{_skills.Count} procedures · { _skills.Count(item => item.State is "Trusted" or "Validated") } eligible";
    }

    private void MatchSkillsSurface()
    {
        var objective = string.IsNullOrWhiteSpace(SkillQuery) ? CommandText : SkillQuery.Trim();
        var matches = _runtime.Skills.Match(new SkillMatchRequest(objective, ProjectKey: CurrentProjectKey()), 5);
        SkillStatus = matches.Count == 0
            ? "No eligible procedure matched. Fresh planning remains available."
            : string.Join("\n", matches.Select(match => $"{match.Skill.Id}/{match.Skill.Version} {match.Score:0.00} · {match.Explanation}"));
    }

    private void BuildNavigation()
    {
        _navigationGroups.Clear();
        _navigationItems.Clear();

        UiNavigationItem Item(
            string label,
            RailDestination? destination,
            string group,
            string icon,
            ICommand command,
            string description)
        {
            var item = new UiNavigationItem(label, destination, group, icon, command, description);
            item.SetSelected(destination == SelectedRail);
            _navigationItems.Add(item);
            return item;
        }

        _navigationGroups.Add(new UiNavigationGroup("WORK", [
            Item("Mission", RailDestination.Mission, "WORK", NavigationIcons.Mission, SelectMissionCommand, "Mission graph and execution state"),
            Item("Chat", RailDestination.Chat, "WORK", NavigationIcons.Chat, SelectChatCommand, "Direct conversation with Abraxius"),
            Item("Agents", RailDestination.Agents, "WORK", NavigationIcons.Agents, SelectAgentsCommand, "Specialist instances and assignments"),
            Item("Artifacts", RailDestination.Artifacts, "WORK", NavigationIcons.Artifacts, SelectArtifactsCommand, "Review immutable outputs and provenance")
        ]));
        _navigationGroups.Add(new UiNavigationGroup("KNOWLEDGE", [
            Item("Memory", RailDestination.Memory, "KNOWLEDGE", NavigationIcons.Memory, SelectMemoryCommand, "Search project knowledge"),
            Item("Skills", RailDestination.Skills, "KNOWLEDGE", NavigationIcons.Skills, SelectSkillsCommand, "Validated procedures"),
            Item("Debrief", RailDestination.Debrief, "KNOWLEDGE", NavigationIcons.Debrief, SelectDebriefCommand, "Interactive mission debrief"),
            Item("Evaluation", RailDestination.Evaluation, "KNOWLEDGE", NavigationIcons.Evaluation, SelectEvaluationCommand, "Evaluation Lab and release gates")
        ]));
        _navigationGroups.Add(new UiNavigationGroup("SYSTEM", [
            Item("Terminal", RailDestination.Terminal, "SYSTEM", NavigationIcons.Terminal, SelectTerminalCommand, "Direct terminal surface"),
            Item("Diagnostics", RailDestination.Diagnostics, "SYSTEM", NavigationIcons.Diagnostics, SelectDiagnosticsCommand, "AXL and runtime diagnostics"),
            Item("Fabric", RailDestination.Fabric, "SYSTEM", NavigationIcons.Fabric, SelectFabricCommand, "Distributed node fabric"),
            Item("Compute", RailDestination.Compute, "SYSTEM", NavigationIcons.Compute, SelectComputeCommand, "Local models and compute"),
            Item("Extensions", RailDestination.Extensions, "SYSTEM", NavigationIcons.Extensions, SelectExtensionsCommand, "Installed plugin extensions")
        ]));
        _navigationGroups.Add(new UiNavigationGroup("ATTENTION", [
            Item("Needs You", RailDestination.NeedsYou, "ATTENTION", NavigationIcons.NeedsYou, SelectNeedsYouCommand, "Items waiting for a decision"),
            Item("Security", RailDestination.Security, "ATTENTION", NavigationIcons.Security, SelectSecurityCommand, "Security grants and audit")
        ]));
        _navigationGroups.Add(new UiNavigationGroup("UTILITY", [
            Item("Commands", null, "UTILITY", NavigationIcons.Commands, OpenCommandPaletteCommand, "Open command palette"),
            Item("Settings", RailDestination.Settings, "UTILITY", NavigationIcons.Settings, SelectSettingsCommand, "Background and notification settings")
        ]));

        UpdateNavigationBadge();
        OnPropertyChanged(nameof(NavigationGroups));
    }

    private void UpdateNavigationBadge()
    {
        var needsYou = _navigationItems.FirstOrDefault(item => item.Destination == RailDestination.NeedsYou);
        if (needsYou is null) return;
        needsYou.BadgeText = NeedsYouCount > 0 ? NeedsYouCount.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private void RegisterCommands()
    {
        Register("mission.run", "Run mission", "Submit the current intent to the runtime", "Mission", "⌘↵", _ => new ValueTask(SubmitAsync()));
        Register("mission.cancel", "Cancel execution", "Stop the active execution", "Execution", "Esc", _ => { _runtime.CancelActiveExecution(); return ValueTask.CompletedTask; });
        Register("chat.open", "Open Chat", "Talk with Abraxius in a persistent conversation", "Chat", "", _ => { SelectRail(RailDestination.Chat); return ValueTask.CompletedTask; });
        Register("mission.graph", "Show semantic graph", "View dependency flow with semantic zoom", "Mission", "⌘1", _ => { SelectRail(RailDestination.Mission); MissionView = MissionViewMode.Graph; return ValueTask.CompletedTask; });
        Register("mission.lanes", "Show execution lanes", "View actual overlap and critical path timing", "Mission", "⌘2", _ => { SelectRail(RailDestination.Mission); MissionView = MissionViewMode.Lanes; return ValueTask.CompletedTask; });
        Register("mission.agents", "Show agent grid", "Inspect executor groups and active operations", "Execution", "⌘3", _ => { SelectRail(RailDestination.Agents); return ValueTask.CompletedTask; });
        Register("mission.activity", "Show activity stream", "Open typed execution blocks", "Mission", "⌘4", _ => { SelectRail(RailDestination.Mission); MissionView = MissionViewMode.Activity; return ValueTask.CompletedTask; });
        Register("panel.inspector", "Toggle inspector", "Show or hide contextual task details", "Execution", "⌘I", _ => { IsInspectorVisible = !IsInspectorVisible; return ValueTask.CompletedTask; });
        Register("panel.activity", "Toggle activity deck", "Show or hide the live activity deck", "Execution", "⌘J", _ => { IsActivityVisible = !IsActivityVisible; return ValueTask.CompletedTask; });
        Register("panel.terminal", "Open terminal", "Open a direct-executable terminal session", "Workspace", "⌘`", _ => { SelectRail(RailDestination.Terminal); return ValueTask.CompletedTask; });
        Register("diagnostics.performance", "Show performance diagnostics", "Inspect UI and scheduler pressure", "Execution", "", _ => { SelectRail(RailDestination.Diagnostics); return ValueTask.CompletedTask; });
        Register("diagnostics.axl", "Validate AXL", "Inspect canonical AXL without executing it", "Developer", "", _ => { SelectRail(RailDestination.Diagnostics); ParseAxl(); return ValueTask.CompletedTask; });
        Register("memory.explorer", "Open memory explorer", "Search persistent project knowledge and inspect retrieval evidence", "Memory", "", _ => { SelectRail(RailDestination.Memory); Forget(_memoryExplorer.SearchAsync()); return ValueTask.CompletedTask; });
        Register("skills.explorer", "Open Skills explorer", "Inspect validated procedures and procedural intelligence", "Skills", "", _ => { SelectRail(RailDestination.Skills); RefreshSkillsSurface(); return ValueTask.CompletedTask; });
        Register("skills.match", "Match a Skill", "Find a validated procedure for the current objective", "Skills", "", _ => { SelectRail(RailDestination.Skills); MatchSkillsSurface(); return ValueTask.CompletedTask; });
        Register("skills.refresh", "Refresh Skills", "Reload the local Skill registry view", "Skills", "", _ => { RefreshSkillsSurface(); return ValueTask.CompletedTask; });
        Register("progression.open", "Open Progression", "View verified career, mastery, achievements, Prestige, and statistics", "Progression", "", _ => { SelectRail(RailDestination.Progression); _progression.Refresh(); return ValueTask.CompletedTask; });
        Register("artifacts.open", "Open Artifacts", "Inspect immutable mission outputs, revisions, verification, and provenance", "Artifacts", "", _ => { SelectRail(RailDestination.Artifacts); _artifacts.ShowLibraryCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("artifacts.reviews", "Open Review Queue", "Review exact artifact revisions waiting for a product decision", "Artifacts", "", _ => { SelectRail(RailDestination.Artifacts); _artifacts.ShowReviewQueueCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("artifacts.approve", "Approve current Artifact", "Approve the exact selected artifact revision", "Artifacts", "", _ => { _artifacts.ApproveCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("artifacts.reject", "Reject current Artifact", "Reject the exact selected artifact revision", "Artifacts", "", _ => { _artifacts.RejectCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("artifacts.request-changes", "Request Artifact changes", "Return actionable feedback without mutating artifact history", "Artifacts", "", _ => { _artifacts.RequestChangesCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("evaluation.open", "Open Evaluation Lab", "Inspect suites, runs, comparisons, gates, and regressions", "Evaluation", "", _ => { SelectRail(RailDestination.Evaluation); Forget(_evaluation.RefreshAsync()); return ValueTask.CompletedTask; });
        Register("evaluation.smoke", "Run Smoke Evals", "Run the selected bounded evaluation suite", "Evaluation", "", _ => { SelectRail(RailDestination.Evaluation); _evaluation.RunSmokeCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("evaluation.compare", "Compare with Baseline", "Compare the two most recent compatible runs", "Evaluation", "", _ => { SelectRail(RailDestination.Evaluation); _evaluation.CompareRecentCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("evaluation.regressions", "Open Regressions", "Inspect release-blocking measured regressions", "Evaluation", "", _ => { SelectRail(RailDestination.Evaluation); _evaluation.ShowRegressionsCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("fabric.open", "Open Fabric", "Inspect authenticated nodes, resources, placement capabilities, and connectivity", "Fabric", "", _ => { SelectRail(RailDestination.Fabric); _fabric.Refresh(); return ValueTask.CompletedTask; });
        Register("compute.open", "Open Compute", "Inspect devices, model residency, workloads, and local inference backends", "Compute", "", _ => { SelectRail(RailDestination.Compute); Forget(_compute.RefreshAsync()); return ValueTask.CompletedTask; });
        Register("plugins.open", "Open Extensions", "Inspect installed plugins, permissions, isolated hosts, and diagnostics", "Extensions", "", _ => { SelectRail(RailDestination.Extensions); Forget(_extensions.RefreshAsync()); return ValueTask.CompletedTask; });
        Register("plugins.restart", "Restart Extension", "Restart the selected isolated PluginHost", "Extensions", "", _ => { SelectRail(RailDestination.Extensions); _extensions.RestartCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("presence.needs-you", "Open Needs You", "Review durable approvals and decisions waiting for you", "Presence", "", _ => { SelectRail(RailDestination.NeedsYou); Forget(RefreshNeedsYouAsync()); return ValueTask.CompletedTask; });
        Register("security.open", "Open Security", "Inspect bounded authority, grants, secrets, policies, and audit", "Security", "", _ => { SelectRail(RailDestination.Security); Forget(RefreshSecurityAsync()); return ValueTask.CompletedTask; });
        Register("security.lockdown", "Toggle Security Lockdown", "Immediately deny new mutations, external effects, and secret use", "Security", "", _ => { _runtime.Security.Kernel.Lockdown = !_runtime.Security.Kernel.Lockdown; Forget(RefreshSecurityAsync()); return ValueTask.CompletedTask; });
        Register("presence.settings", "Open Background & Notifications", "Configure close, background, privacy, and quiet-hours behavior", "Presence", "", _ => { SelectRail(RailDestination.Settings); return ValueTask.CompletedTask; });
        Register("progression.athena", "Open Athena mastery", "Inspect Coordinator mastery", "Progression", "", _ => { SelectRail(RailDestination.Progression); _progression.SelectSpecialistsCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("progression.orion", "Open Orion mastery", "Inspect Investigator mastery", "Progression", "", _ => { SelectRail(RailDestination.Progression); _progression.SelectSpecialistsCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("progression.daedalus", "Open Daedalus mastery", "Inspect Builder mastery", "Progression", "", _ => { SelectRail(RailDestination.Progression); _progression.SelectSpecialistsCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("progression.argus", "Open Argus mastery", "Inspect Verifier mastery", "Progression", "", _ => { SelectRail(RailDestination.Progression); _progression.SelectSpecialistsCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("debrief.create", "Create Debrief", "Turn current project evidence into a grounded expert discussion", "Debrief", "", _ => new ValueTask(CreateDebriefAsync()));
        Register("debrief.open", "Open Debrief", "Open the interactive Debrief player", "Debrief", "", _ => { SelectRail(RailDestination.Debrief); return ValueTask.CompletedTask; });
        Register("debrief.pause", "Pause Debrief", "Stop speech while preserving episode position", "Debrief", "", _ => new ValueTask(PauseDebriefAsync()));
        Register("debrief.resume", "Resume Debrief", "Resume the current grounded discussion", "Debrief", "", _ => new ValueTask(ResumeDebriefAsync()));
        Register("debrief.ask", "Ask Debrief", "Ask the current specialist a grounded question", "Debrief", "", _ => new ValueTask(AskDebriefAsync()));
        Register("debrief.to-mission", "Turn Debrief into mission", "Create a normal policy-governed mission from the current discussion", "Debrief", "", _ => new ValueTask(TurnDebriefIntoMissionAsync()));
        Register("settings.motion", "Toggle reduced motion", "Respect a quieter visual activity profile", "Settings", "", _ => { ReducedMotion = !ReducedMotion; return ValueTask.CompletedTask; });
        Register("settings.density", "Cycle density", "Switch compact, comfortable, and touch density", "Settings", "", _ => { Density = Density switch { UiDensity.Compact => UiDensity.Comfortable, UiDensity.Comfortable => UiDensity.Touch, _ => UiDensity.Compact }; return ValueTask.CompletedTask; });
        Register("activity.filter", "Cycle activity filter", "Filter the typed activity stream", "Execution", "", _ => { CycleActivityFilterCommand.Execute(null); return ValueTask.CompletedTask; });
        Register("voice.toggle", "Start voice input", "Open the microphone voice session", "Voice", "", _context => { Forget(ToggleVoiceAsync()); return ValueTask.CompletedTask; });
        Register("voice.interrupt", "Interrupt speech", "Stop the current spoken response", "Voice", "Esc", _context => { Forget(_voice.InterruptAsync("command interrupt")); return ValueTask.CompletedTask; });
        Register("voice.mode", "Cycle voice mode", "Switch always-listening, wake-word, push-to-talk, and manual voice modes", "Voice", "", _ =>
        {
            VoiceSettings = VoiceSettings with
            {
                Mode = VoiceSettings.Mode switch
                {
                    VoiceMode.AlwaysListening => VoiceMode.WakeWord,
                    VoiceMode.WakeWord => VoiceMode.PushToTalk,
                    VoiceMode.PushToTalk => VoiceMode.Manual,
                    _ => VoiceMode.AlwaysListening
                }
            };
            OnPropertyChanged(nameof(VoiceSettingsText));
            return ValueTask.CompletedTask;
        });
        Register("voice.privacy", "Toggle private voice", "Keep speech routing local when enabled", "Voice", "", _ =>
        {
            VoiceSettings = VoiceSettings with { PrivateMode = !VoiceSettings.PrivateMode };
            OnPropertyChanged(nameof(VoiceSettingsText));
            return ValueTask.CompletedTask;
        });
        Register("voice.routing", "Cycle speech routing", "Change quality, balanced, local-first, or private speech routing", "Voice", "", _ =>
        {
            VoiceSettings = VoiceSettings with
            {
                RoutingMode = VoiceSettings.RoutingMode switch
                {
                    SpeechRoutingMode.Quality => SpeechRoutingMode.BalancedQuality,
                    SpeechRoutingMode.BalancedQuality => SpeechRoutingMode.LocalFirst,
                    SpeechRoutingMode.LocalFirst => SpeechRoutingMode.Private,
                    _ => SpeechRoutingMode.Quality
                }
            };
            OnPropertyChanged(nameof(VoiceSettingsText));
            return ValueTask.CompletedTask;
        });
        Register("voice.barge-in", "Toggle barge-in", "Allow speech to interrupt active Abraxius playback", "Voice", "", _ =>
        {
            VoiceSettings = VoiceSettings with { BargeInEnabled = !VoiceSettings.BargeInEnabled };
            OnPropertyChanged(nameof(VoiceSettingsText));
            return ValueTask.CompletedTask;
        });
        Register("update.check", "Check for updates", "Ask the trusted release source for a newer compatible build", "Updates", "", _ => new ValueTask(CheckForUpdatesAsync(download: true)));
        Register("update.download", "Download available update", "Download and verify the selected release in the background", "Updates", "", _ => new ValueTask(DownloadUpdateAsync()));
        Register("update.restart", "Restart to update", "Checkpoint and restart into the downloaded release", "Updates", "", _ => new ValueTask(RestartToUpdateAsync()));
        Register("update.channel", "Change update channel", "Cycle stable, beta, and development release channels", "Updates", "", _ => new ValueTask(CycleUpdateChannelAsync()));
        Register("update.diagnostics", "Open update diagnostics", "Inspect installation ownership and update state", "Updates", "", _ => { SelectRail(RailDestination.Diagnostics); return ValueTask.CompletedTask; });
    }

    private async Task ToggleVoiceAsync()
    {
        if (VoiceIsActive)
        {
            _voiceSessionCancellation?.Cancel();
            VoiceStatus = "VOICE STOPPING";
            return;
        }

        _voiceSessionCancellation?.Dispose();
        _voiceSessionCancellation = new CancellationTokenSource();
        VoiceStatus = "VOICE STARTING";
        try
        {
            var settings = VoiceSettings;
            await _voice.RunAsync(new VoiceSessionOptions(
                Mode: settings.WakeWordEnabled ? VoiceMode.WakeWord : settings.Mode,
                RoutingMode: settings.RoutingMode,
                Capture: new AudioCaptureOptions(AudioFormat.NormalizedSpeech, TimeSpan.FromMilliseconds(20), settings.InputDeviceId, settings.Preprocessing),
                Playback: new AudioPlaybackOptions(AudioFormat.NormalizedSpeech, settings.OutputDeviceId),
                Voice: settings.Voice,
                Language: settings.Language,
                AutoSubmitFinalTranscript: settings.AutoSubmitFinalTranscript,
                BargeInEnabled: settings.BargeInEnabled,
                PrivateMode: settings.PrivateMode), _voiceSessionCancellation.Token).ConfigureAwait(false);
        }
        catch (SpeechProviderException exception)
        {
            VoiceState = VoiceTurnState.Error;
            VoiceStatus = $"VOICE {exception.Error.Code}";
        }
        catch (OperationCanceledException) when (_voiceSessionCancellation?.IsCancellationRequested == true)
        {
            VoiceState = VoiceTurnState.Idle;
            VoiceStatus = "VOICE IDLE";
        }
        finally
        {
            _voiceSessionCancellation?.Dispose();
            _voiceSessionCancellation = null;
            OnPropertyChanged(nameof(VoiceIsActive));
        }
    }

    private async Task ObserveVoiceEventsAsync()
    {
        try
        {
            await foreach (var item in _voiceEvents.ReadEventsAsync(_voiceLifetime.Token).ConfigureAwait(false))
            {
                _voiceMetrics.Observe(item);
                _dispatcher.Post(() =>
                {
                    VoiceState = item switch
                    {
                        VoiceEvent.ListeningStarted => VoiceTurnState.Listening,
                        VoiceEvent.SpeechDetected => VoiceTurnState.SpeechDetected,
                        VoiceEvent.PartialTranscriptUpdated => VoiceTurnState.Transcribing,
                        VoiceEvent.TranscriptFinalized => VoiceTurnState.Processing,
                        VoiceEvent.SpeechGenerationStarted => VoiceTurnState.Speaking,
                        VoiceEvent.PlaybackStarted => VoiceTurnState.Speaking,
                        VoiceEvent.VoiceInterrupted => VoiceTurnState.Interrupted,
                        VoiceEvent.PlaybackStopped => VoiceTurnState.Listening,
                        VoiceEvent.ErrorEvent => VoiceTurnState.Error,
                        _ => VoiceState
                    };
                    if (item is VoiceEvent.PartialTranscriptUpdated partial) VoiceTranscript = partial.Text;
                    if (item is VoiceEvent.TranscriptFinalized finalized) VoiceTranscript = finalized.Text;
                    VoiceStatus = $"VOICE {VoiceState.ToString().ToUpperInvariant()}";
                    OnPropertyChanged(nameof(VoiceIsActive));
                    OnPropertyChanged(nameof(VoiceDiagnosticsText));
                });
            }
        }
        catch (OperationCanceledException) when (_voiceLifetime.IsCancellationRequested)
        {
        }
    }

    private async Task ObserveVoiceTelemetryAsync()
    {
        try
        {
            var lastPublished = System.Diagnostics.Stopwatch.GetTimestamp();
            await foreach (var item in _voiceEvents.ReadTelemetryAsync(_voiceLifetime.Token).ConfigureAwait(false))
            {
                _voiceMetrics.Observe(item);
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(lastPublished);
                if (elapsed < TimeSpan.FromMilliseconds(100)) continue;
                lastPublished = System.Diagnostics.Stopwatch.GetTimestamp();
                _dispatcher.Post(() => OnPropertyChanged(nameof(VoiceDiagnosticsText)));
            }
        }
        catch (OperationCanceledException) when (_voiceLifetime.IsCancellationRequested)
        {
        }
    }

    private async Task CheckForUpdatesAsync(bool download)
    {
        try
        {
            var result = await _updates.CheckAsync().ConfigureAwait(false);
            if (result.IsAvailable && download)
            {
                await DownloadUpdateAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _dispatcher.Post(() => OnPropertyChanged(nameof(UpdateStatusText)));
        }
    }

    private async Task MonitorUpdatesAsync()
    {
        if (!_updates.IsSupported)
        {
            return;
        }

        try
        {
            await new UpdateMonitor(_updates).RunAsync(_updateLifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested)
        {
        }
    }

    private async Task DownloadUpdateAsync()
    {
        var update = _updates.AvailableUpdate;
        if (update is null)
        {
            return;
        }

        var progress = new Progress<UpdateProgress>(value =>
        {
            _updateProgress = value;
            OnPropertyChanged(nameof(UpdateStatusText));
        });
        await _updates.DownloadAsync(update, progress).ConfigureAwait(false);
    }

    private async Task RestartToUpdateAsync()
    {
        var update = _updates.DownloadedUpdate;
        if (update is null)
        {
            return;
        }

        await _updateCoordinator.ApplyAsync(update, UpdateApplyMode.ApplyOnExit).ConfigureAwait(false);
    }

    private void OnUpdateStateChanged(object? sender, UpdateStateChangedEventArgs eventArgs)
    {
        _dispatcher.Post(() =>
        {
            _updateProgress = eventArgs.Current == UpdateState.Downloading ? _updateProgress : null;
            OnPropertyChanged(nameof(UpdateStatusText));
            OnPropertyChanged(nameof(UpdateDetailsText));
            OnPropertyChanged(nameof(VersionText));
            OnPropertyChanged(nameof(IsUpdateReady));
            OnPropertyChanged(nameof(AvailableUpdate));
            (DownloadUpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (RestartUpdateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        });
    }

    private async Task CycleUpdateChannelAsync()
    {
        var next = _updates.Channel switch
        {
            UpdateChannel.Stable => UpdateChannel.Beta,
            UpdateChannel.Beta => UpdateChannel.Development,
            _ => UpdateChannel.Stable
        };
        if (await _updates.SetChannelAsync(next).ConfigureAwait(false))
        {
            _dispatcher.Post(() =>
            {
                OnPropertyChanged(nameof(UpdateStatusText));
                OnPropertyChanged(nameof(UpdateDetailsText));
            });
        }
    }

    private void Register(string id, string title, string description, string category, string shortcut, Func<CommandContext, ValueTask> execute) =>
        _commands.Register(new CommandDescriptor(id, title, description, category, shortcut, execute));

    private void RefreshCommandResults()
    {
        CommandResults = _commands.Search(CommandSearchText)
            .Select(command => new CommandItemViewModel(command, command.CreateCommand(this)))
            .ToArray();
    }

    private void SetStatus(string status) => _dispatcher.Post(() => Status = status);

    private static void Forget(Task operation) => _ = operation;

    private static void Forget(ValueTask operation) => _ = operation.AsTask();

    private void SaveLayout() => _ = Task.Run(() => _layoutStore.Save(_layout));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class LayoutPreferencesStore
{
    private const int CurrentSchemaVersion = 1;
    private readonly string _path;

    public LayoutPreferencesStore(IPlatformEnvironment environment)
    {
        var provider = new DefaultPlatformPathProvider(environment);
        _path = Path.Combine(provider.ApplicationDataDirectory, "ui-layout.json");
    }

    public UiLayoutPreferences Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(_path));
                if (document.RootElement.TryGetProperty("preferences", out var preferences))
                {
                    return JsonSerializer.Deserialize<UiLayoutPreferences>(preferences.GetRawText()) ?? new UiLayoutPreferences();
                }

                // Schema 0 was the unwrapped layout record. Read it once and the next save
                // upgrades it to a versioned envelope without losing user layout preferences.
                var legacy = JsonSerializer.Deserialize<UiLayoutPreferences>(document.RootElement.GetRawText()) ?? new UiLayoutPreferences();
                Save(legacy);
                return legacy;
            }
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }

        return new UiLayoutPreferences();
    }

    public void Save(UiLayoutPreferences preferences)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(new PersistedLayoutState(CurrentSchemaVersion, preferences)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PersistedLayoutState(int SchemaVersion, UiLayoutPreferences Preferences);
}

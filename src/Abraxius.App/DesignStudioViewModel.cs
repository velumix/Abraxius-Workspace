using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Media.Imaging;
using Abraxius.Design;
using Abraxius.Runtime;
using Abraxius.Security;

namespace Abraxius.App;

public sealed record DesignSurfaceRow(DesignSurfaceId Id, string Name, string Category, string Profiles)
{
    public override string ToString() => Name;
}

public sealed class DesignCandidateRow : INotifyPropertyChanged
{
    private readonly DesignCandidate _candidate;
    private readonly int _ordinal;
    private bool _selected;
    public DesignCandidateRow(DesignCandidate candidate, int ordinal)
    {
        _candidate = candidate;
        _ordinal = ordinal;
        PreviewImage = CreatePreview(candidate.ScreenshotPng);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public DesignCandidate Candidate => _candidate;
    public int Ordinal => _ordinal;
    public string Label => ((char)('A' + Math.Clamp(_ordinal - 1, 0, 25))).ToString();
    public string Title => _candidate.Title;
    public string Provider => _candidate.SafeMetadata.GetValueOrDefault("provider", "Google Stitch");
    public string Viewport => $"{_candidate.Viewport.Width}×{_candidate.Viewport.Height} · {_candidate.Viewport.Profile}";
    public string Artifact => _candidate.ArtifactReference ?? "Not persisted";
    public string PreviewStatus => _candidate.ScreenshotPng is { Length: > 0 } ? "Preview captured" : "Provider preview unavailable";
    public bool HasPreview => PreviewImage is not null;
    public Bitmap? PreviewImage { get; }
    public bool IsSelected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } }

    private static Bitmap? CreatePreview(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try { using var stream = new MemoryStream(bytes, writable: false); return new Bitmap(stream); }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>Design Studio state. It owns no provider logic; generation remains in Phase 22's DesignRuntime.</summary>
public sealed class DesignStudioViewModel : INotifyPropertyChanged
{
    private readonly DesignRuntime _runtime;
    private readonly IUiDispatcher _dispatcher;
    private readonly Func<DesignCandidate, Task>? _implement;
    private readonly Func<Task>? _connect;
    private string _objective = "Make this surface clearer, calmer, and easier to use.";
    private DesignSurfaceRow? _selectedSurface;
    private DesignSession? _session;
    private DesignCandidateRow? _selectedCandidate;
    private string _status = "READY";
    private string _providerStatus = "CHECKING PROVIDER";
    private bool _isWorking;

    public DesignStudioViewModel(DesignRuntime runtime, IUiDispatcher dispatcher, Func<DesignCandidate, Task>? implement = null, Func<Task>? connect = null)
    {
        _runtime = runtime;
        _dispatcher = dispatcher;
        _implement = implement;
        _connect = connect;
        Surfaces = runtime.Surfaces.List().Select(surface => new DesignSurfaceRow(surface.Id, surface.DisplayName, surface.Category.ToString(), string.Join(" · ", surface.Describe().SafeResponsiveProfiles))).ToArray();
        _selectedSurface = Surfaces.FirstOrDefault(row => row.Id == DesignSurfaceId.ChatWorkspace) ?? (Surfaces.Count > 0 ? Surfaces[0] : null);
        GenerateCommand = new AsyncRelayCommand(GenerateAsync, () => !IsWorking && SelectedSurface is not null && !string.IsNullOrWhiteSpace(Objective));
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsWorking && _connect is not null);
        SelectCandidateCommand = new RelayCommand(parameter => SelectCandidate(parameter as DesignCandidateRow));
        ImplementCommand = new AsyncRelayCommand(ImplementAsync, () => SelectedCandidate is not null && !IsWorking);
        RefineCommand = new AsyncRelayCommand(RefineAsync, () => SelectedCandidate is not null && !IsWorking);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<DesignSurfaceRow> Surfaces { get; }
    public ObservableCollection<DesignCandidateRow> Candidates { get; } = [];
    public ICommand GenerateCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand SelectCandidateCommand { get; }
    public ICommand ImplementCommand { get; }
    public ICommand RefineCommand { get; }
    public string Objective { get => _objective; set { if (SetProperty(ref _objective, value)) (GenerateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public DesignSurfaceRow? SelectedSurface { get => _selectedSurface; set { if (SetProperty(ref _selectedSurface, value)) (GenerateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public DesignCandidateRow? SelectedCandidate { get => _selectedCandidate; private set { if (!SetProperty(ref _selectedCandidate, value)) return; (ImplementCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (RefineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public DesignSession? Session { get => _session; private set { SetProperty(ref _session, value); OnPropertyChanged(nameof(SessionState)); OnPropertyChanged(nameof(SessionSummary)); } }
    public string SessionState => Session?.State.ToString().ToUpperInvariant() ?? "NO ACTIVE SESSION";
    public string SessionSummary => Session?.Generation is { } generation ? $"{generation.SafeCandidates.Length} candidate(s) · {generation.Provider} · source {generation.SourceSnapshot.SnapshotHash[..12]}" : "No candidates yet.";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string ProviderStatus { get => _providerStatus; private set => SetProperty(ref _providerStatus, value); }
    public bool IsWorking { get => _isWorking; private set { if (!SetProperty(ref _isWorking, value)) return; (GenerateCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (ConnectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (ImplementCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (RefineCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); } }
    public bool HasCandidates => Candidates.Count > 0;

    public async Task RefreshAsync()
    {
        try
        {
            var health = await _runtime.GetHealthAsync().ConfigureAwait(true);
            ProviderStatus = $"{health.State.ToString().ToUpperInvariant()} · {health.Message}";
        }
        catch (Exception exception) { ProviderStatus = $"UNAVAILABLE · {exception.Message}"; }
    }

    public async Task ConnectAsync()
    {
        if (_connect is null) return;
        IsWorking = true;
        Status = "CONNECTING · SYSTEM BROWSER";
        try
        {
            await _connect().ConfigureAwait(true);
            await RefreshAsync().ConfigureAwait(true);
            Status = "CONNECTED · READY TO GENERATE";
        }
        catch (Exception exception) { Status = $"CONNECTION FAILED · {exception.Message}"; }
        finally { IsWorking = false; }
    }

    public async Task GenerateAsync()
    {
        if (SelectedSurface is null || string.IsNullOrWhiteSpace(Objective)) return;
        IsWorking = true;
        Status = "CAPTURING · COMPILING · GENERATING";
        Candidates.Clear();
        OnPropertyChanged(nameof(HasCandidates));
        try
        {
            var session = await _runtime.Orchestrator.GenerateAsync(SelectedSurface.Id, Objective.Trim(), new DesignCaptureRequest(DesignViewportProfile.Expanded, 1920, 1080, Mode: DesignCaptureMode.SyntheticContent), DataClassification.Internal, 3).ConfigureAwait(true);
            ApplySession(session);
            Status = $"READY · {Candidates.Count} CANDIDATES";
        }
        catch (DesignProviderSecurityException exception) { Status = $"BLOCKED · {exception.Message}"; }
        catch (Exception exception) { Status = $"FAILED · {exception.Message}"; }
        finally { IsWorking = false; }
    }

    public void ApplySession(DesignSession session)
    {
        Session = session;
        Candidates.Clear();
        var ordinal = 1;
        foreach (var candidate in session.Generation?.SafeCandidates ?? []) Candidates.Add(new DesignCandidateRow(candidate, ordinal++));
        if (Candidates.Count > 0) SelectCandidate(Candidates[0]);
        OnPropertyChanged(nameof(HasCandidates));
    }

    private void SelectCandidate(DesignCandidateRow? row)
    {
        foreach (var candidate in Candidates) candidate.IsSelected = ReferenceEquals(candidate, row);
        SelectedCandidate = row;
    }

    private async Task ImplementAsync()
    {
        if (SelectedCandidate is null || _implement is null) return;
        await _implement(SelectedCandidate.Candidate).ConfigureAwait(true);
    }

    private async Task RefineAsync()
    {
        if (Session is null || SelectedCandidate is null) return;
        var instruction = Objective.Trim();
        IsWorking = true;
        Status = "REFINING · PINNED TO SELECTED CANDIDATE";
        try { ApplySession(await _runtime.Orchestrator.RefineAsync(Session.Id, SelectedCandidate.Candidate.Id, instruction).ConfigureAwait(true)); Status = "READY · REFINED CANDIDATE ADDED"; }
        catch (Exception exception) { Status = $"REFINE FAILED · {exception.Message}"; }
        finally { IsWorking = false; }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

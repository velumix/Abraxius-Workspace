using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Abraxius.Core;
using Abraxius.Evaluation;
using Abraxius.Protocol;
using Abraxius.Runtime;

namespace Abraxius.App;

public enum EvaluationSection { Overview, Suites, Runs, Comparisons, Models, Specialists, Skills, Retrieval, Security, Regressions }
public sealed record EvalSuiteRow(string Id, string Version, string Name, string Domain, int Cases, string State);
public sealed record EvalRunRow(string Id, string Suite, string Candidate, string Status, string Cases, string Started, string Gate);
public sealed record EvalRegressionRow(string Id, string Suite, string Metric, string Delta, string Severity, string State, string Evidence);
public sealed record EvalMetricRow(string Name, string Value, string Unit, string Samples);

public sealed class EvaluationViewModel : INotifyPropertyChanged
{
    private readonly AbraxiusRuntimeHost _runtime; private readonly IUiDispatcher _dispatcher;
    private EvaluationSection _section; private EvalSuiteRow? _selectedSuite; private EvalRunRow? _selectedRun; private EvalRegressionRow? _selectedRegression;
    private string _status = "EVALUATION READY"; private string _overview = "No evaluation run selected."; private bool _running;
    public EvaluationViewModel(AbraxiusRuntimeHost runtime, IUiDispatcher dispatcher)
    {
        _runtime = runtime; _dispatcher = dispatcher;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync); RunSmokeCommand = new AsyncRelayCommand(RunSmokeAsync, () => !IsRunning);
        CompareRecentCommand = new AsyncRelayCommand(CompareRecentAsync, () => Runs.Count >= 2 && !IsRunning);
        CreateMissionCommand = new AsyncRelayCommand(CreateMissionAsync, () => SelectedRegression is not null && !IsRunning);
        ShowOverviewCommand = SectionCommand(EvaluationSection.Overview); ShowSuitesCommand = SectionCommand(EvaluationSection.Suites); ShowRunsCommand = SectionCommand(EvaluationSection.Runs);
        ShowModelsCommand = SectionCommand(EvaluationSection.Models); ShowSpecialistsCommand = SectionCommand(EvaluationSection.Specialists); ShowSkillsCommand = SectionCommand(EvaluationSection.Skills);
        ShowRetrievalCommand = SectionCommand(EvaluationSection.Retrieval); ShowSecurityCommand = SectionCommand(EvaluationSection.Security); ShowRegressionsCommand = SectionCommand(EvaluationSection.Regressions);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<EvalSuiteRow> Suites { get; } = []; public ObservableCollection<EvalRunRow> Runs { get; } = []; public ObservableCollection<EvalRegressionRow> Regressions { get; } = []; public ObservableCollection<EvalMetricRow> Metrics { get; } = [];
    public ICommand RefreshCommand { get; } public ICommand RunSmokeCommand { get; } public ICommand CompareRecentCommand { get; } public ICommand CreateMissionCommand { get; }
    public ICommand ShowOverviewCommand { get; } public ICommand ShowSuitesCommand { get; } public ICommand ShowRunsCommand { get; } public ICommand ShowModelsCommand { get; } public ICommand ShowSpecialistsCommand { get; } public ICommand ShowSkillsCommand { get; } public ICommand ShowRetrievalCommand { get; } public ICommand ShowSecurityCommand { get; } public ICommand ShowRegressionsCommand { get; }
    public EvaluationSection Section { get => _section; private set { if (_section == value) return; _section = value; OnPropertyChanged(); OnPropertyChanged(nameof(SectionTitle)); } }
    public string SectionTitle => Section.ToString().ToUpperInvariant();
    public EvalSuiteRow? SelectedSuite { get => _selectedSuite; set { if (_selectedSuite == value) return; _selectedSuite = value; OnPropertyChanged(); } }
    public EvalRunRow? SelectedRun { get => _selectedRun; set { if (_selectedRun == value) return; _selectedRun = value; OnPropertyChanged(); if (value is not null) _ = InspectRunAsync(value); } }
    public EvalRegressionRow? SelectedRegression { get => _selectedRegression; set { if (_selectedRegression == value) return; _selectedRegression = value; OnPropertyChanged(); RaiseCommands(); } }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; OnPropertyChanged(); } }
    public string Overview { get => _overview; private set { if (_overview == value) return; _overview = value; OnPropertyChanged(); } }
    public bool IsRunning { get => _running; private set { if (_running == value) return; _running = value; OnPropertyChanged(); RaiseCommands(); } }

    public async Task RefreshAsync()
    {
        var suites = await _runtime.Evaluation.Store.ListSuitesAsync().ConfigureAwait(false); var runs = await _runtime.Evaluation.Store.ListRunsAsync(limit: 500).ConfigureAwait(false); var regressions = await _runtime.Evaluation.Store.ListRegressionsAsync(limit: 500).ConfigureAwait(false);
        _dispatcher.Post(() =>
        {
            Suites.Clear(); foreach (var suite in suites.GroupBy(static item => item.Id).Select(static group => group.OrderByDescending(item => item.Version, StringComparer.Ordinal).First())) Suites.Add(new(suite.Id.Value, suite.Version, suite.Name, suite.Domain.ToString(), suite.Cases.Length, suite.State.ToString()));
            Runs.Clear(); foreach (var run in runs) Runs.Add(new(run.Id.ToString(), run.SuiteId.Value, run.Candidate, run.Status.ToString(), $"{run.Passed} pass · {run.Failed} fail · {run.InfrastructureFailures} infra", run.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), run.ReleaseBlocked ? "BLOCKED" : "RECORDED"));
            Regressions.Clear(); foreach (var regression in regressions) Regressions.Add(new(regression.Id.ToString(), regression.SuiteId.Value, regression.MetricId.Value, regression.Delta.ToString("+0.###;-0.###;0", CultureInfo.InvariantCulture), regression.Severity.ToString(), regression.State.ToString(), regression.Evidence));
            Status = $"{Suites.Count} SUITES · {Runs.Count} RUNS · {Regressions.Count} REGRESSIONS"; RaiseCommands();
        });
    }

    private async Task RunSmokeAsync()
    {
        IsRunning = true; Status = "RUNNING CORE MISSION SMOKE";
        try
        {
            var suite = BuiltInEvalSuites.Find(SelectedSuite?.Id ?? "core.mission-smoke") ?? BuiltInEvalSuites.Find("core.mission-smoke")!;
            var version = typeof(AbraxiusRuntimeHost).Assembly.GetName().Version?.ToString() ?? "unknown";
            var environment = EvalEnvironmentCapture.Capture(version, "working-tree", typeof(Avalonia.Application).Assembly.GetName().Version?.ToString() ?? "unknown", "axl/1", "security/1");
            var candidate = new EvalCandidate(EvalCandidateId.New(), $"Abraxius {version}", "working-tree", "working-tree");
            var run = await _runtime.RunEvaluationAsync(new(suite, candidate, environment, Preset: EvalSamplingPreset.Smoke)).ConfigureAwait(false);
            _dispatcher.Post(() => { Status = $"{run.Status.ToString().ToUpperInvariant()} · {run.CaseResults.Length} CASE EXECUTIONS"; }); await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception exception) { _dispatcher.Post(() => Status = $"EVAL ERROR · {exception.GetType().Name}"); }
        finally { _dispatcher.Post(() => IsRunning = false); }
    }

    private async Task CompareRecentAsync()
    {
        if (Runs.Count < 2 || !Guid.TryParse(Runs[1].Id, out var baseline) || !Guid.TryParse(Runs[0].Id, out var candidate)) return;
        IsRunning = true;
        try { var comparison = await _runtime.Evaluation.CompareAsync(new(baseline), new(candidate)).ConfigureAwait(false); _dispatcher.Post(() => { Status = comparison.ReleaseBlocked ? "RELEASE BLOCKED" : "COMPARISON COMPLETE"; Overview = $"Comparable workload {comparison.SameWorkload}\nEnvironment compatible {comparison.EnvironmentCompatible}\nRegressions {comparison.Regressions.Length}\nImprovements {comparison.Improvements.Length}\nGate {(comparison.ReleaseBlocked ? "BLOCKED" : "PASS")}"; Section = EvaluationSection.Comparisons; }); await RefreshAsync().ConfigureAwait(false); }
        catch (Exception exception) { _dispatcher.Post(() => Status = $"COMPARE ERROR · {exception.GetType().Name}"); }
        finally { _dispatcher.Post(() => IsRunning = false); }
    }

    private async Task InspectRunAsync(EvalRunRow row)
    {
        if (!Guid.TryParse(row.Id, out var id)) return; var run = await _runtime.Evaluation.Store.GetRunAsync(new(id)).ConfigureAwait(false); if (run is null) return;
        _dispatcher.Post(() =>
        {
            Metrics.Clear(); foreach (var metric in run.Metrics) Metrics.Add(new(metric.MetricId.Value, metric.Value?.ToString("0.####", CultureInfo.InvariantCulture) ?? metric.Availability.ToString(), metric.Unit, metric.SampleCount.ToString(CultureInfo.InvariantCulture)));
            Overview = $"{run.Candidate.Name}\nSuite {run.SuiteId}/{run.SuiteVersion}\nEnvironment {run.Environment.Fingerprint}\nGit {run.Environment.GitCommit}\nStatus {run.Status}\nCases {run.CaseResults.Length}\nEval artifact {run.ReportArtifactRevision?.ToString() ?? "none"}\nProgression INELIGIBLE";
        });
    }

    private async Task CreateMissionAsync()
    {
        if (SelectedRegression is null || !Guid.TryParse(SelectedRegression.Id, out var id)) return;
        var regression = (await _runtime.Evaluation.Store.ListRegressionsAsync(limit: 1000).ConfigureAwait(false)).FirstOrDefault(item => item.Id == new EvalRegressionId(id)); if (regression is null) return;
        var objective = $"Investigate and repair evaluation regression {regression.MetricId} in {regression.SuiteId}. Baseline {regression.Baseline:R}; candidate {regression.Candidate:R}.";
        IsRunning = true; try { await _runtime.RunMissionAsync(new Intent(objective, CorrelationId.New(), new Dictionary<string, string> { ["evalRegressionId"] = regression.Id.ToString(), ["evalSuiteId"] = regression.SuiteId.Value, ["evalMetricId"] = regression.MetricId.Value })).ConfigureAwait(false); _dispatcher.Post(() => Status = "REGRESSION MISSION COMPLETED"); } catch (Exception exception) { _dispatcher.Post(() => Status = $"MISSION ERROR · {exception.GetType().Name}"); } finally { _dispatcher.Post(() => IsRunning = false); }
    }

    private RelayCommand SectionCommand(EvaluationSection section) => new(() => Section = section);
    private void RaiseCommands() { (RunSmokeCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (CompareRecentCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (CreateMissionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

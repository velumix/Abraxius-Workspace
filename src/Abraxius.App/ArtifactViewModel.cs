using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Abraxius.Artifacts;
using Abraxius.Protocol;
using Abraxius.Security;

namespace Abraxius.App;

public enum ArtifactSurfaceMode { Library, ReviewQueue }
public sealed record ArtifactRow(string Id, string RevisionId, string Title, string Kind, string State, string Producer, string Verification, string Approval, string Updated, string Summary);
public sealed record ArtifactRevisionRow(string Id, string Label, string State, string Hash, string Created, bool IsCurrent);
public sealed record ArtifactDiffRow(string Marker, string OldLine, string NewLine, string Text);

public sealed class ArtifactViewModel : INotifyPropertyChanged
{
    private readonly ArtifactRuntime _runtime;
    private readonly IUiDispatcher _dispatcher;
    private ArtifactSurfaceMode _mode;
    private ArtifactRow? _selected;
    private string _status = "ARTIFACTS READY";
    private string _inspector = "Select an artifact to inspect exact revision provenance.";
    private string _reviewReason = string.Empty;

    public ArtifactViewModel(ArtifactRuntime runtime, IUiDispatcher dispatcher)
    {
        _runtime = runtime; _dispatcher = dispatcher;
        ShowLibraryCommand = new RelayCommand(() => { Mode = ArtifactSurfaceMode.Library; _ = RefreshAsync(); });
        ShowReviewQueueCommand = new RelayCommand(() => { Mode = ArtifactSurfaceMode.ReviewQueue; _ = RefreshAsync(); });
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        InspectCommand = new AsyncRelayCommand(InspectSelectedAsync, () => Selected is not null);
        ApproveCommand = new AsyncRelayCommand(() => DecideAsync(ArtifactApprovalState.Approved), CanDecide);
        RejectCommand = new AsyncRelayCommand(() => DecideAsync(ArtifactApprovalState.Rejected), CanDecide);
        RequestChangesCommand = new AsyncRelayCommand(() => DecideAsync(ArtifactApprovalState.ChangesRequested), CanDecide);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ArtifactRow> Items { get; } = [];
    public ObservableCollection<ArtifactRevisionRow> Revisions { get; } = [];
    public ObservableCollection<ArtifactDiffRow> DiffLines { get; } = [];
    public ICommand ShowLibraryCommand { get; }
    public ICommand ShowReviewQueueCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand InspectCommand { get; }
    public ICommand ApproveCommand { get; }
    public ICommand RejectCommand { get; }
    public ICommand RequestChangesCommand { get; }
    public ArtifactSurfaceMode Mode { get => _mode; private set { if (_mode == value) return; _mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsLibrary)); OnPropertyChanged(nameof(IsReviewQueue)); } }
    public bool IsLibrary => Mode == ArtifactSurfaceMode.Library;
    public bool IsReviewQueue => Mode == ArtifactSurfaceMode.ReviewQueue;
    public ArtifactRow? Selected { get => _selected; set { if (_selected == value) return; _selected = value; OnPropertyChanged(); RaiseCommands(); if (value is not null) _ = InspectSelectedAsync(); } }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; OnPropertyChanged(); } }
    public string Inspector { get => _inspector; private set { if (_inspector == value) return; _inspector = value; OnPropertyChanged(); } }
    public string ReviewReason { get => _reviewReason; set { if (_reviewReason == value) return; _reviewReason = value; OnPropertyChanged(); } }

    public async Task RefreshAsync()
    {
        try
        {
            IReadOnlyList<ArtifactDescriptor> descriptors;
            if (Mode == ArtifactSurfaceMode.ReviewQueue)
            {
                var reviews = await _runtime.Reviews.GetQueueAsync().ConfigureAwait(false);
                var ids = reviews.Select(static item => item.ArtifactId).Distinct().ToHashSet();
                var all = await _runtime.Store.QueryAsync(new ArtifactQuery(Limit: 1000)).ConfigureAwait(false);
                descriptors = all.Where(item => ids.Contains(item.Id)).ToArray();
            }
            else descriptors = await _runtime.Store.QueryAsync(new ArtifactQuery(Limit: 1000)).ConfigureAwait(false);
            _dispatcher.Post(() =>
            {
                Items.Clear();
                foreach (var descriptor in descriptors)
                {
                    var verification = descriptor.State is ArtifactState.Verified or ArtifactState.AwaitingReview or ArtifactState.Approved or ArtifactState.Integrated or ArtifactState.Published ? "ARGUS ✓" : descriptor.State == ArtifactState.VerificationFailed ? "ARGUS FAILED" : "NOT VERIFIED";
                    var approval = descriptor.State is ArtifactState.Approved or ArtifactState.Integrated or ArtifactState.Published ? "APPROVED" : descriptor.State == ArtifactState.Rejected ? "REJECTED" : "PENDING";
                    Items.Add(new(descriptor.Id.ToString(), descriptor.CurrentRevision.ToString(), descriptor.Title, descriptor.Kind.Value, descriptor.State.ToString(), descriptor.Producer.DisplayName, verification, approval,
                        descriptor.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), $"{descriptor.Classification.Level} · {descriptor.Retention}"));
                }
                Status = $"{Items.Count:N0} {(Mode == ArtifactSurfaceMode.ReviewQueue ? "AWAITING REVIEW" : "ARTIFACTS")}";
            });
        }
        catch (Exception exception) { _dispatcher.Post(() => Status = $"ARTIFACT ERROR · {exception.GetType().Name}"); }
    }

    private async Task InspectSelectedAsync()
    {
        if (Selected is null || !ArtifactId.TryParse(Selected.Id, out var id)) return;
        var aggregate = await _runtime.Store.GetAsync(id).ConfigureAwait(false);
        if (aggregate is null) return;
        var current = aggregate.CurrentRevision;
        var revisions = aggregate.Revisions.OrderByDescending(static item => item.RevisionNumber).Select(item => new ArtifactRevisionRow(item.Id.ToString(), $"v{item.RevisionNumber}",
            aggregate.SafeVerifications.LastOrDefault(value => value.ArtifactRevisionId == item.Id)?.Result.ToString() ?? "Not verified", item.RevisionHash[..12], item.CreatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), item.Id == aggregate.Descriptor.CurrentRevision)).ToArray();
        var diff = Array.Empty<ArtifactDiffRow>();
        if (current.ParentRevisionId is { } parentId && aggregate.Revisions.SingleOrDefault(item => item.Id == parentId) is { } parent)
        {
            await using var oldContent = await _runtime.Content.OpenReadAsync(parent.Content.BlobId).ConfigureAwait(false);
            await using var newContent = await _runtime.Content.OpenReadAsync(current.Content.BlobId).ConfigureAwait(false);
            var document = await _runtime.Diffs.Resolve(parent, current).CompareAsync(parent, oldContent, current, newContent, new ArtifactDiffOptions(MaximumLines: 20_000)).ConfigureAwait(false);
            diff = document.Hunks.SelectMany(static hunk => hunk.Lines).Select(static line => new ArtifactDiffRow(line.Prefix.ToString(), line.OldLine?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, line.NewLine?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, line.Text)).ToArray();
        }
        var verification = aggregate.SafeVerifications.LastOrDefault(item => item.ArtifactRevisionId == current.Id);
        var approval = aggregate.SafeApprovals.LastOrDefault(item => item.ArtifactRevisionId == current.Id);
        _dispatcher.Post(() =>
        {
            Revisions.Clear(); foreach (var revision in revisions) Revisions.Add(revision);
            DiffLines.Clear(); foreach (var line in diff) DiffLines.Add(line);
            Inspector = $"REVISION v{current.RevisionNumber} · {current.RevisionHash[..16]}\nProducer {current.Producer.DisplayName}\nMission {current.Provenance.MissionId?.ToString() ?? "none"}\nTrajectory {current.Provenance.TrajectoryId ?? "none"}\nContent {current.Content.MediaType} · {current.Content.Length:N0} bytes\nVerification {verification?.Result.ToString() ?? "Not verified"}\nApproval {approval?.State.ToString() ?? "Pending"}\nDependencies {current.SafeDependencies.Length} · Evidence {current.SafeEvidenceRefs.Length}";
            RaiseCommands();
        });
    }

    private bool CanDecide() => Selected is not null;
    private async Task DecideAsync(ArtifactApprovalState decision)
    {
        if (Selected is null || !ArtifactId.TryParse(Selected.Id, out var id)) return;
        var aggregate = await _runtime.Store.GetAsync(id).ConfigureAwait(false);
        var review = aggregate?.SafeReviews.LastOrDefault(item => item.ArtifactRevisionId == aggregate.Descriptor.CurrentRevision && item.State is ArtifactReviewState.Pending or ArtifactReviewState.Viewed or ArtifactReviewState.ChangesRequested);
        if (review is null) { Status = "NO CURRENT REVIEW"; return; }
        await _runtime.Reviews.DecideAsync(review.Id, new PrincipalId("user:local"), decision, ReviewReason).ConfigureAwait(false);
        _dispatcher.Post(() => { ReviewReason = string.Empty; Status = decision.ToString().ToUpperInvariant(); _ = RefreshAsync(); });
    }

    private void RaiseCommands()
    {
        (InspectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (ApproveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RejectCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged(); (RequestChangesCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

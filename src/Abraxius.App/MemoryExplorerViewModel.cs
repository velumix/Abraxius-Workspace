using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Abraxius.Memory;

namespace Abraxius.App;

public sealed record MemoryResultItemViewModel(
    MemoryId Id,
    string Kind,
    string Scope,
    string Title,
    string Preview,
    string ScoreText,
    string Explanation,
    bool IsStale,
    bool IsConflict);

public sealed class MemoryExplorerViewModel : INotifyPropertyChanged
{
    private readonly IHybridMemoryRetriever _retriever;
    private string _query = "ExecutionGraph";
    private string _status = "MEMORY READY";
    private string _details = "Select a memory result to inspect provenance and ranking.";
    private MemoryResultItemViewModel? _selected;
    private int _resultCount;

    public MemoryExplorerViewModel(IHybridMemoryRetriever retriever)
    {
        _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));
        SearchCommand = new AsyncRelayCommand(SearchAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<MemoryResultItemViewModel> Results { get; } = [];
    public ICommand SearchCommand { get; }
    public string Query { get => _query; set => SetProperty(ref _query, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string Details { get => _details; private set => SetProperty(ref _details, value); }
    public int ResultCount { get => _resultCount; private set => SetProperty(ref _resultCount, value); }
    public MemoryResultItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value) || value is null) return;
            Details = $"{value.Kind} · {value.Scope}\n{value.Explanation}\n{(value.IsStale ? "SOURCE STALE · " : string.Empty)}{(value.IsConflict ? "CONFLICT DETECTED" : "PROVENANCE AVAILABLE")}";
        }
    }

    public async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;
        Status = "RETRIEVING";
        try
        {
            var result = await _retriever.RetrieveAsync(new MemorySearchQuery(Query.Trim(), Limit: 12)).ConfigureAwait(true);
            Results.Clear();
            foreach (var hit in result.Hits)
            {
                Results.Add(new MemoryResultItemViewModel(hit.Entry.Id, hit.Entry.Kind.ToString(), $"{hit.Entry.Scope}:{hit.Entry.ScopeKey}", hit.Entry.Title, Collapse(hit.Entry.Content), $"{hit.Score:0.000}", hit.Explanation, hit.IsStale, hit.IsConflict));
            }

            ResultCount = Results.Count;
            Status = $"{ResultCount} RESULTS · {result.Latency.TotalMilliseconds:0} MS";
            if (result.Conflicts.Count > 0) Status += $" · {result.Conflicts.Count} CONFLICT";
        }
        catch (OperationCanceledException)
        {
            Status = "RETRIEVAL CANCELLED";
        }
        catch (Exception exception)
        {
            Status = $"MEMORY ERROR · {exception.GetType().Name}";
            Details = exception.Message;
        }
    }

    private static string Collapse(string content) => content.Replace('\r', ' ').Replace('\n', ' ').Trim() is { Length: > 220 } value ? value[..220] + "…" : content.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Abraxius.Models;
using Abraxius.Protocol;

namespace Abraxius.App;

public sealed record UiChatMessage(
    Guid Id,
    string Speaker,
    string Text,
    DateTimeOffset Timestamp,
    bool IsUser,
    bool IsStreaming = false,
    bool IsError = false)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    public string StateText => IsStreaming ? "TYPING" : IsError ? "ERROR" : string.Empty;
    public bool IsAssistant => !IsUser;
}

/// <summary>Conversation state for the dedicated chat room. Mission execution remains an explicit action.</summary>
public sealed class ChatViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int MaximumTranscriptCharacters = 24_000;
    private readonly IModelProvider _model;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<(bool IsUser, string Text)> _history = [];
    private readonly string _sessionKey = $"chat:{Guid.NewGuid():N}";
    private CancellationTokenSource? _sendCancellation;
    private string _input = string.Empty;
    private string _status = "READY · ASK ABRAXIUS ANYTHING";
    private string _jobStatus = "NO ACTIVE JOB";
    private string _jobDetail = "Chat is ready. Use Run as mission when you want Abraxius to execute work.";
    private string _jobExecution = "";
    private string _queuedObjective = string.Empty;
    private bool _isSending;
    private int _disposed;

    public ChatViewModel(IModelProvider model, IUiDispatcher dispatcher)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        SendCommand = new AsyncRelayCommand(SendAsync, () => !IsSending && !string.IsNullOrWhiteSpace(Input));
        CancelCommand = new RelayCommand(_ => Cancel(), () => IsSending);
        ClearCommand = new RelayCommand(_ => Clear());
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<UiChatMessage> Messages { get; } = [];
    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearCommand { get; }
    public string Input
    {
        get => _input;
        set
        {
            if (!SetProperty(ref _input, value)) return;
            (SendCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string JobStatus
    {
        get => _jobStatus;
        private set => SetProperty(ref _jobStatus, value);
    }

    public string JobDetail
    {
        get => _jobDetail;
        private set => SetProperty(ref _jobDetail, value);
    }

    public string JobExecution
    {
        get => _jobExecution;
        private set => SetProperty(ref _jobExecution, value);
    }

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (!SetProperty(ref _isSending, value)) return;
            (SendCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanSendToMission));
        }
    }

    public bool HasMessages => Messages.Count > 0;
    public bool CanSendToMission => !IsSending && !string.IsNullOrWhiteSpace(LatestUserText);
    public string? LatestUserText => _history.LastOrDefault(item => item.IsUser).Text is { } text ? text : null;

    public async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsSending) return;

        Input = string.Empty;
        var userMessage = new UiChatMessage(Guid.NewGuid(), "YOU", text, DateTimeOffset.UtcNow, true);
        AddMessage(userMessage);
        _history.Add((true, text));
        TrimHistory();

        var assistantId = Guid.NewGuid();
        AddMessage(new UiChatMessage(assistantId, "ABRAXIUS", "Thinking…", DateTimeOffset.UtcNow, false, IsStreaming: true));
        IsSending = true;
        Status = "THINKING · STREAMING RESPONSE";
        using var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;

        var response = new System.Text.StringBuilder();
        try
        {
            var request = new ModelRequest(
                BuildPrompt(),
                SystemPrompt: "You are Abraxius, a helpful AI workstation companion. Answer naturally and clearly. Do not claim to have changed files, run tools, or completed a mission from chat alone. If the user wants an action, explain that they can use Run as mission.",
                Priority: WorkPriority.Interactive,
                Metadata: new Dictionary<string, string> { ["surface"] = "chat-room" })
            {
                TaskClass = IntelligenceTaskClass.SimpleQuestion,
                Complexity = IntelligenceComplexity.Simple,
                RequiredCapabilities = [ModelCapability.Streaming],
                RequiredContextTokens = 8_192,
                MaxOutputTokens = 2_048,
                Temperature = 0.4m,
                Stream = true,
                SessionKey = _sessionKey,
                DataClassification = DataClassification.Internal
            };

            await foreach (var item in _model.StreamAsync(request, cancellation.Token))
            {
                switch (item)
                {
                    case ModelStreamEvent.Token token:
                        response.Append(token.Text);
                        ReplaceMessage(assistantId, current => current with { Text = response.ToString() });
                        break;
                    case ModelStreamEvent.Completed completed when response.Length == 0:
                        response.Append(completed.Result.Text);
                        ReplaceMessage(assistantId, current => current with { Text = response.ToString() });
                        break;
                    case ModelStreamEvent.Completed:
                        break;
                }
            }

            // A few compatible gateways advertise streaming but return an empty stream.
            // Recover through the normal request path so the chat never ends on a blank bubble.
            if (response.Length == 0)
            {
                var fallback = await _model.InferAsync(request with { Stream = false }, cancellation.Token);
                response.Append(fallback.Text);
            }

            var finalText = response.ToString().Trim();
            if (finalText.Length == 0) finalText = "I didn’t receive a response from the model.";
            ReplaceMessage(assistantId, current => current with { Text = finalText, IsStreaming = false });
            _history.Add((false, finalText));
            TrimHistory();
            Status = "READY · RESPONSE COMPLETE";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var partial = response.ToString().Trim();
            ReplaceMessage(assistantId, current => current with
            {
                Text = partial.Length == 0 ? "Response cancelled." : $"{partial}\n\n[response cancelled]",
                IsStreaming = false
            });
            Status = "READY · RESPONSE CANCELLED";
        }
        catch (Exception exception)
        {
            ReplaceMessage(assistantId, current => current with
            {
                Text = $"I couldn’t answer that: {exception.Message}",
                IsStreaming = false,
                IsError = true
            });
            Status = "MODEL ERROR · CHECK ROUTE OR COMPUTE";
        }
        finally
        {
            _sendCancellation = null;
            IsSending = false;
            OnPropertyChanged(nameof(LatestUserText));
            OnPropertyChanged(nameof(CanSendToMission));
        }
    }

    public void Cancel() => _sendCancellation?.Cancel();

    public void Clear()
    {
        if (IsSending) return;
        _history.Clear();
        Messages.Clear();
        Status = "READY · NEW CONVERSATION";
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(LatestUserText));
        OnPropertyChanged(nameof(CanSendToMission));
    }

    public void UpdateMission(UiGraphSnapshot snapshot)
    {
        var metrics = snapshot.Metrics;
        if (snapshot.ExecutionId is null || metrics.TotalTasks == 0)
        {
            if (_queuedObjective.Length > 0)
            {
                JobStatus = "JOB STARTING";
                JobDetail = $"Queued from chat · {_queuedObjective}";
                return;
            }

            JobStatus = "NO ACTIVE JOB";
            JobDetail = "Chat is ready. Use Run as mission when you want Abraxius to execute work.";
            JobExecution = string.Empty;
            return;
        }

        JobExecution = $"EXECUTION {snapshot.ExecutionId}"[..Math.Min(24, $"EXECUTION {snapshot.ExecutionId}".Length)];
        JobDetail = $"{metrics.CompletedTasks}/{metrics.TotalTasks} complete · {metrics.RunningTasks} running · {metrics.QueuedTasks} queued";
        JobStatus = metrics.RunningTasks > 0 || metrics.QueuedTasks > 0 || metrics.ReadyTasks > 0
            ? "JOB RUNNING"
            : metrics.FailedTasks > 0 || metrics.TimedOutTasks > 0
                ? "JOB NEEDS ATTENTION"
                : metrics.CancelledTasks > 0
                    ? "JOB CANCELLED"
                    : metrics.CompletedTasks >= metrics.TotalTasks
                        ? "JOB COMPLETE"
                        : "JOB WAITING";
        if (!string.IsNullOrWhiteSpace(snapshot.MissionSummary))
        {
            JobDetail = $"{JobDetail} · {snapshot.MissionSummary}";
        }
    }

    public void SetJobQueued(string objective)
    {
        _queuedObjective = objective.Trim();
        JobExecution = string.Empty;
        JobStatus = "JOB STARTING";
        JobDetail = $"Queued from chat · {_queuedObjective}";
    }

    public void SetJobFinished(bool succeeded, string detail)
    {
        _queuedObjective = string.Empty;
        JobStatus = succeeded ? "JOB COMPLETE" : "JOB NEEDS ATTENTION";
        JobDetail = detail;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _sendCancellation?.Cancel();
            _sendCancellation?.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private string BuildPrompt()
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("Conversation so far:");
        foreach (var turn in _history.TakeLast(24))
        {
            builder.Append(turn.IsUser ? "USER: " : "ABRAXIUS: ");
            builder.AppendLine(turn.Text);
        }

        builder.AppendLine("ABRAXIUS:");
        return builder.ToString();
    }

    private void TrimHistory()
    {
        while (_history.Count > 2 && _history.Sum(static item => item.Text.Length) > MaximumTranscriptCharacters)
        {
            _history.RemoveAt(0);
        }
    }

    private void AddMessage(UiChatMessage message)
    {
        _dispatcher.Post(() =>
        {
            Messages.Add(message);
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(LatestUserText));
            OnPropertyChanged(nameof(CanSendToMission));
        });
    }

    private void ReplaceMessage(Guid id, Func<UiChatMessage, UiChatMessage> update)
    {
        _dispatcher.Post(() =>
        {
            var index = Messages.Select((message, index) => (message, index)).FirstOrDefault(item => item.message.Id == id).index;
            if (index < 0 || index >= Messages.Count || Messages[index].Id != id) return;
            Messages[index] = update(Messages[index]);
        });
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

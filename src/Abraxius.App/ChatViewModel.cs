using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Abraxius.Models;
using Abraxius.Protocol;

namespace Abraxius.App;

public sealed record ChatSpecialistProfile(
    string DisplayName,
    string Role,
    string Mission,
    bool CanDelegate);

/// <summary>Conversation state for the dedicated chat room. Mission execution remains an explicit action.</summary>
public sealed class ChatViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int MaximumTranscriptCharacters = 24_000;
    private readonly IModelProvider _model;
    private readonly IUiDispatcher _dispatcher;
    private readonly List<(bool IsUser, string Text)> _history = [];
    private readonly IReadOnlyDictionary<string, ChatSpecialistProfile> _specialistProfiles;
    private readonly string _sessionKey = $"chat:{Guid.NewGuid():N}";
    private CancellationTokenSource? _sendCancellation;
    private string _input = string.Empty;
    private string _status = "READY · ASK ABRAXIUS ANYTHING";
    private string _jobStatus = "NO ACTIVE JOB";
    private string _jobDetail = "Chat is ready. Use Run as mission when you want Abraxius to execute work.";
    private string _jobExecution = "";
    private string _queuedObjective = string.Empty;
    private ChatMode _mode;
    private string _activeSpecialist = string.Empty;
    private IReadOnlyList<ChatSuggestion> _suggestions = [];
    private Func<string, IReadOnlyList<ChatSuggestion>>? _commandSearch;
    private readonly IReadOnlyList<string> _specialistNames;
    private bool _isSending;
    private int _disposed;

    public ChatViewModel(
        IModelProvider model,
        IUiDispatcher dispatcher,
        IEnumerable<string>? specialistNames = null,
        IEnumerable<ChatSpecialistProfile>? specialistProfiles = null)
    {
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _specialistProfiles = (specialistProfiles ?? BuiltInChatSpecialists())
            .Where(static profile => !string.IsNullOrWhiteSpace(profile.DisplayName))
            .GroupBy(static profile => profile.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.OrdinalIgnoreCase);
        _specialistNames = specialistNames?.Where(static name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray()
            ?? _specialistProfiles.Keys.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        SendCommand = new AsyncRelayCommand(SendAsync, () => !IsSending && !string.IsNullOrWhiteSpace(Input));
        CancelCommand = new RelayCommand(_ => Cancel(), () => IsSending);
        ClearCommand = new RelayCommand(_ => Clear());
        ToggleModeCommand = new RelayCommand(_ => IsMissionMode = !IsMissionMode);
        AddProjectContextCommand = new RelayCommand(_ => AddProjectContext());
        RemoveContextCommand = new RelayCommand(parameter => RemoveContext(parameter as ChatContextChip));
        ToggleMentionSuggestionsCommand = new RelayCommand(_ => ToggleSuggestions('@'));
        ToggleCommandSuggestionsCommand = new RelayCommand(_ => ToggleSuggestions('/'));
        SelectSuggestionCommand = new RelayCommand(parameter => SelectSuggestion(parameter as ChatSuggestion));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<ChatContextChip> ContextChips { get; } = [];
    public bool HasContext => ContextChips.Count > 0;
    public ICommand SendCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ToggleModeCommand { get; }
    public ICommand AddProjectContextCommand { get; }
    public ICommand RemoveContextCommand { get; }
    public ICommand ToggleMentionSuggestionsCommand { get; }
    public ICommand ToggleCommandSuggestionsCommand { get; }
    public ICommand SelectSuggestionCommand { get; }
    public string Input
    {
        get => _input;
        set
        {
            if (!SetProperty(ref _input, value)) return;
            (SendCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            UpdateSuggestions();
        }
    }

    public ChatMode Mode
    {
        get => _mode;
        private set
        {
            if (!SetProperty(ref _mode, value)) return;
            OnPropertyChanged(nameof(IsMissionMode));
            OnPropertyChanged(nameof(ModeLabel));
            OnPropertyChanged(nameof(ComposerPlaceholder));
            OnPropertyChanged(nameof(ShowSendAction));
            OnPropertyChanged(nameof(ShowRunAction));
        }
    }
    public bool IsMissionMode { get => Mode == ChatMode.Mission; private set => Mode = value ? ChatMode.Mission : ChatMode.Chat; }
    public string ModeLabel => IsMissionMode ? "Mission" : "Chat";
    public string ComposerPlaceholder => IsMissionMode ? "Describe the mission Abraxius should execute…" : "Ask Abraxius…";
    public string ActiveSpecialist { get => _activeSpecialist; private set => SetProperty(ref _activeSpecialist, value); }
    public IReadOnlyList<ChatSuggestion> Suggestions { get => _suggestions; private set { if (!SetProperty(ref _suggestions, value)) return; OnPropertyChanged(nameof(HasSuggestions)); } }
    public bool HasSuggestions => Suggestions.Count > 0;

    public string Status
    {
        get => _status;
        private set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(CompactStatus));
        }
    }

    public string JobStatus
    {
        get => _jobStatus;
        private set
        {
            if (!SetProperty(ref _jobStatus, value)) return;
            OnPropertyChanged(nameof(CompactStatus));
            OnPropertyChanged(nameof(HasActiveJob));
        }
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

    public string CompactStatus => IsSending ? "Thinking…" : JobStatus != "NO ACTIVE JOB" ? JobStatus : Status.Contains("ERROR", StringComparison.OrdinalIgnoreCase) ? "Needs attention" : "Ready";
    public bool HasActiveJob => JobStatus != "NO ACTIVE JOB";
    public bool ShowSendAction => !IsSending && !IsMissionMode;
    public bool ShowRunAction => !IsSending && IsMissionMode;

    public bool IsSending
    {
        get => _isSending;
        private set
        {
            if (!SetProperty(ref _isSending, value)) return;
            (SendCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
            (CancelCommand as RelayCommand)?.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(CanSendToMission));
            OnPropertyChanged(nameof(CompactStatus));
            OnPropertyChanged(nameof(ShowSendAction));
            OnPropertyChanged(nameof(ShowRunAction));
        }
    }

    public bool HasMessages => Messages.Count > 0;
    public bool IsEmpty => !HasMessages;
    public bool CanSendToMission => !IsSending && !string.IsNullOrWhiteSpace(LatestUserText);
    public string? LatestUserText => _history.LastOrDefault(item => item.IsUser).Text is { } text ? text : null;

    public async Task SendAsync()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsSending) return;

        // Capture the selected owner before clearing the composer. Clearing Input
        // updates mention suggestions and intentionally clears transient targeting.
        var conversationSpecialist = CurrentSpecialist;
        Input = string.Empty;
        var userMessage = new ChatMessageViewModel(Guid.NewGuid(), "YOU", text, DateTimeOffset.UtcNow, true);
        AddMessage(userMessage);
        _history.Add((true, text));
        TrimHistory();

        var assistantId = Guid.NewGuid();
        var assistantMessage = new ChatMessageViewModel(
            assistantId,
            $"ABRAXIUS · {conversationSpecialist.DisplayName.ToUpperInvariant()}",
            "Thinking…",
            DateTimeOffset.UtcNow,
            false,
            isStreaming: true);
        AddMessage(assistantMessage);
        IsSending = true;
        Status = "THINKING · STREAMING RESPONSE";
        using var cancellation = new CancellationTokenSource();
        _sendCancellation = cancellation;

        await using var streaming = new ChatStreamingBuffer(_dispatcher, output => ReplaceMessageText(assistantId, output));
        try
        {
            var request = new ModelRequest(
                BuildPrompt(conversationSpecialist),
                SystemPrompt: BuildSystemPrompt(conversationSpecialist),
                Priority: WorkPriority.Interactive,
                Metadata: new Dictionary<string, string>
                {
                    ["surface"] = "chat-room",
                    ["orchestration"] = "agent-kernel",
                    ["coordinator"] = "Athena",
                    ["conversation.specialist"] = conversationSpecialist.DisplayName,
                    ["conversation.role"] = conversationSpecialist.Role
                })
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
                        streaming.Append(token.Text);
                        break;
                    case ModelStreamEvent.Completed completed when streaming.Text.Length == 0:
                        streaming.Append(completed.Result.Text);
                        break;
                    case ModelStreamEvent.Completed:
                        break;
                }
            }

            // A few compatible gateways advertise streaming but return an empty stream.
            // Recover through the normal request path so the chat never ends on a blank bubble.
            if (streaming.Text.Length == 0)
            {
                var fallback = await _model.InferAsync(request with { Stream = false }, cancellation.Token);
                streaming.Append(fallback.Text);
            }

            var finalText = (await streaming.CompleteAsync()).Trim();
            if (finalText.Length == 0) finalText = "I didn’t receive a response from the model.";
            ReplaceMessageComplete(assistantId, finalText);
            _history.Add((false, finalText));
            TrimHistory();
            Status = "READY · RESPONSE COMPLETE";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            var partial = streaming.Text.Trim();
            await streaming.CompleteAsync();
            ReplaceMessageComplete(assistantId, partial.Length == 0 ? "Response cancelled." : $"{partial}\n\n[response cancelled]", cancelled: true);
            Status = "READY · RESPONSE CANCELLED";
        }
        catch (Exception exception)
        {
            await streaming.CompleteAsync();
            ReplaceMessageComplete(assistantId, $"I couldn’t answer that: {exception.Message}", error: true);
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

    /// <summary>Moves the current composer text into the transcript when Mission mode is used.</summary>
    public string? TakeMissionObjective()
    {
        var text = Input.Trim();
        if (text.Length == 0) return LatestUserText;
        Input = string.Empty;
        AddMessage(new ChatMessageViewModel(Guid.NewGuid(), "YOU", text, DateTimeOffset.UtcNow, true));
        _history.Add((true, text));
        TrimHistory();
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LatestUserText));
        OnPropertyChanged(nameof(CanSendToMission));
        return text;
    }

    public void SetCommandSearch(Func<string, IReadOnlyList<ChatSuggestion>> search) => _commandSearch = search;

    public void Clear()
    {
        if (IsSending) return;
        _history.Clear();
            Messages.Clear();
            Status = "READY · NEW CONVERSATION";
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LatestUserText));
        OnPropertyChanged(nameof(CanSendToMission));
    }

    private void AddProjectContext()
    {
        if (ContextChips.Any(static chip => chip.Id == "current-project")) return;
        ContextChips.Add(new ChatContextChip("current-project", "Current project", "Project"));
        OnPropertyChanged(nameof(ContextChips));
        OnPropertyChanged(nameof(HasContext));
    }

    private void RemoveContext(ChatContextChip? chip)
    {
        if (chip is null) return;
        ContextChips.Remove(chip);
        OnPropertyChanged(nameof(ContextChips));
        OnPropertyChanged(nameof(HasContext));
    }

    private void ToggleSuggestions(char prefix)
    {
        if (!Input.TrimStart().StartsWith(prefix))
        {
            Input = Input.TrimEnd() + prefix;
        }
        else
        {
            UpdateSuggestions(force: true);
        }
    }

    private void SelectSuggestion(ChatSuggestion? suggestion)
    {
        if (suggestion is null) return;
        var prefixIndex = Input.Contains(suggestion.Prefix, StringComparison.Ordinal)
            ? Input.IndexOf(suggestion.Prefix, StringComparison.Ordinal)
            : 0;
        var leading = Input[..prefixIndex];
        Input = leading + suggestion.Prefix + suggestion.Value + " ";
        if (suggestion.Prefix == "@") ActiveSpecialist = suggestion.Value;
        Suggestions = [];
    }

    private void UpdateSuggestions(bool force = false)
    {
        var value = Input.TrimStart();
        if (value.StartsWith('@'))
        {
            var token = value[1..].Split(' ', '\t', '\r', '\n')[0];
            if (force || token.Length > 0)
            {
                Suggestions = _specialistNames
                    .Where(name => name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                    .Take(6)
                    .Select(name => new ChatSuggestion("@", name, $"@{name}", "Target this specialist directly"))
                    .ToArray();
                ActiveSpecialist = _specialistNames.FirstOrDefault(name => string.Equals(name, token, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
                return;
            }
        }
        else if (value.StartsWith('/'))
        {
            var token = value[1..].Split(' ', '\t', '\r', '\n')[0];
            Suggestions = (_commandSearch?.Invoke(token) ?? [])
                .Take(8)
                .Select(command => new ChatSuggestion("/", command.Value, command.Label, command.Detail))
                .ToArray();
            return;
        }

        if (!force) ActiveSpecialist = string.Empty;
        Suggestions = [];
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

    private string BuildPrompt(ChatSpecialistProfile specialist)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("ABRAXIUS CHAT ROUTING:");
        builder.AppendLine("COORDINATOR: Athena");
        builder.Append("CONVERSATIONAL OWNER: ").Append(specialist.DisplayName).Append(" (").Append(specialist.Role).AppendLine(")");
        builder.AppendLine("Handoff semantics: Athena coordinates; Orion investigates; Daedalus proposes implementation; Argus verifies. A handoff is only complete when the real Mission runtime reports it.");
        builder.AppendLine("Conversation so far:");
        foreach (var turn in _history.TakeLast(24))
        {
            builder.Append(turn.IsUser ? "USER: " : "ABRAXIUS: ");
            builder.AppendLine(turn.Text);
        }

        if (!string.IsNullOrWhiteSpace(ActiveSpecialist))
        {
            builder.Append("TARGET SPECIALIST: ").Append(ActiveSpecialist).AppendLine();
        }

        if (ContextChips.Count > 0)
        {
            builder.AppendLine("EXPLICIT CONTEXT:");
            foreach (var context in ContextChips)
            {
                builder.Append("- ").Append(context.Kind).Append(": ").Append(context.Label).AppendLine();
            }
        }

        builder.AppendLine("ABRAXIUS:");
        return builder.ToString();
    }

    private static string BuildSystemPrompt(ChatSpecialistProfile specialist)
    {
        var delegation = specialist.CanDelegate
            ? "You may explain a proposed handoff to Orion, Daedalus, or Argus, but do not claim that it ran unless the user explicitly launched Mission mode and the runtime reported the result."
            : $"If the request requires another role, explain that {specialist.DisplayName} will hand it back to Athena for coordination or recommend the appropriate specialist; do not impersonate another specialist.";

        return string.Join(Environment.NewLine,
        [
            $"You are {specialist.DisplayName}, Abraxius's {specialist.Role} specialist.",
            $"Your responsibility is: {specialist.Mission}",
            "You are speaking inside the Abraxius Chat workspace, not as the underlying model provider.",
            "Never introduce yourself as Felo, OmniRoute, an API, a model, or any other provider identity.",
            "Never mention provider branding unless the user explicitly asks about routing or diagnostics.",
            "Preserve Abraxius specialist identity and use the role above consistently.",
            delegation,
            "Chat alone is conversational and must not claim to have changed files, run tools, used secrets, completed a mission, or verified an artifact.",
            "When the user wants execution, direct them to Run as Mission. Mission mode uses the existing AgentKernel handoff chain and its real runtime state.",
            "Answer naturally, clearly, and helpfully. Do not fabricate progress, evidence, tool output, or specialist activity."
        ]);
    }

    private ChatSpecialistProfile CurrentSpecialist =>
        !string.IsNullOrWhiteSpace(ActiveSpecialist) && _specialistProfiles.TryGetValue(ActiveSpecialist, out var selected)
            ? selected
            : _specialistProfiles.TryGetValue("Athena", out var athena)
                ? athena
                : new ChatSpecialistProfile("Athena", "Coordinator", "Mission strategist and coordinator.", true);

    private static IReadOnlyList<ChatSpecialistProfile> BuiltInChatSpecialists() =>
    [
        new("Athena", "Coordinator", "Mission strategist and coordinator.", true),
        new("Orion", "Investigator", "Evidence-led repository and systems investigation.", false),
        new("Daedalus", "Builder", "Constrained implementation and repair.", false),
        new("Argus", "Verifier", "Independent verification and regression detection.", false)
    ];

    private void TrimHistory()
    {
        while (_history.Count > 2 && _history.Sum(static item => item.Text.Length) > MaximumTranscriptCharacters)
        {
            _history.RemoveAt(0);
        }
    }

    private void AddMessage(ChatMessageViewModel message)
    {
        _dispatcher.Post(() =>
        {
            Messages.Add(message);
            OnPropertyChanged(nameof(HasMessages));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(LatestUserText));
            OnPropertyChanged(nameof(CanSendToMission));
        });
    }

    private void ReplaceMessageText(Guid id, string text)
    {
        _dispatcher.Post(() =>
        {
            var message = Messages.FirstOrDefault(item => item.Id == id);
            message?.UpdateStreamingText(text);
        });
    }

    private void ReplaceMessageComplete(Guid id, string text, bool error = false, bool cancelled = false)
    {
        _dispatcher.Post(() => Messages.FirstOrDefault(item => item.Id == id)?.Complete(text, error, cancelled));
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

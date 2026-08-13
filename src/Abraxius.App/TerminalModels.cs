using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Windows.Input;
using Abraxius.Platform;

namespace Abraxius.App;

public sealed record TerminalLine(DateTimeOffset Timestamp, string Text, bool IsError = false);

public interface ITerminalSession : IAsyncDisposable
{
    string Title { get; }
    IReadOnlyList<TerminalLine> Lines { get; }
    event EventHandler<TerminalLine>? LineAdded;
    ValueTask ExecuteAsync(string commandLine, CancellationToken cancellationToken = default);
}

public interface ITerminalSurface
{
    ITerminalSession CreateSession(string title, string? workingDirectory = null);
}

/// <summary>Direct-executable terminal adapter. It deliberately does not invoke a platform shell.</summary>
public sealed class ProcessTerminalSurface(IProcessExecutionService? processService) : ITerminalSurface
{
    public ITerminalSession CreateSession(string title, string? workingDirectory = null) =>
        new ProcessTerminalSession(title, workingDirectory, processService);
}

public sealed class ProcessTerminalSession : ITerminalSession
{
    private readonly string? _workingDirectory;
    private readonly IProcessExecutionService? _processService;
    private readonly object _gate = new();
    private readonly List<TerminalLine> _lines = [];
    private int _disposed;

    public ProcessTerminalSession(string title, string? workingDirectory, IProcessExecutionService? processService)
    {
        Title = title;
        _workingDirectory = workingDirectory;
        _processService = processService;
    }

    public string Title { get; }
    public IReadOnlyList<TerminalLine> Lines
    {
        get
        {
            lock (_gate)
            {
                return _lines.ToArray();
            }
        }
    }

    public event EventHandler<TerminalLine>? LineAdded;

    public async ValueTask ExecuteAsync(string commandLine, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return;
        }

        Add(new TerminalLine(DateTimeOffset.UtcNow, $"$ {commandLine}"));
        if (_processService is null)
        {
            Add(new TerminalLine(DateTimeOffset.UtcNow, "Terminal process service is unavailable in this host.", IsError: true));
            return;
        }

        var arguments = ParseArguments(commandLine);
        if (arguments.IsDefaultOrEmpty)
        {
            return;
        }

        var result = await _processService.ExecuteAsync(
            new ProcessRequest(arguments[0], arguments.RemoveAt(0), _workingDirectory, Timeout: TimeSpan.FromMinutes(2)),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || result.Value is null)
        {
            Add(new TerminalLine(DateTimeOffset.UtcNow, result.Error?.Message ?? "Process execution failed.", IsError: true));
            return;
        }

        AppendLines(result.Value.StandardOutput, false);
        AppendLines(result.Value.StandardError, true);
        Add(new TerminalLine(DateTimeOffset.UtcNow, result.Value.TimedOut
            ? "process timed out"
            : $"exit {result.Value.ExitCode} · {result.Value.Duration.TotalMilliseconds:F0} ms", result.Value.ExitCode != 0));
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private void AppendLines(string text, bool isError)
    {
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            Add(new TerminalLine(DateTimeOffset.UtcNow, line, isError));
        }
    }

    private void Add(TerminalLine line)
    {
        lock (_gate)
        {
            _lines.Add(line);
            if (_lines.Count > 10_000)
            {
                _lines.RemoveRange(0, _lines.Count - 10_000);
            }
        }

        LineAdded?.Invoke(this, line);
    }

    private static ImmutableArray<string> ParseArguments(string commandLine)
    {
        var values = ImmutableArray.CreateBuilder<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var character in commandLine.TrimStart('$', ' '))
        {
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    values.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(character);
        }

        if (current.Length > 0)
        {
            values.Add(current.ToString());
        }

        return values.ToImmutable();
    }
}

public sealed class TerminalViewModel : IAsyncDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private readonly ITerminalSession _session;
    private string _input = string.Empty;

    public TerminalViewModel(IUiDispatcher dispatcher, ITerminalSurface surface)
    {
        _dispatcher = dispatcher;
        _session = surface.CreateSession("Local process", Environment.CurrentDirectory);
        _session.LineAdded += OnLineAdded;
        ExecuteCommand = new AsyncRelayCommand(ExecuteAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<TerminalLine> Lines { get; } = [];
    public string Title => _session.Title;
    public string Input
    {
        get => _input;
        set
        {
            if (_input == value)
            {
                return;
            }

            _input = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Input)));
        }
    }

    public ICommand ExecuteCommand { get; }

    public async Task ExecuteAsync()
    {
        var command = Input.Trim();
        if (command.Length == 0)
        {
            return;
        }

        Input = string.Empty;
        await _session.ExecuteAsync(command).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _session.LineAdded -= OnLineAdded;
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private void OnLineAdded(object? sender, TerminalLine line) => _dispatcher.Post(() =>
    {
        Lines.Add(line);
        while (Lines.Count > 2_000)
        {
            Lines.RemoveAt(0);
        }
    });
}

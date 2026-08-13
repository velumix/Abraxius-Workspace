using System.Text;

namespace Abraxius.App;

/// <summary>Coalesces fast token streams into bounded visual updates.</summary>
public sealed class ChatStreamingBuffer : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<string> _apply;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;
    private readonly StringBuilder _fullText = new();
    private bool _dirty;
    private bool _stopped;

    public ChatStreamingBuffer(IUiDispatcher dispatcher, Action<string> apply, TimeSpan? cadence = null)
    {
        _dispatcher = dispatcher;
        _apply = apply;
        Cadence = cadence ?? TimeSpan.FromMilliseconds(33);
        _pump = PumpAsync();
    }

    public TimeSpan Cadence { get; }
    public int FlushCount { get; private set; }

    public string Text
    {
        get
        {
            lock (_gate) return _fullText.ToString();
        }
    }

    public void Append(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            if (_stopped) return;
            _fullText.Append(text);
            _dirty = true;
        }
    }

    public async ValueTask<string> CompleteAsync()
    {
        string final;
        lock (_gate)
        {
            if (_stopped) return _fullText.ToString();
            _stopped = true;
            final = _fullText.ToString();
            _dirty = false;
        }

        _lifetime.Cancel();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown of the coalescing pump.
        }

        _dispatcher.Post(() =>
        {
            FlushCount++;
            _apply(final);
        });
        return final;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) _stopped = true;
        _lifetime.Cancel();
        try { await _pump.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(Cadence, _lifetime.Token).ConfigureAwait(false);
                string? snapshot = null;
                lock (_gate)
                {
                    if (_stopped) return;
                    if (_dirty)
                    {
                        snapshot = _fullText.ToString();
                        _dirty = false;
                    }
                }

                if (snapshot is null) continue;
                _dispatcher.Post(() =>
                {
                    FlushCount++;
                    _apply(snapshot);
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal/cancellation is the expected end of the pump.
        }
    }
}

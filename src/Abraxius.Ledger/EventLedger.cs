using System.Text.Json;
using System.Threading.Channels;
using System.Collections.Concurrent;
using Abraxius.Protocol;

namespace Abraxius.Ledger;

public sealed record LedgerEntry(
    long Sequence,
    DateTimeOffset Timestamp,
    RuntimeEventKind Kind,
    ExecutionId ExecutionId,
    TaskId? TaskId,
    CorrelationId CorrelationId,
    string Source,
    string PayloadType,
    string PayloadJson)
{
    public static LedgerEntry FromEvent(RuntimeEvent runtimeEvent) => new(
        runtimeEvent.Sequence,
        runtimeEvent.Timestamp,
        runtimeEvent.Kind,
        runtimeEvent.ExecutionId,
        runtimeEvent.RelatedTaskId,
        runtimeEvent.CorrelationId,
        runtimeEvent.Source,
        runtimeEvent.GetType().Name,
        JsonSerializer.Serialize(runtimeEvent, runtimeEvent.GetType()));
}

public interface IEventLedger : IAsyncDisposable
{
    ValueTask AppendAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken = default);
    ValueTask FlushAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<LedgerEntry> ReadAsync(ExecutionId? executionId = null, CancellationToken cancellationToken = default);
}

public sealed class BufferedEventLedger : IEventLedger
{
    private readonly string _path;
    private readonly Channel<LedgerEntry> _queue;
    private readonly int _batchSize;
    private readonly object _flushGate = new();
    private readonly List<TaskCompletionSource<bool>> _flushWaiters = [];
    private Task? _writerTask;
    private bool _writerBusy;
    private int _started;

    public BufferedEventLedger(string path, int capacity = 8192, int batchSize = 64)
    {
        _path = Path.GetFullPath(path);
        _batchSize = Math.Max(1, batchSize);
        var options = new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        };
        _queue = Channel.CreateBounded<LedgerEntry>(options);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _writerTask = Task.Run(WriterLoopAsync);
    }

    public ValueTask AppendAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_writerTask is null, this);
        return _queue.Writer.WriteAsync(LedgerEntry.FromEvent(runtimeEvent), cancellationToken);
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_writerTask is null)
        {
            return;
        }

        TaskCompletionSource<bool>? waiter = null;
        lock (_flushGate)
        {
            if (_queue.Reader.Count == 0 && !_writerBusy)
            {
                return;
            }

            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _flushWaiters.Add(waiter);
        }

        await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<LedgerEntry> ReadAsync(
        ExecutionId? executionId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            yield break;
        }

        await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        using var reader = new StreamReader(stream);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            LedgerEntry? entry;
            try
            {
                entry = JsonSerializer.Deserialize<LedgerEntry>(line);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not null && (executionId is null || entry.ExecutionId == executionId.Value))
            {
                yield return entry;
            }
        }
    }

    private async Task WriterLoopAsync()
    {
        await using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true);
        await using var writer = new StreamWriter(stream) { AutoFlush = false };
        var batch = new List<LedgerEntry>(_batchSize);

        while (await _queue.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            lock (_flushGate)
            {
                _writerBusy = true;
            }

            while (batch.Count < _batchSize && _queue.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count > 0)
            {
                await WriteBatchAsync(writer, batch).ConfigureAwait(false);
                batch.Clear();
            }

            lock (_flushGate)
            {
                _writerBusy = false;
                if (_queue.Reader.Count == 0)
                {
                    SignalFlushWaiters();
                }
            }
        }

        if (batch.Count > 0)
        {
            await WriteBatchAsync(writer, batch).ConfigureAwait(false);
        }

        await writer.FlushAsync().ConfigureAwait(false);
        lock (_flushGate)
        {
            _writerBusy = false;
            SignalFlushWaiters();
        }
    }

    private void SignalFlushWaiters()
    {
        foreach (var waiter in _flushWaiters)
        {
            waiter.TrySetResult(true);
        }

        _flushWaiters.Clear();
    }

    private static async Task WriteBatchAsync(StreamWriter writer, IReadOnlyList<LedgerEntry> batch)
    {
        foreach (var entry in batch)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(entry)).ConfigureAwait(false);
        }

        await writer.FlushAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_writerTask is null)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _writerTask.ConfigureAwait(false);
        _writerTask = null;
    }
}

/// <summary>Process-local ledger for browser, mobile, and sandboxed hosts without durable file access.</summary>
public sealed class InMemoryEventLedger : IEventLedger
{
    private readonly ConcurrentQueue<LedgerEntry> _entries = new();
    private int _disposed;

    public ValueTask AppendAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        _entries.Enqueue(LedgerEntry.FromEvent(runtimeEvent));
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<LedgerEntry> ReadAsync(
        ExecutionId? executionId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (executionId is null || entry.ExecutionId == executionId.Value)
            {
                yield return entry;
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }
}

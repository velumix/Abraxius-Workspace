using System.Collections.Immutable;
using System.Threading.Channels;
using Abraxius.Protocol;

namespace Abraxius.Telemetry;

public sealed class RuntimeEventHub : IRuntimeEventSink, IAsyncDisposable
{
    private readonly object _gate = new();
    private ImmutableArray<Subscriber> _subscribers = ImmutableArray<Subscriber>.Empty;
    private long _sequence;
    private bool _disposed;

    public long PublishedCount => Interlocked.Read(ref _sequence);

    public RuntimeEventSubscription Subscribe(
        int capacity = 4096,
        bool lossy = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = lossy ? BoundedChannelFullMode.DropOldest : BoundedChannelFullMode.Wait,
            SingleWriter = false,
            SingleReader = true,
            AllowSynchronousContinuations = false
        };
        var channel = Channel.CreateBounded<RuntimeEvent>(options);
        var subscriber = new Subscriber(channel, lossy);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscribers = _subscribers.Add(subscriber);
        }

        var subscription = new RuntimeEventSubscription(this, subscriber);
        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(static state => ((RuntimeEventSubscription)state!).DisposeSynchronously(), subscription);
        }

        return subscription;
    }

    public async ValueTask PublishAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtimeEvent);
        Subscriber[] subscribers;
        RuntimeEvent sequenced;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            sequenced = runtimeEvent with { Sequence = Interlocked.Increment(ref _sequence) };
            subscribers = _subscribers.ToArray();
        }

        if (subscribers.Length == 0)
        {
            return;
        }

        foreach (var subscriber in subscribers)
        {
            await subscriber.Channel.Writer.WriteAsync(sequenced, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Remove(Subscriber subscriber)
    {
        lock (_gate)
        {
            if (_subscribers.Contains(subscriber))
            {
                _subscribers = _subscribers.Remove(subscriber);
                subscriber.Channel.Writer.TryComplete();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return ValueTask.CompletedTask;
            }

            _disposed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Channel.Writer.TryComplete();
            }

            _subscribers = ImmutableArray<Subscriber>.Empty;
        }

        return ValueTask.CompletedTask;
    }

    internal sealed record Subscriber(Channel<RuntimeEvent> Channel, bool Lossy);

    public sealed class RuntimeEventSubscription : IAsyncEnumerable<RuntimeEvent>, IAsyncDisposable
    {
        private readonly RuntimeEventHub _owner;
        private readonly Subscriber _subscriber;
        private int _disposed;

        internal RuntimeEventSubscription(RuntimeEventHub owner, Subscriber subscriber)
        {
            _owner = owner;
            _subscriber = subscriber;
        }

        public IAsyncEnumerator<RuntimeEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            _subscriber.Channel.Reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Remove(_subscriber);
            }

            return ValueTask.CompletedTask;
        }

        internal void DisposeSynchronously()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Remove(_subscriber);
            }
        }
    }
}

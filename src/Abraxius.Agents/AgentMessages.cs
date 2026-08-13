using System.Collections.Concurrent;
using System.Threading.Channels;
using Abraxius.Protocol;

namespace Abraxius.Agents;

public sealed record AgentMessageEnvelope(
    AgentMessageId MessageId,
    MissionId MissionId,
    AssignmentId? AssignmentId,
    SpecialistInstanceId FromInstance,
    SpecialistInstanceId? ToInstance,
    SpecialistRole? ToRole,
    CorrelationId CorrelationId,
    DateTimeOffset Timestamp);

public abstract record AgentMessage(AgentMessageEnvelope Envelope)
{
    public abstract string Kind { get; }
}

public sealed record AssignmentMessage(AgentMessageEnvelope Envelope, AgentAssignment Assignment) : AgentMessage(Envelope) { public override string Kind => "assignment"; }
public sealed record EvidenceRequestMessage(AgentMessageEnvelope Envelope, string Query) : AgentMessage(Envelope) { public override string Kind => "evidence.request"; }
public sealed record EvidenceResponseMessage(AgentMessageEnvelope Envelope, string Summary, IReadOnlyList<EvidenceId> Evidence) : AgentMessage(Envelope) { public override string Kind => "evidence.response"; }
public sealed record HandoffMessage(AgentMessageEnvelope Envelope, string Objective, IReadOnlyList<EvidenceId> Evidence, string Confidence) : AgentMessage(Envelope) { public override string Kind => "handoff"; }
public sealed record ImplementationReadyMessage(AgentMessageEnvelope Envelope, string Summary, IReadOnlyList<EvidenceId> Evidence) : AgentMessage(Envelope) { public override string Kind => "implementation.ready"; }
public sealed record VerificationRequestMessage(AgentMessageEnvelope Envelope, string Objective) : AgentMessage(Envelope) { public override string Kind => "verification.request"; }
public sealed record VerificationResultMessage(AgentMessageEnvelope Envelope, VerificationStatus Status, string Summary, IReadOnlyList<EvidenceId> Evidence) : AgentMessage(Envelope) { public override string Kind => "verification.result"; }
public sealed record BlockedMessage(AgentMessageEnvelope Envelope, string Reason) : AgentMessage(Envelope) { public override string Kind => "blocked"; }
public sealed record EscalationRequestMessage(AgentMessageEnvelope Envelope, string Reason) : AgentMessage(Envelope) { public override string Kind => "escalation.request"; }
public sealed record ProgressSummaryMessage(AgentMessageEnvelope Envelope, string Summary, double Progress) : AgentMessage(Envelope) { public override string Kind => "progress"; }
public sealed record CancellationMessage(AgentMessageEnvelope Envelope, string Reason) : AgentMessage(Envelope) { public override string Kind => "cancellation"; }

public interface IAgentMessageBus
{
    ValueTask PublishAsync(AgentMessage message, CancellationToken cancellationToken = default);
    AgentMessageSubscription Subscribe(SpecialistInstanceId instanceId);
    AgentMessageSubscription SubscribeAll();
}

public sealed class AgentMessageSubscription : IAsyncDisposable
{
    private readonly ChannelReader<AgentMessage> _reader;
    private readonly Func<ValueTask> _dispose;
    internal AgentMessageSubscription(ChannelReader<AgentMessage> reader, Func<ValueTask> dispose) { _reader = reader; _dispose = dispose; }
    public IAsyncEnumerable<AgentMessage> ReadAllAsync(CancellationToken cancellationToken = default) => _reader.ReadAllAsync(cancellationToken);
    public ValueTask DisposeAsync() => _dispose();
}

public sealed class AgentMessageBus : IAgentMessageBus
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<SpecialistInstanceId, Channel<AgentMessage>> _recipients = new();
    private readonly ConcurrentDictionary<Guid, Channel<AgentMessage>> _observers = new();
    public AgentMessageBus(int capacity = 512) => _capacity = Math.Max(8, capacity);

    public async ValueTask PublishAsync(AgentMessage message, CancellationToken cancellationToken = default)
    {
        foreach (var pair in _recipients)
        {
            if (message.Envelope.ToInstance is { } target && target != pair.Key) continue;
            if (message.Envelope.ToInstance is null && message.Envelope.ToRole is null) continue;
            await pair.Value.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
        }

        foreach (var observer in _observers.Values) await observer.Writer.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public AgentMessageSubscription Subscribe(SpecialistInstanceId instanceId)
    {
        var channel = Channel.CreateBounded<AgentMessage>(new BoundedChannelOptions(_capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
        _recipients[instanceId] = channel;
        return new AgentMessageSubscription(channel.Reader, () => RemoveAsync(_recipients, instanceId, channel));
    }

    public AgentMessageSubscription SubscribeAll()
    {
        var key = Guid.NewGuid();
        var channel = Channel.CreateBounded<AgentMessage>(new BoundedChannelOptions(_capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false });
        _observers[key] = channel;
        return new AgentMessageSubscription(channel.Reader, () => RemoveAsync(_observers, key, channel));
    }

    private static ValueTask RemoveAsync<TKey>(ConcurrentDictionary<TKey, Channel<AgentMessage>> map, TKey key, Channel<AgentMessage> channel) where TKey : notnull
    {
        map.TryRemove(new KeyValuePair<TKey, Channel<AgentMessage>>(key, channel));
        channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public sealed class AgentEventHub
{
    private readonly int _capacity;
    private readonly ConcurrentDictionary<Guid, Channel<AgentEvent>> _subscriptions = new();
    public AgentEventHub(int capacity = 1024) => _capacity = Math.Max(16, capacity);
    public AgentEventSubscription Subscribe()
    {
        var key = Guid.NewGuid();
        var channel = Channel.CreateBounded<AgentEvent>(new BoundedChannelOptions(_capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false });
        _subscriptions[key] = channel;
        return new AgentEventSubscription(channel.Reader, () => { _subscriptions.TryRemove(key, out _); channel.Writer.TryComplete(); return ValueTask.CompletedTask; });
    }
    public void Publish(AgentEvent value) { foreach (var channel in _subscriptions.Values) channel.Writer.TryWrite(value); }
}

public sealed class AgentEventSubscription
{
    private readonly ChannelReader<AgentEvent> _reader;
    private readonly Func<ValueTask> _dispose;
    internal AgentEventSubscription(ChannelReader<AgentEvent> reader, Func<ValueTask> dispose) { _reader = reader; _dispose = dispose; }
    public IAsyncEnumerable<AgentEvent> ReadAllAsync(CancellationToken cancellationToken = default) => _reader.ReadAllAsync(cancellationToken);
    public ValueTask DisposeAsync() => _dispose();
}

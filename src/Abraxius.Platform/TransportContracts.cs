using System.Collections.Immutable;
using System.Text.Json;
using System.Threading.Channels;
using Abraxius.Protocol;

namespace Abraxius.Platform;

public readonly record struct RemoteHostId(Guid Value)
{
    public static RemoteHostId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum ConnectionState
{
    Connecting,
    Connected,
    Reconnecting,
    Disconnected,
    Failed
}

public enum ConnectivityState
{
    Online,
    Limited,
    Offline
}

public sealed record TransportEndpoint(Uri Address, string? DisplayName = null);

public sealed record ConnectionSnapshot(
    ConnectionState State,
    DateTimeOffset ChangedAt,
    int Attempt = 0,
    string? Reason = null);

public sealed record ConnectionResult(
    bool Succeeded,
    ConnectionSnapshot State,
    PlatformError? Error = null);

public sealed record CapabilityAdvertisement(
    CapabilityId Capability,
    CapabilityAvailability Availability,
    string? Version = null,
    IReadOnlyDictionary<string, string>? Constraints = null);

public sealed record RemoteCapabilityAdvertisement(
    RemoteHostId HostId,
    string DisplayName,
    ProtocolVersion ProtocolVersion,
    PlatformDescriptor Platform,
    RuntimeExecutionMode ExecutionMode,
    ImmutableArray<CapabilityAdvertisement> Capabilities);

public sealed record CapabilityNegotiationResult(
    bool Compatible,
    ProtocolVersion NegotiatedVersion,
    ImmutableArray<CapabilityAdvertisement> Capabilities,
    PlatformError? Error = null);

public static class CapabilityNegotiator
{
    public static CapabilityNegotiationResult Negotiate(
        ProtocolVersion localVersion,
        RemoteCapabilityAdvertisement remote)
    {
        ArgumentNullException.ThrowIfNull(remote);
        if (localVersion.Major != remote.ProtocolVersion.Major)
        {
            return new CapabilityNegotiationResult(
                false,
                localVersion,
                ImmutableArray<CapabilityAdvertisement>.Empty,
                new PlatformError(
                    PlatformErrorCode.ProtocolMismatch,
                    $"Protocol major version {remote.ProtocolVersion.Major} is incompatible with {localVersion.Major}."));
        }

        var negotiated = new ProtocolVersion(
            localVersion.Major,
            Math.Min(localVersion.Minor, remote.ProtocolVersion.Minor));
        return new CapabilityNegotiationResult(true, negotiated, remote.Capabilities);
    }
}

public sealed record TransportMessage(
    ProtocolVersion Version,
    string MessageType,
    CorrelationId CorrelationId,
    ExecutionId? ExecutionId,
    JsonElement Payload)
{
    public static TransportMessage From<T>(ProtocolEnvelope<T> envelope, string? messageType = null) =>
        new(
            envelope.Version,
            messageType ?? envelope.MessageType,
            envelope.CorrelationId,
            envelope.ExecutionId,
            JsonSerializer.SerializeToElement(envelope.Payload, ProtocolJson.CreateOptions()));
}

public sealed record ExecutionStateQuery(ExecutionId ExecutionId, CorrelationId CorrelationId);

public interface IAbraxiusTransport : IAsyncDisposable
{
    ConnectionSnapshot State { get; }

    ValueTask<ConnectionResult> ConnectAsync(
        TransportEndpoint endpoint,
        CancellationToken cancellationToken = default);

    ValueTask SendAsync(TransportMessage message, CancellationToken cancellationToken = default);

    IAsyncEnumerable<TransportMessage> ReceiveAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(string? reason = null, CancellationToken cancellationToken = default);
}

public static class TransportExtensions
{
    public static ValueTask SendAsync<T>(
        this IAbraxiusTransport transport,
        ProtocolEnvelope<T> envelope,
        CancellationToken cancellationToken = default) =>
        transport.SendAsync(TransportMessage.From(envelope), cancellationToken);
}

/// <summary>A bounded loopback transport used by tests and local adapters.
/// It models backpressure without pretending to be a network implementation.</summary>
public sealed class InMemoryAbraxiusTransport : IAbraxiusTransport
{
    private readonly Channel<TransportMessage> _incoming;
    private readonly ChannelWriter<TransportMessage> _outgoing;
    private readonly CancellationTokenSource _lifetime = new();
    private ConnectionSnapshot _state = new(ConnectionState.Disconnected, DateTimeOffset.UtcNow);
    private int _disposed;

    private InMemoryAbraxiusTransport(Channel<TransportMessage> incoming, ChannelWriter<TransportMessage> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public ConnectionSnapshot State => _state;

    public static (InMemoryAbraxiusTransport Left, InMemoryAbraxiusTransport Right) CreatePair(int capacity = 256)
    {
        var leftToRight = Channel.CreateBounded<TransportMessage>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        var rightToLeft = Channel.CreateBounded<TransportMessage>(new BoundedChannelOptions(Math.Max(1, capacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        return (new InMemoryAbraxiusTransport(rightToLeft, leftToRight.Writer), new InMemoryAbraxiusTransport(leftToRight, rightToLeft.Writer));
    }

    public ValueTask<ConnectionResult> ConnectAsync(TransportEndpoint endpoint, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = new ConnectionSnapshot(ConnectionState.Connected, DateTimeOffset.UtcNow);
        return ValueTask.FromResult(new ConnectionResult(true, _state));
    }

    public async ValueTask SendAsync(TransportMessage message, CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await _outgoing.WriteAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TransportMessage> ReceiveAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        await foreach (var message in _incoming.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    public ValueTask DisconnectAsync(string? reason = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = new ConnectionSnapshot(ConnectionState.Disconnected, DateTimeOffset.UtcNow, Reason: reason);
        _incoming.Writer.TryComplete();
        _outgoing.TryComplete();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await DisconnectAsync("disposed").ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private void EnsureConnected()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_state.State != ConnectionState.Connected)
        {
            throw new InvalidOperationException("The transport is not connected.");
        }
    }
}

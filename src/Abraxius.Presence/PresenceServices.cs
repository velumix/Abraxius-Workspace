using System.Collections.Immutable;

namespace Abraxius.Presence;

public interface ITrayService : IAsyncDisposable
{
    bool IsSupported { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask SetStateAsync(TrayPresentationState state, CancellationToken cancellationToken = default);
    ValueTask ShowMenuAsync(CancellationToken cancellationToken = default);
}

public interface INativeNotificationService
{
    bool IsAvailable { get; }
    ValueTask<bool> DeliverAsync(AbraxiusNotification notification, CancellationToken cancellationToken = default);
}

public interface IInAppNotificationSink
{
    ValueTask DeliverAsync(AbraxiusNotification notification, CancellationToken cancellationToken = default);
}

public interface INotificationPermissionService
{
    ValueTask<NotificationPermissionState> GetStateAsync(CancellationToken cancellationToken = default);
    ValueTask<NotificationPermissionState> RequestAsync(CancellationToken cancellationToken = default);
}

public interface IAttentionPolicy
{
    AttentionDecision Evaluate(AbraxiusNotification notification, AttentionContext context);
}

public interface INotificationHub
{
    event EventHandler<NotificationDeliveryResult>? Delivered;
    NotificationDiagnostics Diagnostics { get; }
    IReadOnlyList<AbraxiusNotification> History { get; }
    ValueTask<NotificationDeliveryResult> PublishAsync(AbraxiusNotification notification, AttentionContext context, CancellationToken cancellationToken = default);
}

public interface INeedsYouStore : IAsyncDisposable
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask UpsertAsync(NeedsYouItem item, CancellationToken cancellationToken = default);
    ValueTask<NeedsYouItem?> GetAsync(NeedsYouId id, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<NeedsYouItem>> ListAsync(bool includeResolved = false, CancellationToken cancellationToken = default);
}

public interface IPresenceSettingsStore
{
    ValueTask<PresenceSettings?> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(PresenceSettings settings, CancellationToken cancellationToken = default);
}

public sealed class InMemoryPresenceSettingsStore : IPresenceSettingsStore
{
    private PresenceSettings? _settings;
    public ValueTask<PresenceSettings?> LoadAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_settings); }
    public ValueTask SaveAsync(PresenceSettings settings, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _settings = settings; return ValueTask.CompletedTask; }
}

public interface INeedsYouService
{
    event EventHandler? Changed;
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask<NeedsYouItem> CreateAsync(NeedsYouItem item, AttentionContext context, CancellationToken cancellationToken = default);
    ValueTask<NeedsYouItem?> MarkViewedAsync(NeedsYouId id, CancellationToken cancellationToken = default);
    ValueTask<NeedsYouItem?> SnoozeAsync(NeedsYouId id, DateTimeOffset until, CancellationToken cancellationToken = default);
    ValueTask<NeedsYouItem?> ResolveAsync(NeedsYouId id, NeedsYouResolution resolution, string? note = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<NeedsYouItem>> ListAsync(bool includeResolved = false, CancellationToken cancellationToken = default);
}

public enum ActivationKind { TrayOpen, NotificationAction, DeepLink, FileActivation, Startup }

public sealed record ActivationRequest(ActivationKind Kind, string? Route = null, NotificationActionId? Action = null, NotificationTarget? Target = null);
public sealed record ActivationResult(bool Accepted, string Surface, NotificationTarget? Target = null, string? Error = null);

public interface IActivationRouter
{
    event EventHandler<ActivationResult>? Activated;
    ValueTask<ActivationResult> RouteAsync(ActivationRequest request, CancellationToken cancellationToken = default);
}

public interface IBackgroundRuntimeCoordinator
{
    event EventHandler<PresenceSnapshot>? Changed;
    PresenceSnapshot Snapshot { get; }
    void SetWindowState(WindowPresenceState state);
    void SetMode(BackgroundExecutionMode mode);
    void SetActiveMissionCount(int count);
    void SetPendingNeedsYouCount(int count);
    ValueTask CheckpointAsync(string reason, CancellationToken cancellationToken = default);
}

public interface ITrayStateAggregator
{
    TrayPresentationState Build(PresenceSnapshot snapshot, string connectionState = "Connected", string updateState = "Current");
}

public sealed class NullTrayService : ITrayService
{
    public bool IsSupported => false;
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask SetStateAsync(TrayPresentationState state, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask ShowMenuAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class UnavailableNativeNotificationService : INativeNotificationService
{
    public bool IsAvailable => false;
    public ValueTask<bool> DeliverAsync(AbraxiusNotification notification, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(false); }
}

public sealed class ConfigurableNativeNotificationService : INativeNotificationService
{
    private INativeNotificationService _inner = new UnavailableNativeNotificationService();
    public bool IsAvailable => Volatile.Read(ref _inner).IsAvailable;
    public void Configure(INativeNotificationService service) => Interlocked.Exchange(ref _inner, service ?? throw new ArgumentNullException(nameof(service)));
    public ValueTask<bool> DeliverAsync(AbraxiusNotification notification, CancellationToken cancellationToken = default) => Volatile.Read(ref _inner).DeliverAsync(notification, cancellationToken);
}

public sealed class ConfigurableNotificationPermissionService : INotificationPermissionService
{
    private INotificationPermissionService _inner = new StaticNotificationPermissionService();
    public void Configure(INotificationPermissionService service) => Interlocked.Exchange(ref _inner, service ?? throw new ArgumentNullException(nameof(service)));
    public ValueTask<NotificationPermissionState> GetStateAsync(CancellationToken cancellationToken = default) => Volatile.Read(ref _inner).GetStateAsync(cancellationToken);
    public ValueTask<NotificationPermissionState> RequestAsync(CancellationToken cancellationToken = default) => Volatile.Read(ref _inner).RequestAsync(cancellationToken);
}

public sealed class StaticNotificationPermissionService(NotificationPermissionState state = NotificationPermissionState.Unavailable) : INotificationPermissionService
{
    private NotificationPermissionState _state = state;
    public ValueTask<NotificationPermissionState> GetStateAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_state); }
    public ValueTask<NotificationPermissionState> RequestAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); if (_state == NotificationPermissionState.NotRequested) _state = NotificationPermissionState.Denied; return ValueTask.FromResult(_state); }
}

public sealed class InMemoryInAppNotificationSink : IInAppNotificationSink
{
    private readonly object _gate = new();
    private readonly Queue<AbraxiusNotification> _items = new();
    public int Limit { get; }
    public InMemoryInAppNotificationSink(int limit = 64) => Limit = Math.Max(8, limit);
    public IReadOnlyList<AbraxiusNotification> Items { get { lock (_gate) return _items.ToArray(); } }
    public event EventHandler<AbraxiusNotification>? Received;
    public ValueTask DeliverAsync(AbraxiusNotification notification, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) { _items.Enqueue(notification); while (_items.Count > Limit) _items.Dequeue(); }
        Received?.Invoke(this, notification);
        return ValueTask.CompletedTask;
    }
}

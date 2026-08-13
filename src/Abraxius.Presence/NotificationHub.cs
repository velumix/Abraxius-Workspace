namespace Abraxius.Presence;

public sealed class NotificationHub : INotificationHub
{
    private readonly IAttentionPolicy _policy;
    private readonly INativeNotificationService _native;
    private readonly IInAppNotificationSink _inApp;
    private readonly object _gate = new();
    private readonly Queue<AbraxiusNotification> _history = new();
    private readonly Dictionary<string, DateTimeOffset> _dedupe = new(StringComparer.Ordinal);
    private readonly Queue<DateTimeOffset> _nativeWindow = new();
    private long _generated, _nativeDelivered, _inAppDelivered, _suppressed, _coalesced;
    private DateTimeOffset? _lastDelivery;
    private string? _lastSuppression;
    private NotificationPermissionState _permission;

    public NotificationHub(IAttentionPolicy policy, INativeNotificationService native, IInAppNotificationSink inApp)
    {
        _policy = policy; _native = native; _inApp = inApp;
    }

    public event EventHandler<NotificationDeliveryResult>? Delivered;
    public IReadOnlyList<AbraxiusNotification> History { get { lock (_gate) return _history.Reverse().ToArray(); } }
    public NotificationDiagnostics Diagnostics { get { lock (_gate) return new(_permission, _generated, _nativeDelivered, _inAppDelivered, _suppressed, _coalesced, _lastDelivery, _lastSuppression); } }

    public async ValueTask<NotificationDeliveryResult> PublishAsync(AbraxiusNotification notification, AttentionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        AttentionDecision decision;
        lock (_gate)
        {
            _generated++;
            _permission = context.Permission;
            TrimDedupe(context.Now);
            if (!string.IsNullOrWhiteSpace(notification.DeduplicationKey) && _dedupe.TryGetValue(notification.DeduplicationKey, out var prior) && context.Now - prior < TimeSpan.FromMinutes(2))
            {
                _coalesced++;
                return Complete(new(NotificationDelivery.None, false, "Coalesced duplicate", notification));
            }
            if (!string.IsNullOrWhiteSpace(notification.DeduplicationKey)) _dedupe[notification.DeduplicationKey] = context.Now;
            decision = _policy.Evaluate(notification, context);
            if (decision.Delivery is NotificationDelivery.Native or NotificationDelivery.NativeAndInApp && !AllowNative(context.Now, notification.Severity, context.Settings.MaximumNativePerMinute))
                decision = new(NotificationDelivery.InApp, "Rate limited", decision.CreateNeedsYou, decision.Redact);
            _history.Enqueue(notification);
            while (_history.Count > Math.Max(16, context.Settings.HistoryLimit)) _history.Dequeue();
        }

        var delivered = false;
        var outbound = decision.Redact ? Redact(notification, context.Settings.PreviewPrivacy) : notification;
        if (decision.Delivery is NotificationDelivery.InApp or NotificationDelivery.NativeAndInApp)
        {
            await _inApp.DeliverAsync(outbound, cancellationToken).ConfigureAwait(false);
            lock (_gate) { _inAppDelivered++; _lastDelivery = context.Now; }
            delivered = true;
        }
        if (decision.Delivery is NotificationDelivery.Native or NotificationDelivery.NativeAndInApp or NotificationDelivery.Critical)
        {
            var nativeDelivered = await _native.DeliverAsync(outbound, cancellationToken).ConfigureAwait(false);
            delivered = nativeDelivered || delivered;
            lock (_gate) { if (nativeDelivered) _nativeDelivered++; _lastDelivery = delivered ? context.Now : _lastDelivery; }
            if (!nativeDelivered && decision.Delivery != NotificationDelivery.NativeAndInApp)
            {
                await _inApp.DeliverAsync(outbound, cancellationToken).ConfigureAwait(false);
                lock (_gate) { _inAppDelivered++; _lastDelivery = context.Now; }
                delivered = true;
            }
        }
        if (!delivered)
        {
            lock (_gate) { _suppressed++; _lastSuppression = decision.Reason; }
        }
        return Complete(new(decision.Delivery, delivered, decision.Reason, outbound));
    }

    private NotificationDeliveryResult Complete(NotificationDeliveryResult result) { Delivered?.Invoke(this, result); return result; }
    private bool AllowNative(DateTimeOffset now, NotificationSeverity severity, int limit)
    {
        while (_nativeWindow.Count > 0 && now - _nativeWindow.Peek() >= TimeSpan.FromMinutes(1)) _nativeWindow.Dequeue();
        if (severity == NotificationSeverity.Critical) return true;
        if (_nativeWindow.Count >= Math.Max(1, limit)) return false;
        _nativeWindow.Enqueue(now); return true;
    }
    private void TrimDedupe(DateTimeOffset now)
    {
        foreach (var key in _dedupe.Where(pair => now - pair.Value > TimeSpan.FromMinutes(10)).Select(static pair => pair.Key).ToArray()) _dedupe.Remove(key);
    }
    private static AbraxiusNotification Redact(AbraxiusNotification notification, NotificationPrivacy configured) => configured == NotificationPrivacy.Hidden || notification.Privacy == NotificationPrivacy.Hidden
        ? notification with { Title = "Abraxius", Body = "Abraxius needs your attention." }
        : notification with { Body = notification.Severity == NotificationSeverity.Completion ? "Background work completed." : "Open Abraxius to review details." };
}

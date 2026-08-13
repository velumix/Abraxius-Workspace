namespace Abraxius.Presence;

public sealed class DefaultAttentionPolicy : IAttentionPolicy
{
    public AttentionDecision Evaluate(AbraxiusNotification notification, AttentionContext context)
    {
        if (notification.Expiry is { } expiry && expiry <= context.Now) return new(NotificationDelivery.None, "Expired");
        var needsYou = notification.Category == NotificationCategory.NeedsYou || notification.Severity == NotificationSeverity.AttentionRequired;
        var redacted = context.SensitiveProject || notification.Privacy is NotificationPrivacy.Redacted or NotificationPrivacy.Hidden || context.Settings.PreviewPrivacy != NotificationPrivacy.Full;

        if (context.WindowState == WindowPresenceState.VisibleFocused && context.Settings.InAppWhenFocused)
            return new(NotificationDelivery.InApp, "Window focused", needsYou, redacted);

        if (context.Settings.EffectiveQuietHours.Contains(context.Now) && notification.Severity != NotificationSeverity.Critical)
            return new(needsYou ? NotificationDelivery.InApp : NotificationDelivery.None, "Quiet hours", needsYou, redacted);

        if (!IsEnabled(notification, context.Settings))
            return new(needsYou ? NotificationDelivery.InApp : NotificationDelivery.None, "Category disabled", needsYou, redacted);

        if (!context.NativeAvailable || context.Permission != NotificationPermissionState.Granted)
            return new(NotificationDelivery.InApp, "Native notifications unavailable", needsYou, redacted);

        return new(notification.Severity == NotificationSeverity.Critical ? NotificationDelivery.Critical : NotificationDelivery.Native, "Native delivery", needsYou, redacted);
    }

    private static bool IsEnabled(AbraxiusNotification notification, PresenceSettings settings) => notification.Category switch
    {
        NotificationCategory.NeedsYou => settings.NativeNeedsYou,
        NotificationCategory.Mission => settings.NativeMissionCompletion,
        NotificationCategory.Verification => settings.NativeVerificationFailure,
        NotificationCategory.Update => settings.NativeUpdates,
        _ => notification.Severity >= NotificationSeverity.Warning
    };
}

public sealed class TrayStateAggregator : ITrayStateAggregator
{
    public TrayPresentationState Build(PresenceSnapshot snapshot, string connectionState = "Connected", string updateState = "Current")
    {
        var state = snapshot.PendingNeedsYouCount > 0 ? TrayRuntimeState.AttentionRequired
            : snapshot.ActiveMissionCount > 0 ? TrayRuntimeState.Working
            : string.Equals(connectionState, "Connected", StringComparison.OrdinalIgnoreCase) ? TrayRuntimeState.Idle : TrayRuntimeState.Degraded;
        var detail = snapshot.PendingNeedsYouCount > 0
            ? $"{snapshot.ActiveMissionCount} missions running · {snapshot.PendingNeedsYouCount} needs you"
            : snapshot.ActiveMissionCount > 0 ? $"{snapshot.ActiveMissionCount} missions running" : $"Idle · {connectionState}";
        return new(state, snapshot.ActiveMissionCount, snapshot.PendingNeedsYouCount, snapshot.AdmissionPaused ? 0 : snapshot.ActiveMissionCount, connectionState, updateState, $"Abraxius\n{detail}", snapshot.PendingNeedsYouCount > 0 ? NotificationSeverity.AttentionRequired : null);
    }
}

public sealed class BackgroundRuntimeCoordinator : IBackgroundRuntimeCoordinator
{
    private readonly object _gate = new();
    private readonly ITrayStateAggregator _tray;
    private readonly Func<string, CancellationToken, ValueTask> _checkpoint;
    private PresenceSnapshot _snapshot;

    public BackgroundRuntimeCoordinator(ITrayStateAggregator? tray = null, Func<string, CancellationToken, ValueTask>? checkpoint = null)
    {
        _tray = tray ?? new TrayStateAggregator();
        _checkpoint = checkpoint ?? (static (_, cancellationToken) => { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; });
        _snapshot = new(WindowPresenceState.Unavailable, BackgroundExecutionMode.ContinueNormally, false, 0, 0,
            new(TrayRuntimeState.Idle, 0, 0, 0, "Connected", "Current", "Abraxius\nIdle"), DateTimeOffset.UtcNow);
    }

    public event EventHandler<PresenceSnapshot>? Changed;
    public PresenceSnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public void SetWindowState(WindowPresenceState state) => Update(item => item with { WindowState = state });
    public void SetMode(BackgroundExecutionMode mode) => Update(item => item with { BackgroundMode = mode, AdmissionPaused = mode is BackgroundExecutionMode.PauseNonCritical or BackgroundExecutionMode.PauseAll });
    public void SetActiveMissionCount(int count) => Update(item => item with { ActiveMissionCount = Math.Max(0, count) });
    public void SetPendingNeedsYouCount(int count) => Update(item => item with { PendingNeedsYouCount = Math.Max(0, count) });
    public ValueTask CheckpointAsync(string reason, CancellationToken cancellationToken = default) => _checkpoint(reason, cancellationToken);

    private void Update(Func<PresenceSnapshot, PresenceSnapshot> mutate)
    {
        PresenceSnapshot next;
        lock (_gate)
        {
            next = mutate(_snapshot) with { UpdatedAt = DateTimeOffset.UtcNow };
            next = next with { Tray = _tray.Build(next) };
            if (next == _snapshot) return;
            _snapshot = next;
        }
        Changed?.Invoke(this, next);
    }
}

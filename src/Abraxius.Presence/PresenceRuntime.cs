using System.Collections.Immutable;
using Abraxius.Agents;

namespace Abraxius.Presence;

public sealed class PresenceRuntime : IAsyncDisposable
{
    private readonly AgentKernel _agents;
    private readonly ConfigurableNativeNotificationService _native;
    private readonly ConfigurableNotificationPermissionService _permission;
    private readonly IPresenceSettingsStore _settingsStore;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _observedTransitions = new(StringComparer.Ordinal);
    private AgentEventSubscription? _subscription;
    private Task? _pump;
    private int _started;
    private int _disposed;

    public PresenceRuntime(AgentKernel agents, INeedsYouStore store, IPresenceSettingsStore settingsStore)
    {
        _agents = agents;
        _native = new ConfigurableNativeNotificationService();
        _permission = new ConfigurableNotificationPermissionService();
        _settingsStore = settingsStore;
        InApp = new InMemoryInAppNotificationSink();
        Notifications = new NotificationHub(new DefaultAttentionPolicy(), _native, InApp);
        NeedsYou = new NeedsYouService(store, Notifications);
        Activation = new ActivationRouter();
        Background = new BackgroundRuntimeCoordinator();
        Settings = new PresenceSettings();
        NeedsYou.Changed += OnNeedsYouChanged;
    }

    public PresenceSettings Settings { get; private set; }
    public NotificationHub Notifications { get; }
    public NeedsYouService NeedsYou { get; }
    public InMemoryInAppNotificationSink InApp { get; }
    public ActivationRouter Activation { get; }
    public BackgroundRuntimeCoordinator Background { get; }

    public void Configure(PresenceSettings settings, INativeNotificationService? native = null, INotificationPermissionService? permission = null)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (native is not null) _native.Configure(native);
        if (permission is not null) _permission.Configure(permission);
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        await NeedsYou.InitializeAsync(cancellationToken).ConfigureAwait(false);
        Settings = await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false) ?? Settings;
        await RefreshNeedsYouCountAsync(cancellationToken).ConfigureAwait(false);
        _subscription = _agents.Events.Subscribe();
        _pump = Task.Run(ObserveAgentsAsync, CancellationToken.None);
    }

    public async ValueTask UpdateSettingsAsync(PresenceSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        Background.SetMode(settings.BackgroundMode);
        await _settingsStore.SaveAsync(settings, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<AttentionContext> CreateContextAsync(bool sensitiveProject = false, CancellationToken cancellationToken = default)
    {
        var permission = await _permission.GetStateAsync(cancellationToken).ConfigureAwait(false);
        return new(Background.Snapshot.WindowState, Settings, DateTimeOffset.UtcNow, _native.IsAvailable, permission, sensitiveProject);
    }

    public async ValueTask<NeedsYouItem> RequestAsync(MissionId? missionId, AssignmentId? assignmentId, string source, NeedsYouReason reason, string summary, NotificationActionId action, IReadOnlyList<Abraxius.Protocol.EvidenceId>? evidence = null, DateTimeOffset? deadline = null, string? sourceEventId = null, CancellationToken cancellationToken = default)
    {
        var item = new NeedsYouItem(NeedsYouId.New(), missionId, assignmentId, source, reason, NotificationSeverity.AttentionRequired, action, summary,
            evidence?.ToImmutableArray() ?? ImmutableArray<Abraxius.Protocol.EvidenceId>.Empty, DateTimeOffset.UtcNow, deadline, SourceEventId: sourceEventId);
        return await NeedsYou.CreateAsync(item, await CreateContextAsync(cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PublishAsync(AbraxiusNotification notification, CancellationToken cancellationToken = default) =>
        _ = await Notifications.PublishAsync(notification, await CreateContextAsync(cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);

    private async Task ObserveAgentsAsync()
    {
        if (_subscription is null) return;
        try
        {
            await foreach (var item in _subscription.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (item.Kind != AgentEventKind.MissionStateChanged || item.Payload is not Mission mission) continue;
                Background.SetActiveMissionCount(_agents.Missions.Count(static current => current.State is MissionState.Planning or MissionState.Executing or MissionState.Verifying));
                var key = $"mission:{mission.Id}:{mission.State}";
                lock (_observedTransitions) { if (!_observedTransitions.Add(key)) continue; }
                if (mission.State == MissionState.Succeeded)
                {
                    await PublishAsync(new AbraxiusNotification(NotificationId.New(), NotificationCategory.Mission, NotificationSeverity.Completion,
                        "Mission verified", mission.Intent.Objective, new NotificationTarget(mission.Id), [new(new NotificationActionId("mission.open"), "Open")],
                        "AgentKernel", item.Timestamp, DeduplicationKey: key, Privacy: NotificationPrivacy.Redacted, SourceEventId: key), _lifetime.Token).ConfigureAwait(false);
                }
                else if (mission.State == MissionState.Blocked)
                {
                    await RequestAsync(mission.Id, null, "Athena", NeedsYouReason.AmbiguousChoice, item.Detail ?? "Mission is blocked and requires review.",
                        new NotificationActionId("needs-you.review"), mission.SafeEvidence, sourceEventId: key, cancellationToken: _lifetime.Token).ConfigureAwait(false);
                }
                else if (mission.State == MissionState.Failed)
                {
                    await PublishAsync(new AbraxiusNotification(NotificationId.New(), NotificationCategory.Verification, NotificationSeverity.Warning,
                        "Mission verification failed", item.Detail ?? mission.Intent.Objective, new NotificationTarget(mission.Id), [new(new NotificationActionId("mission.open"), "Inspect")],
                        "Argus", item.Timestamp, DeduplicationKey: key, Privacy: NotificationPrivacy.Redacted, SourceEventId: key), _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private void OnNeedsYouChanged(object? sender, EventArgs e) => _ = RefreshNeedsYouCountAsync(_lifetime.Token);
    private async Task RefreshNeedsYouCountAsync(CancellationToken cancellationToken)
    {
        try { Background.SetPendingNeedsYouCount((await NeedsYou.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false)).Count); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        NeedsYou.Changed -= OnNeedsYouChanged;
        _lifetime.Cancel();
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
        if (_pump is not null) { try { await _pump.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        _lifetime.Dispose();
    }
}

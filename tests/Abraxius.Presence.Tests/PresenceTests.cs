using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Presence;
using Abraxius.Protocol;
using Abraxius.Runtime;
using Xunit;

namespace Abraxius.Presence.Tests;

public sealed class PresenceTests
{
    [Fact]
    public void QuietHoursHandleCrossMidnightAndTimezone()
    {
        var quiet = new QuietHours(true, new TimeOnly(23, 0), new TimeOnly(7, 0), "UTC");
        Assert.True(quiet.Contains(new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero)));
        Assert.True(quiet.Contains(new DateTimeOffset(2026, 1, 2, 6, 59, 0, TimeSpan.Zero)));
        Assert.False(quiet.Contains(new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task FocusedWindowUsesInAppWithoutNativeDuplicate()
    {
        var native = new FakeNative(); var inApp = new InMemoryInAppNotificationSink();
        var hub = new NotificationHub(new DefaultAttentionPolicy(), native, inApp);
        var result = await hub.PublishAsync(Notification(), Context(WindowPresenceState.VisibleFocused));
        Assert.Equal(NotificationDelivery.InApp, result.Delivery);
        Assert.Single(inApp.Items); Assert.Equal(0, native.Count);
    }

    [Fact]
    public async Task DuplicateNotificationIsCoalesced()
    {
        var hub = new NotificationHub(new DefaultAttentionPolicy(), new FakeNative(), new InMemoryInAppNotificationSink());
        var first = await hub.PublishAsync(Notification("same"), Context(WindowPresenceState.Hidden));
        var second = await hub.PublishAsync(Notification("same"), Context(WindowPresenceState.Hidden));
        Assert.True(first.Delivered); Assert.False(second.Delivered); Assert.Equal("Coalesced duplicate", second.Reason);
        Assert.Equal(1, hub.Diagnostics.Coalesced);
    }

    [Fact]
    public async Task NeedsYouRemainsPendingWhenDeliveryIsUnavailable()
    {
        await using var store = new InMemoryNeedsYouStore();
        var hub = new NotificationHub(new DefaultAttentionPolicy(), new UnavailableNativeNotificationService(), new InMemoryInAppNotificationSink());
        var service = new NeedsYouService(store, hub); await service.InitializeAsync();
        var item = new NeedsYouItem(NeedsYouId.New(), MissionId.New(), null, "Daedalus", NeedsYouReason.ApprovalRequired,
            NotificationSeverity.AttentionRequired, new NotificationActionId("needs-you.review"), "Review a gated mutation.", ImmutableArray<Abraxius.Protocol.EvidenceId>.Empty, DateTimeOffset.UtcNow);
        await service.CreateAsync(item, Context(WindowPresenceState.Hidden, native: false, permission: NotificationPermissionState.Denied));
        Assert.Equal(NeedsYouState.Pending, Assert.Single(await service.ListAsync()).State);
    }

    [Fact]
    public async Task ResolveIsDurableAndDoesNotDeleteItem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abraxius-presence-{Guid.NewGuid():N}", "presence.db");
        try
        {
            var id = NeedsYouId.New();
            await using (var store = new SqliteNeedsYouStore(path))
            {
                var service = new NeedsYouService(store, new NotificationHub(new DefaultAttentionPolicy(), new FakeNative(), new InMemoryInAppNotificationSink())); await service.InitializeAsync();
                await service.CreateAsync(new NeedsYouItem(id, null, null, "Athena", NeedsYouReason.AmbiguousChoice, NotificationSeverity.AttentionRequired, new("needs-you.review"), "Choose direction.", [], DateTimeOffset.UtcNow), Context(WindowPresenceState.VisibleFocused));
                await service.ResolveAsync(id, NeedsYouResolution.Approved);
            }
            await using var reopened = new SqliteNeedsYouStore(path); await reopened.InitializeAsync();
            Assert.Empty(await reopened.ListAsync());
            var resolved = Assert.Single(await reopened.ListAsync(includeResolved: true));
            Assert.Equal(NeedsYouResolution.Approved, resolved.Resolution);
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task ActivationRejectsUnknownActionsAndSchemes()
    {
        var router = new ActivationRouter();
        Assert.False((await router.RouteAsync(new ActivationRequest(ActivationKind.NotificationAction, Action: new("shell.exec"), Route: "abraxius://home"))).Accepted);
        Assert.False((await router.RouteAsync(new ActivationRequest(ActivationKind.DeepLink, Route: "file:///etc/passwd"))).Accepted);
        Assert.True((await router.RouteAsync(new ActivationRequest(ActivationKind.DeepLink, Route: $"abraxius://needs-you/{NeedsYouId.New()}"))).Accepted);
        var mission = MissionId.New(); var assignment = AssignmentId.New();
        var exact = await router.RouteAsync(new ActivationRequest(ActivationKind.DeepLink, Route: $"abraxius://mission/{mission}/assignment/{assignment}"));
        Assert.True(exact.Accepted); Assert.Equal(assignment, exact.Target?.AssignmentId);
    }

    [Fact]
    public void BackgroundModesAreEventDrivenAndPauseAdmissionPrecisely()
    {
        var coordinator = new BackgroundRuntimeCoordinator(); var changes = 0; coordinator.Changed += (_, _) => changes++;
        coordinator.SetWindowState(WindowPresenceState.Hidden); coordinator.SetActiveMissionCount(3); coordinator.SetMode(BackgroundExecutionMode.PauseNonCritical);
        Assert.True(coordinator.Snapshot.AdmissionPaused); Assert.Equal(3, coordinator.Snapshot.ActiveMissionCount); Assert.Equal(TrayRuntimeState.Working, coordinator.Snapshot.Tray.RuntimeState); Assert.Equal(3, changes);
        coordinator.SetMode(BackgroundExecutionMode.ContinueNormally); Assert.False(coordinator.Snapshot.AdmissionPaused);
    }

    [Fact]
    public async Task HistoryAndNativeRateAreBounded()
    {
        var native = new FakeNative(); var hub = new NotificationHub(new DefaultAttentionPolicy(), native, new InMemoryInAppNotificationSink());
        var settings = new PresenceSettings(HistoryLimit: 16, MaximumNativePerMinute: 2, PreviewPrivacy: NotificationPrivacy.Full);
        for (var index = 0; index < 40; index++) await hub.PublishAsync(Notification($"n-{index}"), new(WindowPresenceState.Hidden, settings, DateTimeOffset.UtcNow.AddMilliseconds(index), true, NotificationPermissionState.Granted));
        Assert.Equal(16, hub.History.Count); Assert.Equal(2, native.Count); Assert.True(hub.Diagnostics.InAppDelivered >= 38);
    }

    [Fact]
    public async Task MissionContinuesWhileHiddenAndProducesOneCompletionNotification()
    {
        await using var runtime = AbraxiusRuntimeHost.CreateDefault(new RuntimeHostOptions(UseFileEvidence: false, UseFileLedger: false, UseFileProgression: false, UseFilePresence: false));
        var native = new FakeNative();
        runtime.Presence.Configure(runtime.Presence.Settings with { PreviewPrivacy = NotificationPrivacy.Full }, native, new StaticNotificationPermissionService(NotificationPermissionState.Granted));
        await runtime.StartAsync();
        runtime.Presence.Background.SetWindowState(WindowPresenceState.Hidden);
        var result = await runtime.RunMissionAsync(new Intent("Find references to ExecutionGraph.", CorrelationId.New()), explicitRole: SpecialistRole.Investigator);
        Assert.True(result.Succeeded);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        while (runtime.Presence.Notifications.History.Count == 0 && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.Single(runtime.Presence.Notifications.History, static item => item.Category == NotificationCategory.Mission);
        Assert.Equal(1, native.Count);
    }

    private static AbraxiusNotification Notification(string key = "mission:one") => new(NotificationId.New(), NotificationCategory.Mission, NotificationSeverity.Completion, "Mission verified", "Completed.", new NotificationTarget(), [new(new("mission.open"), "Open")], "test", DateTimeOffset.UtcNow, DeduplicationKey: key);
    private static AttentionContext Context(WindowPresenceState state, bool native = true, NotificationPermissionState permission = NotificationPermissionState.Granted) => new(state, new PresenceSettings(PreviewPrivacy: NotificationPrivacy.Full), DateTimeOffset.UtcNow, native, permission);

    private sealed class FakeNative : INativeNotificationService
    {
        public int Count { get; private set; }
        public bool IsAvailable => true;
        public ValueTask<bool> DeliverAsync(AbraxiusNotification notification, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Count++; return ValueTask.FromResult(true); }
    }
}

using Abraxius.Presence;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class PresenceBenchmarks
{
    private readonly DefaultAttentionPolicy _policy = new();
    private readonly TrayStateAggregator _aggregator = new();
    private readonly AbraxiusNotification _notification = new(NotificationId.New(), NotificationCategory.Mission, NotificationSeverity.Completion, "Mission verified", "Completed.", new NotificationTarget(), [], "benchmark", DateTimeOffset.UtcNow, DeduplicationKey: "benchmark");
    private readonly AttentionContext _context = new(WindowPresenceState.Hidden, new PresenceSettings(PreviewPrivacy: NotificationPrivacy.Redacted), DateTimeOffset.UtcNow, true, NotificationPermissionState.Granted);

    [Benchmark]
    public AttentionDecision Classify() => _policy.Evaluate(_notification, _context);

    [Benchmark]
    public TrayPresentationState Aggregate() => _aggregator.Build(new PresenceSnapshot(WindowPresenceState.Hidden, BackgroundExecutionMode.ContinueNormally, false, 3, 1, new(TrayRuntimeState.Working, 3, 1, 3, "Connected", "Current", "Abraxius"), DateTimeOffset.UtcNow));
}

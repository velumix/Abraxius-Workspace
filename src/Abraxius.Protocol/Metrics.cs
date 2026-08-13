namespace Abraxius.Protocol;

public sealed record RuntimeMetricsSnapshot(
    long TotalTasks,
    long CompletedTasks,
    long FailedTasks,
    long CancelledTasks,
    long TimedOutTasks,
    long SkippedTasks,
    long RunningTasks,
    long ReadyTasks,
    long QueuedTasks,
    long MaxObservedConcurrency,
    long EventsPublished,
    DateTimeOffset CapturedAt,
    long QueueWaitTicks = 0,
    long ExecutionTicks = 0);

public interface IRuntimeMetricsSource
{
    RuntimeMetricsSnapshot Snapshot();
}

using Abraxius.Protocol;

namespace Abraxius.Scheduler;

public sealed class SchedulerMetrics : IRuntimeMetricsSource
{
    private long _totalTasks;
    private long _completedTasks;
    private long _failedTasks;
    private long _cancelledTasks;
    private long _timedOutTasks;
    private long _skippedTasks;
    private long _runningTasks;
    private long _readyTasks;
    private long _queuedTasks;
    private long _maxObservedConcurrency;
    private long _eventsPublished;
    private long _queueWaitTicks;
    private long _executionTicks;

    public void RecordCreated(int count) => Interlocked.Add(ref _totalTasks, count);
    public void RecordReady() => Interlocked.Increment(ref _readyTasks);
    public void RecordQueued()
    {
        Interlocked.Increment(ref _queuedTasks);
        Interlocked.Decrement(ref _readyTasks);
    }

    public void RecordRemovedFromReady() => Interlocked.Decrement(ref _readyTasks);
    public void RecordRemovedFromQueue() => Interlocked.Decrement(ref _queuedTasks);

    public void RecordStarted()
    {
        var running = Interlocked.Increment(ref _runningTasks);
        Interlocked.Decrement(ref _queuedTasks);
        UpdateMax(ref _maxObservedConcurrency, running);
    }

    public void RecordCompleted()
    {
        Interlocked.Increment(ref _completedTasks);
        Interlocked.Decrement(ref _runningTasks);
    }

    public void RecordFailed()
    {
        Interlocked.Increment(ref _failedTasks);
        Interlocked.Decrement(ref _runningTasks);
    }

    public void RecordCancelled(bool wasRunning = true)
    {
        Interlocked.Increment(ref _cancelledTasks);
        if (wasRunning)
        {
            Interlocked.Decrement(ref _runningTasks);
        }
    }

    public void RecordTimedOut(bool wasRunning = true)
    {
        Interlocked.Increment(ref _timedOutTasks);
        if (wasRunning)
        {
            Interlocked.Decrement(ref _runningTasks);
        }
    }

    public void RecordSkipped() => Interlocked.Increment(ref _skippedTasks);
    public void RecordEvent() => Interlocked.Increment(ref _eventsPublished);
    public void RecordQueueWait(TimeSpan value) => Interlocked.Add(ref _queueWaitTicks, Math.Max(0, value.Ticks));
    public void RecordExecution(TimeSpan value) => Interlocked.Add(ref _executionTicks, Math.Max(0, value.Ticks));

    public RuntimeMetricsSnapshot Snapshot() => new(
        Interlocked.Read(ref _totalTasks),
        Interlocked.Read(ref _completedTasks),
        Interlocked.Read(ref _failedTasks),
        Interlocked.Read(ref _cancelledTasks),
        Interlocked.Read(ref _timedOutTasks),
        Interlocked.Read(ref _skippedTasks),
        Interlocked.Read(ref _runningTasks),
        Interlocked.Read(ref _readyTasks),
        Interlocked.Read(ref _queuedTasks),
        Interlocked.Read(ref _maxObservedConcurrency),
        Interlocked.Read(ref _eventsPublished),
        DateTimeOffset.UtcNow,
        Interlocked.Read(ref _queueWaitTicks),
        Interlocked.Read(ref _executionTicks));

    private static void UpdateMax(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}

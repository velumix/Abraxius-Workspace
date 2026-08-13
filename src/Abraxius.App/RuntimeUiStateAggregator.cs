using System.Collections.Immutable;
using Abraxius.Core;
using Abraxius.Protocol;
using Abraxius.Telemetry;

namespace Abraxius.App;

/// <summary>
/// Converts the runtime's bounded event stream into frame-sized presentation snapshots.
/// The scheduler never waits for this consumer and the UI never reads scheduler state directly.
/// </summary>
public sealed class RuntimeUiStateAggregator : IAsyncDisposable
{
    private readonly RuntimeEventHub _hub;
    private readonly IUiDispatcher _dispatcher;
    private readonly Action<UiGraphSnapshot> _apply;
    private readonly IRuntimeMetricsSource? _metricsSource;
    private readonly UiStateStore _store = new();
    private readonly CancellationTokenSource _lifetime = new();
    private RuntimeEventHub.RuntimeEventSubscription? _subscription;
    private Task? _consumer;
    private Task? _frames;
    private long _eventsConsumed;
    private long _framesApplied;
    private long _coalescedEvents;
    private long _pending;
    private long _eventGeneration;
    private int _disposed;

    public RuntimeUiStateAggregator(
        RuntimeEventHub hub,
        IUiDispatcher dispatcher,
        Action<UiGraphSnapshot> apply,
        IRuntimeMetricsSource? metricsSource = null)
    {
        _hub = hub;
        _dispatcher = dispatcher;
        _apply = apply;
        _metricsSource = metricsSource;
    }

    public UiAggregationMetrics Metrics => new(
        Volatile.Read(ref _eventsConsumed),
        Volatile.Read(ref _framesApplied),
        Volatile.Read(ref _coalescedEvents));

    public async Task StartAsync()
    {
        if (_consumer is not null)
        {
            return;
        }

        _subscription = _hub.Subscribe(10_000, lossy: true, _lifetime.Token);
        _consumer = Task.Run(ConsumeAsync);
        _frames = Task.Run(FrameLoopAsync);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task ConsumeAsync()
    {
        if (_subscription is null)
        {
            return;
        }

        try
        {
            await foreach (var runtimeEvent in ((IAsyncEnumerable<RuntimeEvent>)_subscription)
                .WithCancellation(_lifetime.Token)
                .ConfigureAwait(false))
            {
                _store.Apply(runtimeEvent);
                Interlocked.Increment(ref _eventsConsumed);
                Interlocked.Increment(ref _eventGeneration);
                if (Interlocked.Exchange(ref _pending, 1) != 0)
                {
                    Interlocked.Increment(ref _coalescedEvents);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task FrameLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(16));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (Interlocked.Exchange(ref _pending, 0) == 0)
                {
                    continue;
                }

                // Let a short event burst settle before publishing a frame. This keeps
                // a task transition from exposing an intermediate Running snapshot to
                // observers while preserving the bounded 16 ms presentation cadence.
                var generation = Volatile.Read(ref _eventGeneration);
                await Task.Delay(TimeSpan.FromMilliseconds(2), _lifetime.Token).ConfigureAwait(false);
                if (generation != Volatile.Read(ref _eventGeneration))
                {
                    Interlocked.Exchange(ref _pending, 1);
                    continue;
                }

                var snapshot = _store.Snapshot(_metricsSource?.Snapshot());
                _dispatcher.Post(() =>
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    _apply(snapshot);
                    Interlocked.Increment(ref _framesApplied);
                });
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync().ConfigureAwait(false);
        }

        var tasks = new[] { _consumer, _frames }.Where(static task => task is not null).Cast<Task>().ToArray();
        if (tasks.Length > 0)
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        _lifetime.Dispose();
    }
}

public readonly record struct UiAggregationMetrics(
    long ConsumedEvents,
    long AppliedFrames,
    long CoalescedEvents);

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);
}

internal sealed class UiStateStore
{
    private const int EventRetention = 512;
    private const int ActivityRetention = 10_000;
    private readonly object _gate = new();
    private readonly Dictionary<TaskId, UiTaskSnapshot> _tasks = new();
    private readonly List<UiEventLine> _events = new();
    private readonly List<ActivityBlock> _blocks = new();
    private RuntimeMetricsSnapshot _derivedMetrics = UiGraphSnapshot.Empty.Metrics;
    private ExecutionId? _executionId;
    private string? _missionSummary;

    public void Apply(RuntimeEvent runtimeEvent)
    {
        lock (_gate)
        {
            if (runtimeEvent is ExecutionStartedEvent)
            {
                _tasks.Clear();
                _events.Clear();
                _blocks.Clear();
                _missionSummary = null;
            }

            _executionId = runtimeEvent.ExecutionId;
            _events.Add(new UiEventLine(runtimeEvent.Sequence, runtimeEvent.Timestamp, runtimeEvent.Kind, Describe(runtimeEvent)));
            if (_events.Count > EventRetention)
            {
                _events.RemoveRange(0, _events.Count - EventRetention);
            }

            _blocks.Add(ActivityBlockFactory.Create(runtimeEvent));
            if (_blocks.Count > ActivityRetention)
            {
                _blocks.RemoveRange(0, _blocks.Count - ActivityRetention);
            }

            switch (runtimeEvent)
            {
                case ExecutionStartedEvent:
                    break;
                case TaskCreatedEvent created:
                    _tasks[created.TaskId] = new UiTaskSnapshot(
                        created.TaskId,
                        created.Label,
                        created.Executor,
                        WorkState.Pending,
                        created.Priority,
                        0,
                        created.Dependencies,
                        null,
                        null,
                        null,
                        null,
                        Source: created.Source);
                    break;
                case TaskReadyEvent ready:
                    UpdateState(ready.TaskId, WorkState.Ready);
                    break;
                case TaskQueuedEvent queued:
                    UpdateState(queued.TaskId, WorkState.Queued);
                    break;
                case TaskStartedEvent started:
                    UpdateState(started.TaskId, WorkState.Running, started.Attempt, started.Timestamp);
                    break;
                case TaskProgressEvent progress:
                    UpdateState(progress.TaskId, progress: Math.Clamp(progress.Progress, 0, 1));
                    break;
                case TaskCompletedEvent completed:
                    UpdateState(
                        completed.TaskId,
                        WorkState.Succeeded,
                        completedAt: completed.Timestamp,
                        completedTiming: completed.Timing,
                        resultId: completed.ResultId,
                        evidence: completed.Evidence,
                        progress: 1);
                    break;
                case TaskFailedEvent failed:
                    UpdateState(
                        failed.TaskId,
                        WorkState.Failed,
                        completedAt: failed.Timestamp,
                        completedTiming: failed.Timing,
                        error: failed.Error.Message);
                    break;
                case TaskCancelledEvent cancelled:
                    UpdateState(cancelled.TaskId, WorkState.Cancelled, error: cancelled.Reason, completedAt: cancelled.Timestamp);
                    break;
                case TaskTimedOutEvent timeout:
                    UpdateState(timeout.TaskId, WorkState.TimedOut, error: $"Timeout {timeout.Timeout.TotalMilliseconds:F0}ms", completedAt: timeout.Timestamp);
                    break;
                case RuntimeWarningEvent warning when warning.TaskId is { } taskId && warning.Message.Contains("skipped", StringComparison.OrdinalIgnoreCase):
                    UpdateState(taskId, WorkState.Skipped, error: warning.Message);
                    break;
                case ExecutionCompletedEvent completed:
                    _missionSummary = completed.Summary;
                    _derivedMetrics = _derivedMetrics with { CapturedAt = completed.Timestamp };
                    break;
            }

            RecalculateMetrics();
        }
    }

    public UiGraphSnapshot Snapshot(RuntimeMetricsSnapshot? runtimeMetrics = null)
    {
        lock (_gate)
        {
            var tasks = _tasks.Values
                .Select(task => task with { Dependents = FindDependents(task.TaskId) })
                .OrderBy(static task => task.Label, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
            var metrics = runtimeMetrics is { } supplied
                ? supplied with
                {
                    TotalTasks = Math.Max(supplied.TotalTasks, tasks.Length),
                    CapturedAt = DateTimeOffset.UtcNow
                }
                : _derivedMetrics;

            var agents = tasks
                .GroupBy(static task => task.Source ?? task.Executor.ToString(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new UiAgentCard(
                    group.Key,
                    group.First().Executor,
                    group.Any(static task => task.State == WorkState.Running) ? WorkState.Running : group.First().State,
                    group.Where(static task => task.State == WorkState.Running).Select(static task => task.Label).FirstOrDefault() ?? "Idle",
                    group.Count(static task => task.State == WorkState.Running),
                    group.Count(),
                    !group.Any() ? 0 : group.Average(static task => task.Progress)))
                .OrderBy(static agent => agent.Name, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();

            return new UiGraphSnapshot(_executionId, tasks, _events.ToImmutableArray(), metrics)
            {
                Blocks = _blocks.ToImmutableArray(),
                Agents = agents,
                MissionSummary = _missionSummary
            };
        }
    }

    private ImmutableArray<TaskId> FindDependents(TaskId taskId) =>
        _tasks.Values.Where(task => task.Dependencies.Contains(taskId)).Select(static task => task.TaskId).ToImmutableArray();

    private void UpdateState(
        TaskId taskId,
        WorkState? state = null,
        int? attempt = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        TaskTiming? completedTiming = null,
        string? error = null,
        ResultId? resultId = null,
        IReadOnlyList<EvidenceId>? evidence = null,
        double? progress = null)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
        {
            return;
        }

        _tasks[taskId] = task with
        {
            State = state ?? task.State,
            Attempt = attempt ?? task.Attempt,
            StartedAt = startedAt ?? task.StartedAt,
            CompletedAt = completedAt ?? task.CompletedAt,
            Timing = completedTiming ?? task.Timing,
            Error = error ?? task.Error,
            ResultId = resultId ?? task.ResultId,
            Evidence = evidence ?? task.Evidence,
            Progress = progress ?? task.Progress
        };
    }

    private void RecalculateMetrics()
    {
        var values = _tasks.Values.ToArray();
        _derivedMetrics = _derivedMetrics with
        {
            TotalTasks = values.Length,
            CompletedTasks = values.Count(static task => task.State == WorkState.Succeeded),
            FailedTasks = values.Count(static task => task.State == WorkState.Failed),
            CancelledTasks = values.Count(static task => task.State == WorkState.Cancelled),
            TimedOutTasks = values.Count(static task => task.State == WorkState.TimedOut),
            SkippedTasks = values.Count(static task => task.State == WorkState.Skipped),
            RunningTasks = values.Count(static task => task.State == WorkState.Running),
            ReadyTasks = values.Count(static task => task.State == WorkState.Ready),
            QueuedTasks = values.Count(static task => task.State == WorkState.Queued),
            CapturedAt = DateTimeOffset.UtcNow
        };
    }

    private static string Describe(RuntimeEvent runtimeEvent) => runtimeEvent switch
    {
        ExecutionStartedEvent started => $"execution started · {started.TaskCount} tasks",
        ExecutionCompletedEvent completed => $"execution completed · {completed.Summary}",
        TaskCreatedEvent created => $"{created.Label} created",
        TaskReadyEvent ready => $"{ready.TaskId} ready",
        TaskQueuedEvent queued => $"{queued.TaskId} queued · {queued.QueueDepth}/{queued.QueueCapacity}",
        TaskStartedEvent started => $"{started.TaskId} started ({started.Executor})",
        TaskProgressEvent progress => $"{progress.TaskId} progress · {progress.Progress:P0}",
        TaskCompletedEvent completed => $"{completed.TaskId} completed",
        TaskFailedEvent failed => $"{failed.TaskId} failed: {failed.Error.Message}",
        TaskCancelledEvent cancelled => $"{cancelled.TaskId} cancelled: {cancelled.Reason}",
        TaskTimedOutEvent timeout => $"{timeout.TaskId} timed out",
        ToolRequestedEvent tool => $"tool {tool.Operation} → {tool.Target}",
        ToolCompletedEvent tool => $"tool {tool.Capability} · {tool.Duration.TotalMilliseconds:F0}ms",
        ModelRequestedEvent model => $"model {model.Model} requested",
        ModelCompletedEvent model => $"model {model.Model} · {model.Duration.TotalMilliseconds:F0}ms",
        MemoryRequestedEvent memory => $"memory query · {memory.Query}",
        MemoryCompletedEvent memory => $"memory returned {memory.HitCount} hits",
        VerificationStartedEvent => "verification started",
        VerificationCompletedEvent verification => $"verification {(verification.Succeeded ? "passed" : "failed")}",
        QueuePressureEvent pressure => $"{pressure.Executor} queue · {pressure.Depth}/{pressure.Capacity}",
        RuntimeWarningEvent warning => warning.Message,
        RuntimeErrorEvent error => $"{error.Error.Code}: {error.Error.Message}",
        _ => runtimeEvent.Kind.ToString()
    };
}

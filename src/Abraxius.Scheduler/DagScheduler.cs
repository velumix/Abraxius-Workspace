using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Abraxius.Core;
using Abraxius.Protocol;

namespace Abraxius.Scheduler;

public sealed class DagScheduler : IAsyncDisposable
{
    private static readonly IReadOnlyList<ExecutorKind> ExecutorKinds =
        Enum.GetValues<ExecutorKind>();

    private readonly SchedulerOptions _options;
    private readonly IRuntimeEventSink _events;
    private readonly SchedulerMetrics _metrics;
    private readonly CompiledGraphScheduler _compiledGraphScheduler;
    private int _running;

    public DagScheduler(
        SchedulerOptions? options = null,
        IRuntimeEventSink? events = null,
        SchedulerMetrics? metrics = null,
        IWorkExecutorRegistry? executors = null)
    {
        _options = options ?? new SchedulerOptions();
        _events = events ?? NullRuntimeEventSink.Instance;
        _metrics = metrics ?? new SchedulerMetrics();
        _compiledGraphScheduler = new CompiledGraphScheduler(_options, _events, _metrics, executors);
    }

    public SchedulerMetrics Metrics => _metrics;

    /// <summary>Executes the immutable compiled Phase 2 graph using the Phase 4 engine.</summary>
    public Task<ExecutionResult> ExecuteAsync(
        CompiledExecutionGraph graph,
        SchedulerExecutionContext context,
        IEvidenceStore evidenceStore,
        CancellationToken cancellationToken = default) =>
        _compiledGraphScheduler.ExecuteAsync(graph, context, evidenceStore, cancellationToken);

    public async Task<ExecutionResult> ExecuteAsync(
        ExecutionPlan plan,
        IEvidenceStore evidenceStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(evidenceStore);
        plan.Validate();
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            throw new InvalidOperationException("A DagScheduler instance executes one plan at a time.");
        }

        try
        {
            return await ExecuteCoreAsync(plan, evidenceStore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<ExecutionResult> ExecuteCoreAsync(
        ExecutionPlan plan,
        IEvidenceStore evidenceStore,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var correlationId = plan.CorrelationId;
        var states = plan.Nodes.ToDictionary(static node => node.TaskId, static node => new NodeState(node));
        var dependents = BuildDependents(plan);
        var results = new Dictionary<TaskId, WorkResult>();
        var errors = new Dictionary<TaskId, RuntimeError>();
        var ready = new PriorityQueue<WorkNode, ReadyKey>();
        var completion = Channel.CreateBounded<CompletedWork>(new BoundedChannelOptions(Math.Max(1, _options.CompletionChannelCapacity))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var queues = ExecutorKinds.ToDictionary(
            kind => kind,
            kind => new ExecutorQueue(kind, _options.DefaultQueueCapacity));
        var workers = new List<Task>();
        var terminalCount = 0;
        var cancellationObserved = false;

        _metrics.RecordCreated(states.Count);
        await PublishAsync(new ExecutionStartedEvent(
            startedAt,
            plan.ExecutionId,
            correlationId,
            "scheduler",
            states.Count));

        foreach (var node in plan.Nodes.OrderBy(static n => n.CreationOrder).ThenBy(static n => n.TaskId.Value))
        {
            await PublishAsync(new TaskCreatedEvent(
                startedAt,
                plan.ExecutionId,
                node.TaskId,
                correlationId,
                "scheduler",
                node.Label,
                node.Executor,
                node.Priority,
                node.Dependencies));
        }

        foreach (var node in plan.Nodes)
        {
            if (node.Dependencies.Count == 0)
            {
                MarkReady(node, states, ready, plan);
            }
        }

        foreach (var kind in plan.Nodes.Select(static node => node.Executor).Distinct())
        {
            for (var i = 0; i < _options.GetConcurrency(kind); i++)
            {
                workers.Add(WorkerLoopAsync(
                    queues[kind],
                    completion.Writer,
                    states,
                    results,
                    evidenceStore,
                    executionCts.Token));
            }
        }

        var cancellationSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancellationSignal);

        try
        {
            while (terminalCount < states.Count)
            {
                if (!cancellationObserved && cancellationSignal.Task.IsCompleted)
                {
                    cancellationObserved = true;
                    executionCts.Cancel();
                    terminalCount += CancelUnstarted(states, plan, errors, "Execution cancellation requested.");
                }

                if (!cancellationObserved)
                {
                    await DispatchReadyAsync(ready, queues, states, plan).ConfigureAwait(false);
                }

                if (terminalCount >= states.Count)
                {
                    break;
                }

                var readTask = completion.Reader.ReadAsync(CancellationToken.None).AsTask();
                if (!cancellationObserved)
                {
                    var winner = await Task.WhenAny(readTask, cancellationSignal.Task).ConfigureAwait(false);
                    if (winner == cancellationSignal.Task)
                    {
                        cancellationObserved = true;
                        executionCts.Cancel();
                        terminalCount += CancelUnstarted(states, plan, errors, "Execution cancellation requested.");
                        continue;
                    }
                }

                var finished = await readTask.ConfigureAwait(false);
                if (finished.Counted)
                {
                    continue;
                }

                terminalCount++;
                terminalCount += await ProcessCompletionAsync(
                    finished,
                    states,
                    dependents,
                    ready,
                    plan,
                    results,
                    errors).ConfigureAwait(false);
            }
        }
        finally
        {
            executionCts.Cancel();
            foreach (var queue in queues.Values)
            {
                queue.Writer.TryComplete();
            }

            try
            {
                await Task.WhenAll(workers).WaitAsync(_options.ShutdownGracePeriod, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await PublishAsync(new RuntimeWarningEvent(
                    DateTimeOffset.UtcNow,
                    plan.ExecutionId,
                    null,
                    correlationId,
                    "scheduler",
                    "Scheduler worker shutdown exceeded the configured grace period."));
            }
        }

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        var succeeded = states.Values.All(static state => state.State == WorkState.Succeeded);
        if (cancellationObserved)
        {
            await PublishAsync(new ExecutionCancelledEvent(
                DateTimeOffset.UtcNow,
                plan.ExecutionId,
                correlationId,
                "scheduler",
                "Execution cancellation requested."));
        }

        await PublishAsync(new ExecutionCompletedEvent(
            DateTimeOffset.UtcNow,
            plan.ExecutionId,
            correlationId,
            "scheduler",
            succeeded,
            elapsed,
            succeeded ? "Execution completed successfully." : "Execution completed with non-successful tasks."));

        return new ExecutionResult(
            plan.ExecutionId,
            succeeded,
            cancellationObserved,
            elapsed,
            new Dictionary<TaskId, WorkResult>(results),
            new Dictionary<TaskId, RuntimeError>(errors),
            states.ToDictionary(static pair => pair.Key, static pair => pair.Value.Snapshot()));
    }

    private async Task DispatchReadyAsync(
        PriorityQueue<WorkNode, ReadyKey> ready,
        IReadOnlyDictionary<ExecutorKind, ExecutorQueue> queues,
        IReadOnlyDictionary<TaskId, NodeState> states,
        ExecutionPlan plan)
    {
        while (ready.TryDequeue(out var node, out _))
        {
            var state = states[node.TaskId];
            if (!state.TryTransition(WorkState.Ready, WorkState.Queued))
            {
                continue;
            }

            var queue = queues[node.Executor];
            var queuedAt = DateTimeOffset.UtcNow;
            state.MarkQueued(queuedAt);
            queue.IncrementDepth();
            try
            {
                await queue.Writer.WriteAsync(new QueuedWork(node), CancellationToken.None).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                queue.DecrementDepth();
                state.TryTransition(WorkState.Queued, WorkState.Failed);
                throw;
            }

            _metrics.RecordQueued();
            var depth = queue.Depth;
            await PublishAsync(new TaskQueuedEvent(
                queuedAt,
                plan.ExecutionId,
                node.TaskId,
                plan.CorrelationId,
                "scheduler",
                node.Executor,
                depth,
                queue.Capacity));
            if (depth >= (queue.Capacity * 0.8))
            {
                await PublishAsync(new QueuePressureEvent(
                    DateTimeOffset.UtcNow,
                    plan.ExecutionId,
                    plan.CorrelationId,
                    "scheduler",
                    node.Executor,
                    depth,
                    queue.Capacity));
            }
        }
    }

    private async Task WorkerLoopAsync(
        ExecutorQueue queue,
        ChannelWriter<CompletedWork> completion,
        IReadOnlyDictionary<TaskId, NodeState> states,
        Dictionary<TaskId, WorkResult> results,
        IEvidenceStore evidenceStore,
        CancellationToken executionToken)
    {
        await foreach (var queued in queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            var node = queued.Node;
            var state = states[node.TaskId];
            queue.DecrementDepth();
            if (!state.TryTransition(WorkState.Queued, WorkState.Running))
            {
                continue;
            }

            _metrics.RecordStarted();
            state.MarkStarted(DateTimeOffset.UtcNow);
            var progress = new Progress<ProgressUpdate>(update =>
            {
                _ = PublishAsync(new TaskProgressEvent(
                    DateTimeOffset.UtcNow,
                    node.ExecutionId,
                    node.TaskId,
                    CorrelationId.New(),
                    "scheduler",
                    update.ClampedValue,
                    update.Message)).AsTask();
            });
            var queuedAt = state.QueuedAt ?? state.CreatedAt;
            var dependencyResults = new Dictionary<TaskId, WorkResult>();
            lock (results)
            {
                foreach (var dependency in node.Dependencies)
                {
                    if (results.TryGetValue(dependency, out var dependencyResult))
                    {
                        dependencyResults[dependency] = dependencyResult;
                    }
                }
            }
            WorkResult? result = null;
            RuntimeError? error = null;
            WorkState finalState = WorkState.Failed;
            TaskTiming? timing = null;

            for (var attempt = 1; attempt <= node.RetryPolicy.EffectiveMaxAttempts; attempt++)
            {
                state.SetAttempt(attempt);
                await PublishAsync(new TaskStartedEvent(
                    DateTimeOffset.UtcNow,
                    node.ExecutionId,
                    node.TaskId,
                    CorrelationId.New(),
                    "scheduler",
                    node.Executor,
                    attempt));

                using var timeoutCts = new CancellationTokenSource();
                using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(executionToken, timeoutCts.Token);
                var timeout = node.Timeout ?? _options.DefaultTimeout;
                if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                {
                    timeoutCts.CancelAfter(timeout);
                }

                if (node.Deadline is { } deadline)
                {
                    var remaining = deadline - DateTimeOffset.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        timeoutCts.Cancel();
                    }
                    else
                    {
                        timeoutCts.CancelAfter(remaining);
                    }
                }

                var operationStarted = Stopwatch.GetTimestamp();
                try
                {
                    var context = new WorkExecutionContext(
                        node.TaskId,
                        node.ExecutionId,
                        CorrelationId.New(),
                        dependencyResults,
                        evidenceStore,
                        progress,
                        operationCts.Token);
                    result = await node.Operation(context).ConfigureAwait(false);
                    if (result is null)
                    {
                        throw new InvalidOperationException("A work operation returned a null result.");
                    }

                    finalState = WorkState.Succeeded;
                    timing = CreateTiming(queuedAt, state.StartedAt ?? DateTimeOffset.UtcNow, operationStarted);
                    break;
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !executionToken.IsCancellationRequested)
                {
                    finalState = WorkState.TimedOut;
                    error = new RuntimeError(
                        ErrorCategory.Timeout,
                        "operation_timeout",
                        $"Task '{node.Label}' exceeded its timeout.");
                    timing = CreateTiming(queuedAt, state.StartedAt ?? DateTimeOffset.UtcNow, operationStarted);
                    await PublishAsync(new TaskTimedOutEvent(
                        DateTimeOffset.UtcNow,
                        node.ExecutionId,
                        node.TaskId,
                        CorrelationId.New(),
                        "scheduler",
                        timeout));
                    break;
                }
                catch (OperationCanceledException)
                {
                    finalState = WorkState.Cancelled;
                    error = new RuntimeError(
                        ErrorCategory.Cancellation,
                        "operation_cancelled",
                        $"Task '{node.Label}' was cancelled.");
                    timing = CreateTiming(queuedAt, state.StartedAt ?? DateTimeOffset.UtcNow, operationStarted);
                    break;
                }
                catch (Exception exception)
                {
                    error = exception is WorkExecutionException workExecutionException
                        ? workExecutionException.Error
                        : new RuntimeError(
                            ErrorCategory.Unknown,
                            "operation_failed",
                            $"Task '{node.Label}' failed.",
                            exception.ToString(),
                            IsTransient(exception));
                    finalState = WorkState.Failed;
                    timing = CreateTiming(queuedAt, state.StartedAt ?? DateTimeOffset.UtcNow, operationStarted);
                    if (attempt < node.RetryPolicy.EffectiveMaxAttempts &&
                        (!node.RetryPolicy.RetryTransientOnly || error.IsTransient))
                    {
                        var backoff = node.RetryPolicy.Backoff ?? TimeSpan.FromMilliseconds(50 * attempt);
                        await Task.Delay(backoff, executionToken).ConfigureAwait(false);
                        continue;
                    }

                    break;
                }
            }

            state.Complete(finalState, result, error, timing);
            if (result is not null && finalState == WorkState.Succeeded)
            {
                lock (results)
                {
                    results[node.TaskId] = result;
                }

                _metrics.RecordCompleted();
                await PublishAsync(new TaskCompletedEvent(
                    DateTimeOffset.UtcNow,
                    node.ExecutionId,
                    node.TaskId,
                    CorrelationId.New(),
                    "scheduler",
                    result.ResultId,
                    result.Evidence,
                    timing ?? new TaskTiming(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
                    result.Summary));
            }
            else if (finalState == WorkState.Cancelled)
            {
                _metrics.RecordCancelled(wasRunning: false);
                await PublishAsync(new TaskCancelledEvent(
                    DateTimeOffset.UtcNow,
                    node.ExecutionId,
                    node.TaskId,
                    CorrelationId.New(),
                    "scheduler",
                    error?.Message ?? "Task cancelled."));
            }
            else if (finalState == WorkState.TimedOut)
            {
                _metrics.RecordTimedOut();
                await PublishAsync(new TaskFailedEvent(
                    DateTimeOffset.UtcNow,
                    node.ExecutionId,
                    node.TaskId,
                    CorrelationId.New(),
                    "scheduler",
                    error ?? new RuntimeError(ErrorCategory.Timeout, "operation_timeout", "Task timed out."),
                    timing));
            }
            else
            {
                _metrics.RecordFailed();
                await PublishAsync(new TaskFailedEvent(
                    DateTimeOffset.UtcNow,
                    node.ExecutionId,
                    node.TaskId,
                    CorrelationId.New(),
                    "scheduler",
                    error ?? new RuntimeError(ErrorCategory.Unknown, "operation_failed", "Task failed."),
                    timing));
            }

            await completion.WriteAsync(new CompletedWork(node.TaskId, finalState, result, error, timing, false), CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private async Task<int> ProcessCompletionAsync(
        CompletedWork completed,
        IReadOnlyDictionary<TaskId, NodeState> states,
        IReadOnlyDictionary<TaskId, List<TaskId>> dependents,
        PriorityQueue<WorkNode, ReadyKey> ready,
        ExecutionPlan plan,
        IReadOnlyDictionary<TaskId, WorkResult> results,
        Dictionary<TaskId, RuntimeError> errors)
    {
        if (completed.Error is not null)
        {
            errors[completed.TaskId] = completed.Error;
        }

        if (!dependents.TryGetValue(completed.TaskId, out var children))
        {
            return 0;
        }

        var skippedCount = 0;
        foreach (var childId in children)
        {
            var child = states[childId];
            if (child.IsTerminal)
            {
                continue;
            }

            var dependencyStates = child.Node.Dependencies.Select(dependency => states[dependency].State).ToArray();
            if (dependencyStates.Any(static state => state is WorkState.Failed or WorkState.Cancelled or WorkState.TimedOut or WorkState.Skipped))
            {
                if (child.TryTransitionAnyNonTerminal(WorkState.Skipped))
                {
                    errors[childId] = new RuntimeError(
                        ErrorCategory.Dependency,
                        "dependency_unsatisfied",
                        $"Task '{child.Node.Label}' was skipped because a dependency did not succeed.");
                    _metrics.RecordSkipped();
                    await PublishAsync(new RuntimeWarningEvent(
                        DateTimeOffset.UtcNow,
                        plan.ExecutionId,
                        childId,
                        plan.CorrelationId,
                        "scheduler",
                        errors[childId].Message));
                    skippedCount += await ProcessCompletionAsync(
                        new CompletedWork(childId, WorkState.Skipped, null, errors[childId], null, true),
                        states,
                        dependents,
                        ready,
                        plan,
                        results,
                        errors).ConfigureAwait(false);
                    skippedCount++;
                }
            }
            else if (dependencyStates.All(static state => state == WorkState.Succeeded) &&
                     child.TryTransition(WorkState.Pending, WorkState.Ready))
            {
                _metrics.RecordReady();
                ready.Enqueue(child.Node, ReadyKey.For(child.Node));
                await PublishAsync(new TaskReadyEvent(
                    DateTimeOffset.UtcNow,
                    plan.ExecutionId,
                    childId,
                    plan.CorrelationId,
                    "scheduler"));
            }
        }

        return skippedCount;
    }

    private int CancelUnstarted(
        IReadOnlyDictionary<TaskId, NodeState> states,
        ExecutionPlan plan,
        Dictionary<TaskId, RuntimeError> errors,
        string reason)
    {
        var count = 0;
        foreach (var state in states.Values)
        {
            if (state.TryTransitionAnyNonTerminal(WorkState.Cancelled, onlyBeforeRunning: true))
            {
                count++;
                errors[state.Node.TaskId] = new RuntimeError(ErrorCategory.Cancellation, "execution_cancelled", reason);
                _metrics.RecordCancelled(wasRunning: false);
                _ = PublishAsync(new TaskCancelledEvent(
                    DateTimeOffset.UtcNow,
                    plan.ExecutionId,
                    state.Node.TaskId,
                    plan.CorrelationId,
                    "scheduler",
                    reason)).AsTask();
            }
        }

        return count;
    }

    private void MarkReady(
        WorkNode node,
        IReadOnlyDictionary<TaskId, NodeState> states,
        PriorityQueue<WorkNode, ReadyKey> ready,
        ExecutionPlan plan)
    {
        if (states[node.TaskId].TryTransition(WorkState.Pending, WorkState.Ready))
        {
            _metrics.RecordReady();
            ready.Enqueue(node, ReadyKey.For(node));
            _ = PublishAsync(new TaskReadyEvent(
                DateTimeOffset.UtcNow,
                plan.ExecutionId,
                node.TaskId,
                plan.CorrelationId,
                "scheduler")).AsTask();
        }
    }

    private static Dictionary<TaskId, List<TaskId>> BuildDependents(ExecutionPlan plan)
    {
        var dependents = plan.Nodes.ToDictionary(static node => node.TaskId, static _ => new List<TaskId>());
        foreach (var node in plan.Nodes)
        {
            foreach (var dependency in node.Dependencies)
            {
                dependents[dependency].Add(node.TaskId);
            }
        }

        foreach (var children in dependents.Values)
        {
            children.Sort(static (left, right) => left.Value.CompareTo(right.Value));
        }

        return dependents;
    }

    private async ValueTask PublishAsync(RuntimeEvent runtimeEvent)
    {
        _metrics.RecordEvent();
        await _events.PublishAsync(runtimeEvent).ConfigureAwait(false);
    }

    private static TaskTiming CreateTiming(DateTimeOffset queuedAt, DateTimeOffset startedAt, long operationStarted)
    {
        var completedAt = DateTimeOffset.UtcNow;
        var execution = Stopwatch.GetElapsedTime(operationStarted);
        return new TaskTiming(startedAt - queuedAt, execution, completedAt - queuedAt);
    }

    private static bool IsTransient(Exception exception) =>
        exception is TimeoutException or IOException or HttpRequestException;

    public ValueTask DisposeAsync() => _compiledGraphScheduler.DisposeAsync();

    private sealed class NodeState
    {
        private readonly object _gate = new();
        private WorkResult? _result;
        private RuntimeError? _error;
        private TaskTiming? _timing;

        public NodeState(WorkNode node)
        {
            Node = node;
            CreatedAt = DateTimeOffset.UtcNow;
        }

        public WorkNode Node { get; }
        public WorkState State { get; private set; } = WorkState.Pending;
        public int Attempt { get; private set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? QueuedAt { get; private set; }
        public DateTimeOffset? StartedAt { get; private set; }
        public DateTimeOffset? CompletedAt { get; private set; }
        public bool IsTerminal => State is WorkState.Succeeded or WorkState.Failed or WorkState.Cancelled or WorkState.TimedOut or WorkState.Skipped;

        public bool TryTransition(WorkState expected, WorkState next)
        {
            lock (_gate)
            {
                if (State != expected || !IsValidTransition(State, next))
                {
                    return false;
                }

                State = next;
                return true;
            }
        }

        public bool TryTransitionAnyNonTerminal(WorkState next, bool onlyBeforeRunning = false)
        {
            lock (_gate)
            {
                if (IsTerminal || (onlyBeforeRunning && State == WorkState.Running) || !IsValidTransition(State, next))
                {
                    return false;
                }

                State = next;
                CompletedAt = DateTimeOffset.UtcNow;
                return true;
            }
        }

        public void MarkQueued(DateTimeOffset timestamp) => QueuedAt = timestamp;
        public void MarkStarted(DateTimeOffset timestamp) => StartedAt = timestamp;
        public void SetAttempt(int attempt) => Attempt = attempt;

        public void Complete(WorkState state, WorkResult? result, RuntimeError? error, TaskTiming? timing)
        {
            lock (_gate)
            {
                State = state;
                _result = result;
                _error = error;
                _timing = timing;
                CompletedAt = DateTimeOffset.UtcNow;
            }
        }

        public TaskExecutionSnapshot Snapshot() => new(
            Node.TaskId,
            Node.ExecutionId,
            Node.Label,
            Node.Executor,
            Node.Priority,
            State,
            Attempt,
            Node.Dependencies,
            _result?.ResultId,
            _result?.Evidence ?? Array.Empty<EvidenceId>(),
            _error,
            CreatedAt,
            StartedAt,
            CompletedAt,
            _timing);

        private static bool IsValidTransition(WorkState current, WorkState next) => current switch
        {
            WorkState.Pending => next is WorkState.Ready or WorkState.Cancelled or WorkState.Skipped,
            WorkState.Ready => next is WorkState.Queued or WorkState.Cancelled or WorkState.Skipped,
            WorkState.Queued => next is WorkState.Running or WorkState.Cancelled or WorkState.Skipped,
            WorkState.Running => next is WorkState.Succeeded or WorkState.Failed or WorkState.Cancelled or WorkState.TimedOut,
            _ => false
        };
    }

    private sealed record QueuedWork(WorkNode Node);

    private sealed record CompletedWork(
        TaskId TaskId,
        WorkState State,
        WorkResult? Result,
        RuntimeError? Error,
        TaskTiming? Timing,
        bool Counted);

    private sealed class ExecutorQueue
    {
        private long _depth;

        public ExecutorQueue(ExecutorKind kind, int capacity)
        {
            Kind = kind;
            Capacity = Math.Max(1, capacity);
            var options = new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            };
            Channel = System.Threading.Channels.Channel.CreateBounded<QueuedWork>(options);
        }

        public ExecutorKind Kind { get; }
        public int Capacity { get; }
        public Channel<QueuedWork> Channel { get; }
        public ChannelReader<QueuedWork> Reader => Channel.Reader;
        public ChannelWriter<QueuedWork> Writer => Channel.Writer;
        public int Depth => Math.Min(Capacity, (int)Math.Max(0, Interlocked.Read(ref _depth)));
        public void IncrementDepth() => Interlocked.Increment(ref _depth);
        public void DecrementDepth() => Interlocked.Decrement(ref _depth);
    }

    private readonly record struct ReadyKey(int Priority, int CreationOrder, Guid TaskId) : IComparable<ReadyKey>
    {
        public static ReadyKey For(WorkNode node) => new(-(int)node.Priority, node.CreationOrder, node.TaskId.Value);
        public int CompareTo(ReadyKey other)
        {
            var priority = Priority.CompareTo(other.Priority);
            if (priority != 0) return priority;
            var order = CreationOrder.CompareTo(other.CreationOrder);
            return order != 0 ? order : TaskId.CompareTo(other.TaskId);
        }
    }

    private sealed class NullRuntimeEventSink : IRuntimeEventSink
    {
        public static NullRuntimeEventSink Instance { get; } = new();
        public ValueTask PublishAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}

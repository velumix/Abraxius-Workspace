using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading.Channels;
using Abraxius.Core;
using Abraxius.Platform;
using Abraxius.Protocol;

namespace Abraxius.Scheduler;

/// <summary>
/// Scheduler for the immutable Phase 2 execution graph. The coordinator owns dependency
/// propagation; executor workers only claim work and publish completions.
/// </summary>
internal sealed class CompiledGraphScheduler : IAsyncDisposable
{
    private static readonly ActivitySource ActivitySource = new("Abraxius.Scheduler");
    private readonly SchedulerOptions _options;
    private readonly IRuntimeEventSink _events;
    private readonly SchedulerMetrics _metrics;
    private readonly IWorkExecutorRegistry? _defaultExecutors;
    private readonly Dictionary<ExecutorKind, ExecutorPool> _pools;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _startGate = new();
    private Task[]? _workers;
    private int _activeExecutions;
    private int _disposed;

    public CompiledGraphScheduler(
        SchedulerOptions options,
        IRuntimeEventSink events,
        SchedulerMetrics metrics,
        IWorkExecutorRegistry? defaultExecutors)
    {
        _options = options;
        _events = events;
        _metrics = metrics;
        _defaultExecutors = defaultExecutors;
        _pools = Enum.GetValues<ExecutorKind>()
            .ToDictionary(
                kind => kind,
                kind => new ExecutorPool(
                    kind,
                    Math.Max(1, _options.GetQueueCapacity(kind)),
                    Math.Max(1, _options.GetConcurrency(kind)),
                    Math.Max(1, _options.HighPriorityBurstLimit),
                    _options.PriorityAgingInterval));
    }

    public async Task<ExecutionResult> ExecuteAsync(
        CompiledExecutionGraph graph,
        SchedulerExecutionContext context,
        IEvidenceStore evidenceStore,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evidenceStore);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        if (graph.Source.ExecutionId != context.ExecutionId)
        {
            throw new ExecutionAdmissionException(new RuntimeError(
                ErrorCategory.Validation,
                "execution_id_mismatch",
                "The scheduler context and execution graph have different execution IDs."));
        }

        if (Interlocked.Increment(ref _activeExecutions) > Math.Max(1, _options.MaxConcurrentExecutions))
        {
            Interlocked.Decrement(ref _activeExecutions);
            throw new ExecutionAdmissionException(new RuntimeError(
                ErrorCategory.Scheduler,
                "execution_admission_full",
                "The scheduler has reached its configured concurrent execution limit.",
                IsTransient: true));
        }

        EnsureWorkersStarted();
        try
        {
            var session = new GraphExecutionSession(this, graph, context with
            {
                Executors = context.Executors ?? _defaultExecutors ?? UnsupportedRegistry.Instance
            }, evidenceStore, cancellationToken);
            return await session.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeExecutions);
        }
    }

    private void EnsureWorkersStarted()
    {
        if (_workers is not null)
        {
            return;
        }

        lock (_startGate)
        {
            if (_workers is not null)
            {
                return;
            }

            var workers = new List<Task>();
            foreach (var pool in _pools.Values)
            {
                workers.AddRange(pool.StartWorkersAsync(ExecuteWorkItemAsync, _shutdown.Token));
            }

            _workers = workers.ToArray();
        }
    }

    private async Task ExecuteWorkItemAsync(WorkItem item, CancellationToken shutdownToken)
    {
        var session = item.Session;
        if (!session.TryStart(item))
        {
            return;
        }

        WorkCompletion completion;
        try
        {
            completion = await session.ExecuteWorkAsync(item, shutdownToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            completion = WorkCompletion.Failure(
                item,
                new RuntimeError(
                    ErrorCategory.Scheduler,
                    "executor_boundary_failure",
                    "The executor boundary failed before producing a structured result.",
                    exception.ToString(),
                    IsTransient: false));
        }

        await session.PostCompletionAsync(completion).ConfigureAwait(false);
    }

    private bool TryEnqueue(WorkItem item, out QueuePressureSnapshot pressure) =>
        _pools[item.Executor].TryEnqueue(item, out pressure);

    internal void RecordQueueWait(TimeSpan value) => _metrics.RecordQueueWait(value);
    internal void RecordExecution(TimeSpan value) => _metrics.RecordExecution(value);
    internal Task PublishAsync(RuntimeEvent runtimeEvent) => _events.PublishAsync(runtimeEvent).AsTask();
    internal void RecordStarted() => _metrics.RecordStarted();
    internal void RecordCompleted() => _metrics.RecordCompleted();
    internal void RecordFailed() => _metrics.RecordFailed();
    internal void RecordCancelled(bool running) => _metrics.RecordCancelled(running);
    internal void RecordTimedOut(bool running = true) => _metrics.RecordTimedOut(running);
    internal void RecordSkipped() => _metrics.RecordSkipped();
    internal void RecordReady() => _metrics.RecordReady();
    internal void RecordQueued() => _metrics.RecordQueued();
    internal void RecordRemovedFromReady() => _metrics.RecordRemovedFromReady();
    internal void RecordRemovedFromQueue() => _metrics.RecordRemovedFromQueue();
    internal void RecordEvent() => _metrics.RecordEvent();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        foreach (var pool in _pools.Values)
        {
            pool.Complete();
        }

        if (_workers is not null)
        {
            try
            {
                await Task.WhenAll(_workers).WaitAsync(_options.ShutdownGracePeriod, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The runtime remains usable for already-completed executions; a non-cooperative
                // provider is isolated behind its executor worker and cannot corrupt graph state.
            }
        }

        foreach (var pool in _pools.Values)
        {
            pool.Dispose();
        }

        _shutdown.Dispose();
    }

    private sealed class UnsupportedRegistry : IWorkExecutorRegistry
    {
        public static UnsupportedRegistry Instance { get; } = new();

        public bool TryGet(ExecutorKind kind, out IWorkExecutor executor)
        {
            executor = new UnsupportedWorkExecutor(kind);
            return true;
        }
    }

    private sealed class WorkItem
    {
        public WorkItem(
            GraphExecutionSession session,
            int nodeIndex,
            int attempt,
            ExecutorKind executor,
            WorkPriority priority,
            long queuedTimestamp,
            DateTimeOffset queuedAt,
            IReadOnlyDictionary<NodeId, WorkResult> dependencyResults)
        {
            Session = session;
            NodeIndex = nodeIndex;
            Attempt = attempt;
            Executor = executor;
            Priority = priority;
            QueuedTimestamp = queuedTimestamp;
            QueuedAt = queuedAt;
            DependencyResults = dependencyResults;
        }

        public GraphExecutionSession Session { get; }
        public int NodeIndex { get; }
        public int Attempt { get; }
        public ExecutorKind Executor { get; }
        public WorkPriority Priority { get; }
        public long QueuedTimestamp { get; }
        public DateTimeOffset QueuedAt { get; }
        public IReadOnlyDictionary<NodeId, WorkResult> DependencyResults { get; }
    }

    private sealed record QueuePressureSnapshot(ExecutorKind Executor, int Depth, int Capacity)
    {
        public bool IsElevated => Depth >= Capacity * 0.8;
    }

    private sealed class ExecutorPool : IDisposable
    {
        private readonly int _workerCount;
        private readonly PriorityWorkQueue _queue;
        private CancellationTokenSource? _linkedShutdown;

        public ExecutorPool(
            ExecutorKind kind,
            int capacity,
            int workerCount,
            int highPriorityBurstLimit,
            TimeSpan priorityAgingInterval)
        {
            Kind = kind;
            _workerCount = workerCount;
            _queue = new PriorityWorkQueue(capacity, highPriorityBurstLimit, priorityAgingInterval);
        }

        public ExecutorKind Kind { get; }

        public IEnumerable<Task> StartWorkersAsync(
            Func<WorkItem, CancellationToken, Task> execute,
            CancellationToken shutdownToken)
        {
            _linkedShutdown = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
            for (var index = 0; index < _workerCount; index++)
            {
                yield return WorkerLoopAsync(execute, _linkedShutdown.Token);
            }
        }

        public bool TryEnqueue(WorkItem item, out QueuePressureSnapshot pressure)
        {
            var accepted = _queue.TryEnqueue(item);
            pressure = new QueuePressureSnapshot(Kind, _queue.Count, _queue.Capacity);
            return accepted;
        }

        public void Complete()
        {
            _linkedShutdown?.Cancel();
            _queue.Complete();
        }

        public void Dispose()
        {
            _linkedShutdown?.Dispose();
            _queue.Dispose();
        }

        private async Task WorkerLoopAsync(
            Func<WorkItem, CancellationToken, Task> execute,
            CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    var item = await _queue.ReadAsync(cancellationToken).ConfigureAwait(false);
                    await execute(item, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (ChannelClosedException)
            {
            }
        }
    }

    private sealed class PriorityWorkQueue : IDisposable
    {
        private readonly Channel<WorkItem>[] _channels;
        private readonly SemaphoreSlim _signal;
        private readonly object _gate = new();
        private readonly int _highPriorityBurstLimit;
        private readonly TimeSpan _priorityAgingInterval;
        private int _count;
        private int _highPriorityBurst;
        private bool _completed;

        public PriorityWorkQueue(int capacity, int highPriorityBurstLimit, TimeSpan priorityAgingInterval)
        {
            Capacity = Math.Max(1, capacity);
            _highPriorityBurstLimit = Math.Max(1, highPriorityBurstLimit);
            _priorityAgingInterval = priorityAgingInterval <= TimeSpan.Zero
                ? TimeSpan.FromMilliseconds(250)
                : priorityAgingInterval;
            _channels = Enumerable.Range(0, 4)
                .Select(_ => Channel.CreateBounded<WorkItem>(new BoundedChannelOptions(Capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = false,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                }))
                .ToArray();
            _signal = new SemaphoreSlim(0, Capacity);
        }

        public int Capacity { get; }
        public int Count => Volatile.Read(ref _count);

        public bool TryEnqueue(WorkItem item)
        {
            lock (_gate)
            {
                if (_completed || _count >= Capacity)
                {
                    return false;
                }

                var priority = Math.Clamp((int)item.Priority, 0, 3);
                if (!_channels[priority].Writer.TryWrite(item))
                {
                    return false;
                }

                _count++;
            }

            _signal.Release();
            return true;
        }

        public async ValueTask<WorkItem> ReadAsync(CancellationToken cancellationToken)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                var priority = SelectPriority();
                if (priority < 0)
                {
                    throw new ChannelClosedException();
                }

                if (!_channels[priority].Reader.TryRead(out var item))
                {
                    throw new InvalidOperationException("The executor queue signal and item count diverged.");
                }

                _count--;
                if (priority >= (int)WorkPriority.Interactive)
                {
                    _highPriorityBurst++;
                }
                else
                {
                    _highPriorityBurst = 0;
                }

                return item;
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;
                foreach (var channel in _channels)
                {
                    channel.Writer.TryComplete();
                }
            }

            // Wake every possible worker; cancellation also handles workers that are between reads.
            for (var index = 0; index < Capacity; index++)
            {
                try
                {
                    _signal.Release();
                }
                catch (SemaphoreFullException)
                {
                    break;
                }
            }
        }

        public void Dispose()
        {
            Complete();
            _signal.Dispose();
        }

        private int SelectPriority()
        {
            var now = DateTimeOffset.UtcNow;
            var highest = HighestAvailablePriority();
            if (highest < 0)
            {
                return -1;
            }

            if (_highPriorityBurst >= _highPriorityBurstLimit)
            {
                for (var priority = highest - 1; priority >= 0; priority--)
                {
                    if (_channels[priority].Reader.TryPeek(out _))
                    {
                        return priority;
                    }
                }
            }

            for (var priority = highest - 1; priority >= 0; priority--)
            {
                if (_channels[priority].Reader.TryPeek(out var item) &&
                    now - item.QueuedAt >= _priorityAgingInterval)
                {
                    return priority;
                }
            }

            return highest;
        }

        private int HighestAvailablePriority()
        {
            for (var priority = _channels.Length - 1; priority >= 0; priority--)
            {
                if (_channels[priority].Reader.TryPeek(out _))
                {
                    return priority;
                }
            }

            return -1;
        }
    }

    private sealed class GraphExecutionSession : IDisposable
    {
        private readonly CompiledGraphScheduler _owner;
        private readonly CompiledExecutionGraph _graph;
        private readonly SchedulerExecutionContext _context;
        private readonly IEvidenceStore _evidenceStore;
        private readonly Channel<WorkCompletion> _completions;
        private readonly NodeRuntimeState[] _states;
        private readonly Dictionary<TaskId, WorkResult> _results = new();
        private readonly Dictionary<TaskId, RuntimeError> _errors = new();
        private readonly PriorityReadyQueue _ready;
        private readonly int[] _activeByExecutor = new int[Enum.GetValues<ExecutorKind>().Length];
        private readonly Dictionary<SpeculationGroupId, SpeculationState> _speculationGroups;
        private readonly List<Task> _retryTasks = [];
        private readonly CancellationTokenSource _executionTimeout = new();
        private readonly CancellationTokenSource _sessionCancellation;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private int _terminalCount;
        private int _activeOperations;
        private int _reservedOperations;
        private int _cancelRequested;
        private int _executionTimedOut;
        private int _completed;
        private int _disposed;

        public GraphExecutionSession(
            CompiledGraphScheduler owner,
            CompiledExecutionGraph graph,
            SchedulerExecutionContext context,
            IEvidenceStore evidenceStore,
            CancellationToken cancellationToken)
        {
            _owner = owner;
            _graph = graph;
            _context = context;
            _evidenceStore = evidenceStore;
            _completions = Channel.CreateBounded<WorkCompletion>(new BoundedChannelOptions(Math.Max(1, owner._options.CompletionChannelCapacity))
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });
            _states = graph.Nodes.Select((node, index) => new NodeRuntimeState(
                node,
                graph.InitialDependencyCounts[index],
                node.Dependencies.Select(dependency => graph.Nodes[graph.IndexById[dependency]].TaskId).ToArray())).ToArray();
            _ready = new PriorityReadyQueue(owner._options.HighPriorityBurstLimit, owner._options.PriorityAgingInterval);
            _speculationGroups = BuildSpeculationGroups(graph);
            _sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _executionTimeout.Token);
            var timeout = context.ExecutionTimeout;
            if (timeout is { } duration && duration > TimeSpan.Zero)
            {
                _executionTimeout.CancelAfter(duration);
            }

            if (context.Constraints.Deadline is { } deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    Interlocked.Exchange(ref _executionTimedOut, 1);
                    _executionTimeout.Cancel();
                }
                else
                {
                    _executionTimeout.CancelAfter(remaining);
                }
            }

            _executionTimeout.Token.Register(static state => ((GraphExecutionSession)state!).MarkExecutionTimedOut(), this);
        }

        public async Task<ExecutionResult> RunAsync()
        {
            _owner._metrics.RecordCreated(_states.Length);
            await PublishAsync(new ExecutionStartedEvent(
                _startedAt,
                _graph.Source.ExecutionId,
                _graph.Source.CorrelationId,
                "graph-scheduler",
                _states.Length)).ConfigureAwait(false);

            foreach (var node in _graph.Nodes)
            {
                await PublishAsync(new TaskCreatedEvent(
                    _startedAt,
                    _graph.Source.ExecutionId,
                    node.TaskId,
                    _graph.Source.CorrelationId,
                    "graph-scheduler",
                    node.WorkKind.ToString(),
                    ExecutionKindMapping.ToExecutorKind(node.WorkKind),
                    node.Priority,
                    node.Dependencies.Select(dependency => _graph.Nodes[_graph.IndexById[dependency]].TaskId).ToArray())).ConfigureAwait(false);
            }

            for (var index = 0; index < _states.Length; index++)
            {
                if (_states[index].RemainingDependencies == 0 && _states[index].TryReady())
                {
                    EnqueueReady(index);
                }
            }

            while (Volatile.Read(ref _terminalCount) < _states.Length || Volatile.Read(ref _activeOperations) > 0)
            {
                if (_sessionCancellation.IsCancellationRequested)
                {
                    CancelNotStarted();
                }

                DispatchReady();
                if (Volatile.Read(ref _terminalCount) >= _states.Length && Volatile.Read(ref _activeOperations) == 0)
                {
                    break;
                }

                var completion = await _completions.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                await ProcessCompletionAsync(completion).ConfigureAwait(false);
            }

            if (_retryTasks.Count > 0)
            {
                await Task.WhenAll(_retryTasks).ConfigureAwait(false);
            }

            var elapsed = DateTimeOffset.UtcNow - _startedAt;
            var snapshots = _states.ToDictionary(state => state.Node.TaskId, static state => state.Snapshot());
            var succeeded = true;
            for (var index = 0; index < _states.Length; index++)
            {
                if (_states[index].State == ExecutionState.Succeeded ||
                    _speculationGroups.Values.Any(group => group.IsResolvedLoser(index)))
                {
                    continue;
                }

                succeeded = false;
                break;
            }
            var cancelled = Volatile.Read(ref _cancelRequested) != 0 && Volatile.Read(ref _executionTimedOut) == 0;
            var result = new ExecutionResult(
                _graph.Source.ExecutionId,
                succeeded,
                cancelled,
                elapsed,
                new Dictionary<TaskId, WorkResult>(_results),
                new Dictionary<TaskId, RuntimeError>(_errors),
                snapshots);

            await PublishAsync(new ExecutionCompletedEvent(
                DateTimeOffset.UtcNow,
                _graph.Source.ExecutionId,
                _graph.Source.CorrelationId,
                "graph-scheduler",
                succeeded,
                elapsed,
                succeeded ? "Execution completed successfully." : "Execution completed with non-successful tasks.")).ConfigureAwait(false);

            Interlocked.Exchange(ref _completed, 1);
            Dispose();
            return result;
        }

        public bool TryStart(WorkItem item)
        {
            var state = _states[item.NodeIndex];
            if (state.Attempt != item.Attempt || !state.TryStart())
            {
                return false;
            }

            Interlocked.Increment(ref _activeOperations);
            _owner.RecordStarted();
            return true;
        }

        public async Task<WorkCompletion> ExecuteWorkAsync(WorkItem item, CancellationToken shutdownToken)
        {
            var node = _states[item.NodeIndex].Node;
            var state = _states[item.NodeIndex];
            var executorKind = item.Executor;
            var operationTimeout = node.Timeout ?? _context.Constraints.DefaultTimeout ?? _owner._options.DefaultTimeout;
            using var timeoutCancellation = new CancellationTokenSource();
            using var nodeCancellation = new CancellationTokenSource();
            state.AttachAttemptCancellation(nodeCancellation);
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                _sessionCancellation.Token,
                timeoutCancellation.Token,
                nodeCancellation.Token,
                shutdownToken);

            if (operationTimeout > TimeSpan.Zero && operationTimeout != Timeout.InfiniteTimeSpan)
            {
                timeoutCancellation.CancelAfter(operationTimeout);
            }

            if (node.Deadline is { } deadline)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    timeoutCancellation.Cancel();
                }
                else
                {
                    timeoutCancellation.CancelAfter(remaining);
                }
            }

            var started = Stopwatch.GetTimestamp();
            using var activity = ActivitySource.StartActivity("abraxius.task", ActivityKind.Internal);
            activity?.SetTag("abraxius.execution_id", _graph.Source.ExecutionId.ToString());
            activity?.SetTag("abraxius.task_id", node.TaskId.ToString());
            activity?.SetTag("abraxius.correlation_id", _context.CorrelationId.ToString());
            activity?.SetTag("abraxius.executor", executorKind.ToString());
            activity?.SetTag("abraxius.attempt", item.Attempt);
            await PublishAsync(new TaskStartedEvent(
                DateTimeOffset.UtcNow,
                _graph.Source.ExecutionId,
                node.TaskId,
                _context.CorrelationId,
                "graph-scheduler",
                executorKind,
                item.Attempt)).ConfigureAwait(false);

            try
            {
                var dependencyResults = item.DependencyResults;

                var progress = new Progress<ProgressUpdate>(update =>
                {
                    _ = PublishAsync(new TaskProgressEvent(
                        DateTimeOffset.UtcNow,
                        _graph.Source.ExecutionId,
                        node.TaskId,
                        _context.CorrelationId,
                        "graph-scheduler",
                        update.ClampedValue,
                        update.Message));
                });
                var workContext = new SchedulerWorkContext
                {
                    Node = node,
                    Execution = _context,
                    DependencyResults = dependencyResults,
                    EvidenceStore = _evidenceStore,
                    Progress = progress,
                    CancellationToken = operationCancellation.Token
                };
                var capability = CapabilityFor(node);
                var route = _context.CapabilityResolver.Resolve(capability, allowRemote: _context.RemoteExecutor is not null);
                WorkResult? result;
                if (route.Route.Placement == ExecutionPlacement.Remote && route.Route.HostId is { } host && _context.RemoteExecutor is { } remote)
                {
                    activity?.SetTag("abraxius.placement", "remote");
                    activity?.SetTag("abraxius.remote_host_id", host.ToString());
                    result = await remote.ExecuteAsync(new RemoteWorkRequest(host, node.ExecutionId, node.TaskId, _context.CorrelationId, node, dependencyResults), workContext).ConfigureAwait(false);
                }
                else
                {
                    if (!_context.Executors.TryGet(executorKind, out var executor))
                    {
                        throw new WorkExecutionException(new RuntimeError(ErrorCategory.Scheduler, "executor_unavailable", $"No executor is registered for {executorKind}."));
                    }
                    activity?.SetTag("abraxius.placement", "local");
                    result = await executor.ExecuteAsync(node, workContext).ConfigureAwait(false);
                }
                result = result
                    ?? throw new WorkExecutionException(new RuntimeError(
                        ErrorCategory.Scheduler,
                        "null_work_result",
                        $"Executor {executorKind} returned a null result."));
                if (timeoutCancellation.IsCancellationRequested &&
                    !_sessionCancellation.IsCancellationRequested &&
                    !nodeCancellation.IsCancellationRequested)
                {
                    return WorkCompletion.Timeout(item, new RuntimeError(
                        ErrorCategory.Timeout,
                        "operation_timeout",
                        $"Task '{node.WorkKind}' exceeded its timeout."), Stopwatch.GetElapsedTime(started));
                }

                if (_sessionCancellation.IsCancellationRequested || nodeCancellation.IsCancellationRequested)
                {
                    return WorkCompletion.Cancelled(item, new RuntimeError(
                        ErrorCategory.Cancellation,
                        "operation_cancelled",
                        "The operation was cancelled before its result could be committed."), Stopwatch.GetElapsedTime(started));
                }
                return WorkCompletion.Success(item, result, Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested &&
                                                     !_sessionCancellation.IsCancellationRequested &&
                                                     !nodeCancellation.IsCancellationRequested)
            {
                return WorkCompletion.Timeout(item, new RuntimeError(
                    ErrorCategory.Timeout,
                    "operation_timeout",
                    $"Task '{node.WorkKind}' exceeded its timeout."), Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException)
            {
                var error = Volatile.Read(ref _executionTimedOut) != 0
                    ? new RuntimeError(ErrorCategory.Timeout, "execution_timeout", "The execution deadline elapsed.")
                    : new RuntimeError(ErrorCategory.Cancellation, "operation_cancelled", "The operation was cancelled.");
                return WorkCompletion.Cancelled(item, error, Stopwatch.GetElapsedTime(started));
            }
            catch (WorkExecutionException exception)
            {
                return WorkCompletion.Failure(item, exception.Error, Stopwatch.GetElapsedTime(started));
            }
            catch (Exception exception)
            {
                return WorkCompletion.Failure(item, new RuntimeError(
                    ErrorCategory.Scheduler,
                    "executor_failed",
                    $"Executor {executorKind} failed.",
                    exception.ToString(),
                    IsTransient(exception)), Stopwatch.GetElapsedTime(started));
            }
            finally
            {
                state.ClearAttemptCancellation();
            }
        }

        private static CapabilityId CapabilityFor(ExecutionNodeDefinition node) => node.Work switch
        {
            ToolWorkDescriptor tool => tool.Capability,
            ModelWorkDescriptor => new CapabilityId("ModelInference"),
            MemoryWorkDescriptor => new CapabilityId("Memory"),
            VerificationWorkDescriptor => new CapabilityId("Verification"),
            CpuWorkDescriptor => new CapabilityId("Cpu"),
            IoWorkDescriptor => new CapabilityId("Io"),
            BackgroundWorkDescriptor => new CapabilityId("Background"),
            _ => new CapabilityId(node.WorkKind.ToString())
        };

        public async Task PostCompletionAsync(WorkCompletion completion)
        {
            await _completions.Writer.WriteAsync(completion).ConfigureAwait(false);
        }

        private void DispatchReady()
        {
            var blocked = new List<ReadyItem>();
            while (_ready.TryDequeue(DateTimeOffset.UtcNow, out var ready))
            {
                var state = _states[ready.NodeIndex];
                if (state.State != ExecutionState.Ready)
                {
                    continue;
                }

                var kind = ExecutionKindMapping.ToExecutorKind(state.Node.WorkKind);
                if (!CanReserve(kind))
                {
                    blocked.Add(ready);
                    continue;
                }

                var queuedAt = DateTimeOffset.UtcNow;
                var attempt = state.PrepareAttempt();
                if (!state.TryQueue())
                {
                    continue;
                }

                Reserve(kind);
                var item = new WorkItem(
                    this,
                    ready.NodeIndex,
                    attempt,
                    kind,
                    state.Node.Priority,
                    Stopwatch.GetTimestamp(),
                    queuedAt,
                    CreateDependencySnapshot(state.Node));
                if (!_owner.TryEnqueue(item, out var pressure))
                {
                    Release(kind);
                    state.TryReturnToReady();
                    blocked.Add(ready);
                    if (pressure.IsElevated)
                    {
                        _ = PublishAsync(new QueuePressureEvent(
                            DateTimeOffset.UtcNow,
                            _graph.Source.ExecutionId,
                            _context.CorrelationId,
                            "graph-scheduler",
                            pressure.Executor,
                            pressure.Depth,
                            pressure.Capacity));
                    }

                    continue;
                }

                _owner.RecordQueued();
                _ = PublishAsync(new TaskQueuedEvent(
                    queuedAt,
                    _graph.Source.ExecutionId,
                    state.Node.TaskId,
                    _context.CorrelationId,
                    "graph-scheduler",
                    kind,
                    pressure.Depth,
                    pressure.Capacity));
            }

            foreach (var ready in blocked)
            {
                _ready.Enqueue(ready.NodeIndex, ready.Priority, ready.CreationOrder, ready.NodeId, ready.ReadyAt);
            }
        }

        private async Task ProcessCompletionAsync(WorkCompletion completion)
        {
            var state = _states[completion.NodeIndex];
            if (completion.Status == WorkCompletionStatus.RetryReady)
            {
                if (state.Attempt == completion.Attempt && state.TryRetryReady())
                {
                    EnqueueReady(completion.NodeIndex);
                }

                return;
            }

            if (state.Attempt != completion.Attempt || state.State != ExecutionState.Running)
            {
                return;
            }

            var kind = completion.Executor;
            Release(kind);
            Interlocked.Decrement(ref _activeOperations);
            var timing = state.CreateTiming(completion.ExecutionDuration);
            _owner.RecordQueueWait(timing.QueueLatency);
            _owner.RecordExecution(timing.ExecutionLatency);

            if (completion.Status == WorkCompletionStatus.Succeeded &&
                !_sessionCancellation.IsCancellationRequested)
            {
                state.Complete(ExecutionState.Succeeded, completion.Result, null, timing);
                _results[state.Node.TaskId] = completion.Result!;
                Interlocked.Increment(ref _terminalCount);
                _owner.RecordCompleted();
                await PublishAsync(new TaskCompletedEvent(
                    DateTimeOffset.UtcNow,
                    _graph.Source.ExecutionId,
                    state.Node.TaskId,
                    _context.CorrelationId,
                    "graph-scheduler",
                    completion.Result!.ResultId,
                    completion.Result.Evidence,
                    timing,
                    completion.Result.Summary)).ConfigureAwait(false);
                ResolveSpeculationWinner(completion.NodeIndex);
                ReleaseDependents(completion.NodeIndex);
                return;
            }

            if (completion.Status == WorkCompletionStatus.Failed && CanRetry(state.Node, completion.Error))
            {
                state.TryWaiting(completion.Error!);
                var delay = CalculateRetryDelay(state.Node.RetryPolicy, state.Attempt);
                _retryTasks.Add(ScheduleRetryAsync(completion.NodeIndex, delay));
                return;
            }

            var finalState = completion.Status == WorkCompletionStatus.TimedOut || Volatile.Read(ref _executionTimedOut) != 0
                ? ExecutionState.TimedOut
                : completion.Status == WorkCompletionStatus.Cancelled || _sessionCancellation.IsCancellationRequested
                    ? ExecutionState.Cancelled
                    : ExecutionState.Failed;
            var error = completion.Error ?? new RuntimeError(
                finalState == ExecutionState.Cancelled ? ErrorCategory.Cancellation : ErrorCategory.Scheduler,
                finalState == ExecutionState.Cancelled ? "operation_cancelled" : "operation_failed",
                "The operation did not complete successfully.");
            state.Complete(finalState, null, error, timing);
            _errors[state.Node.TaskId] = error;
            Interlocked.Increment(ref _terminalCount);
            switch (finalState)
            {
                case ExecutionState.Cancelled:
                    _owner.RecordCancelled(running: true);
                    await PublishAsync(new TaskCancelledEvent(DateTimeOffset.UtcNow, _graph.Source.ExecutionId, state.Node.TaskId, _context.CorrelationId, "graph-scheduler", error.Message)).ConfigureAwait(false);
                    break;
                case ExecutionState.TimedOut:
                    _owner.RecordTimedOut();
                    await PublishAsync(new TaskTimedOutEvent(DateTimeOffset.UtcNow, _graph.Source.ExecutionId, state.Node.TaskId, _context.CorrelationId, "graph-scheduler", state.Node.Timeout ?? _owner._options.DefaultTimeout)).ConfigureAwait(false);
                    break;
                default:
                    _owner.RecordFailed();
                    await PublishAsync(new TaskFailedEvent(DateTimeOffset.UtcNow, _graph.Source.ExecutionId, state.Node.TaskId, _context.CorrelationId, "graph-scheduler", error, timing)).ConfigureAwait(false);
                    break;
            }

            SkipDependents(completion.NodeIndex, error);
        }

        private async Task ScheduleRetryAsync(int nodeIndex, TimeSpan delay)
        {
            try
            {
                await Task.Delay(delay, _sessionCancellation.Token).ConfigureAwait(false);
                await _completions.Writer.WriteAsync(WorkCompletion.RetryReady(nodeIndex, _states[nodeIndex].Attempt), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_sessionCancellation.IsCancellationRequested)
            {
            }
        }

        private void ReleaseDependents(int completedIndex)
        {
            foreach (var dependentIndex in _graph.DependentIndexes[completedIndex])
            {
                var dependent = _states[dependentIndex];
                if (Interlocked.Decrement(ref dependent.RemainingDependencies) == 0 && dependent.TryReady())
                {
                    EnqueueReady(dependentIndex);
                }
            }
        }

        private void SkipDependents(int failedIndex, RuntimeError cause)
        {
            var pending = new Queue<int>();
            foreach (var dependent in _graph.DependentIndexes[failedIndex])
            {
                pending.Enqueue(dependent);
            }

            while (pending.TryDequeue(out var index))
            {
                var state = _states[index];
                if (!state.TrySkip(out var previousState))
                {
                    continue;
                }

                if (previousState == ExecutionState.Ready)
                {
                    _owner.RecordRemovedFromReady();
                }

                var error = new RuntimeError(
                    ErrorCategory.Dependency,
                    "dependency_unsatisfied",
                    $"Task '{state.Node.WorkKind}' was skipped because a required dependency failed.",
                    cause.Code);
                _errors[state.Node.TaskId] = error;
                Interlocked.Increment(ref _terminalCount);
                _owner.RecordSkipped();
                _ = PublishAsync(new RuntimeWarningEvent(DateTimeOffset.UtcNow, _graph.Source.ExecutionId, state.Node.TaskId, _context.CorrelationId, "graph-scheduler", error.Message));
                foreach (var child in _graph.DependentIndexes[index])
                {
                    pending.Enqueue(child);
                }
            }
        }

        private void CancelNotStarted()
        {
            if (Interlocked.Exchange(ref _cancelRequested, 1) == 0)
            {
                foreach (var state in _states)
                {
                    var terminalState = Volatile.Read(ref _executionTimedOut) != 0
                        ? ExecutionState.TimedOut
                        : ExecutionState.Cancelled;
                    if (state.TryCancelNotStarted(out var previousState, terminalState))
                    {
                        if (previousState == ExecutionState.Queued)
                        {
                            Release(ExecutionKindMapping.ToExecutorKind(state.Node.WorkKind));
                            _owner.RecordRemovedFromQueue();
                        }
                        else if (previousState == ExecutionState.Ready)
                        {
                            _owner.RecordRemovedFromReady();
                        }

                        _errors[state.Node.TaskId] = new RuntimeError(
                            Volatile.Read(ref _executionTimedOut) != 0 ? ErrorCategory.Timeout : ErrorCategory.Cancellation,
                            Volatile.Read(ref _executionTimedOut) != 0 ? "execution_timeout" : "execution_cancelled",
                            "Execution cancellation was requested.");
                        Interlocked.Increment(ref _terminalCount);
                        if (terminalState == ExecutionState.TimedOut)
                        {
                            _owner.RecordTimedOut(running: false);
                        }
                        else
                        {
                            _owner.RecordCancelled(running: false);
                        }
                        if (terminalState == ExecutionState.TimedOut)
                        {
                            _ = PublishAsync(new TaskTimedOutEvent(
                                DateTimeOffset.UtcNow,
                                _graph.Source.ExecutionId,
                                state.Node.TaskId,
                                _context.CorrelationId,
                                "graph-scheduler",
                                state.Node.Timeout ?? _owner._options.DefaultTimeout));
                        }
                        else
                        {
                            _ = PublishAsync(new TaskCancelledEvent(
                                DateTimeOffset.UtcNow,
                                _graph.Source.ExecutionId,
                                state.Node.TaskId,
                                _context.CorrelationId,
                                "graph-scheduler",
                                "Execution cancellation was requested."));
                        }
                    }
                }
            }
        }

        private IReadOnlyDictionary<NodeId, WorkResult> CreateDependencySnapshot(ExecutionNodeDefinition node)
        {
            if (node.Dependencies.IsEmpty)
            {
                return ImmutableDictionary<NodeId, WorkResult>.Empty;
            }

            var results = new Dictionary<NodeId, WorkResult>(node.Dependencies.Length);
            foreach (var dependency in node.Dependencies)
            {
                var taskId = _graph.Nodes[_graph.IndexById[dependency]].TaskId;
                if (_results.TryGetValue(taskId, out var result))
                {
                    results[dependency] = result;
                }
            }

            return results;
        }

        private void MarkExecutionTimedOut() => Interlocked.Exchange(ref _executionTimedOut, 1);

        private void EnqueueReady(int nodeIndex)
        {
            var state = _states[nodeIndex];
            _ready.Enqueue(nodeIndex, state.Node.Priority, state.Node.CreationOrder, state.Node.Id, DateTimeOffset.UtcNow);
            _owner.RecordReady();
            _ = PublishAsync(new TaskReadyEvent(DateTimeOffset.UtcNow, _graph.Source.ExecutionId, state.Node.TaskId, _context.CorrelationId, "graph-scheduler"));
        }

        private bool CanReserve(ExecutorKind kind)
        {
            var budget = _context.EffectiveBudget;
            var maxTotal = budget.MaximumConcurrency;
            if (_context.Constraints.MaxParallelism > 0)
            {
                maxTotal = Math.Min(maxTotal, _context.Constraints.MaxParallelism);
            }

            if (_reservedOperations >= maxTotal)
            {
                return false;
            }

            return _activeByExecutor[(int)kind] < GetSessionExecutorLimit(kind);
        }

        private int GetSessionExecutorLimit(ExecutorKind kind)
        {
            var configured = _owner._options.GetConcurrency(kind);
            var budget = _context.EffectiveBudget;
            var deviceLimit = kind switch
            {
                ExecutorKind.Model => budget.ModelConcurrency,
                ExecutorKind.Tool => budget.ToolConcurrency,
                ExecutorKind.Memory => budget.CpuWorkerLimit,
                ExecutorKind.Cpu => budget.CpuWorkerLimit,
                ExecutorKind.Io => budget.CpuWorkerLimit,
                ExecutorKind.Verification => budget.CpuWorkerLimit,
                ExecutorKind.Background => budget.AllowBackgroundActivity ? Math.Max(1, budget.CpuWorkerLimit / 2) : 1,
                _ => configured
            };
            return Math.Max(1, Math.Min(configured, deviceLimit <= 0 ? 1 : deviceLimit));
        }

        private void Reserve(ExecutorKind kind)
        {
            _reservedOperations++;
            _activeByExecutor[(int)kind]++;
        }

        private void Release(ExecutorKind kind)
        {
            _reservedOperations = Math.Max(0, _reservedOperations - 1);
            _activeByExecutor[(int)kind] = Math.Max(0, _activeByExecutor[(int)kind] - 1);
        }

        private bool CanRetry(ExecutionNodeDefinition node, RuntimeError? error) =>
            error is not null &&
            (!node.RetryPolicy.RetryTransientOnly || error.IsTransient) &&
            node.RetryPolicy.EffectiveMaxAttempts > _states[_graph.IndexById[node.Id]].Attempt &&
            error.Category is not (ErrorCategory.Cancellation or ErrorCategory.Policy or ErrorCategory.Validation);

        private static bool IsTransient(Exception exception) =>
            exception is TimeoutException or IOException or HttpRequestException;

        private static TimeSpan CalculateRetryDelay(RetryPolicy policy, int attempt)
        {
            var initial = policy.InitialDelay;
            if (initial <= TimeSpan.Zero || policy.BackoffStrategy == RetryBackoffStrategy.None)
            {
                return TimeSpan.Zero;
            }

            return policy.BackoffStrategy switch
            {
                RetryBackoffStrategy.Exponential => TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(1).Ticks, initial.Ticks * Math.Max(1, 1L << Math.Min(20, attempt - 1)))),
                _ => initial
            };
        }

        private void ResolveSpeculationWinner(int winnerIndex)
        {
            var groupId = _states[winnerIndex].Node.SpeculationGroupId;
            if (groupId is not { } id || !_speculationGroups.TryGetValue(id, out var group) || !group.TryWin(winnerIndex))
            {
                return;
            }

            foreach (var candidate in group.Candidates)
            {
                if (candidate == winnerIndex)
                {
                    continue;
                }

                var state = _states[candidate];
                state.RequestAttemptCancellation();
                if (state.TryCancelNotStarted(out var previousState))
                {
                    if (previousState == ExecutionState.Queued)
                    {
                        Release(ExecutionKindMapping.ToExecutorKind(state.Node.WorkKind));
                        _owner.RecordRemovedFromQueue();
                    }
                    else if (previousState == ExecutionState.Ready)
                    {
                        _owner.RecordRemovedFromReady();
                    }

                    _errors[state.Node.TaskId] = new RuntimeError(ErrorCategory.Cancellation, "speculation_lost", "Speculative branch lost and was cancelled.");
                    Interlocked.Increment(ref _terminalCount);
                    _owner.RecordCancelled(running: false);
                }
            }
        }

        private static Dictionary<SpeculationGroupId, SpeculationState> BuildSpeculationGroups(CompiledExecutionGraph graph)
        {
            var groups = new Dictionary<SpeculationGroupId, SpeculationState>();
            foreach (var definition in graph.Source.SpeculationGroups)
            {
                groups[definition.Id] = new SpeculationState(
                    definition.Candidates.Select(candidate => graph.IndexById[candidate]).ToImmutableArray(),
                    definition.WinnerPolicy);
            }

            return groups;
        }

        private async Task PublishAsync(RuntimeEvent runtimeEvent)
        {
            _owner.RecordEvent();
            await _owner.PublishAsync(runtimeEvent).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _sessionCancellation.Dispose();
            _executionTimeout.Dispose();
        }
    }

    private sealed class NodeRuntimeState
    {
        private readonly object _cancellationGate = new();
        private CancellationTokenSource? _attemptCancellation;
        private int _state;
        private WorkResult? _result;
        private RuntimeError? _error;
        private TaskTiming? _timing;

        public NodeRuntimeState(ExecutionNodeDefinition node, int remainingDependencies, IReadOnlyList<TaskId> dependencyTaskIds)
        {
            Node = node;
            RemainingDependencies = remainingDependencies;
            DependencyTaskIds = dependencyTaskIds;
            CreatedAt = DateTimeOffset.UtcNow;
        }

        public ExecutionNodeDefinition Node { get; }
        public IReadOnlyList<TaskId> DependencyTaskIds { get; }
        public int RemainingDependencies;
        public int Attempt { get; private set; }
        public DateTimeOffset CreatedAt { get; }
        public DateTimeOffset? QueuedAt { get; private set; }
        public DateTimeOffset? StartedAt { get; private set; }
        public DateTimeOffset? CompletedAt { get; private set; }
        public ExecutionState State => (ExecutionState)Volatile.Read(ref _state);

        public bool TryReady() => Interlocked.CompareExchange(ref _state, (int)ExecutionState.Ready, (int)ExecutionState.Pending) == (int)ExecutionState.Pending;

        public bool TryRetryReady() => Interlocked.CompareExchange(ref _state, (int)ExecutionState.Ready, (int)ExecutionState.Waiting) == (int)ExecutionState.Waiting;

        public int PrepareAttempt() => ++Attempt;

        public bool TryQueue()
        {
            if (Interlocked.CompareExchange(ref _state, (int)ExecutionState.Queued, (int)ExecutionState.Ready) != (int)ExecutionState.Ready)
            {
                return false;
            }

            QueuedAt = DateTimeOffset.UtcNow;
            return true;
        }

        public bool TryReturnToReady() => Interlocked.CompareExchange(ref _state, (int)ExecutionState.Ready, (int)ExecutionState.Queued) == (int)ExecutionState.Queued;

        public bool TryStart()
        {
            if (Interlocked.CompareExchange(ref _state, (int)ExecutionState.Running, (int)ExecutionState.Queued) != (int)ExecutionState.Queued)
            {
                return false;
            }

            StartedAt = DateTimeOffset.UtcNow;
            return true;
        }

        public bool TryWaiting(RuntimeError error)
        {
            _error = error;
            return Interlocked.CompareExchange(ref _state, (int)ExecutionState.Waiting, (int)ExecutionState.Running) == (int)ExecutionState.Running;
        }

        public bool TrySkip(out ExecutionState previousState) => TryTerminal(ExecutionState.Skipped, out previousState);

        public bool TryCancelNotStarted(out ExecutionState previousState, ExecutionState terminalState = ExecutionState.Cancelled)
        {
            while (true)
            {
                var current = State;
                previousState = current;
                if (current is ExecutionState.Running or ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.TimedOut or ExecutionState.Skipped)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, (int)terminalState, (int)current) == (int)current)
                {
                    CompletedAt = DateTimeOffset.UtcNow;
                    return true;
                }
            }
        }

        public void Complete(ExecutionState state, WorkResult? result, RuntimeError? error, TaskTiming timing)
        {
            _result = result;
            _error = error;
            _timing = timing;
            CompletedAt = DateTimeOffset.UtcNow;
            Volatile.Write(ref _state, (int)state);
        }

        public TaskTiming CreateTiming(TimeSpan executionDuration)
        {
            var queued = QueuedAt ?? CreatedAt;
            var started = StartedAt ?? queued;
            return new TaskTiming(started - queued, executionDuration, DateTimeOffset.UtcNow - queued);
        }

        public void AttachAttemptCancellation(CancellationTokenSource cancellation)
        {
            lock (_cancellationGate)
            {
                _attemptCancellation = cancellation;
            }
        }

        public void ClearAttemptCancellation()
        {
            lock (_cancellationGate)
            {
                _attemptCancellation = null;
            }
        }

        public void RequestAttemptCancellation()
        {
            lock (_cancellationGate)
            {
                _attemptCancellation?.Cancel();
            }
        }

        public TaskExecutionSnapshot Snapshot() => new(
            Node.TaskId,
            Node.ExecutionId,
            Node.WorkKind.ToString(),
            ExecutionKindMapping.ToExecutorKind(Node.WorkKind),
            Node.Priority,
            (WorkState)State,
            Attempt,
            DependencyTaskIds,
            _result?.ResultId,
            _result?.Evidence ?? Array.Empty<EvidenceId>(),
            _error,
            CreatedAt,
            StartedAt,
            CompletedAt,
            _timing);

        private bool TryTerminal(ExecutionState state, out ExecutionState previousState)
        {
            while (true)
            {
                var current = State;
                previousState = current;
                if (current is ExecutionState.Running or ExecutionState.Succeeded or ExecutionState.Failed or ExecutionState.Cancelled or ExecutionState.TimedOut or ExecutionState.Skipped)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, (int)state, (int)current) == (int)current)
                {
                    CompletedAt = DateTimeOffset.UtcNow;
                    return true;
                }
            }
        }
    }

    private sealed record WorkCompletion(
        int NodeIndex,
        int Attempt,
        ExecutorKind Executor,
        WorkCompletionStatus Status,
        WorkResult? Result,
        RuntimeError? Error,
        TimeSpan ExecutionDuration)
    {
        public static WorkCompletion Success(WorkItem item, WorkResult result, TimeSpan duration) =>
            new(item.NodeIndex, item.Attempt, item.Executor, WorkCompletionStatus.Succeeded, result, null, duration);

        public static WorkCompletion Failure(WorkItem item, RuntimeError error, TimeSpan duration = default) =>
            new(item.NodeIndex, item.Attempt, item.Executor, WorkCompletionStatus.Failed, null, error, duration);

        public static WorkCompletion Timeout(WorkItem item, RuntimeError error, TimeSpan duration) =>
            new(item.NodeIndex, item.Attempt, item.Executor, WorkCompletionStatus.TimedOut, null, error, duration);

        public static WorkCompletion Cancelled(WorkItem item, RuntimeError error, TimeSpan duration) =>
            new(item.NodeIndex, item.Attempt, item.Executor, WorkCompletionStatus.Cancelled, null, error, duration);

        public static WorkCompletion RetryReady(int nodeIndex, int attempt) =>
            new(nodeIndex, attempt, ExecutorKind.Background, WorkCompletionStatus.RetryReady, null, null, TimeSpan.Zero);
    }

    private enum WorkCompletionStatus
    {
        Succeeded,
        Failed,
        Cancelled,
        TimedOut,
        RetryReady
    }

    private sealed class SpeculationState(ImmutableArray<int> candidates, SpeculationPolicy policy)
    {
        private int _winner = -1;

        public ImmutableArray<int> Candidates { get; } = candidates;
        public SpeculationPolicy Policy { get; } = policy;

        public bool TryWin(int candidate) => Interlocked.CompareExchange(ref _winner, candidate, -1) == -1;

        public bool IsResolvedLoser(int candidate) =>
            Volatile.Read(ref _winner) >= 0 && Candidates.Contains(candidate) && Volatile.Read(ref _winner) != candidate;
    }

    private sealed class PriorityReadyQueue
    {
        private readonly Queue<ReadyItem>[] _queues = Enumerable.Range(0, 4).Select(_ => new Queue<ReadyItem>()).ToArray();
        private readonly int _highPriorityBurstLimit;
        private readonly TimeSpan _agingInterval;
        private int _highPriorityBurst;

        public PriorityReadyQueue(int highPriorityBurstLimit, TimeSpan agingInterval)
        {
            _highPriorityBurstLimit = Math.Max(1, highPriorityBurstLimit);
            _agingInterval = agingInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(250) : agingInterval;
        }

        public void Enqueue(int nodeIndex, WorkPriority priority, int creationOrder, NodeId nodeId, DateTimeOffset readyAt) =>
            _queues[Math.Clamp((int)priority, 0, 3)].Enqueue(new ReadyItem(nodeIndex, priority, creationOrder, nodeId, readyAt));

        public bool TryDequeue(DateTimeOffset now, out ReadyItem item)
        {
            var highest = Highest();
            if (highest < 0)
            {
                item = default;
                return false;
            }

            var selected = highest;
            if (_highPriorityBurst >= _highPriorityBurstLimit)
            {
                for (var priority = highest - 1; priority >= 0; priority--)
                {
                    if (_queues[priority].Count > 0)
                    {
                        selected = priority;
                        break;
                    }
                }
            }
            else
            {
                for (var priority = highest - 1; priority >= 0; priority--)
                {
                    if (_queues[priority].Count > 0 && now - _queues[priority].Peek().ReadyAt >= _agingInterval)
                    {
                        selected = priority;
                        break;
                    }
                }
            }

            item = _queues[selected].Dequeue();
            _highPriorityBurst = selected >= (int)WorkPriority.Interactive ? _highPriorityBurst + 1 : 0;
            return true;
        }

        private int Highest()
        {
            for (var priority = _queues.Length - 1; priority >= 0; priority--)
            {
                if (_queues[priority].Count > 0)
                {
                    return priority;
                }
            }

            return -1;
        }
    }

    private readonly record struct ReadyItem(
        int NodeIndex,
        WorkPriority Priority,
        int CreationOrder,
        NodeId NodeId,
        DateTimeOffset ReadyAt);
}

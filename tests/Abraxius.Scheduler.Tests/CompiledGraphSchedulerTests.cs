using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Abraxius.Core;
using Abraxius.Memory;
using Abraxius.Platform;
using Abraxius.Protocol;
using Abraxius.Scheduler;
using Xunit;

namespace Abraxius.Scheduler.Tests;

public sealed class CompiledGraphSchedulerTests
{
    [Fact]
    public async Task IndependentNodesActuallyOverlap()
    {
        var active = 0;
        var maximum = 0;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = Registry(ExecutorKind.Cpu, async (_, context) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMax(ref maximum, current);
            if (current == 4)
            {
                started.TrySetResult(true);
            }

            try
            {
                await release.Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                return WorkResult.Empty();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var graph = Graph(Enumerable.Range(0, 4).Select(_ => new CpuWorkDescriptor("blocked")));

        await using var scheduler = new DagScheduler(Options(4), executors: registry);
        var execution = scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(maximum >= 2);
        release.TrySetResult(true);

        var result = await execution;
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task FanInWaitsForEveryDependency()
    {
        var gates = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var dependentStarted = 0;
        var branchNodes = gates.Select((_, index) => Node(new CpuWorkDescriptor($"branch-{index}"))).ToArray();
        var dependent = Node(new CpuWorkDescriptor("dependent"), branchNodes.Select(static node => node.Id).ToImmutableArray());
        var graph = Compile([.. branchNodes, dependent]);
        var registry = Registry(ExecutorKind.Cpu, async (node, context) =>
        {
            if (node.Id == dependent.Id)
            {
                Interlocked.Increment(ref dependentStarted);
                return WorkResult.Empty();
            }

            var index = Array.IndexOf(branchNodes, node);
            await gates[index].Task.WaitAsync(context.CancellationToken).ConfigureAwait(false);
            return WorkResult.Empty();
        });

        await using var scheduler = new DagScheduler(Options(3), executors: registry);
        var execution = scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref dependentStarted));
        gates[0].TrySetResult(true);
        gates[1].TrySetResult(true);
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref dependentStarted));
        gates[2].TrySetResult(true);
        var result = await execution;

        Assert.True(result.Succeeded);
        Assert.Equal(1, dependentStarted);
    }

    [Fact]
    public async Task ExecutorLimitIsEnforcedByCompiledScheduler()
    {
        var active = 0;
        var maximum = 0;
        var registry = Registry(ExecutorKind.Cpu, async (_, context) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMax(ref maximum, current);
            try
            {
                await Task.Delay(20, context.CancellationToken).ConfigureAwait(false);
                return WorkResult.Empty();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var graph = Graph(Enumerable.Range(0, 20).Select(_ => new CpuWorkDescriptor("short")));

        await using var scheduler = new DagScheduler(Options(4), executors: registry);
        var result = await scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        Assert.InRange(maximum, 1, 4);
    }

    [Fact]
    public async Task InteractivePriorityIsAdmittedBeforeBackgroundWork()
    {
        _executionId = ExecutionId.New();
        _creationOrder = 0;
        var background = Enumerable.Range(0, 3)
            .Select(_ => Node(new CpuWorkDescriptor("background"), priority: WorkPriority.Background))
            .ToArray();
        var interactive = Node(new CpuWorkDescriptor("interactive"), priority: WorkPriority.Interactive);
        var graph = Compile([.. background, interactive]);
        var observed = new ConcurrentQueue<WorkPriority>();
        var registry = Registry(ExecutorKind.Cpu, (node, _) =>
        {
            observed.Enqueue(node.Priority);
            return ValueTask.FromResult(WorkResult.Empty());
        });

        await using var scheduler = new DagScheduler(Options(1), executors: registry);
        var result = await scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        Assert.Equal(WorkPriority.Interactive, observed.First());
    }

    [Fact]
    public async Task CancellationDoesNotStartDescendants()
    {
        var started = 0;
        var root = Node(new CpuWorkDescriptor("root"));
        var child = Node(new CpuWorkDescriptor("child"), [root.Id]);
        var graph = Compile([root, child]);
        var registry = Registry(ExecutorKind.Cpu, async (node, context) =>
        {
            Interlocked.Increment(ref started);
            await Task.Delay(TimeSpan.FromSeconds(5), context.CancellationToken).ConfigureAwait(false);
            return WorkResult.Empty();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await using var scheduler = new DagScheduler(Options(2), executors: registry);
        var result = await scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore(), cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, started);
        Assert.All(result.Tasks.Values, task => Assert.Equal(WorkState.Cancelled, task.State));
    }

    [Fact]
    public async Task NonCooperativeTimeoutCannotCommitSuccess()
    {
        var node = Node(new CpuWorkDescriptor("slow"), timeout: TimeSpan.FromMilliseconds(30));
        var graph = Compile([node]);
        var registry = Registry(ExecutorKind.Cpu, async (_, _) =>
        {
            await Task.Delay(120).ConfigureAwait(false);
            return WorkResult.Empty("late");
        });

        await using var scheduler = new DagScheduler(Options(1), executors: registry);
        var result = await scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());

        Assert.False(result.Succeeded);
        Assert.Equal(WorkState.TimedOut, result.Tasks[node.TaskId].State);
        Assert.Equal(ErrorCategory.Timeout, result.Errors[node.TaskId].Category);
    }

    [Fact]
    public async Task ExecutionTimeoutTerminatesRunningWorkAsTimedOut()
    {
        var node = Node(new CpuWorkDescriptor("execution-timeout"));
        var graph = Compile([node]);
        var registry = Registry(ExecutorKind.Cpu, async (_, context) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), context.CancellationToken).ConfigureAwait(false);
            return WorkResult.Empty();
        });
        var context = Context(graph, registry) with { ExecutionTimeout = TimeSpan.FromMilliseconds(30) };

        await using var scheduler = new DagScheduler(Options(1), executors: registry);
        var result = await scheduler.ExecuteAsync(graph, context, new InMemoryEvidenceStore());

        Assert.False(result.Succeeded);
        Assert.False(result.Cancelled);
        Assert.Equal(WorkState.TimedOut, result.Tasks[node.TaskId].State);
        Assert.Equal(ErrorCategory.Timeout, result.Errors[node.TaskId].Category);
    }

    [Fact]
    public async Task FailureSkipsAllReachableRequiredDescendants()
    {
        var failed = Node(new CpuWorkDescriptor("fail"));
        var child = Node(new CpuWorkDescriptor("child"), [failed.Id]);
        var grandchild = Node(new CpuWorkDescriptor("grandchild"), [child.Id]);
        var graph = Compile([failed, child, grandchild]);
        var registry = Registry(ExecutorKind.Cpu, (_, _) =>
            throw new WorkExecutionException(new RuntimeError(ErrorCategory.Tool, "failed", "expected failure")));

        await using var scheduler = new DagScheduler(Options(1), executors: registry);
        var result = await scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());

        Assert.Equal(WorkState.Failed, result.Tasks[failed.TaskId].State);
        Assert.Equal(WorkState.Skipped, result.Tasks[child.TaskId].State);
        Assert.Equal(WorkState.Skipped, result.Tasks[grandchild.TaskId].State);
    }

    [Fact]
    public async Task TransientRetryKeepsTaskIdentityAndIncrementsAttempt()
    {
        var attempts = 0;
        var node = Node(new CpuWorkDescriptor("retry"), retryPolicy: new RetryPolicy(2, TimeSpan.Zero, true));
        var graph = Compile([node]);
        var registry = Registry(ExecutorKind.Cpu, (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                throw new WorkExecutionException(new RuntimeError(ErrorCategory.Transport, "temporary", "retry", IsTransient: true));
            }

            return ValueTask.FromResult(WorkResult.Empty("success"));
        });

        await using var scheduler = new DagScheduler(Options(1), executors: registry);
        var result = await scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        Assert.Equal(2, attempts);
        Assert.Equal(2, result.Tasks[node.TaskId].Attempt);
        Assert.Equal(node.TaskId, result.Tasks[node.TaskId].TaskId);
    }

    [Fact]
    public async Task ExecutionSpecificParallelismIsEnforced()
    {
        var active = 0;
        var maximum = 0;
        var registry = Registry(ExecutorKind.Cpu, async (_, context) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMax(ref maximum, current);
            try
            {
                await Task.Delay(20, context.CancellationToken).ConfigureAwait(false);
                return WorkResult.Empty();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var graph = Graph(Enumerable.Range(0, 8).Select(_ => new CpuWorkDescriptor("bounded")));
        var context = Context(graph, registry) with { Constraints = new ExecutionConstraints(maxParallelism: 2) };

        await using var scheduler = new DagScheduler(Options(8), executors: registry);
        var result = await scheduler.ExecuteAsync(graph, context, new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        Assert.InRange(maximum, 1, 2);
    }

    [Fact]
    public async Task LateCompletionAfterCancellationCannotRestoreSuccess()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var node = Node(new CpuWorkDescriptor("late"));
        var graph = Compile([node]);
        var registry = Registry(ExecutorKind.Cpu, async (_, _) =>
        {
            await release.Task.ConfigureAwait(false);
            return WorkResult.Empty("stale");
        });
        using var cancellation = new CancellationTokenSource();
        await using var scheduler = new DagScheduler(Options(1), executors: registry);
        var execution = scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore(), cancellation.Token);
        await Task.Delay(40);
        cancellation.Cancel();
        release.TrySetResult(true);

        var result = await execution;
        Assert.False(result.Succeeded);
        Assert.Equal(WorkState.Cancelled, result.Tasks[node.TaskId].State);
        Assert.False(result.Results.ContainsKey(node.TaskId));
    }

    [Fact]
    public async Task BoundedCompiledQueueCompletesAllWorkWithoutUnboundedAdmission()
    {
        var sink = new CapturingSink();
        var registry = Registry(ExecutorKind.Tool, async (_, context) =>
        {
            await Task.Delay(12, context.CancellationToken).ConfigureAwait(false);
            return WorkResult.Empty();
        });
        var graph = Graph(Enumerable.Range(0, 12).Select(_ => new ToolWorkDescriptor(new CapabilityId("test"), "read", new ActionTarget("memory"))));
        await using var scheduler = new DagScheduler(new SchedulerOptions
        {
            DefaultQueueCapacity = 1,
            CompletionChannelCapacity = 8,
            ConcurrencyLimits = Enum.GetValues<ExecutorKind>().ToDictionary(kind => kind, _ => 1)
        }, sink, executors: registry);

        var result = await scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        Assert.NotEmpty(sink.Events);
    }

    [Fact]
    public async Task FirstSuccessfulSpeculationCancelsLoserWithoutFailingExecution()
    {
        var group = SpeculationGroupId.New();
        var slow = Node(new CpuWorkDescriptor("slow"), speculationGroupId: group);
        var fast = Node(new CpuWorkDescriptor("fast"), speculationGroupId: group);
        var graph = new ExecutionGraph(
            slow.ExecutionId,
            CorrelationId.New(),
            [slow, fast],
            speculationGroups: [new SpeculationGroupDefinition(group, [slow.Id, fast.Id])]).Compile();
        var registry = Registry(ExecutorKind.Cpu, async (node, context) =>
        {
            if (node.Id == slow.Id)
            {
                await Task.Delay(250, context.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(20, context.CancellationToken).ConfigureAwait(false);
            }

            return WorkResult.Empty(node.Work.ToString());
        });

        await using var scheduler = new DagScheduler(Options(2), executors: registry);
        var result = await scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        Assert.Equal(WorkState.Succeeded, result.Tasks[fast.TaskId].State);
        Assert.Equal(WorkState.Cancelled, result.Tasks[slow.TaskId].State);
    }

    [Fact]
    public async Task ThousandNodeCompiledGraphCompletesWithIndexedPropagation()
    {
        var registry = Registry(ExecutorKind.Cpu, (_, _) => ValueTask.FromResult(WorkResult.Empty()));
        var graph = Graph(Enumerable.Range(0, 1_000).Select(_ => new CpuWorkDescriptor("noop")));
        await using var scheduler = new DagScheduler(Options(12), executors: registry);

        var result = await scheduler.ExecuteAsync(graph, Context(graph, registry), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        Assert.Equal(1_000, result.Tasks.Count);
    }

    private static ExecutionNodeDefinition Node(
        WorkDescriptor work,
        ImmutableArray<NodeId> dependencies = default,
        TimeSpan? timeout = null,
        RetryPolicy? retryPolicy = null,
        WorkPriority priority = WorkPriority.Normal,
        SpeculationGroupId? speculationGroupId = null)
    {
        return new ExecutionNodeDefinition(
            NodeId.New(),
            TaskId.New(),
            _executionId,
            work,
            dependencies,
            priority: priority,
            timeout: timeout,
            retryPolicy: retryPolicy,
            speculationGroupId: speculationGroupId,
            creationOrder: _creationOrder++);
    }

    private static ExecutionId _executionId = ExecutionId.New();
    private static int _creationOrder;

    private static CompiledExecutionGraph Graph(IEnumerable<WorkDescriptor> work)
    {
        _executionId = ExecutionId.New();
        _creationOrder = 0;
        return Compile(work.Select(item => Node(item)).ToArray());
    }

    private static CompiledExecutionGraph Compile(ExecutionNodeDefinition[] nodes)
    {
        var execution = nodes[0].ExecutionId;
        return new ExecutionGraph(execution, CorrelationId.New(), nodes).Compile();
    }

    private static SchedulerExecutionContext Context(CompiledExecutionGraph graph, IWorkExecutorRegistry registry) => new()
    {
        ExecutionId = graph.Source.ExecutionId,
        CorrelationId = graph.Source.CorrelationId,
        Environment = TestEnvironment(),
        Executors = registry
    };

    private static WorkExecutorRegistry Registry(
        ExecutorKind kind,
        Func<ExecutionNodeDefinition, SchedulerWorkContext, ValueTask<WorkResult>> execute) =>
        new WorkExecutorRegistry([new DelegateWorkExecutor(kind, execute)]);

    private static PlatformEnvironment TestEnvironment()
    {
        var platform = new PlatformDescriptor(
            PlatformFamily.Linux,
            "test",
            "test",
            Architecture.X64,
            ".NET",
            true);
        var device = new DeviceProfile(DeviceClass.Workstation, 16, 64UL * 1024 * 1024 * 1024, false, false, HardwareAccelerationClass.DedicatedGpu);
        var capabilities = new PlatformCapabilitySet(
        [
            new PlatformCapability(PlatformCapabilities.LocalLattice, CapabilityAvailability.Available),
            new PlatformCapability(PlatformCapabilities.LocalModelInference, CapabilityAvailability.Available),
            new PlatformCapability(new CapabilityId("demo"), CapabilityAvailability.Available)
        ]);
        return PlatformEnvironmentFactory.Create(platform, device, capabilities, RuntimeExecutionMode.LocalFull);
    }

    private static SchedulerOptions Options(int concurrency) => new()
    {
        DefaultTimeout = TimeSpan.FromSeconds(2),
        DefaultQueueCapacity = 8,
        CompletionChannelCapacity = 32,
        ConcurrencyLimits = Enum.GetValues<ExecutorKind>().ToDictionary(kind => kind, _ => concurrency)
    };

    private static void UpdateMax(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }

    private sealed class CapturingSink : IRuntimeEventSink
    {
        public ConcurrentQueue<RuntimeEvent> Events { get; } = new();

        public ValueTask PublishAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken = default)
        {
            Events.Enqueue(runtimeEvent);
            return ValueTask.CompletedTask;
        }
    }
}

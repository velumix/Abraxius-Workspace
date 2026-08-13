using System.Collections.Concurrent;
using System.Diagnostics;
using Abraxius.Core;
using Abraxius.Memory;
using Abraxius.Protocol;
using Abraxius.Scheduler;
using Xunit;

namespace Abraxius.Scheduler.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task IndependentFanoutOverlapsWork()
    {
        var active = 0;
        var maxActive = 0;
        var options = TestOptions(4);
        await using var scheduler = new DagScheduler(options);
        var execution = ExecutionId.New();
        var nodes = Enumerable.Range(0, 4).Select(index =>
            Node(execution, $"branch-{index}", ExecutorKind.Cpu, async context =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMax(ref maxActive, current);
                await Task.Delay(150, context.CancellationToken);
                Interlocked.Decrement(ref active);
                return WorkResult.Empty();
            }, creationOrder: index)).ToArray();

        var stopwatch = Stopwatch.StartNew();
        var result = await scheduler.ExecuteAsync(new ExecutionPlan(execution, CorrelationId.New(), nodes), new InMemoryEvidenceStore());
        stopwatch.Stop();

        Assert.True(maxActive > 1);
        Assert.True(result.Succeeded);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 100, 800);
    }

    [Fact]
    public async Task DependencyOrderingDoesNotStartChildEarly()
    {
        var aComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bComplete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var execution = ExecutionId.New();
        var a = Node(execution, "A", ExecutorKind.Cpu, async context =>
        {
            await Task.Delay(20, context.CancellationToken);
            aComplete.SetResult(true);
            return WorkResult.Empty();
        }, creationOrder: 0);
        var b = Node(execution, "B", ExecutorKind.Cpu, async context =>
        {
            await Task.Delay(60, context.CancellationToken);
            bComplete.SetResult(true);
            return WorkResult.Empty();
        }, creationOrder: 1);
        var c = Node(execution, "C", ExecutorKind.Cpu, context =>
        {
            cStarted.SetResult(aComplete.Task.IsCompletedSuccessfully && bComplete.Task.IsCompletedSuccessfully);
            return ValueTask.FromResult(WorkResult.Empty());
        }, [a.TaskId, b.TaskId], creationOrder: 2);

        await using var scheduler = new DagScheduler(TestOptions(2));
        var result = await scheduler.ExecuteAsync(new ExecutionPlan(execution, CorrelationId.New(), [a, b, c]), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        Assert.True(await cStarted.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void CycleDetectionRejectsGraph()
    {
        var execution = ExecutionId.New();
        var aId = TaskId.New();
        var bId = TaskId.New();
        var cId = TaskId.New();
        var nodes = new[]
        {
            Node(execution, "A", ExecutorKind.Cpu, _ => ValueTask.FromResult(WorkResult.Empty()), [cId], creationOrder: 0, taskId: aId),
            Node(execution, "B", ExecutorKind.Cpu, _ => ValueTask.FromResult(WorkResult.Empty()), [aId], creationOrder: 1, taskId: bId),
            Node(execution, "C", ExecutorKind.Cpu, _ => ValueTask.FromResult(WorkResult.Empty()), [bId], creationOrder: 2, taskId: cId)
        };

        var plan = new ExecutionPlan(execution, CorrelationId.New(), nodes);
        Assert.Throws<PlanValidationException>(plan.Validate);
    }

    [Fact]
    public async Task ConcurrencyLimitIsNeverExceeded()
    {
        var active = 0;
        var maxActive = 0;
        var execution = ExecutionId.New();
        var nodes = Enumerable.Range(0, 12).Select(index => Node(execution, $"work-{index}", ExecutorKind.Tool, async context =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMax(ref maxActive, current);
            await Task.Delay(40, context.CancellationToken);
            Interlocked.Decrement(ref active);
            return WorkResult.Empty();
        }, creationOrder: index)).ToArray();

        var options = TestOptions(4);
        await using var scheduler = new DagScheduler(options);
        var result = await scheduler.ExecuteAsync(new ExecutionPlan(execution, CorrelationId.New(), nodes), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        Assert.InRange(maxActive, 1, 4);
    }

    [Fact]
    public async Task CancellationPreventsWaitingDescendantsFromStarting()
    {
        var execution = ExecutionId.New();
        var childStarted = 0;
        var root = Node(execution, "root", ExecutorKind.Cpu, async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), context.CancellationToken);
            return WorkResult.Empty();
        }, creationOrder: 0);
        var child = Node(execution, "child", ExecutorKind.Cpu, context =>
        {
            Interlocked.Increment(ref childStarted);
            return ValueTask.FromResult(WorkResult.Empty());
        }, [root.TaskId], creationOrder: 1);
        await using var scheduler = new DagScheduler(TestOptions(2));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await scheduler.ExecuteAsync(new ExecutionPlan(execution, CorrelationId.New(), [root, child]), new InMemoryEvidenceStore(), cancellation.Token);

        Assert.True(result.Cancelled);
        Assert.Equal(0, childStarted);
        Assert.Equal(WorkState.Cancelled, result.Tasks[root.TaskId].State);
        Assert.Equal(WorkState.Cancelled, result.Tasks[child.TaskId].State);
    }

    [Fact]
    public async Task TimeoutProducesStructuredTimeoutState()
    {
        var execution = ExecutionId.New();
        var node = Node(execution, "hang", ExecutorKind.Io, async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), context.CancellationToken);
            return WorkResult.Empty();
        }, timeout: TimeSpan.FromMilliseconds(50));
        await using var scheduler = new DagScheduler(TestOptions(1));

        var result = await scheduler.ExecuteAsync(new ExecutionPlan(execution, CorrelationId.New(), [node]), new InMemoryEvidenceStore());

        Assert.False(result.Succeeded);
        Assert.Equal(WorkState.TimedOut, result.Tasks[node.TaskId].State);
        Assert.Equal(ErrorCategory.Timeout, result.Errors[node.TaskId].Category);
    }

    [Fact]
    public async Task FailurePropagationSkipsInvalidDescendant()
    {
        var execution = ExecutionId.New();
        var failed = Node(execution, "failed", ExecutorKind.Cpu, _ => throw new InvalidOperationException("expected"), creationOrder: 0);
        var descendantStarted = 0;
        var descendant = Node(execution, "descendant", ExecutorKind.Cpu, _ =>
        {
            Interlocked.Increment(ref descendantStarted);
            return ValueTask.FromResult(WorkResult.Empty());
        }, [failed.TaskId], creationOrder: 1);
        await using var scheduler = new DagScheduler(TestOptions(1));

        var result = await scheduler.ExecuteAsync(new ExecutionPlan(execution, CorrelationId.New(), [failed, descendant]), new InMemoryEvidenceStore());

        Assert.False(result.Succeeded);
        Assert.Equal(WorkState.Failed, result.Tasks[failed.TaskId].State);
        Assert.Equal(WorkState.Skipped, result.Tasks[descendant.TaskId].State);
        Assert.Equal(0, descendantStarted);
        Assert.Equal(ErrorCategory.Dependency, result.Errors[descendant.TaskId].Category);
    }

    [Fact]
    public async Task BoundedQueueReportsPressureWithoutUnboundedDepth()
    {
        var sink = new CapturingSink();
        var execution = ExecutionId.New();
        var nodes = Enumerable.Range(0, 8).Select(index => Node(execution, $"queued-{index}", ExecutorKind.Tool, async context =>
        {
            await Task.Delay(30, context.CancellationToken);
            return WorkResult.Empty();
        }, creationOrder: index)).ToArray();
        await using var scheduler = new DagScheduler(new SchedulerOptions
        {
            DefaultQueueCapacity = 2,
            ConcurrencyLimits = new Dictionary<ExecutorKind, int>
            {
                [ExecutorKind.Tool] = 1
            }
        }, sink);

        var result = await scheduler.ExecuteAsync(new ExecutionPlan(execution, CorrelationId.New(), nodes), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        var pressure = sink.Events.OfType<QueuePressureEvent>().ToArray();
        Assert.NotEmpty(pressure);
        Assert.All(pressure, item => Assert.InRange(item.Depth, 1, item.Capacity));
    }

    [Fact]
    public async Task ResultsRemainCorrelatedToTheirTask()
    {
        var execution = ExecutionId.New();
        var nodes = Enumerable.Range(0, 4).Select(index => Node(execution, $"result-{index}", ExecutorKind.Cpu, _ =>
            ValueTask.FromResult(new WorkResult(ResultId.New(), $"result-{index}", [])), creationOrder: index)).ToArray();
        await using var scheduler = new DagScheduler(TestOptions(4));

        var result = await scheduler.ExecuteAsync(new ExecutionPlan(execution, CorrelationId.New(), nodes), new InMemoryEvidenceStore());

        Assert.True(result.Succeeded);
        foreach (var node in nodes)
        {
            Assert.Equal($"result-{node.Label.Split('-')[1]}", result.Results[node.TaskId].Summary);
            Assert.Equal(node.TaskId, result.Tasks[node.TaskId].TaskId);
        }
    }

    private static WorkNode Node(
        ExecutionId execution,
        string label,
        ExecutorKind executor,
        WorkOperation operation,
        IReadOnlyList<TaskId>? dependencies = null,
        TimeSpan? timeout = null,
        int creationOrder = 0,
        TaskId? taskId = null) => new(
        taskId ?? TaskId.New(),
        execution,
        label,
        executor,
        operation,
        dependencies,
        timeout: timeout,
        creationOrder: creationOrder);

    private static SchedulerOptions TestOptions(int concurrency) => new()
    {
        DefaultTimeout = TimeSpan.FromSeconds(2),
        DefaultQueueCapacity = 32,
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

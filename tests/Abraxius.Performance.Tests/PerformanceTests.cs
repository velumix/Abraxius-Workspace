using System.Diagnostics;
using Abraxius.Core;
using Abraxius.Memory;
using Abraxius.Protocol;
using Abraxius.Scheduler;
using Xunit;

namespace Abraxius.Performance.Tests;

public sealed class PerformanceTests
{
    [Fact]
    public async Task ParallelCriticalPathIsShorterThanSerialSum()
    {
        var execution = ExecutionId.New();
        var nodes = Enumerable.Range(0, 4).Select(index => new WorkNode(
            TaskId.New(),
            execution,
            $"branch-{index}",
            ExecutorKind.Io,
            async context =>
            {
                await Task.Delay(80, context.CancellationToken);
                return WorkResult.Empty();
            },
            creationOrder: index)).ToArray();
        await using var scheduler = new DagScheduler(new SchedulerOptions
        {
            DefaultQueueCapacity = 8,
            DefaultTimeout = TimeSpan.FromSeconds(2),
            ConcurrencyLimits = new Dictionary<ExecutorKind, int> { [ExecutorKind.Io] = 4 }
        });

        var stopwatch = Stopwatch.StartNew();
        var result = await scheduler.ExecuteAsync(new ExecutionPlan(execution, CorrelationId.New(), nodes), new InMemoryEvidenceStore());
        stopwatch.Stop();

        Assert.True(result.Succeeded);
        Assert.InRange(stopwatch.ElapsedMilliseconds, 60, 500);
    }
}

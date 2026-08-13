using Abraxius.Protocol;
using Abraxius.Core;

namespace Abraxius.Scheduler;

public sealed class SchedulerOptions
{
    public int DefaultQueueCapacity { get; init; } = 128;
    public int CompletionChannelCapacity { get; init; } = 512;
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxConcurrentExecutions { get; init; } = 8;
    public int HighPriorityBurstLimit { get; init; } = 16;
    public TimeSpan PriorityAgingInterval { get; init; } = TimeSpan.FromMilliseconds(250);
    public IReadOnlyDictionary<ExecutorKind, int> ConcurrencyLimits { get; init; } = CreateDefaults();

    public int GetConcurrency(ExecutorKind kind) =>
        ConcurrencyLimits.TryGetValue(kind, out var value) ? Math.Max(1, value) : 1;

    public int GetQueueCapacity(ExecutorKind kind) => Math.Max(1, DefaultQueueCapacity);

    public static IReadOnlyDictionary<ExecutorKind, int> CreateDefaults()
    {
        var cpu = Math.Max(1, Environment.ProcessorCount);
        return new Dictionary<ExecutorKind, int>
        {
            [ExecutorKind.Model] = 4,
            [ExecutorKind.Tool] = 32,
            [ExecutorKind.Memory] = 16,
            [ExecutorKind.Cpu] = cpu,
            [ExecutorKind.Io] = Math.Max(4, cpu * 2),
            [ExecutorKind.Verification] = cpu,
            [ExecutorKind.Background] = Math.Max(2, cpu / 2)
        };
    }
}

public sealed record TaskExecutionSnapshot(
    TaskId TaskId,
    ExecutionId ExecutionId,
    string Label,
    ExecutorKind Executor,
    WorkPriority Priority,
    WorkState State,
    int Attempt,
    IReadOnlyList<TaskId> Dependencies,
    ResultId? ResultId,
    IReadOnlyList<EvidenceId> Evidence,
    RuntimeError? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    TaskTiming? Timing);

public sealed record ExecutionResult(
    ExecutionId ExecutionId,
    bool Succeeded,
    bool Cancelled,
    TimeSpan Elapsed,
    IReadOnlyDictionary<TaskId, WorkResult> Results,
    IReadOnlyDictionary<TaskId, RuntimeError> Errors,
    IReadOnlyDictionary<TaskId, TaskExecutionSnapshot> Tasks);

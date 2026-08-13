namespace Abraxius.Protocol;

public abstract record RuntimeEvent(
    RuntimeEventKind Kind,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId? RelatedTaskId,
    CorrelationId CorrelationId,
    string Source)
{
    public long Sequence { get; init; }
}

public sealed record ExecutionStartedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    CorrelationId CorrelationId,
    string Source,
    int TaskCount)
    : RuntimeEvent(RuntimeEventKind.ExecutionStarted, Timestamp, ExecutionId, null, CorrelationId, Source);

public sealed record ExecutionCompletedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    CorrelationId CorrelationId,
    string Source,
    bool Succeeded,
    TimeSpan Elapsed,
    string Summary)
    : RuntimeEvent(RuntimeEventKind.ExecutionCompleted, Timestamp, ExecutionId, null, CorrelationId, Source)
{
    public TimeSpan DurationValue => Elapsed;
}

public sealed record ExecutionFailedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    CorrelationId CorrelationId,
    string Source,
    RuntimeError Error)
    : RuntimeEvent(RuntimeEventKind.ExecutionFailed, Timestamp, ExecutionId, null, CorrelationId, Source);

public sealed record ExecutionCancelledEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    CorrelationId CorrelationId,
    string Source,
    string Reason)
    : RuntimeEvent(RuntimeEventKind.ExecutionCancelled, Timestamp, ExecutionId, null, CorrelationId, Source);

public sealed record TaskCreatedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Label,
    ExecutorKind Executor,
    WorkPriority Priority,
    IReadOnlyList<TaskId> Dependencies)
    : RuntimeEvent(RuntimeEventKind.TaskCreated, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record TaskReadyEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source)
    : RuntimeEvent(RuntimeEventKind.TaskReady, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record TaskQueuedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    ExecutorKind Executor,
    int QueueDepth,
    int QueueCapacity)
    : RuntimeEvent(RuntimeEventKind.TaskQueued, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record TaskStartedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    ExecutorKind Executor,
    int Attempt)
    : RuntimeEvent(RuntimeEventKind.TaskStarted, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record TaskProgressEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    double Progress,
    string? Message)
    : RuntimeEvent(RuntimeEventKind.TaskProgress, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record TaskCompletedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    ResultId ResultId,
    IReadOnlyList<EvidenceId> Evidence,
    TaskTiming Timing,
    string? Summary)
    : RuntimeEvent(RuntimeEventKind.TaskCompleted, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record TaskFailedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    RuntimeError Error,
    TaskTiming? Timing)
    : RuntimeEvent(RuntimeEventKind.TaskFailed, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record TaskCancelledEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Reason)
    : RuntimeEvent(RuntimeEventKind.TaskCancelled, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record TaskTimedOutEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    TimeSpan Timeout)
    : RuntimeEvent(RuntimeEventKind.TaskTimedOut, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record QueuePressureEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    CorrelationId CorrelationId,
    string Source,
    ExecutorKind Executor,
    int Depth,
    int Capacity)
    : RuntimeEvent(RuntimeEventKind.QueuePressure, Timestamp, ExecutionId, null, CorrelationId, Source);

public sealed record RuntimeWarningEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId? TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Message)
    : RuntimeEvent(RuntimeEventKind.RuntimeWarning, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record RuntimeErrorEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId? TaskId,
    CorrelationId CorrelationId,
    string Source,
    RuntimeError Error)
    : RuntimeEvent(RuntimeEventKind.RuntimeError, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record ToolRequestedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Capability,
    string Operation,
    string Target)
    : RuntimeEvent(RuntimeEventKind.ToolRequested, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record ToolStartedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Capability,
    string Operation)
    : RuntimeEvent(RuntimeEventKind.ToolStarted, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record ToolCompletedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Capability,
    bool Succeeded,
    TimeSpan Duration,
    IReadOnlyList<EvidenceId> Evidence)
    : RuntimeEvent(RuntimeEventKind.ToolCompleted, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record ModelRequestedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Model,
    WorkPriority Priority)
    : RuntimeEvent(RuntimeEventKind.ModelRequested, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record ModelCompletedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Model,
    TimeSpan Duration,
    int? InputTokens,
    int? OutputTokens)
    : RuntimeEvent(RuntimeEventKind.ModelCompleted, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record ModelStreamingEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Model,
    string Text)
    : RuntimeEvent(RuntimeEventKind.ModelStreaming, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record IntelligenceRouteSelectedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Tier,
    string Gateway,
    string Route,
    string Reason,
    decimal? EstimatedCost)
    : RuntimeEvent(RuntimeEventKind.IntelligenceRouteSelected, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record MemoryRequestedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    string Query)
    : RuntimeEvent(RuntimeEventKind.MemoryRequested, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record MemoryCompletedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    TimeSpan Duration,
    int HitCount)
    : RuntimeEvent(RuntimeEventKind.MemoryCompleted, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record VerificationStartedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source)
    : RuntimeEvent(RuntimeEventKind.VerificationStarted, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

public sealed record VerificationCompletedEvent(
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    string Source,
    bool Succeeded,
    TimeSpan Duration,
    string Summary)
    : RuntimeEvent(RuntimeEventKind.VerificationCompleted, Timestamp, ExecutionId, TaskId, CorrelationId, Source);

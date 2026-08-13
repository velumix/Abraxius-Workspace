namespace Abraxius.Protocol;

public enum WorkState
{
    Pending,
    Ready,
    Queued,
    Running,
    Waiting,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    Skipped
}

public enum WorkPriority
{
    Background = 0,
    Low = Background,
    Normal = 1,
    Interactive = 2,
    High = Interactive,
    Critical = 3
}

public enum ExecutionState
{
    Pending,
    Ready,
    Queued,
    Running,
    Waiting,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut,
    Skipped
}

public enum ExecutorKind
{
    Model,
    Tool,
    Memory,
    Cpu,
    Io,
    Verification,
    Background
}

public enum RuntimeEventKind
{
    ExecutionStarted,
    ExecutionCompleted,
    ExecutionFailed,
    ExecutionCancelled,
    TaskCreated,
    TaskReady,
    TaskQueued,
    TaskStarted,
    TaskProgress,
    TaskCompleted,
    TaskFailed,
    TaskCancelled,
    TaskTimedOut,
    ToolRequested,
    ToolStarted,
    ToolCompleted,
    ModelRequested,
    ModelStreaming,
    ModelCompleted,
    IntelligenceRouteSelected,
    MemoryRequested,
    MemoryCompleted,
    VerificationStarted,
    VerificationCompleted,
    QueuePressure,
    RuntimeWarning,
    RuntimeError
}

public enum ErrorCategory
{
    Validation,
    Scheduler,
    Dependency,
    Model,
    Tool,
    Policy,
    Memory,
    Transport,
    Timeout,
    Cancellation,
    Persistence,
    Configuration,
    Verification,
    Unknown
}

public enum WorkIntensity
{
    None,
    Low,
    Medium,
    High
}

public readonly record struct ResourceHints(
    WorkIntensity Cpu = WorkIntensity.None,
    WorkIntensity Io = WorkIntensity.None,
    WorkIntensity Memory = WorkIntensity.None,
    WorkIntensity Gpu = WorkIntensity.None,
    long? EstimatedMemoryBytes = null,
    TimeSpan? ExpectedDuration = null,
    string? ResourceGroup = null);

public sealed record ResourceHint(
    int? CpuWeight = null,
    int? MemoryMegabytes = null,
    bool RequiresGpu = false,
    string? ResourceGroup = null);

public sealed record RuntimeError(
    ErrorCategory Category,
    string Code,
    string Message,
    string? Detail = null,
    bool IsTransient = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

public enum RetryBackoffStrategy
{
    None,
    Fixed,
    Exponential
}

public sealed record RetryPolicy(
    int MaxAttempts = 1,
    TimeSpan? Backoff = null,
    bool RetryTransientOnly = true)
{
    public int EffectiveMaxAttempts => Math.Max(1, MaxAttempts);
    public TimeSpan InitialDelay => Backoff ?? TimeSpan.Zero;
    public RetryBackoffStrategy BackoffStrategy { get; init; } = RetryBackoffStrategy.Fixed;
}

public sealed record TaskTiming(
    TimeSpan QueueLatency,
    TimeSpan ExecutionLatency,
    TimeSpan TotalLatency);

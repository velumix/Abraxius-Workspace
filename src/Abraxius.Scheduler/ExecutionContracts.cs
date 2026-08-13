using System.Collections.Frozen;
using System.Collections.Immutable;
using Abraxius.Core;
using Abraxius.Platform;
using Abraxius.Protocol;

namespace Abraxius.Scheduler;

/// <summary>Maps a compiled graph node to the executor pool that should admit it.</summary>
public static class ExecutionKindMapping
{
    public static ExecutorKind ToExecutorKind(WorkKind kind) => kind switch
    {
        WorkKind.ModelInference => ExecutorKind.Model,
        WorkKind.ToolExecution => ExecutorKind.Tool,
        WorkKind.MemoryLookup => ExecutorKind.Memory,
        WorkKind.Cpu => ExecutorKind.Cpu,
        WorkKind.Io => ExecutorKind.Io,
        WorkKind.Verification => ExecutorKind.Verification,
        WorkKind.Synthesis => ExecutorKind.Model,
        WorkKind.Background => ExecutorKind.Background,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown work kind.")
    };
}

/// <summary>Context supplied to a typed executor for one graph node attempt.</summary>
public sealed record SchedulerWorkContext
{
    public required ExecutionNodeDefinition Node { get; init; }
    public required SchedulerExecutionContext Execution { get; init; }
    public required IReadOnlyDictionary<NodeId, WorkResult> DependencyResults { get; init; }
    public required IEvidenceStore EvidenceStore { get; init; }
    public required IProgress<ProgressUpdate> Progress { get; init; }
    public required CancellationToken CancellationToken { get; init; }
}

/// <summary>Platform and resource inputs for one graph execution.</summary>
public sealed record SchedulerExecutionContext
{
    public required ExecutionId ExecutionId { get; init; }
    public required CorrelationId CorrelationId { get; init; }
    public required IPlatformEnvironment Environment { get; init; }
    public required IWorkExecutorRegistry Executors { get; init; }
    public ExecutionConstraints Constraints { get; init; } = new();
    public TimeSpan? ExecutionTimeout { get; init; }
    public ExecutionBudget? Budget { get; init; }
    public IExecutionBudgetProvider? BudgetProvider { get; init; }
    public IRemoteWorkExecutor? RemoteExecutor { get; init; }
    /// <summary>Trusted runtime-originated identity correlation. Providers must treat values as metadata, never as grants.</summary>
    public IReadOnlyDictionary<string, string>? SecurityContext { get; init; }
    public ImmutableArray<RemoteCapabilityAdvertisement> RemoteHosts { get; init; } = ImmutableArray<RemoteCapabilityAdvertisement>.Empty;

    public CapabilityResolver CapabilityResolver => new(Environment, RemoteHosts);

    public ExecutionBudget EffectiveBudget => BudgetProvider?.Current ?? Budget ?? Environment.Budget;
}

/// <summary>Supplies a scheduler budget that can be changed without rebuilding a runtime.</summary>
public interface IExecutionBudgetProvider
{
    ExecutionBudget Current { get; }
}

public sealed class MutableExecutionBudgetProvider(ExecutionBudget initial) : IExecutionBudgetProvider
{
    private ExecutionBudget _current = initial ?? throw new ArgumentNullException(nameof(initial));

    public ExecutionBudget Current => Volatile.Read(ref _current);

    public void Update(ExecutionBudget budget) => Interlocked.Exchange(ref _current, budget ?? throw new ArgumentNullException(nameof(budget)));
}

/// <summary>Typed executor boundary. Implementations belong to runtime/provider adapter projects.</summary>
public interface IWorkExecutor
{
    ExecutorKind Kind { get; }

    ValueTask<WorkResult> ExecuteAsync(
        ExecutionNodeDefinition node,
        SchedulerWorkContext context);
}

public interface IWorkExecutorRegistry
{
    bool TryGet(ExecutorKind kind, out IWorkExecutor executor);
}

public sealed class WorkExecutorRegistry : IWorkExecutorRegistry
{
    private readonly FrozenDictionary<ExecutorKind, IWorkExecutor> _executors;

    public WorkExecutorRegistry(IEnumerable<IWorkExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _executors = executors
            .GroupBy(static executor => executor.Kind)
            .ToFrozenDictionary(static group => group.Key, static group => group.Last());
    }

    public bool TryGet(ExecutorKind kind, out IWorkExecutor executor) => _executors.TryGetValue(kind, out executor!);
}

/// <summary>Simple executor adapter useful for deterministic tests and small operators.</summary>
public sealed class DelegateWorkExecutor : IWorkExecutor
{
    private readonly Func<ExecutionNodeDefinition, SchedulerWorkContext, ValueTask<WorkResult>> _execute;

    public DelegateWorkExecutor(
        ExecutorKind kind,
        Func<ExecutionNodeDefinition, SchedulerWorkContext, ValueTask<WorkResult>> execute)
    {
        Kind = kind;
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }

    public ExecutorKind Kind { get; }

    public ValueTask<WorkResult> ExecuteAsync(ExecutionNodeDefinition node, SchedulerWorkContext context) =>
        _execute(node, context);
}

public sealed class UnsupportedWorkExecutor(ExecutorKind kind) : IWorkExecutor
{
    public ExecutorKind Kind { get; } = kind;

    public ValueTask<WorkResult> ExecuteAsync(ExecutionNodeDefinition node, SchedulerWorkContext context) =>
        throw new WorkExecutionException(new RuntimeError(
            ErrorCategory.Scheduler,
            "executor_unavailable",
            $"No executor is registered for {node.WorkKind} ({Kind})."));
}

public sealed class WorkExecutionException(RuntimeError error) : Exception(error?.Message)
{
    public RuntimeError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}

public sealed record RemoteWorkRequest(
    RemoteHostId HostId,
    ExecutionId ExecutionId,
    TaskId TaskId,
    CorrelationId CorrelationId,
    ExecutionNodeDefinition Node,
    IReadOnlyDictionary<NodeId, WorkResult> DependencyResults);

public interface IRemoteWorkExecutor
{
    ValueTask<WorkResult> ExecuteAsync(
        RemoteWorkRequest request,
        SchedulerWorkContext context);
}

public sealed class ExecutionAdmissionException(RuntimeError error) : Exception(error?.Message)
{
    public RuntimeError Error { get; } = error ?? throw new ArgumentNullException(nameof(error));
}

using Abraxius.Protocol;
using Abraxius.Artifacts;

namespace Abraxius.App;

/// <summary>Presentation-level activity categories. The ledger remains the source of full fidelity.</summary>
public enum ActivityBlockKind
{
    Intent,
    Plan,
    Agent,
    Tool,
    Evidence,
    Terminal,
    Verification,
    Artifact,
    Result,
    Warning,
    Error
}

/// <summary>A typed, compact activity record suitable for virtualized presentation.</summary>
public abstract record ActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId? TaskId,
    ActivityBlockKind Kind,
    string Title,
    string Detail,
    string Status,
    bool RequiresAttention = false)
{
    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture);
    public string TaskText => TaskId is { } taskId ? taskId.ToString()[..8] : string.Empty;
    public string Glyph => Kind switch
    {
        ActivityBlockKind.Intent => "◆",
        ActivityBlockKind.Plan => "◇",
        ActivityBlockKind.Agent => "●",
        ActivityBlockKind.Tool => "↗",
        ActivityBlockKind.Evidence => "▤",
        ActivityBlockKind.Terminal => "▣",
        ActivityBlockKind.Verification => "✓",
        ActivityBlockKind.Artifact => "▱",
        ActivityBlockKind.Result => "→",
        ActivityBlockKind.Warning => "!",
        ActivityBlockKind.Error => "×",
        _ => "·"
    };
}

public sealed record IntentActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    string Objective)
    : ActivityBlock(Sequence, Timestamp, ExecutionId, null, ActivityBlockKind.Intent, "Intent", Objective, "RECEIVED");

public sealed record PlanActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    string Detail)
    : ActivityBlock(Sequence, Timestamp, ExecutionId, null, ActivityBlockKind.Plan, "Execution graph", Detail, "PLANNED");

public sealed record AgentActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId RelatedTaskId,
    string Agent,
    string Detail,
    string Status)
    : ActivityBlock(Sequence, Timestamp, ExecutionId, RelatedTaskId, ActivityBlockKind.Agent, Agent, Detail, Status);

public sealed record ToolActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId RelatedTaskId,
    string Operation,
    string Target,
    string Status,
    bool Failed = false)
    : ActivityBlock(Sequence, Timestamp, ExecutionId, RelatedTaskId, ActivityBlockKind.Tool, Operation, Target, Status, Failed);

public sealed record EvidenceActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId? RelatedTaskId,
    string Detail,
    string Status = "AVAILABLE")
    : ActivityBlock(Sequence, Timestamp, ExecutionId, RelatedTaskId, ActivityBlockKind.Evidence, "Evidence", Detail, Status);

public sealed record TerminalActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId? RelatedTaskId,
    string Detail,
    string Status = "OUTPUT")
    : ActivityBlock(Sequence, Timestamp, ExecutionId, RelatedTaskId, ActivityBlockKind.Terminal, "Terminal", Detail, Status);

public sealed record VerificationActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId RelatedTaskId,
    string Detail,
    bool Passed)
    : ActivityBlock(Sequence, Timestamp, ExecutionId, RelatedTaskId, ActivityBlockKind.Verification, "Verification", Detail, Passed ? "PASSED" : "FAILED", !Passed);

public sealed record ResultActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId? RelatedTaskId,
    string Detail,
    string Status = "COMPLETED")
    : ActivityBlock(Sequence, Timestamp, ExecutionId, RelatedTaskId, ActivityBlockKind.Result, "Task result", Detail, Status);

public abstract record ArtifactActivityBlock(long Sequence, DateTimeOffset Timestamp, ExecutionId ExecutionId, TaskId? RelatedTaskId,
    ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Title, string Detail, string Status, bool RequiresAttention = false)
    : ActivityBlock(Sequence, Timestamp, ExecutionId, RelatedTaskId, ActivityBlockKind.Artifact, Title, Detail, Status, RequiresAttention);
public sealed record ArtifactCreatedBlock(long Sequence, DateTimeOffset Timestamp, ExecutionId ExecutionId, TaskId? TaskId, ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Detail) : ArtifactActivityBlock(Sequence, Timestamp, ExecutionId, TaskId, ArtifactId, RevisionId, "Artifact created", Detail, "CANDIDATE");
public sealed record ArtifactRevisionBlock(long Sequence, DateTimeOffset Timestamp, ExecutionId ExecutionId, TaskId? TaskId, ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Detail) : ArtifactActivityBlock(Sequence, Timestamp, ExecutionId, TaskId, ArtifactId, RevisionId, "Artifact revised", Detail, "NEW REVISION");
public sealed record ArtifactVerificationBlock(long Sequence, DateTimeOffset Timestamp, ExecutionId ExecutionId, TaskId? TaskId, ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Detail, bool Passed) : ArtifactActivityBlock(Sequence, Timestamp, ExecutionId, TaskId, ArtifactId, RevisionId, "Artifact verification", Detail, Passed ? "PASSED" : "FAILED", !Passed);
public sealed record ArtifactReviewRequestedBlock(long Sequence, DateTimeOffset Timestamp, ExecutionId ExecutionId, TaskId? TaskId, ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Detail) : ArtifactActivityBlock(Sequence, Timestamp, ExecutionId, TaskId, ArtifactId, RevisionId, "Review requested", Detail, "NEEDS YOU", true);
public sealed record ArtifactApprovedBlock(long Sequence, DateTimeOffset Timestamp, ExecutionId ExecutionId, TaskId? TaskId, ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Detail) : ArtifactActivityBlock(Sequence, Timestamp, ExecutionId, TaskId, ArtifactId, RevisionId, "Artifact approved", Detail, "APPROVED");
public sealed record ArtifactRejectedBlock(long Sequence, DateTimeOffset Timestamp, ExecutionId ExecutionId, TaskId? TaskId, ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Detail) : ArtifactActivityBlock(Sequence, Timestamp, ExecutionId, TaskId, ArtifactId, RevisionId, "Artifact rejected", Detail, "REJECTED", true);
public sealed record ArtifactIntegratedBlock(long Sequence, DateTimeOffset Timestamp, ExecutionId ExecutionId, TaskId? TaskId, ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Detail) : ArtifactActivityBlock(Sequence, Timestamp, ExecutionId, TaskId, ArtifactId, RevisionId, "Artifact integrated", Detail, "INTEGRATED");
public sealed record ArtifactPublishedBlock(long Sequence, DateTimeOffset Timestamp, ExecutionId ExecutionId, TaskId? TaskId, ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Detail) : ArtifactActivityBlock(Sequence, Timestamp, ExecutionId, TaskId, ArtifactId, RevisionId, "Artifact published", Detail, "PUBLISHED");

public sealed record WarningActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId? RelatedTaskId,
    string Detail)
    : ActivityBlock(Sequence, Timestamp, ExecutionId, RelatedTaskId, ActivityBlockKind.Warning, "Runtime warning", Detail, "ATTENTION", true);

public sealed record ErrorActivityBlock(
    long Sequence,
    DateTimeOffset Timestamp,
    ExecutionId ExecutionId,
    TaskId? RelatedTaskId,
    string Detail)
    : ActivityBlock(Sequence, Timestamp, ExecutionId, RelatedTaskId, ActivityBlockKind.Error, "Runtime error", Detail, "ERROR", true);

internal static class ActivityBlockFactory
{
    public static ActivityBlock Create(RuntimeEvent runtimeEvent) => runtimeEvent switch
    {
        ExecutionStartedEvent started => new PlanActivityBlock(started.Sequence, started.Timestamp, started.ExecutionId, $"{started.TaskCount} executable nodes admitted."),
        TaskCreatedEvent created => new PlanActivityBlock(created.Sequence, created.Timestamp, created.ExecutionId, $"{created.Label} · {created.Executor} · {created.Dependencies.Count} dependencies."),
        TaskStartedEvent started => new AgentActivityBlock(started.Sequence, started.Timestamp, started.ExecutionId, started.TaskId, started.Executor.ToString(), $"Attempt {started.Attempt}", "RUNNING"),
        ToolRequestedEvent tool => new ToolActivityBlock(tool.Sequence, tool.Timestamp, tool.ExecutionId, tool.TaskId, tool.Operation, tool.Target, "REQUESTED"),
        ToolStartedEvent tool => new ToolActivityBlock(tool.Sequence, tool.Timestamp, tool.ExecutionId, tool.TaskId, tool.Operation, tool.Capability, "RUNNING"),
        ToolCompletedEvent tool => new ToolActivityBlock(tool.Sequence, tool.Timestamp, tool.ExecutionId, tool.TaskId, tool.Capability, $"{tool.Duration.TotalMilliseconds:F0} ms · {tool.Evidence.Count} evidence", tool.Succeeded ? "COMPLETED" : "FAILED", !tool.Succeeded),
        ModelRequestedEvent model => new AgentActivityBlock(model.Sequence, model.Timestamp, model.ExecutionId, model.TaskId, model.Model, "Model inference requested", "RUNNING"),
        ModelStreamingEvent streaming => new AgentActivityBlock(streaming.Sequence, streaming.Timestamp, streaming.ExecutionId, streaming.TaskId, streaming.Model, streaming.Text, "STREAMING"),
        IntelligenceRouteSelectedEvent route => new AgentActivityBlock(route.Sequence, route.Timestamp, route.ExecutionId, route.TaskId, "Intelligence", $"{route.Tier} · {route.Gateway}/{route.Route}", "ROUTE SELECTED"),
        ModelCompletedEvent model => new ResultActivityBlock(model.Sequence, model.Timestamp, model.ExecutionId, model.TaskId, $"{model.Model} · {model.Duration.TotalMilliseconds:F0} ms", "MODEL COMPLETE"),
        MemoryRequestedEvent memory => new AgentActivityBlock(memory.Sequence, memory.Timestamp, memory.ExecutionId, memory.TaskId, "Memory", memory.Query, "RUNNING"),
        MemoryCompletedEvent memory => new EvidenceActivityBlock(memory.Sequence, memory.Timestamp, memory.ExecutionId, memory.TaskId, $"{memory.HitCount} references · {memory.Duration.TotalMilliseconds:F0} ms"),
        VerificationStartedEvent verification => new VerificationActivityBlock(verification.Sequence, verification.Timestamp, verification.ExecutionId, verification.TaskId, "Verification started", false),
        VerificationCompletedEvent verification => new VerificationActivityBlock(verification.Sequence, verification.Timestamp, verification.ExecutionId, verification.TaskId, verification.Summary, verification.Succeeded),
        TaskCompletedEvent completed => new ResultActivityBlock(completed.Sequence, completed.Timestamp, completed.ExecutionId, completed.TaskId, completed.Summary ?? $"Result {completed.ResultId}", "SUCCEEDED"),
        TaskFailedEvent failed => new ErrorActivityBlock(failed.Sequence, failed.Timestamp, failed.ExecutionId, failed.TaskId, $"{failed.Error.Code}: {failed.Error.Message}"),
        TaskCancelledEvent cancelled => new WarningActivityBlock(cancelled.Sequence, cancelled.Timestamp, cancelled.ExecutionId, cancelled.TaskId, $"Cancelled: {cancelled.Reason}"),
        TaskTimedOutEvent timeout => new ErrorActivityBlock(timeout.Sequence, timeout.Timestamp, timeout.ExecutionId, timeout.TaskId, $"Timed out after {timeout.Timeout.TotalMilliseconds:F0} ms"),
        RuntimeWarningEvent warning => new WarningActivityBlock(warning.Sequence, warning.Timestamp, warning.ExecutionId, warning.TaskId, warning.Message),
        RuntimeErrorEvent error => new ErrorActivityBlock(error.Sequence, error.Timestamp, error.ExecutionId, error.TaskId, $"{error.Error.Code}: {error.Error.Message}"),
        ExecutionCompletedEvent completed => new ResultActivityBlock(completed.Sequence, completed.Timestamp, completed.ExecutionId, null, completed.Summary, completed.Succeeded ? "EXECUTION COMPLETE" : "EXECUTION ATTENTION"),
        _ => new AgentActivityBlock(runtimeEvent.Sequence, runtimeEvent.Timestamp, runtimeEvent.ExecutionId, runtimeEvent.RelatedTaskId ?? TaskId.Empty, runtimeEvent.Source, runtimeEvent.Kind.ToString(), runtimeEvent.Kind.ToString().ToUpperInvariant())
    };
}

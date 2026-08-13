namespace Abraxius.Protocol;

public readonly record struct TaskId(Guid Value)
{
    public static TaskId New() => new(Guid.NewGuid());
    public static TaskId Empty => new(Guid.Empty);
    public static bool TryParse(string? value, out TaskId id)
    {
        var parsed = Guid.TryParse(value, out var guid);
        id = new TaskId(parsed ? guid : Guid.Empty);
        return parsed;
    }
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ExecutionId(Guid Value)
{
    public static ExecutionId New() => new(Guid.NewGuid());
    public static ExecutionId Empty => new(Guid.Empty);
    public static bool TryParse(string? value, out ExecutionId id)
    {
        var parsed = Guid.TryParse(value, out var guid);
        id = new ExecutionId(parsed ? guid : Guid.Empty);
        return parsed;
    }
    public override string ToString() => Value.ToString("N");
}

public readonly record struct IntentId(Guid Value)
{
    public static IntentId New() => new(Guid.NewGuid());
    public static IntentId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ModelRequestId(Guid Value)
{
    public static ModelRequestId New() => new(Guid.NewGuid());
    public static ModelRequestId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString("N");
}

public readonly record struct CorrelationId(Guid Value)
{
    public static CorrelationId New() => new(Guid.NewGuid());
    public static CorrelationId Empty => new(Guid.Empty);
    public static bool TryParse(string? value, out CorrelationId id)
    {
        var parsed = Guid.TryParse(value, out var guid);
        id = new CorrelationId(parsed ? guid : Guid.Empty);
        return parsed;
    }
    public override string ToString() => Value.ToString("N");
}

public readonly record struct EvidenceId(Guid Value)
{
    public static EvidenceId New() => new(Guid.NewGuid());
    public static EvidenceId Empty => new(Guid.Empty);
    public static bool TryParse(string? value, out EvidenceId id)
    {
        var parsed = Guid.TryParse(value, out var guid);
        id = new EvidenceId(parsed ? guid : Guid.Empty);
        return parsed;
    }
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ArtifactId(Guid Value)
{
    public static ArtifactId New() => new(Guid.NewGuid());
    public static ArtifactId Empty => new(Guid.Empty);
    public static bool TryParse(string? value, out ArtifactId id)
    {
        var parsed = Guid.TryParse(value, out var guid);
        id = new ArtifactId(parsed ? guid : Guid.Empty);
        return parsed;
    }
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ResultId(Guid Value)
{
    public static ResultId New() => new(Guid.NewGuid());
    public static ResultId Empty => new(Guid.Empty);
    public static bool TryParse(string? value, out ResultId id)
    {
        var parsed = Guid.TryParse(value, out var guid);
        id = new ResultId(parsed ? guid : Guid.Empty);
        return parsed;
    }
    public override string ToString() => Value.ToString("N");
}

public readonly record struct NodeId(Guid Value)
{
    public static NodeId New() => new(Guid.NewGuid());
    public static NodeId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString("N");
}

public readonly record struct AgentId(Guid Value)
{
    public static AgentId New() => new(Guid.NewGuid());
    public static AgentId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString("N");
}

public readonly record struct SpeculationGroupId(Guid Value)
{
    public static SpeculationGroupId New() => new(Guid.NewGuid());
    public static SpeculationGroupId Empty => new(Guid.Empty);
    public override string ToString() => Value.ToString("N");
}

/// <summary>A stable, human-readable capability identity.</summary>
public readonly record struct CapabilityId
{
    public CapabilityId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A capability ID cannot be empty.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }

    public override string ToString() => Value;

    public static implicit operator CapabilityId(string value) => new(value);
    public static implicit operator string(CapabilityId value) => value.Value;
}

public interface IRuntimeEventSink
{
    ValueTask PublishAsync(RuntimeEvent runtimeEvent, CancellationToken cancellationToken = default);
}

public sealed record WorkEnvelope<T>(
    TaskId TaskId,
    ExecutionId ExecutionId,
    CorrelationId CorrelationId,
    TaskId? ParentTaskId,
    WorkPriority Priority,
    DateTimeOffset? Deadline,
    T Payload);

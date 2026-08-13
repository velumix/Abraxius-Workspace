using System.Collections.Immutable;
using Abraxius.Protocol;

namespace Abraxius.Core;

public sealed record AgentDescriptor
{
    public AgentDescriptor(
        AgentId id,
        string displayName,
        string role,
        ImmutableArray<WorkKind> supportedWork = default,
        string? modelPreference = null,
        ImmutableArray<CapabilityId> capabilities = default,
        ExecutionConstraints? permissions = null)
    {
        Id = id;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? id.ToString() : displayName;
        Role = role;
        SupportedWork = supportedWork.IsDefault ? ImmutableArray<WorkKind>.Empty : supportedWork;
        ModelPreference = modelPreference;
        Capabilities = capabilities.IsDefault ? ImmutableArray<CapabilityId>.Empty : capabilities;
        Permissions = permissions ?? new ExecutionConstraints();
    }

    public AgentId Id { get; }
    public string DisplayName { get; }
    public string Role { get; }
    public ImmutableArray<WorkKind> SupportedWork { get; }
    public string? ModelPreference { get; }
    public ImmutableArray<CapabilityId> Capabilities { get; }
    public ExecutionConstraints Permissions { get; }
}

public sealed record AgentCapability(
    string Name,
    WorkKind WorkKind,
    ImmutableArray<CapabilityId> Capabilities,
    string? Description = null);

public interface IAgentDescriptorSource
{
    ValueTask<AgentDescriptor?> GetAsync(AgentId agentId, CancellationToken cancellationToken = default);
}

public interface IActionPolicy
{
    ValueTask<PolicyDecision> EvaluateAsync(
        ProposedAction action,
        PolicyContext context,
        CancellationToken cancellationToken = default);
}

public sealed record PolicyContext(
    ExecutionId ExecutionId,
    CorrelationId CorrelationId,
    TaskId? TaskId,
    ExecutionConstraints Constraints,
    AgentId? Agent = null);

public enum PolicyDecisionKind
{
    Allow,
    Deny,
    RequireApproval
}

public sealed record PolicyDecision(
    PolicyDecisionKind Kind,
    string Reason,
    RuntimeError? Error = null)
{
    public bool IsAllowed => Kind == PolicyDecisionKind.Allow;
}

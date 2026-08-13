using System.Collections.Immutable;
using Abraxius.Axl;
using Abraxius.Core;
using Abraxius.Memory;
using Abraxius.Protocol;

namespace Abraxius.Agents;

public readonly record struct SpecialistDefinitionId(string Value)
{
    public override string ToString() => Value;
}

public readonly record struct SpecialistInstanceId(Guid Value)
{
    public static SpecialistInstanceId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct MissionId(Guid Value)
{
    public static MissionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct AssignmentId(Guid Value)
{
    public static AssignmentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct AgentMessageId(Guid Value)
{
    public static AgentMessageId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct HypothesisId(Guid Value)
{
    public static HypothesisId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum SpecialistRole { Coordinator, Investigator, Builder, Verifier, DomainExpert }
public enum SpecialistState { Idle, Assigned, Running, Waiting, Blocked, Failed, Unavailable }
public enum MissionState { Created, Planning, Executing, Verifying, Blocked, Succeeded, Failed, Cancelled }
public enum AssignmentState { Created, Queued, Running, Waiting, Blocked, Completed, Failed, Cancelled }
public enum MutationPolicy { Deny, Proposed, Gated }
public enum WorkspacePolicy { SharedReadOnly, SharedMutable, IsolatedWorktree, TemporaryWorkspace, RemoteWorkspace }
public enum AgentHostKind { InProcess, LocalWorker, Container, RemoteNode, CloudWorker }
public enum VerificationStatus { Passed, Failed, Inconclusive }
public enum HypothesisState { Proposed, Supported, Rejected, Confirmed }
public enum AgentEventKind
{
    MissionCreated, MissionStateChanged, SpecialistStateChanged, AssignmentCreated,
    AssignmentStateChanged, MessageObserved, HypothesisUpdated, MissionCompleted
}

public sealed record CognitiveBudget(
    int MaxPlanningRounds = 3,
    int MaxReplans = 2,
    int MaxModelCalls = 12,
    int MaxDelegations = 12,
    int MaxEvidenceExpansion = 32,
    int MaxFrontierEscalations = 1,
    TimeSpan? MaximumDuration = null);

public sealed record AutonomyBudget(
    int MaxActions = 32,
    int MaxMutations = 8,
    int MaxSubAssignments = 12,
    decimal MaximumCost = 0,
    int MaximumParallelSpecialists = 4,
    int MaxDelegationDepth = 3);

public sealed record SpecialistCapabilityPolicy(
    IReadOnlySet<CapabilityId> AllowedCapabilities,
    MutationPolicy Mutation = MutationPolicy.Deny,
    bool AllowToolCalls = true)
{
    public bool Allows(CapabilityId capability) => AllowedCapabilities.Contains(capability);
}

public sealed record SpecialistModelPolicy(
    string RouteProfile = "FreeFirst",
    bool RequireCodingCapability = false,
    bool PreferAlternateFamily = false,
    int? MaxOutputTokens = null);

public sealed record SpecialistMemoryPolicy(
    IReadOnlySet<MemoryKind>? PreferredKinds = null,
    MemoryPrivacyClass MaximumPrivacy = MemoryPrivacyClass.Sensitive,
    MemoryRetrievalMode Mode = MemoryRetrievalMode.Hybrid,
    bool RequireEvidence = false);

public sealed record SpecialistPlanningPolicy(
    int Horizon = 5,
    bool AllowDelegation = false,
    bool RequireEvidenceForMutation = true);

public sealed record SpecialistVerificationPolicy(
    bool Required = true,
    int MaxRepairAttempts = 1,
    bool PreferIndependentModelFamily = false);

public sealed record SpecialistWorkspacePolicy(
    WorkspacePolicy Mode = WorkspacePolicy.SharedReadOnly,
    string? Root = null,
    bool AllowSharedWrites = false);

public sealed record SpecialistMission(string Summary, IReadOnlyList<string>? Responsibilities = null)
{
    public IReadOnlyList<string> SafeResponsibilities => Responsibilities ?? Array.Empty<string>();
}

public sealed record SpecialistDefinition
{
    public required SpecialistDefinitionId Id { get; init; }
    public required SpecialistRole Role { get; init; }
    public required string DisplayName { get; init; }
    public required SpecialistMission Mission { get; init; }
    public required SpecialistCapabilityPolicy CapabilityPolicy { get; init; }
    public required SpecialistModelPolicy ModelPolicy { get; init; }
    public required SpecialistMemoryPolicy MemoryPolicy { get; init; }
    public required SpecialistPlanningPolicy PlanningPolicy { get; init; }
    public required SpecialistVerificationPolicy VerificationPolicy { get; init; }
    public required SpecialistWorkspacePolicy WorkspacePolicy { get; init; }
    public required CognitiveBudget CognitiveBudget { get; init; }
    public required AutonomyBudget AutonomyBudget { get; init; }
    public AgentHostKind HostKind { get; init; } = AgentHostKind.InProcess;
    public ImmutableArray<string> Aliases { get; init; } = ImmutableArray<string>.Empty;
}

public sealed record SpecialistInstance(
    SpecialistInstanceId Id,
    SpecialistDefinitionId DefinitionId,
    SpecialistRole Role,
    string DisplayName,
    SpecialistState State = SpecialistState.Idle,
    AssignmentId? AssignmentId = null,
    AgentHostKind Host = AgentHostKind.InProcess,
    DateTimeOffset? UpdatedAt = null);

public sealed record MissionSuccessContract(
    string Objective,
    IReadOnlyList<string> RequiredOutcomes,
    IReadOnlyList<string> VerificationRequirements,
    IReadOnlyList<string>? Constraints = null,
    IReadOnlyList<string>? OptionalOutcomes = null,
    IReadOnlyList<string>? FailureConditions = null)
{
    public IReadOnlyList<string> SafeConstraints => Constraints ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeOptionalOutcomes => OptionalOutcomes ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeFailureConditions => FailureConditions ?? Array.Empty<string>();
}

public sealed record Mission(
    MissionId Id,
    Intent Intent,
    MissionSuccessContract SuccessContract,
    WorkPriority Priority,
    CognitiveBudget CognitiveBudget,
    AutonomyBudget AutonomyBudget,
    WorkspacePolicy Workspace,
    MissionState State = MissionState.Created,
    ImmutableArray<AssignmentId> Assignments = default,
    ImmutableArray<EvidenceId> Evidence = default,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? CompletedAt = null)
{
    public ImmutableArray<AssignmentId> SafeAssignments => Assignments.IsDefault ? ImmutableArray<AssignmentId>.Empty : Assignments;
    public ImmutableArray<EvidenceId> SafeEvidence => Evidence.IsDefault ? ImmutableArray<EvidenceId>.Empty : Evidence;
}

public sealed record AgentAssignment(
    AssignmentId Id,
    MissionId MissionId,
    SpecialistRole Role,
    SpecialistInstanceId InstanceId,
    string Objective,
    IReadOnlyList<string> SuccessCriteria,
    IReadOnlyList<EvidenceId>? Evidence = null,
    IReadOnlyList<AssignmentId>? Dependencies = null,
    IReadOnlyList<string>? Constraints = null,
    CognitiveBudget? CognitiveBudget = null,
    AutonomyBudget? AutonomyBudget = null,
    WorkspacePolicy Workspace = WorkspacePolicy.SharedReadOnly,
    WorkPriority Priority = WorkPriority.Interactive,
    AssignmentState State = AssignmentState.Created,
    int Attempt = 0)
{
    public IReadOnlyList<EvidenceId> SafeEvidence => Evidence ?? Array.Empty<EvidenceId>();
    public IReadOnlyList<AssignmentId> SafeDependencies => Dependencies ?? Array.Empty<AssignmentId>();
    public IReadOnlyList<string> SafeConstraints => Constraints ?? Array.Empty<string>();
}

public sealed record MissionContext(
    Mission Mission,
    IReadOnlyList<AgentAssignment> ActiveAssignments,
    IReadOnlyList<EvidenceId> Evidence,
    MemoryContextPackage? MemoryContext = null,
    string? WorkspacePath = null);

public sealed record AgentAssignmentResult(
    bool Succeeded,
    string Summary,
    IReadOnlyList<EvidenceId>? Evidence = null,
    VerificationStatus? Verification = null,
    string? Output = null,
    string? FailureCode = null,
    bool MadeProgress = true,
    IReadOnlyList<ArtifactReference>? Artifacts = null)
{
    public IReadOnlyList<EvidenceId> SafeEvidence => Evidence ?? Array.Empty<EvidenceId>();
    public IReadOnlyList<ArtifactReference> SafeArtifacts => Artifacts ?? Array.Empty<ArtifactReference>();
}

public sealed record MissionResult(
    Mission Mission,
    string Summary,
    IReadOnlyDictionary<AssignmentId, AgentAssignmentResult> AssignmentResults,
    TimeSpan Duration,
    IReadOnlyList<ArtifactReference>? Artifacts = null)
{
    public bool Succeeded => Mission.State == MissionState.Succeeded;
    public IReadOnlyList<ArtifactReference> SafeArtifacts => Artifacts ?? Array.Empty<ArtifactReference>();
}

public sealed record AgentMissionRecord(
    Mission Mission,
    string Summary,
    DateTimeOffset RecordedAt,
    int ResultCount);

public interface IAgentMissionStore
{
    ValueTask<IReadOnlyList<AgentMissionRecord>> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(AgentMissionRecord record, CancellationToken cancellationToken = default);
}

public sealed record Hypothesis(
    HypothesisId Id,
    AssignmentId AssignmentId,
    string Claim,
    double Confidence,
    IReadOnlyList<EvidenceId>? SupportingEvidence = null,
    IReadOnlyList<EvidenceId>? ContradictingEvidence = null,
    HypothesisState State = HypothesisState.Proposed)
{
    public IReadOnlyList<EvidenceId> SafeSupportingEvidence => SupportingEvidence ?? Array.Empty<EvidenceId>();
    public IReadOnlyList<EvidenceId> SafeContradictingEvidence => ContradictingEvidence ?? Array.Empty<EvidenceId>();
}

public sealed record AgentEvent(
    AgentEventKind Kind,
    DateTimeOffset Timestamp,
    MissionId? MissionId = null,
    AssignmentId? AssignmentId = null,
    SpecialistInstanceId? InstanceId = null,
    string? Detail = null,
    object? Payload = null);

public sealed record AgentKernelOptions(
    int MessageBufferCapacity = 512,
    int EventBufferCapacity = 1024,
    int MaxConcurrentSpecialists = 8,
    int MaxMissionHistory = 256,
    TimeSpan? DefaultMissionTimeout = null);

public sealed record AgentPolicyDecision(bool Allowed, string? Reason = null)
{
    public static AgentPolicyDecision Denied(string reason) => new(false, reason);
    public static AgentPolicyDecision Granted() => new(true);
}

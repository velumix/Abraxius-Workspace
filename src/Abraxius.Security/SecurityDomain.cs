using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Protocol;
using Abraxius.Skills;

namespace Abraxius.Security;

public readonly record struct PrincipalId(string Value) { public override string ToString() => Value; }
public readonly record struct UserId(string Value) { public override string ToString() => Value; }
public readonly record struct ActionId(Guid Value) { public static ActionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct AuthorizationDecisionId(Guid Value) { public static AuthorizationDecisionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct AuthorizationGrantId(Guid Value) { public static AuthorizationGrantId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct SecurityAuditEventId(Guid Value) { public static SecurityAuditEventId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct SecretReference(string Value)
{
    public static bool TryParse(string value, out SecretReference reference)
    {
        reference = default;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.Scheme.Equals("secret", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(uri.Host)) return false;
        reference = new SecretReference($"secret://{uri.Host.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/')}");
        return true;
    }
    public override string ToString() => Value;
}

public enum PrincipalType { User, System, Specialist, Skill, Plugin, RemoteNode, Service }
public enum ResourceKind { File, Directory, GitRepository, Network, Secret, Database, Roblox, RemoteNode, Process, ModelProvider, Capability, Artifact, Unknown }
public enum AuthorizationOutcome { Allow, Deny, RequireApproval, AllowWithConstraints }
public enum PolicyEffect { Allow, Deny, RequireApproval }
public enum PolicyLayer { Global, User, Workspace, Project, Mission, Specialist, Skill, Plugin, TemporaryGrant }
public enum PolicyPreset { Conservative, Balanced, Developer, Custom }
public enum DataClassification { Public, Internal, Confidential, Secret, LocalOnly }
public enum SandboxLevel { None, RestrictedProcess, IsolatedWorkspace, Container, RemoteSandbox }
public enum GrantScope { Once, Mission, Project, Timed, Session }

[Flags]
public enum RiskClass
{
    None = 0,
    ReadOnly = 1,
    LowRiskMutation = 2,
    Mutation = 4,
    Destructive = 8,
    ExternalSideEffect = 16,
    Privileged = 32,
    CredentialSensitive = 64,
    Financial = 128,
    Shell = 256
}

public static class SecurityActions
{
    public const string FileRead = "File.Read";
    public const string FileWrite = "File.Write";
    public const string FileDelete = "File.Delete";
    public const string DirectoryList = "Directory.List";
    public const string DirectoryCreate = "Directory.Create";
    public const string ProcessExecute = "Process.Execute";
    public const string ProcessShellExecute = "Process.ShellExecute";
    public const string ProcessTerminate = "Process.Terminate";
    public const string GitRead = "Git.Read";
    public const string GitCommit = "Git.Commit";
    public const string GitPush = "Git.Push";
    public const string GitForcePush = "Git.ForcePush";
    public const string NetworkHttpGet = "Network.HttpGet";
    public const string NetworkHttpPost = "Network.HttpPost";
    public const string SecretUse = "Secret.Use";
    public const string SecretReadRaw = "Secret.ReadRaw";
    public const string DatabaseRead = "Database.Read";
    public const string DatabaseWrite = "Database.Write";
    public const string DatabaseSchemaChange = "Database.SchemaChange";
    public const string DeploymentPublish = "Deployment.Publish";
    public const string RobloxReadStudio = "Roblox.ReadStudio";
    public const string RobloxModifyStudio = "Roblox.ModifyStudio";
    public const string RobloxPublish = "Roblox.Publish";
    public const string NotificationSend = "Notification.Send";
    public const string UpdateInstall = "Update.Install";
    public const string ModelEgress = "Model.Egress";
    public const string MemoryRead = "Memory.Read";
    public const string CapabilityInvoke = "Capability.Invoke";
    public const string ArtifactRead = "Artifact.Read";
    public const string ArtifactReview = "Artifact.Review";
    public const string ArtifactApprove = "Artifact.Approve";
    public const string ArtifactIntegrate = "Artifact.Integrate";
    public const string ArtifactPublish = "Artifact.Publish";
    public const string ArtifactExport = "Artifact.Export";
    public const string ArtifactDelete = "Artifact.Delete";
    public const string FabricExecute = "Fabric.Execute";
    public const string FabricTransfer = "Fabric.Transfer";
    public const string FabricPair = "Fabric.Pair";
    public const string FabricRevoke = "Fabric.Revoke";

    public static ImmutableHashSet<string> Known { get; } = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
        FileRead, FileWrite, FileDelete, DirectoryList, DirectoryCreate, ProcessExecute, ProcessShellExecute, ProcessTerminate,
        GitRead, GitCommit, GitPush, GitForcePush, NetworkHttpGet, NetworkHttpPost, SecretUse, SecretReadRaw,
        DatabaseRead, DatabaseWrite, DatabaseSchemaChange, DeploymentPublish, RobloxReadStudio, RobloxModifyStudio,
        RobloxPublish, NotificationSend, UpdateInstall, ModelEgress, MemoryRead, CapabilityInvoke,
        ArtifactRead, ArtifactReview, ArtifactApprove, ArtifactIntegrate, ArtifactPublish, ArtifactExport, ArtifactDelete,
        FabricExecute, FabricTransfer, FabricPair, FabricRevoke);
}

public sealed record SecuritySubject(
    PrincipalId PrincipalId,
    PrincipalType PrincipalType,
    UserId? UserId = null,
    MissionId? MissionId = null,
    AssignmentId? AssignmentId = null,
    SpecialistInstanceId? AgentInstanceId = null,
    SpecialistRole? SpecialistRole = null,
    SkillExecutionId? SkillExecutionId = null,
    string? PluginId = null,
    string? RemoteNodeId = null)
{
    public static SecuritySubject System(string service = "runtime") => new(new PrincipalId($"system:{service}"), PrincipalType.System);
    public static SecuritySubject Specialist(SpecialistRole role, SpecialistInstanceId instance, MissionId mission, AssignmentId assignment) =>
        new(new PrincipalId($"specialist:{instance}"), PrincipalType.Specialist, MissionId: mission, AssignmentId: assignment, AgentInstanceId: instance, SpecialistRole: role);
}

public sealed record SecurityResource(
    ResourceKind Kind,
    string CanonicalUri,
    string? LocalPath = null,
    string? Host = null,
    int? Port = null,
    string? Scope = null,
    bool IsInternalNetwork = false,
    bool Exists = false)
{
    public bool IsKnown => Kind != ResourceKind.Unknown && Uri.TryCreate(CanonicalUri, UriKind.Absolute, out _);
    public override string ToString() => CanonicalUri;
}

public sealed record ProposedAction(
    ActionId ActionId,
    SecuritySubject Subject,
    string Capability,
    string Operation,
    SecurityResource Resource,
    IReadOnlyDictionary<string, string>? ParametersSummary = null,
    bool ExpectedMutation = false,
    bool ExternalEffect = false,
    RiskClass RiskHints = RiskClass.None,
    ImmutableArray<EvidenceId> EvidenceRefs = default,
    string? Reason = null,
    int ResourceCount = 1,
    long EstimatedBytes = 0,
    SandboxLevel MinimumSandbox = SandboxLevel.None)
{
    public ImmutableArray<EvidenceId> SafeEvidenceRefs => EvidenceRefs.IsDefault ? ImmutableArray<EvidenceId>.Empty : EvidenceRefs;
}

public sealed record AuthorizationContext(
    string? WorkspaceRoot = null,
    string? ProjectId = null,
    string? Repository = null,
    string? Branch = null,
    DataClassification Classification = DataClassification.Internal,
    SandboxLevel AvailableSandbox = SandboxLevel.None,
    bool PlatformCapabilityAvailable = true,
    bool Replay = false,
    bool UserPresent = false,
    int MaxFilesModified = 50,
    long MaxBytesWritten = 50 * 1024 * 1024,
    int MaxExternalRequests = 20,
    decimal MaximumCost = 0);

public sealed record AuthorizationRequest(
    SecuritySubject Subject,
    ProposedAction Action,
    AuthorizationContext Context,
    DateTimeOffset RequestedAt,
    TimeSpan? RequestedDuration = null,
    string SchemaVersion = "security.authorization/1");

public enum AuthorizationReasonCode
{
    AllowedByPolicy,
    AllowedByScopedGrant,
    AllowedReadOnlyWorkspace,
    ApprovalRequiredForExternalMutation,
    ApprovalRequiredForDestructiveAction,
    DeniedOutsideWorkspace,
    DeniedSecretScope,
    DeniedUnknownCapability,
    DeniedUnknownResource,
    DeniedMalformedRequest,
    DeniedDestructiveAction,
    DeniedExpiredGrant,
    DeniedPluginScope,
    DeniedLocalOnlyPolicy,
    DeniedSpecialistPolicy,
    DeniedSandboxUnavailable,
    DeniedReplaySideEffect,
    DeniedLockdown,
    DeniedPolicy,
    DeniedMutationBudget
}

public sealed record AuthorizationConstraints(
    string? ResourcePrefix = null,
    int? MaximumUses = null,
    int? MaximumResourceCount = null,
    long? MaximumBytes = null,
    SandboxLevel MinimumSandbox = SandboxLevel.None,
    DateTimeOffset? ExpiresAt = null);

public sealed record AuthorizationDecision(
    AuthorizationDecisionId DecisionId,
    AuthorizationOutcome Outcome,
    AuthorizationReasonCode ReasonCode,
    string HumanExplanation,
    RiskClass Risk,
    AuthorizationConstraints? Constraints = null,
    AuthorizationGrant? Grant = null,
    ImmutableArray<string> PolicyRefs = default,
    DateTimeOffset? Expiry = null)
{
    public bool IsAllowed => Outcome is AuthorizationOutcome.Allow or AuthorizationOutcome.AllowWithConstraints;
    public ImmutableArray<string> SafePolicyRefs => PolicyRefs.IsDefault ? ImmutableArray<string>.Empty : PolicyRefs;
}

public sealed record PolicyRule(
    string Id,
    PolicyLayer Layer,
    PolicyEffect Effect,
    string ActionPattern,
    ImmutableHashSet<PrincipalType>? PrincipalTypes = null,
    ImmutableHashSet<SpecialistRole>? SpecialistRoles = null,
    ImmutableHashSet<ResourceKind>? ResourceKinds = null,
    string? ResourcePrefix = null,
    RiskClass RiskAny = RiskClass.None,
    DataClassification? MaximumClassification = null,
    string? MissionId = null,
    string? ProjectId = null,
    string Explanation = "Policy rule matched.");

public sealed record AuthorizationGrant(
    AuthorizationGrantId GrantId,
    SecuritySubject Subject,
    ImmutableHashSet<string> Capabilities,
    string ResourcePrefix,
    GrantScope Scope,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string IssuedBy,
    string Reason,
    MissionId? MissionId = null,
    string? ProjectId = null,
    int? MaximumUses = null,
    int Uses = 0,
    bool Revoked = false,
    string SchemaVersion = "security.grant/1")
{
    public bool IsExpired(DateTimeOffset now) => Revoked || now >= ExpiresAt || (MaximumUses is { } maximum && Uses >= maximum);
}

public sealed record AuthorizationExplanation(AuthorizationDecision Decision, ImmutableArray<string> Trace);

public sealed record SecurityStatus(
    PolicyPreset Preset,
    bool Lockdown,
    int ActiveGrants,
    int PendingApprovals,
    int StoredSecrets,
    int RecentDenials,
    ImmutableDictionary<SandboxLevel, bool> SandboxAvailability);

using System.Collections.Immutable;
using Abraxius.Agents;

namespace Abraxius.Security;

public interface IRiskClassifier { RiskClass Classify(ProposedAction action); }

public sealed class DeterministicRiskClassifier : IRiskClassifier
{
    public RiskClass Classify(ProposedAction action)
    {
        var risk = action.Operation switch
        {
            SecurityActions.FileRead or SecurityActions.DirectoryList or SecurityActions.GitRead or SecurityActions.DatabaseRead or SecurityActions.RobloxReadStudio or SecurityActions.MemoryRead or SecurityActions.ArtifactRead or SecurityActions.ArtifactReview => RiskClass.ReadOnly,
            SecurityActions.FileWrite or SecurityActions.DirectoryCreate or SecurityActions.GitCommit or SecurityActions.DatabaseWrite or SecurityActions.RobloxModifyStudio or SecurityActions.ArtifactApprove or SecurityActions.ArtifactIntegrate => RiskClass.Mutation,
            SecurityActions.FileDelete or SecurityActions.GitForcePush or SecurityActions.DatabaseSchemaChange or SecurityActions.ProcessTerminate or SecurityActions.ArtifactDelete => RiskClass.Destructive | RiskClass.Privileged,
            SecurityActions.GitPush or SecurityActions.NetworkHttpPost or SecurityActions.DeploymentPublish or SecurityActions.RobloxPublish or SecurityActions.NotificationSend or SecurityActions.UpdateInstall or SecurityActions.ArtifactPublish or SecurityActions.ArtifactExport => RiskClass.ExternalSideEffect,
            SecurityActions.SecretUse => RiskClass.CredentialSensitive,
            SecurityActions.SecretReadRaw => RiskClass.CredentialSensitive | RiskClass.Privileged,
            SecurityActions.ProcessShellExecute => RiskClass.Privileged | RiskClass.Shell,
            SecurityActions.ProcessExecute => RiskClass.Privileged,
            SecurityActions.NetworkHttpGet or SecurityActions.ModelEgress => RiskClass.ExternalSideEffect,
            SecurityActions.FabricExecute => action.ExpectedMutation ? RiskClass.Mutation | RiskClass.Privileged : RiskClass.ReadOnly,
            SecurityActions.FabricTransfer => RiskClass.Privileged,
            SecurityActions.FabricPair or SecurityActions.FabricRevoke => RiskClass.Privileged | RiskClass.Mutation,
            SecurityActions.CapabilityInvoke => action.ExpectedMutation ? RiskClass.Mutation : RiskClass.ReadOnly,
            _ => RiskClass.Privileged
        };
        if (action.ExpectedMutation) risk |= RiskClass.Mutation;
        if (action.ExternalEffect) risk |= RiskClass.ExternalSideEffect;
        if (action.ResourceCount > 100 || action.EstimatedBytes > 100 * 1024 * 1024) risk |= RiskClass.Destructive;
        return risk;
    }
}

public interface IPolicyEngine
{
    AuthorizationExplanation Evaluate(AuthorizationRequest request, RiskClass risk, AuthorizationGrant? grant = null);
    ImmutableArray<PolicyRule> Rules { get; }
    PolicyPreset Preset { get; }
}

public sealed class DeterministicPolicyEngine : IPolicyEngine
{
    private ImmutableArray<PolicyRule> _rules;
    private int _lockdown;
    public DeterministicPolicyEngine(IEnumerable<PolicyRule>? rules = null, PolicyPreset preset = PolicyPreset.Balanced)
    {
        Preset = preset;
        _rules = (rules ?? SecurityPolicyPresets.Create(preset)).ToImmutableArray();
    }
    public PolicyPreset Preset { get; }
    public ImmutableArray<PolicyRule> Rules => _rules;
    public bool Lockdown { get => Volatile.Read(ref _lockdown) != 0; set => Volatile.Write(ref _lockdown, value ? 1 : 0); }
    public void ReplaceRules(IEnumerable<PolicyRule> rules) => Interlocked.Exchange(ref _rules, rules.ToImmutableArray());

    public AuthorizationExplanation Evaluate(AuthorizationRequest request, RiskClass risk, AuthorizationGrant? grant = null)
    {
        var trace = ImmutableArray.CreateBuilder<string>();
        var action = request.Action;
        if (!Enum.IsDefined(request.Subject.PrincipalType) || string.IsNullOrWhiteSpace(request.Subject.PrincipalId.Value))
            return Deny(AuthorizationReasonCode.DeniedMalformedRequest, "The requesting principal is not recognized.", risk, trace, "identity:invalid");
        if (!SecurityActions.Known.Contains(action.Operation))
            return Deny(AuthorizationReasonCode.DeniedUnknownCapability, $"Capability operation '{action.Operation}' is not registered.", risk, trace, "capability:unknown");
        if (!action.Resource.IsKnown)
            return Deny(AuthorizationReasonCode.DeniedUnknownResource, "The target resource could not be resolved safely.", risk, trace, "resource:unknown");
        if (!request.Context.PlatformCapabilityAvailable)
            return Deny(AuthorizationReasonCode.DeniedPolicy, "The required platform capability is unavailable.", risk, trace, "platform:unavailable");
        if (request.Context.Replay && (risk & (RiskClass.Mutation | RiskClass.ExternalSideEffect | RiskClass.Destructive)) != 0)
            return Deny(AuthorizationReasonCode.DeniedReplaySideEffect, "Trajectory replay cannot repeat mutations or external side effects.", risk, trace, "replay:no-side-effects");
        if (Lockdown && (risk & ~RiskClass.ReadOnly) != 0)
            return Deny(AuthorizationReasonCode.DeniedLockdown, "Lockdown permits safe inspection only.", risk, trace, "global:lockdown");
        if (request.Context.Classification == DataClassification.LocalOnly && action.Resource.Kind is ResourceKind.Network or ResourceKind.ModelProvider or ResourceKind.RemoteNode)
            return Deny(AuthorizationReasonCode.DeniedLocalOnlyPolicy, "LocalOnly data cannot be transmitted to another execution location.", risk, trace, "classification:local-only");
        if (action.Resource.IsInternalNetwork && action.Resource.Kind is ResourceKind.Network or ResourceKind.ModelProvider)
            return Deny(AuthorizationReasonCode.DeniedPolicy, "Requests to loopback, private, link-local, and metadata networks are blocked by default.", risk, trace, "network:ssrf-default-deny");
        if (action.MinimumSandbox > request.Context.AvailableSandbox)
            return Deny(AuthorizationReasonCode.DeniedSandboxUnavailable, $"{action.MinimumSandbox} isolation is required but only {request.Context.AvailableSandbox} is available.", risk, trace, "sandbox:minimum");
        if (action.ResourceCount > request.Context.MaxFilesModified && (risk & RiskClass.Mutation) != 0 || action.EstimatedBytes > request.Context.MaxBytesWritten)
            return Deny(AuthorizationReasonCode.DeniedMutationBudget, "The proposed mutation exceeds the mission's bounded resource budget.", risk, trace, "mission:mutation-budget");
        if (request.Subject.PrincipalType == PrincipalType.Specialist)
        {
            if (request.Subject.SpecialistRole is SpecialistRole.Investigator or SpecialistRole.Verifier or SpecialistRole.Coordinator && (risk & (RiskClass.Mutation | RiskClass.Destructive)) != 0)
                return Deny(AuthorizationReasonCode.DeniedSpecialistPolicy, $"{request.Subject.SpecialistRole} is read-only for this assignment.", risk, trace, "specialist:read-only");
        }

        var matches = _rules.Where(rule => Matches(rule, request, risk)).OrderBy(static rule => rule.Layer).ToArray();
        foreach (var match in matches) trace.Add($"{match.Layer} {match.Id}: {match.Effect} — {match.Explanation}");
        var deny = matches.LastOrDefault(static rule => rule.Effect == PolicyEffect.Deny);
        if (deny is not null) return Deny(AuthorizationReasonCode.DeniedPolicy, deny.Explanation, risk, trace, deny.Id);

        if (grant is not null)
        {
            trace.Add($"TemporaryGrant {grant.GrantId}: Allow — bounded {grant.Scope} grant.");
            var decision = new AuthorizationDecision(AuthorizationDecisionId.New(), AuthorizationOutcome.AllowWithConstraints,
                AuthorizationReasonCode.AllowedByScopedGrant, "Allowed by a valid bounded grant.", risk,
                new AuthorizationConstraints(grant.ResourcePrefix, grant.MaximumUses, ExpiresAt: grant.ExpiresAt), grant,
                trace.ToImmutable(), grant.ExpiresAt);
            return new(decision, trace.ToImmutable());
        }

        var ask = matches.LastOrDefault(static rule => rule.Effect == PolicyEffect.RequireApproval);
        if (ask is not null || (risk & (RiskClass.Destructive | RiskClass.ExternalSideEffect | RiskClass.CredentialSensitive | RiskClass.Financial)) != 0)
        {
            var code = (risk & RiskClass.Destructive) != 0 ? AuthorizationReasonCode.ApprovalRequiredForDestructiveAction : AuthorizationReasonCode.ApprovalRequiredForExternalMutation;
            var explanation = ask?.Explanation ?? "This operation crosses a high-risk authority boundary and needs human approval.";
            return new(new AuthorizationDecision(AuthorizationDecisionId.New(), AuthorizationOutcome.RequireApproval, code, explanation, risk,
                new AuthorizationConstraints(action.Resource.CanonicalUri, MaximumUses: 1, MinimumSandbox: action.MinimumSandbox), PolicyRefs: trace.ToImmutable()), trace.ToImmutable());
        }

        var allow = matches.LastOrDefault(static rule => rule.Effect == PolicyEffect.Allow);
        if (allow is not null)
            return new(new AuthorizationDecision(AuthorizationDecisionId.New(), AuthorizationOutcome.Allow, AuthorizationReasonCode.AllowedByPolicy,
                allow.Explanation, risk, PolicyRefs: trace.ToImmutable()), trace.ToImmutable());
        return Deny(AuthorizationReasonCode.DeniedPolicy, "No policy grants this principal authority over the requested resource.", risk, trace, "default:deny");
    }

    private static AuthorizationExplanation Deny(AuthorizationReasonCode code, string text, RiskClass risk, ImmutableArray<string>.Builder trace, string policy)
    {
        trace.Add($"Final {policy}: Deny — {text}");
        var decision = new AuthorizationDecision(AuthorizationDecisionId.New(), AuthorizationOutcome.Deny, code, text, risk, PolicyRefs: trace.ToImmutable());
        return new(decision, trace.ToImmutable());
    }

    private static bool Matches(PolicyRule rule, AuthorizationRequest request, RiskClass risk)
    {
        var action = request.Action;
        var actionMatch = rule.ActionPattern == "*" || (rule.ActionPattern.EndsWith('*')
            ? action.Operation.StartsWith(rule.ActionPattern[..^1], StringComparison.OrdinalIgnoreCase)
            : action.Operation.Equals(rule.ActionPattern, StringComparison.OrdinalIgnoreCase));
        if (!actionMatch) return false;
        if (rule.PrincipalTypes is not null && !rule.PrincipalTypes.Contains(request.Subject.PrincipalType)) return false;
        if (rule.SpecialistRoles is not null && (request.Subject.SpecialistRole is null || !rule.SpecialistRoles.Contains(request.Subject.SpecialistRole.Value))) return false;
        if (rule.ResourceKinds is not null && !rule.ResourceKinds.Contains(action.Resource.Kind)) return false;
        if (rule.ResourcePrefix is not null && !action.Resource.CanonicalUri.StartsWith(rule.ResourcePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (rule.RiskAny != RiskClass.None && (risk & rule.RiskAny) == 0) return false;
        if (rule.MaximumClassification is { } maximum && request.Context.Classification > maximum) return false;
        if (rule.MissionId is not null && request.Subject.MissionId?.ToString() != rule.MissionId) return false;
        if (rule.ProjectId is not null && request.Context.ProjectId != rule.ProjectId) return false;
        return true;
    }
}

public static class SecurityPolicyPresets
{
    public static ImmutableArray<PolicyRule> Create(PolicyPreset preset)
    {
        var specialists = ImmutableHashSet.Create(PrincipalType.Specialist);
        var systemAndUser = ImmutableHashSet.Create(PrincipalType.System, PrincipalType.User, PrincipalType.Service);
        var builder = ImmutableHashSet.Create(SpecialistRole.Builder);
        var rules = ImmutableArray.CreateBuilder<PolicyRule>();
        rules.Add(new("global.raw-secret-deny", PolicyLayer.Global, PolicyEffect.Deny, SecurityActions.SecretReadRaw, Explanation: "Raw secret extraction is disabled."));
        rules.Add(new("global.force-push-ask", PolicyLayer.Global, PolicyEffect.RequireApproval, SecurityActions.GitForcePush, Explanation: "Force push is destructive and requires explicit review."));
        rules.Add(new("global.read", PolicyLayer.Global, PolicyEffect.Allow, "File.*", RiskAny: RiskClass.ReadOnly, Explanation: "Read-only access is allowed inside a separately bounded workspace scope."));
        rules.Add(new("global.directory-read", PolicyLayer.Global, PolicyEffect.Allow, SecurityActions.DirectoryList, Explanation: "Directory inspection is read-only."));
        rules.Add(new("global.git-read", PolicyLayer.Global, PolicyEffect.Allow, SecurityActions.GitRead, Explanation: "Git inspection is read-only."));
        rules.Add(new("global.memory-read", PolicyLayer.Global, PolicyEffect.Allow, SecurityActions.MemoryRead, Explanation: "Memory retrieval is allowed after scope filtering."));
        rules.Add(new("global.artifact-read", PolicyLayer.Global, PolicyEffect.Allow, SecurityActions.ArtifactRead, Explanation: "Artifact inspection is read-only."));
        rules.Add(new("global.artifact-review", PolicyLayer.Global, PolicyEffect.Allow, SecurityActions.ArtifactReview, Explanation: "Artifact review records a product decision but grants no execution authority."));
        rules.Add(new("fabric.execute-read", PolicyLayer.Global, PolicyEffect.Allow, SecurityActions.FabricExecute, RiskAny: RiskClass.ReadOnly, Explanation: "Read-only execution may use a trusted eligible Fabric node."));
        rules.Add(new("fabric.execute-mutation", PolicyLayer.User, PolicyEffect.RequireApproval, SecurityActions.FabricExecute, RiskAny: RiskClass.Mutation, Explanation: "Remote mutation requires a bounded grant for the target node and mission."));
        rules.Add(new("fabric.transfer", PolicyLayer.User, PolicyEffect.RequireApproval, SecurityActions.FabricTransfer, RiskAny: RiskClass.Privileged, Explanation: "Artifact transfer requires classification and destination authorization."));
        rules.Add(new("fabric.membership", PolicyLayer.User, PolicyEffect.RequireApproval, "Fabric.*", RiskAny: RiskClass.Mutation, Explanation: "Fabric membership changes require explicit user authority."));
        rules.Add(new("user.artifact-approve", PolicyLayer.User, PolicyEffect.Allow, SecurityActions.ArtifactApprove, systemAndUser, Explanation: "The local user may approve an exact artifact revision; integration remains separately authorized."));
        rules.Add(new("external.artifact-publication", PolicyLayer.User, PolicyEffect.RequireApproval, SecurityActions.ArtifactPublish, Explanation: "External artifact publication requires authorization separate from review approval."));
        rules.Add(new("global.capability-read", PolicyLayer.Global, PolicyEffect.Allow, SecurityActions.CapabilityInvoke, RiskAny: RiskClass.ReadOnly, Explanation: "Registered read-only capability invocation is allowed."));
        rules.Add(new("specialist.builder-write", PolicyLayer.Specialist, preset == PolicyPreset.Conservative ? PolicyEffect.RequireApproval : PolicyEffect.Allow,
            SecurityActions.FileWrite, specialists, builder, Explanation: preset == PolicyPreset.Conservative ? "Local mutation requires approval in Conservative mode." : "Daedalus may modify the approved worktree."));
        rules.Add(new("specialist.builder-create", PolicyLayer.Specialist, preset == PolicyPreset.Conservative ? PolicyEffect.RequireApproval : PolicyEffect.Allow,
            SecurityActions.DirectoryCreate, specialists, builder, Explanation: "Daedalus may create resources only in the approved worktree."));
        rules.Add(new("local.process", PolicyLayer.User, preset == PolicyPreset.Conservative ? PolicyEffect.RequireApproval : PolicyEffect.Allow,
            SecurityActions.ProcessExecute, systemAndUser, Explanation: "Direct executable invocation is permitted for approved local development operations."));
        rules.Add(new("external.push", PolicyLayer.User, PolicyEffect.RequireApproval, SecurityActions.GitPush, Explanation: "Git push changes a remote repository and requires approval."));
        rules.Add(new("external.post", PolicyLayer.User, PolicyEffect.RequireApproval, SecurityActions.NetworkHttpPost, Explanation: "External writes require approval."));
        rules.Add(new("secret.use", PolicyLayer.User, PolicyEffect.RequireApproval, SecurityActions.SecretUse, Explanation: "Credential use requires a scoped grant."));
        rules.Add(new("destructive.ask", PolicyLayer.Global, PolicyEffect.RequireApproval, "*", RiskAny: RiskClass.Destructive, Explanation: "Destructive operations require explicit review."));
        return rules.ToImmutable();
    }
}

using Abraxius.Agents;
using Abraxius.Lattice;
using Abraxius.Protocol;
using Abraxius.Security;
using Abraxius.Presence;

namespace Abraxius.Runtime;

/// <summary>Authoritative runtime adapter: every local Lattice capability request is authorized immediately before execution.</summary>
internal sealed class SecurityLatticePolicy(ISecurityKernel security, IResourceCanonicalizer resources, ConfigurableSecurityApprovalSink approvals) : ILatticePolicy
{
    public async ValueTask<RuntimeError?> ValidateAsync(CapabilityRequest request, CapabilityDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var subject = ResolveSubject(request.SecurityContext);
        var (operation, resourceKind, external) = Map(request, descriptor);
        var workspace = request.SecurityContext?.GetValueOrDefault("workspace.root") ?? Environment.CurrentDirectory;
        var target = resourceKind is ResourceKind.File or ResourceKind.Directory && !Path.IsPathRooted(request.Target)
            ? Path.Combine(workspace, request.Target)
            : request.Target;
        if (resourceKind == ResourceKind.Capability) target = $"{descriptor.Name}/{request.Operation}";
        var authorization = await AuthorizationRequestFactory.CreateAsync(resources, subject, descriptor.Name, operation, resourceKind,
            target, new AuthorizationContext(WorkspaceRoot: workspace, Classification: DataClassification.Internal,
                AvailableSandbox: SandboxLevel.IsolatedWorkspace), request.Parameters, mutation: !descriptor.ReadOnly,
            external: external, cancellationToken: cancellationToken).ConfigureAwait(false);
        var decision = await security.AuthorizeAsync(authorization, cancellationToken).ConfigureAwait(false);
        if (decision.IsAllowed) return null;
        if (decision.Outcome == AuthorizationOutcome.RequireApproval)
            await approvals.RequestAsync(authorization, decision, cancellationToken).ConfigureAwait(false);
        return new RuntimeError(ErrorCategory.Policy,
            decision.Outcome == AuthorizationOutcome.RequireApproval ? "security_approval_required" : $"security_{decision.ReasonCode.ToString().ToLowerInvariant()}",
            decision.HumanExplanation,
            IsTransient: decision.Outcome == AuthorizationOutcome.RequireApproval);
    }

    private static SecuritySubject ResolveSubject(IReadOnlyDictionary<string, string>? context)
    {
        if (context is null || !context.TryGetValue("principal.type", out var type) || !type.Equals("Specialist", StringComparison.OrdinalIgnoreCase))
            return SecuritySubject.System("scheduler");
        if (!context.TryGetValue("principal.id", out var principal)) return SecuritySubject.System("scheduler");
        _ = Enum.TryParse<SpecialistRole>(context.GetValueOrDefault("specialist.role"), true, out var role);
        var instance = Guid.TryParse(context.GetValueOrDefault("agent.instance"), out var instanceId) ? new SpecialistInstanceId(instanceId) : (SpecialistInstanceId?)null;
        var mission = Guid.TryParse(context.GetValueOrDefault("mission.id"), out var missionId) ? new MissionId(missionId) : (MissionId?)null;
        var assignment = Guid.TryParse(context.GetValueOrDefault("assignment.id"), out var assignmentId) ? new AssignmentId(assignmentId) : (AssignmentId?)null;
        return new SecuritySubject(new PrincipalId(principal), PrincipalType.Specialist, MissionId: mission, AssignmentId: assignment,
            AgentInstanceId: instance, SpecialistRole: role);
    }

    private static (string Operation, ResourceKind Resource, bool External) Map(CapabilityRequest request, CapabilityDescriptor descriptor)
    {
        var operation = request.Operation.ToLowerInvariant();
        if (descriptor.Name.Equals("filesystem", StringComparison.OrdinalIgnoreCase)) return operation switch
        {
            "read_file" => (SecurityActions.FileRead, ResourceKind.File, false),
            "list_directory" or "search_files" => (SecurityActions.DirectoryList, ResourceKind.Directory, false),
            "write_file" or "patch_file" => (SecurityActions.FileWrite, ResourceKind.File, false),
            "delete_file" => (SecurityActions.FileDelete, ResourceKind.File, false),
            _ => (SecurityActions.CapabilityInvoke, ResourceKind.Capability, !descriptor.ReadOnly)
        };
        if (descriptor.Name.Equals("git", StringComparison.OrdinalIgnoreCase)) return operation switch
        {
            "status" or "diff" or "log" or "show" => (SecurityActions.GitRead, ResourceKind.GitRepository, false),
            "commit" => (SecurityActions.GitCommit, ResourceKind.GitRepository, false),
            "push" => (SecurityActions.GitPush, ResourceKind.GitRepository, true),
            "force_push" => (SecurityActions.GitForcePush, ResourceKind.GitRepository, true),
            _ => (SecurityActions.CapabilityInvoke, ResourceKind.Capability, !descriptor.ReadOnly)
        };
        if (descriptor.Name.Equals("agent-reach.web", StringComparison.OrdinalIgnoreCase) && operation == "read")
        {
            // Keep the network resource kind so SSRF/private-network policy
            // still applies before the read-only capability executes.
            return (SecurityActions.CapabilityInvoke, ResourceKind.Network, false);
        }
        return (SecurityActions.CapabilityInvoke, ResourceKind.Capability, false);
    }
}

internal sealed class PresenceSecurityApprovalSink(ISecurityApprovalService approvals, PresenceRuntime presence) : ISecurityApprovalSink
{
    public async ValueTask RequestAsync(AuthorizationRequest request, AuthorizationDecision decision, CancellationToken cancellationToken = default) =>
        _ = await approvals.RequestAsync(request, decision, await presence.CreateContextAsync(cancellationToken: cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
}

using System.Collections.Immutable;
using Abraxius.Plugin.Contracts;
using Abraxius.Security;

namespace Abraxius.Plugins;

public sealed class PluginPermissionService(IAuthorizationGrantStore grantStore, ISecurityKernel security, IResourceCanonicalizer canonicalizer)
{
    private static readonly Dictionary<string, (string Action, ResourceKind Kind, bool Mutation, bool External)> Mappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["project.read"] = (SecurityActions.FileRead, ResourceKind.Directory, false, false),
        ["project.write"] = (SecurityActions.FileWrite, ResourceKind.Directory, true, false),
        ["process.execute"] = (SecurityActions.ProcessExecute, ResourceKind.Process, false, false),
        ["network.http"] = (SecurityActions.NetworkHttpGet, ResourceKind.Network, false, true),
        ["secret.use"] = (SecurityActions.SecretUse, ResourceKind.Secret, false, false),
        ["background.events"] = (SecurityActions.CapabilityInvoke, ResourceKind.Capability, false, false),
        ["models.backend"] = (SecurityActions.CapabilityInvoke, ResourceKind.ModelProvider, false, false),
        ["compute.telemetry"] = (SecurityActions.CapabilityInvoke, ResourceKind.Capability, false, false),
        ["artifact.preview"] = (SecurityActions.ArtifactRead, ResourceKind.Artifact, false, false),
        ["ui.navigation"] = (SecurityActions.CapabilityInvoke, ResourceKind.Capability, false, false)
    };

    public static SecuritySubject Subject(PluginInstallation installation) => new(new PrincipalId($"plugin:{installation.Package.PluginId.Value}:{installation.Package.Version}"), PrincipalType.Plugin, PluginId: installation.Package.PluginId.Value);

    public AuthorizationGrant Approve(PluginInstallation installation, PluginPermissionGrant permission, DateTimeOffset expiresAt)
    {
        if (!installation.Manifest.Permissions.Any(item => item.Id.Equals(permission.PermissionId, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Undeclared plugin permission cannot be granted.");
        if (!Mappings.TryGetValue(permission.PermissionId, out var mapping)) throw new InvalidOperationException("Permission has no registered Security Kernel mapping.");
        var prefix = permission.ResourceScopes.FirstOrDefault() ?? "capability://none";
        return grantStore.Issue(new AuthorizationGrant(AuthorizationGrantId.New(), Subject(installation), ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, mapping.Action), prefix,
            GrantScope.Session, permission.GrantedAt, expiresAt, permission.GrantedBy, $"Reviewed plugin permission {permission.PermissionId}."));
    }

    public async ValueTask<AuthorizationDecision> AuthorizeAsync(PluginInstallation installation, string declaredPermission, string target, AuthorizationContext context, CancellationToken cancellationToken = default)
    {
        var declaration = installation.Manifest.Permissions.FirstOrDefault(item => item.Id.Equals(declaredPermission, StringComparison.OrdinalIgnoreCase));
        if (declaration is null) return Denied("Plugin requested authority not declared by its approved manifest.");
        var grant = installation.Grants.FirstOrDefault(item => item.PermissionId.Equals(declaredPermission, StringComparison.OrdinalIgnoreCase));
        if (grant is null) return Denied("Plugin permission was declared but not granted by the user.");
        if (!grant.ResourceScopes.Any(scope => target.StartsWith(scope, StringComparison.OrdinalIgnoreCase))) return Denied("Plugin target is outside its granted resource scope.");
        if (!Mappings.TryGetValue(declaredPermission, out var mapping)) return Denied("Plugin permission is not mapped to an executable Security Kernel action.");
        var request = await AuthorizationRequestFactory.CreateAsync(canonicalizer, Subject(installation), $"plugin/{installation.Package.PluginId.Value}/{declaredPermission}", mapping.Action, mapping.Kind, target, context,
            mutation: mapping.Mutation, external: mapping.External, minimumSandbox: ToSecurityLevel(installation.Manifest.MinimumSandboxLevel), cancellationToken: cancellationToken).ConfigureAwait(false);
        return await security.AuthorizeAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static SandboxLevel ToSecurityLevel(PluginSandboxRequirement value) => value switch
    {
        PluginSandboxRequirement.ProcessIsolation => SandboxLevel.RestrictedProcess,
        PluginSandboxRequirement.WorkspaceIsolation or PluginSandboxRequirement.NetworkIsolation => SandboxLevel.IsolatedWorkspace,
        PluginSandboxRequirement.StrongSandbox or PluginSandboxRequirement.WasiCapabilitySandbox => SandboxLevel.Container,
        _ => SandboxLevel.RestrictedProcess
    };
    private static AuthorizationDecision Denied(string reason) => new(AuthorizationDecisionId.New(), AuthorizationOutcome.Deny, AuthorizationReasonCode.DeniedPluginScope, reason, RiskClass.Privileged, PolicyRefs: ["plugin:declared-and-granted"]);
}

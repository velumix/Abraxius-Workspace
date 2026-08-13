using System.Collections.Immutable;
using Abraxius.Plugin.Contracts;

namespace Abraxius.Plugins;

public enum PluginPublisherTrust { Unknown, UserTrusted, RepositoryTrusted, FirstParty, Revoked }
public enum PluginSignatureState { Valid, NotSigned, Invalid, Unavailable }
public enum PluginLifecycleState { Discovered, Inspected, PendingApproval, Installed, Disabled, Starting, Running, Stopping, Crashed, Quarantined, Updating, Uninstalling, Incompatible }
public enum PluginHealthState { Starting, Healthy, Degraded, Unresponsive, Crashed, Quarantined, Stopped }
public enum PluginSandboxGuarantee { ReducedIsolation, ProcessIsolation, WorkspaceIsolation, NetworkIsolation, StrongSandbox, WasiCapabilitySandbox }

public sealed record PluginPackageIdentity(PluginPackageId PackageId, PluginId PluginId, PluginVersion Version, string Sha256, long Length);
public sealed record PluginSignatureResult(PluginSignatureState State, string? Signer, string Explanation);
public sealed record PluginPackageInspection(PluginPackageIdentity Identity, PluginManifest Manifest, PluginSignatureResult Signature, ImmutableArray<string> Errors, ImmutableArray<string> Warnings)
{
    public bool Valid => Errors.Length == 0 && Signature.State != PluginSignatureState.Invalid;
}

public sealed record PluginPermissionGrant(string PermissionId, ImmutableArray<string> ResourceScopes, DateTimeOffset GrantedAt, string GrantedBy);
public sealed record PluginPermissionDifference(ImmutableArray<PluginPermissionDeclaration> Added, ImmutableArray<PluginPermissionDeclaration> Removed, ImmutableArray<PluginPermissionDeclaration> Changed)
{
    public bool ExpandsAuthority => Added.Length > 0 || Changed.Length > 0;
}

public sealed record PluginInstallation(
    PluginInstallationId InstallationId,
    PluginPackageIdentity Package,
    PluginManifest Manifest,
    string PackageDirectory,
    PluginPublisherTrust PublisherTrust,
    PluginSignatureState Signature,
    PluginLifecycleState State,
    PluginHealthState Health,
    ImmutableArray<PluginPermissionGrant> Grants,
    DateTimeOffset InstalledAt,
    PluginHostId? HostId = null,
    string? LastError = null,
    int CrashCount = 0,
    DateTimeOffset? LastCrashAt = null,
    PluginSandboxGuarantee Sandbox = PluginSandboxGuarantee.ReducedIsolation);

public sealed record PluginValidationOptions(bool DeveloperMode = false, long MaximumPackageBytes = 2L * 1024 * 1024 * 1024, long MaximumManifestBytes = 1024 * 1024, int MaximumEntries = 20_000);
public sealed record PluginInstallRequest(string PackagePath, ImmutableArray<PluginPermissionGrant> ApprovedPermissions, PluginPublisherTrust PublisherTrust, bool DeveloperMode = false);
public sealed record PluginHostResourceBudget(long MemoryBytes = 512L * 1024 * 1024, double CpuCores = 1, long LogBytes = 4L * 1024 * 1024, int EventQueueCapacity = 512, int MaximumBackgroundJobs = 4);

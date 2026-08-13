using System.Collections.Immutable;
using Abraxius.Protocol;

namespace Abraxius.Platform;

public enum ExecutionPlacement
{
    Local,
    Remote,
    PermissionRequired,
    Restricted,
    Unavailable
}

public sealed record CapabilityRoute(
    CapabilityId Capability,
    ExecutionPlacement Placement,
    RemoteHostId? HostId = null,
    string? Reason = null);

public sealed record CapabilityResolution(
    CapabilityRoute Route,
    PlatformError? Error = null)
{
    public bool IsExecutable => Error is null && Route.Placement is ExecutionPlacement.Local or ExecutionPlacement.Remote;
}

public sealed class CapabilityResolver
{
    private readonly IPlatformEnvironment _local;
    private readonly ImmutableArray<RemoteCapabilityAdvertisement> _remoteHosts;

    public CapabilityResolver(
        IPlatformEnvironment local,
        IEnumerable<RemoteCapabilityAdvertisement>? remoteHosts = null)
    {
        _local = local ?? throw new ArgumentNullException(nameof(local));
        _remoteHosts = remoteHosts?.ToImmutableArray() ?? ImmutableArray<RemoteCapabilityAdvertisement>.Empty;
    }

    public CapabilityResolution Resolve(
        CapabilityId capability,
        bool allowRemote = true,
        bool preferRemote = false)
    {
        var localAvailability = _local.Capabilities.GetAvailability(capability);
        var remote = allowRemote ? FindRemote(capability) : null;
        if (preferRemote && remote is not null)
        {
            return new CapabilityResolution(new CapabilityRoute(capability, ExecutionPlacement.Remote, remote.HostId, "Remote capability preferred by execution budget."));
        }

        if (localAvailability == CapabilityAvailability.Available)
        {
            return new CapabilityResolution(new CapabilityRoute(capability, ExecutionPlacement.Local, Reason: "Capability is available on the local platform."));
        }

        if (remote is not null)
        {
            return new CapabilityResolution(new CapabilityRoute(capability, ExecutionPlacement.Remote, remote.HostId, "Capability is unavailable locally and advertised by a remote host."));
        }

        var placement = localAvailability switch
        {
            CapabilityAvailability.PermissionRequired => ExecutionPlacement.PermissionRequired,
            CapabilityAvailability.Restricted => ExecutionPlacement.Restricted,
            _ => ExecutionPlacement.Unavailable
        };
        var errorCode = placement switch
        {
            ExecutionPlacement.PermissionRequired => PlatformErrorCode.PermissionRequired,
            ExecutionPlacement.Restricted => PlatformErrorCode.PlatformServiceUnavailable,
            _ => PlatformErrorCode.CapabilityUnavailable
        };
        return new CapabilityResolution(
            new CapabilityRoute(capability, placement, Reason: "No executable local or remote capability was found."),
            new PlatformError(errorCode, $"Capability '{capability}' is not executable in the current environment."));
    }

    private RemoteCapabilityAdvertisement? FindRemote(CapabilityId capability) =>
        _remoteHosts
            .Where(host => host.Capabilities.Any(item => item.Capability == capability && item.Availability == CapabilityAvailability.Available))
            .OrderBy(host => host.HostId.Value)
            .FirstOrDefault();
}

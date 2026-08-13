using System.Net;
using System.Net.Sockets;

namespace Abraxius.Security;

public interface IResourceCanonicalizer
{
    ValueTask<SecurityResource> CanonicalizeAsync(ResourceKind kind, string target, CancellationToken cancellationToken = default);
    bool IsWithin(SecurityResource resource, string canonicalScope);
}

public interface INetworkAddressResolver
{
    ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default);
}

public sealed class SystemNetworkAddressResolver : INetworkAddressResolver
{
    public async ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default) =>
        await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
}

public sealed class ResourceCanonicalizer(INetworkAddressResolver? network = null) : IResourceCanonicalizer
{
    private readonly INetworkAddressResolver _network = network ?? new SystemNetworkAddressResolver();

    public async ValueTask<SecurityResource> CanonicalizeAsync(ResourceKind kind, string target, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(target)) return new(ResourceKind.Unknown, "unknown://malformed");

        try
        {
            return kind switch
            {
                ResourceKind.File or ResourceKind.Directory => CanonicalizePath(kind, target),
                ResourceKind.Network or ResourceKind.ModelProvider => await CanonicalizeNetworkAsync(kind, target, cancellationToken).ConfigureAwait(false),
                ResourceKind.Secret => CanonicalizeSecret(target),
                ResourceKind.GitRepository => CanonicalizeTypedUri(ResourceKind.GitRepository, "git", target),
                ResourceKind.Database => CanonicalizeTypedUri(ResourceKind.Database, "database", target),
                ResourceKind.Roblox => CanonicalizeTypedUri(ResourceKind.Roblox, "roblox", target),
                ResourceKind.RemoteNode => CanonicalizeTypedUri(ResourceKind.RemoteNode, "remote", target),
                ResourceKind.Process => CanonicalizeProcess(target),
                ResourceKind.Capability => CanonicalizeTypedUri(ResourceKind.Capability, "capability", target),
                ResourceKind.Artifact => CanonicalizeTypedUri(ResourceKind.Artifact, "artifact", target),
                _ => new(ResourceKind.Unknown, "unknown://unsupported")
            };
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or UriFormatException or SocketException)
        {
            return new(ResourceKind.Unknown, $"unknown://invalid/{Uri.EscapeDataString(target)}");
        }
    }

    public bool IsWithin(SecurityResource resource, string canonicalScope)
    {
        if (resource.Kind is ResourceKind.File or ResourceKind.Directory)
        {
            if (resource.LocalPath is null) return false;
            var scope = ResolveExistingLinks(Path.GetFullPath(canonicalScope));
            var candidate = ResolveExistingLinks(resource.LocalPath);
            var relative = Path.GetRelativePath(scope, candidate);
            return relative == "." || (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        }

        return resource.CanonicalUri.StartsWith(canonicalScope.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static SecurityResource CanonicalizePath(ResourceKind kind, string target)
    {
        string path;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile) path = uri.LocalPath;
        else path = target;
        path = ResolveExistingLinks(Path.GetFullPath(path));
        var exists = File.Exists(path) || Directory.Exists(path);
        return new(kind, new Uri(path).AbsoluteUri, path, Exists: exists);
    }

    private async ValueTask<SecurityResource> CanonicalizeNetworkAsync(ResourceKind kind, string target, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
            return new(ResourceKind.Unknown, "unknown://invalid-network");

        var builder = new UriBuilder(uri) { Host = uri.IdnHost.ToLowerInvariant(), Fragment = string.Empty };
        var addresses = IPAddress.TryParse(builder.Host, out var literal)
            ? [literal]
            : await _network.ResolveAsync(builder.Host, cancellationToken).ConfigureAwait(false);
        var internalNetwork = addresses.Any(IsInternalAddress);
        return new(kind, builder.Uri.AbsoluteUri, builder.Host, builder.Host, builder.Port, IsInternalNetwork: internalNetwork, Exists: true);
    }

    private static SecurityResource CanonicalizeSecret(string target)
    {
        if (!SecretReference.TryParse(target, out var reference)) return new(ResourceKind.Unknown, "unknown://invalid-secret");
        var uri = new Uri(reference.Value);
        return new(ResourceKind.Secret, reference.Value, Host: uri.Host, Scope: uri.AbsolutePath.Trim('/'), Exists: true);
    }

    private static SecurityResource CanonicalizeTypedUri(ResourceKind kind, string scheme, string target)
    {
        var value = target.Contains("://", StringComparison.Ordinal) ? target : $"{scheme}://{target.TrimStart('/')}";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(uri.Host))
            return new(ResourceKind.Unknown, "unknown://invalid-resource");
        var builder = new UriBuilder(uri) { Host = uri.Host.ToLowerInvariant(), Fragment = string.Empty };
        return new(kind, builder.Uri.AbsoluteUri, Host: builder.Host, Scope: builder.Path.Trim('/'), Exists: true);
    }

    private static SecurityResource CanonicalizeProcess(string target)
    {
        if (target.IndexOfAny(['\0', '\r', '\n']) >= 0) return new(ResourceKind.Unknown, "unknown://invalid-process");
        var executable = Path.IsPathRooted(target) ? Path.GetFullPath(target) : target.Trim();
        return new(ResourceKind.Process, $"process://local/{Uri.EscapeDataString(executable)}", executable, Host: "local", Exists: Path.IsPathRooted(executable) && File.Exists(executable));
    }

    internal static string ResolveExistingLinks(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? throw new ArgumentException("Path has no root.", nameof(path));
        var current = root;
        foreach (var segment in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo? info = Directory.Exists(current) ? new DirectoryInfo(current) : File.Exists(current) ? new FileInfo(current) : null;
            if (info?.LinkTarget is not null)
            {
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is not null) current = Path.GetFullPath(resolved.FullName);
            }
        }
        return Path.GetFullPath(current);
    }

    internal static bool IsInternalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 10 || bytes[0] == 127 || (bytes[0] == 169 && bytes[1] == 254) ||
                   (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 100 && bytes[1] is >= 64 and <= 127) || (bytes[0] == 0) || bytes[0] >= 224;
        }
        if (address.IsIPv4MappedToIPv6) return IsInternalAddress(address.MapToIPv4());
        var ipv6 = address.GetAddressBytes();
        return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (ipv6[0] & 0xfe) == 0xfc;
    }
}

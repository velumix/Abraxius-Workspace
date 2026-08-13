using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Abraxius.Plugin.Contracts;

namespace Abraxius.Plugins;

public interface IPluginStore
{
    string RootPath { get; }
    ValueTask<PluginInstallation> InstallAsync(PluginInstallRequest request, PluginPackageInspection inspection, CancellationToken cancellationToken = default);
    ValueTask RemovePayloadAsync(PluginInstallation installation, CancellationToken cancellationToken = default);
}

public sealed class FilePluginStore(string rootPath) : IPluginStore
{
    public string RootPath { get; } = Path.GetFullPath(rootPath);

    public async ValueTask<PluginInstallation> InstallAsync(PluginInstallRequest request, PluginPackageInspection inspection, CancellationToken cancellationToken = default)
    {
        if (!inspection.Valid) throw new InvalidOperationException("An invalid plugin package cannot be installed.");
        var approved = request.ApprovedPermissions.Select(static grant => grant.PermissionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = inspection.Manifest.Permissions.Where(static permission => permission.Required).Where(permission => !approved.Contains(permission.Id)).Select(static permission => permission.Id).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"Required plugin permissions were not reviewed: {string.Join(", ", missing)}.");
        var packageDirectory = Path.Combine(RootPath, "packages", inspection.Identity.PluginId.Value, inspection.Identity.Version.ToString(), inspection.Identity.Sha256);
        var payloadDirectory = Path.Combine(packageDirectory, "payload");
        if (!Directory.Exists(payloadDirectory))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(packageDirectory)!);
            var staging = packageDirectory + $".staging-{Guid.NewGuid():N}";
            Directory.CreateDirectory(staging);
            try
            {
                var payloadStaging = Path.Combine(staging, "payload");
                Directory.CreateDirectory(payloadStaging);
                await ExtractAsync(request.PackagePath, payloadStaging, cancellationToken).ConfigureAwait(false);
                var packageCopy = Path.Combine(staging, "package.nupkg");
                await CopyAsync(request.PackagePath, packageCopy, cancellationToken).ConfigureAwait(false);
                if (Directory.Exists(packageDirectory)) throw new IOException("Immutable plugin package directory appeared during activation.");
                Directory.Move(staging, packageDirectory);
            }
            catch
            {
                TryDelete(staging);
                throw;
            }
        }
        return new(PluginInstallationId.New(), inspection.Identity, inspection.Manifest, payloadDirectory, request.PublisherTrust,
            inspection.Signature.State, PluginLifecycleState.Installed, PluginHealthState.Stopped, request.ApprovedPermissions,
            DateTimeOffset.UtcNow, Sandbox: PluginSandboxGuarantee.ReducedIsolation);
    }

    public ValueTask RemovePayloadAsync(PluginInstallation installation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Installed package payloads are immutable. Registry removal only detaches the active installation;
        // package garbage collection is a separate, reviewable maintenance operation.
        return ValueTask.CompletedTask;
    }

    private static async Task ExtractAsync(string packagePath, string target, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(packagePath);
        long inflated = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            if (!PluginManifestValidator.IsSafeRelativePath(entry.FullName)) throw new InvalidDataException($"Unsafe package path '{entry.FullName}'.");
            inflated = checked(inflated + entry.Length);
            if (inflated > 8L * 1024 * 1024 * 1024) throw new InvalidDataException("Expanded package exceeds the configured safety limit.");
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(Path.GetFullPath(target) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("Package path escapes staging.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open(); await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(output, 128 * 1024, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, 1024 * 1024, cancellationToken).ConfigureAwait(false);
    }
    private static void TryDelete(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

public interface IPluginRegistry
{
    ImmutableArray<PluginInstallation> Installations { get; }
    PluginInstallation? Find(PluginId id, PluginVersion? version = null);
    ValueTask UpsertAsync(PluginInstallation installation, CancellationToken cancellationToken = default);
    ValueTask RemoveAsync(PluginInstallationId id, CancellationToken cancellationToken = default);
    ValueTask LoadAsync(CancellationToken cancellationToken = default);
}

public sealed class FilePluginRegistry(string path) : IPluginRegistry, IDisposable
{
    private sealed record RegistrySnapshot(ImmutableArray<PluginInstallation> Items);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private RegistrySnapshot _snapshot = new([]);
    public ImmutableArray<PluginInstallation> Installations => Volatile.Read(ref _snapshot).Items;
    public PluginInstallation? Find(PluginId id, PluginVersion? version = null) => Installations.Where(item => item.Package.PluginId == id && (version is null || item.Package.Version == version.Value)).OrderByDescending(static item => item.Package.Version).FirstOrDefault();

    public async ValueTask LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var values = await JsonSerializer.DeserializeAsync(stream, PluginPersistenceJsonContext.Default.PluginInstallationArray, cancellationToken).ConfigureAwait(false);
            Interlocked.Exchange(ref _snapshot, new RegistrySnapshot(values?.ToImmutableArray() ?? []));
        }
        finally { _gate.Release(); }
    }

    public async ValueTask UpsertAsync(PluginInstallation installation, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = Installations.RemoveAll(item => item.InstallationId == installation.InstallationId).Add(installation);
            Interlocked.Exchange(ref _snapshot, new RegistrySnapshot(next));
            await PersistAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask RemoveAsync(PluginInstallationId id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { var next = Installations.RemoveAll(item => item.InstallationId == id); Interlocked.Exchange(ref _snapshot, new RegistrySnapshot(next)); await PersistAsync(next, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static async ValueTask WriteAsync(string destination, ImmutableArray<PluginInstallation> values, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, values.ToArray(), PluginPersistenceJsonContext.Default.PluginInstallationArray, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
    private async ValueTask PersistAsync(ImmutableArray<PluginInstallation> values, CancellationToken cancellationToken)
    {
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temporary = full + $".tmp-{Guid.NewGuid():N}";
        await WriteAsync(temporary, values, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, full, true);
    }
    public void Dispose() => _gate.Dispose();
}

[JsonSerializable(typeof(PluginInstallation[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
internal sealed partial class PluginPersistenceJsonContext : JsonSerializerContext;

public static class PluginPermissionDiff
{
    public static PluginPermissionDifference Compare(PluginManifest current, PluginManifest candidate)
    {
        var old = current.Permissions.ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
        var next = candidate.Permissions.ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
        var added = next.Where(item => !old.ContainsKey(item.Key)).Select(static item => item.Value).ToImmutableArray();
        var removed = old.Where(item => !next.ContainsKey(item.Key)).Select(static item => item.Value).ToImmutableArray();
        var changed = next.Where(item => old.TryGetValue(item.Key, out var before) && !Equivalent(before, item.Value)).Select(static item => item.Value).ToImmutableArray();
        return new(added, removed, changed);
    }
    private static bool Equivalent(PluginPermissionDeclaration left, PluginPermissionDeclaration right) => left.Risk == right.Risk && left.Required == right.Required && left.ResourceScopes.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(right.ResourceScopes);
}

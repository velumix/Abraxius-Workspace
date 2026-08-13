using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Abraxius.Plugin.Contracts;

namespace Abraxius.Plugins;

public interface IPluginManifestParser { ValueTask<PluginManifest> ParseAsync(Stream content, long maximumBytes, CancellationToken cancellationToken = default); }

public sealed class StaticPluginManifestParser : IPluginManifestParser
{
    public async ValueTask<PluginManifest> ParseAsync(Stream content, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.CanSeek && content.Length > maximumBytes) throw new InvalidDataException("Plugin manifest exceeds the configured size limit.");
        await using var bounded = new BoundedReadStream(content, maximumBytes);
        var manifest = await JsonSerializer.DeserializeAsync(bounded, PluginContractJsonContext.Default.PluginManifest, cancellationToken).ConfigureAwait(false);
        if (manifest is null) throw new InvalidDataException("Plugin manifest is empty or invalid JSON.");
        // System.Text.Json preserves a default ImmutableArray when an optional property is absent.
        // Normalize at the static-data boundary so persistence and comparison never encounter the
        // non-enumerable default value.
        return manifest with { Dependencies = manifest.SafeDependencies };
    }
}

public sealed class PluginManifestValidator
{
    private readonly HashSet<int> _supportedSchemas = [1];
    public ImmutableArray<string> Validate(PluginManifest manifest)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        if (!_supportedSchemas.Contains(manifest.SchemaVersion)) errors.Add($"Unsupported manifest schema {manifest.SchemaVersion}.");
        try { _ = manifest.PluginId; } catch (ArgumentException exception) { errors.Add(exception.Message); }
        if (!PluginVersion.TryParse(manifest.Version, out _)) errors.Add("Plugin version must be SemVer major.minor.patch.");
        if (manifest.Requires.PluginApi.Major != PluginApiVersion.Current.Major) errors.Add($"Plugin API {manifest.Requires.PluginApi} is incompatible with host API {PluginApiVersion.Current}.");
        if (string.IsNullOrWhiteSpace(manifest.Name) || string.IsNullOrWhiteSpace(manifest.Publisher)) errors.Add("Plugin name and publisher are required.");
        if (manifest.Entrypoints.Length == 0 && manifest.Contributions.Length > 0) errors.Add("Executable contributions require an entrypoint.");
        foreach (var entrypoint in manifest.Entrypoints)
        {
            if (entrypoint.Tier == PluginExecutionTier.TrustedInProcess) errors.Add("TrustedInProcess entrypoints are not accepted by the ordinary third-party install pipeline.");
            if (!IsSafeRelativePath(entrypoint.Path)) errors.Add($"Entrypoint path '{entrypoint.Path}' is not a safe package-relative path.");
            if (entrypoint.Tier == PluginExecutionTier.ManagedOutOfProcess && string.IsNullOrWhiteSpace(entrypoint.Type)) errors.Add("Managed entrypoints require an explicit generated entry type.");
        }
        var permissionIds = manifest.Permissions.Select(static item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (permissionIds.Count != manifest.Permissions.Length) errors.Add("Permission IDs must be unique.");
        var contributionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var contribution in manifest.Contributions)
        {
            if (!contributionIds.Add(contribution.Id)) errors.Add($"Duplicate contribution ID '{contribution.Id}'.");
            if (contribution.RequiredPermissions.Any(permission => !permissionIds.Contains(permission))) errors.Add($"Contribution '{contribution.Id}' references an undeclared permission.");
        }
        var hardDependencies = manifest.SafeDependencies.Where(static item => !item.Optional).ToArray();
        if (hardDependencies.Any(item => item.PluginId.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase))) errors.Add("A plugin cannot depend on itself.");
        return errors.ToImmutable();
    }

    public static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return false;
        var normalized = value.Replace('\\', '/');
        return !normalized.Split('/').Any(static segment => segment is ".." or "" || segment.Contains(':', StringComparison.Ordinal));
    }
}

public interface IPluginPackageSignatureVerifier
{
    ValueTask<PluginSignatureResult> VerifyAsync(string packagePath, string packageHash, CancellationToken cancellationToken = default);
}

public sealed class PolicyPluginPackageSignatureVerifier(IReadOnlyDictionary<string, string>? trustedPackageHashes = null) : IPluginPackageSignatureVerifier
{
    private readonly IReadOnlyDictionary<string, string> _trusted = trustedPackageHashes ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public ValueTask<PluginSignatureResult> VerifyAsync(string packagePath, string packageHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var file = Path.GetFileName(packagePath);
        return ValueTask.FromResult(_trusted.TryGetValue(file, out var expected)
            ? CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(packageHash))
                ? new PluginSignatureResult(PluginSignatureState.Valid, "configured-trusted-source", "Package hash matches the configured signed-source assertion.")
                : new PluginSignatureResult(PluginSignatureState.Invalid, null, "Package differs from the trusted-source hash.")
            : new PluginSignatureResult(PluginSignatureState.NotSigned, null, "No verifiable package signature assertion was supplied."));
    }
}

public interface IPluginPackageInspector { ValueTask<PluginPackageInspection> InspectAsync(string packagePath, PluginValidationOptions options, CancellationToken cancellationToken = default); }

public sealed class PluginPackageInspector(IPluginManifestParser parser, PluginManifestValidator validator, IPluginPackageSignatureVerifier signatureVerifier) : IPluginPackageInspector
{
    public async ValueTask<PluginPackageInspection> InspectAsync(string packagePath, PluginValidationOptions options, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(packagePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Plugin package was not found.", fullPath);
        if (info.Length > options.MaximumPackageBytes) throw new InvalidDataException("Plugin package exceeds the configured size limit.");
        string hash;
        await using (var input = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        }
        PluginManifest manifest;
        var archiveErrors = ImmutableArray.CreateBuilder<string>();
        using (var archive = ZipFile.OpenRead(fullPath))
        {
            if (archive.Entries.Count > options.MaximumEntries) archiveErrors.Add("Plugin package contains too many entries.");
            foreach (var entry in archive.Entries)
            {
                if (!PluginManifestValidator.IsSafeRelativePath(entry.FullName.TrimEnd('/'))) archiveErrors.Add($"Unsafe package path '{entry.FullName}'.");
            }
            var manifestEntry = archive.GetEntry("abraxius.plugin.json") ?? throw new InvalidDataException("Package does not contain abraxius.plugin.json at its root.");
            await using var stream = manifestEntry.Open();
            manifest = await parser.ParseAsync(stream, options.MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
        }
        var errors = validator.Validate(manifest).AddRange(archiveErrors);
        var signature = await signatureVerifier.VerifyAsync(fullPath, hash, cancellationToken).ConfigureAwait(false);
        if (signature.State == PluginSignatureState.NotSigned && !options.DeveloperMode) errors = errors.Add("Unsigned plugins require explicit Developer Mode policy.");
        if (signature.State == PluginSignatureState.Invalid) errors = errors.Add("Package signature or integrity verification failed.");
        var identity = new PluginPackageIdentity(PluginPackageId.New(), manifest.PluginId, manifest.PluginVersion, hash, info.Length);
        var warnings = signature.State == PluginSignatureState.NotSigned ? ImmutableArray.Create("Unsigned plugin: publisher origin is not cryptographically established.") : ImmutableArray<string>.Empty;
        return new(identity, manifest, signature, errors, warnings);
    }
}

internal sealed class BoundedReadStream(Stream inner, long maximum) : Stream
{
    private long _read;
    public override bool CanRead => inner.CanRead; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) { var read = inner.Read(buffer, offset, count); Ensure(read); return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); Ensure(read); return read; }
    private void Ensure(int count) { _read += count; if (_read > maximum) throw new InvalidDataException("Input exceeded the configured bound."); }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { await inner.DisposeAsync().ConfigureAwait(false); await base.DisposeAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
}

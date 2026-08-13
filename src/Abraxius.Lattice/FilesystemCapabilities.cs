using System.Text;
using System.Globalization;
using Abraxius.Core;
using Abraxius.Protocol;

namespace Abraxius.Lattice;

public sealed class FilesystemCapability : ILatticeCapability
{
    private readonly string _root;
    private readonly IEvidenceStore _evidenceStore;

    public FilesystemCapability(string root, IEvidenceStore evidenceStore)
    {
        _root = Path.GetFullPath(root);
        _evidenceStore = evidenceStore;
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        "filesystem",
        "Read-only bounded filesystem inspection.",
        "{ target: relative path, operation: list_directory | read_file | search_files }",
        ExecutorKind.Io,
        true,
        ["list_directory", "read_file", "search_files"]);

    public async ValueTask<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken cancellationToken = default)
    {
        var path = Resolve(request.Target);
        switch (request.Operation.ToLowerInvariant())
        {
            case "list_directory":
                var entries = Directory.EnumerateFileSystemEntries(path)
                    .Take(500)
                    .Select(Path.GetFileName)
                    .Where(static name => name is not null)
                    .Cast<string>()
                    .ToArray();
                return await StoreTextAsync("directory-listing", request.Target, string.Join(Environment.NewLine, entries), cancellationToken).ConfigureAwait(false);

            case "read_file":
                var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (bytes.LongLength > 10 * 1024 * 1024)
                {
                    return new CapabilityResult(false, null, [], Error: new RuntimeError(ErrorCategory.Tool, "file_too_large", "Read-only file reads are bounded to 10 MiB."));
                }

                return await StoreTextAsync("file", request.Target, Encoding.UTF8.GetString(bytes), cancellationToken).ConfigureAwait(false);

            case "search_files":
                var query = request.Parameters?.GetValueOrDefault("query") ?? string.Empty;
                var matches = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Take(1000)
                    .Where(file => File.ReadLines(file).Take(500).Any(line => line.Contains(query, StringComparison.OrdinalIgnoreCase)))
                    .Select(file => Path.GetRelativePath(_root, file))
                    .ToArray();
                return await StoreTextAsync("search", request.Target, string.Join(Environment.NewLine, matches), cancellationToken).ConfigureAwait(false);

            default:
                return new CapabilityResult(false, null, [], Error: new RuntimeError(ErrorCategory.Tool, "unsupported_operation", $"Unsupported filesystem operation '{request.Operation}'."));
        }
    }

    private async ValueTask<CapabilityResult> StoreTextAsync(string kind, string name, string text, CancellationToken cancellationToken)
    {
        var reference = await _evidenceStore.StoreAsync(new EvidenceInput(kind, name, Encoding.UTF8.GetBytes(text), "text/plain"), cancellationToken).ConfigureAwait(false);
        return new CapabilityResult(true, $"{kind} completed.", [reference.Id], new Dictionary<string, string> { ["size"] = reference.SizeBytes.ToString(CultureInfo.InvariantCulture) });
    }

    private string Resolve(string target)
    {
        var candidate = Path.GetFullPath(Path.Combine(_root, target ?? string.Empty));
        if (!candidate.StartsWith(_root, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The requested path is outside the capability root.");
        }

        return candidate;
    }
}

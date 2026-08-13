using System.Collections.Immutable;

namespace Abraxius.Artifacts;

public sealed record ArtifactDiffOptions(int ContextLines = 3, int MaximumLines = 100_000, long MaximumTextBytes = 64L * 1024 * 1024);
public sealed record ArtifactDiffDocument(string Provider, ImmutableArray<DiffHunk> Hunks, int Added, int Deleted, bool Truncated, string Summary, ImmutableDictionary<string, string>? Metadata = null);

public interface IArtifactDiffProvider
{
    bool CanHandle(ArtifactRevision baseline, ArtifactRevision candidate);
    ValueTask<ArtifactDiffDocument> CompareAsync(ArtifactRevision baseline, Stream baselineContent, ArtifactRevision candidate, Stream candidateContent, ArtifactDiffOptions options, CancellationToken cancellationToken = default);
}

public interface IArtifactPreviewProvider
{
    bool CanHandle(ArtifactRevision revision);
    ValueTask<ArtifactPreview> CreateAsync(ArtifactRevision revision, Stream content, ArtifactPreviewOptions options, CancellationToken cancellationToken = default);
}

public sealed record ArtifactPreviewOptions(long MaximumBytes = 2 * 1024 * 1024, int MaximumLines = 20_000);
public sealed record ArtifactPreview(string Provider, string MediaType, string? Text, ImmutableDictionary<string, string> Metadata, bool Truncated, bool IsSafeRenderedContent = false);

public sealed class ArtifactDiffProviderRegistry(IEnumerable<IArtifactDiffProvider> providers)
{
    private readonly ImmutableArray<IArtifactDiffProvider> _providers = providers.ToImmutableArray();
    public IArtifactDiffProvider Resolve(ArtifactRevision baseline, ArtifactRevision candidate) => _providers.FirstOrDefault(item => item.CanHandle(baseline, candidate)) ?? new BinaryMetadataDiffProvider();
}

public sealed class LinearTextDiffProvider : IArtifactDiffProvider
{
    public bool CanHandle(ArtifactRevision baseline, ArtifactRevision candidate) => IsText(baseline.Content.MediaType) && IsText(candidate.Content.MediaType);

    public async ValueTask<ArtifactDiffDocument> CompareAsync(ArtifactRevision baseline, Stream baselineContent, ArtifactRevision candidate, Stream candidateContent, ArtifactDiffOptions options, CancellationToken cancellationToken = default)
    {
        if (baseline.Content.Length > options.MaximumTextBytes || candidate.Content.Length > options.MaximumTextBytes)
            return new(nameof(LinearTextDiffProvider), [], 0, 0, true, "Text diff exceeds the configured streaming review limit.");
        var oldLines = await ReadLinesAsync(baselineContent, options.MaximumLines, cancellationToken).ConfigureAwait(false);
        var newLines = await ReadLinesAsync(candidateContent, options.MaximumLines, cancellationToken).ConfigureAwait(false);
        var prefix = 0; while (prefix < oldLines.Lines.Count && prefix < newLines.Lines.Count && oldLines.Lines[prefix] == newLines.Lines[prefix]) prefix++;
        var suffix = 0; while (suffix < oldLines.Lines.Count - prefix && suffix < newLines.Lines.Count - prefix && oldLines.Lines[^(suffix + 1)] == newLines.Lines[^(suffix + 1)]) suffix++;
        if (prefix == oldLines.Lines.Count && prefix == newLines.Lines.Count)
            return new(nameof(LinearTextDiffProvider), [], 0, 0, oldLines.Truncated || newLines.Truncated, "No textual changes.");
        var contextStart = Math.Max(0, prefix - options.ContextLines);
        var oldEnd = oldLines.Lines.Count - suffix; var newEnd = newLines.Lines.Count - suffix;
        var lines = ImmutableArray.CreateBuilder<DiffLine>();
        for (var i = contextStart; i < prefix; i++) lines.Add(new(i + 1, i + 1, oldLines.Lines[i], ' '));
        for (var i = prefix; i < oldEnd; i++) lines.Add(new(i + 1, null, oldLines.Lines[i], '-'));
        for (var i = prefix; i < newEnd; i++) lines.Add(new(null, i + 1, newLines.Lines[i], '+'));
        for (var i = 0; i < Math.Min(options.ContextLines, suffix); i++)
        {
            var oldIndex = oldEnd + i; var newIndex = newEnd + i;
            lines.Add(new(oldIndex + 1, newIndex + 1, oldLines.Lines[oldIndex], ' '));
        }
        var hunk = new DiffHunk(contextStart + 1, oldEnd - contextStart, contextStart + 1, newEnd - contextStart, lines.ToImmutable());
        return new(nameof(LinearTextDiffProvider), [hunk], oldEnd - prefix, newEnd - prefix, oldLines.Truncated || newLines.Truncated,
            $"+{newEnd - prefix} -{oldEnd - prefix}");
    }

    private static async ValueTask<(List<string> Lines, bool Truncated)> ReadLinesAsync(Stream stream, int maximum, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var lines = new List<string>(Math.Min(maximum, 4096));
        while (lines.Count < maximum && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line) lines.Add(line);
        return (lines, await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null);
    }
    private static bool IsText(string mediaType) => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase);
}

public sealed class BinaryMetadataDiffProvider : IArtifactDiffProvider
{
    public bool CanHandle(ArtifactRevision baseline, ArtifactRevision candidate) => true;
    public ValueTask<ArtifactDiffDocument> CompareAsync(ArtifactRevision baseline, Stream baselineContent, ArtifactRevision candidate, Stream candidateContent, ArtifactDiffOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = ImmutableDictionary<string, string>.Empty
            .Add("old.hash", baseline.Content.ContentHash).Add("new.hash", candidate.Content.ContentHash)
            .Add("old.size", baseline.Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)).Add("new.size", candidate.Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Add("old.mediaType", baseline.Content.MediaType).Add("new.mediaType", candidate.Content.MediaType);
        return ValueTask.FromResult(new ArtifactDiffDocument(nameof(BinaryMetadataDiffProvider), [], 0, 0, false, baseline.Content.ContentHash == candidate.Content.ContentHash ? "Binary content is unchanged." : "Binary content differs; metadata comparison only.", metadata));
    }
}

public sealed class SafeTextPreviewProvider : IArtifactPreviewProvider
{
    public bool CanHandle(ArtifactRevision revision) => revision.Content.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || revision.Content.MediaType.Contains("json", StringComparison.OrdinalIgnoreCase);
    public async ValueTask<ArtifactPreview> CreateAsync(ArtifactRevision revision, Stream content, ArtifactPreviewOptions options, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, leaveOpen: true);
        var builder = new System.Text.StringBuilder(); var lines = 0; var truncated = false;
        while (lines < options.MaximumLines && builder.Length < options.MaximumBytes && await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line) { builder.AppendLine(line); lines++; }
        if (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null) truncated = true;
        return new(nameof(SafeTextPreviewProvider), revision.Content.MediaType, builder.ToString(), revision.Content.SafeMetadata, truncated, IsSafeRenderedContent: false);
    }
}

public sealed class SafeMetadataPreviewProvider : IArtifactPreviewProvider
{
    public bool CanHandle(ArtifactRevision revision) => true;
    public ValueTask<ArtifactPreview> CreateAsync(ArtifactRevision revision, Stream content, ArtifactPreviewOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = revision.Content.SafeMetadata.SetItem("hash", revision.Content.ContentHash).SetItem("length", revision.Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return ValueTask.FromResult(new ArtifactPreview(nameof(SafeMetadataPreviewProvider), revision.Content.MediaType, null, metadata, false));
    }
}

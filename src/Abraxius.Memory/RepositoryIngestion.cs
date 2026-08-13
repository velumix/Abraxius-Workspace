using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Abraxius.Memory;

public sealed record RepositoryIngestionOptions(
    string RootPath,
    string ProjectKey,
    int MaxConcurrency = 2,
    long MaxFileBytes = 1_048_576,
    int MaxChunkCharacters = 4_000,
    bool GenerateEmbeddings = true,
    IReadOnlySet<string>? IncludedExtensions = null,
    IReadOnlySet<string>? IgnoredDirectoryNames = null);

public sealed record IngestionProgress(int Discovered, int Processed, int Indexed, int Skipped, int Failed, string? CurrentPath = null);

public sealed record RepositoryIngestionResult(
    int Discovered,
    int Indexed,
    int Skipped,
    int Failed,
    long BytesRead,
    TimeSpan Duration,
    IReadOnlyList<string> Errors);

public sealed record GitMetadata(string? Branch, string? Commit);

public interface IGitMetadataProvider
{
    GitMetadata Read(string repositoryRoot);
}

public sealed class RepositoryGitMetadataProvider : IGitMetadataProvider
{
    public GitMetadata Read(string repositoryRoot)
    {
        try
        {
            var gitPath = Path.Combine(repositoryRoot, ".git");
            if (File.Exists(gitPath))
            {
                var pointer = File.ReadAllText(gitPath).Trim();
                if (pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)) gitPath = Path.GetFullPath(Path.Combine(repositoryRoot, pointer[7..].Trim()));
            }

            if (!Directory.Exists(gitPath)) return new GitMetadata(null, null);
            var head = File.ReadAllText(Path.Combine(gitPath, "HEAD")).Trim();
            if (head.StartsWith("ref: ", StringComparison.Ordinal))
            {
                var reference = head[5..];
                var branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference[11..] : null;
                var refPath = Path.Combine(gitPath, reference.Replace('/', Path.DirectorySeparatorChar));
                var commit = File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : ReadPackedReference(gitPath, reference);
                return new GitMetadata(branch, commit);
            }

            return new GitMetadata(null, head.Length == 0 ? null : head);
        }
        catch (IOException) { return new GitMetadata(null, null); }
        catch (UnauthorizedAccessException) { return new GitMetadata(null, null); }
    }

    private static string? ReadPackedReference(string gitPath, string reference)
    {
        var packed = Path.Combine(gitPath, "packed-refs");
        if (!File.Exists(packed)) return null;
        foreach (var line in File.ReadLines(packed))
        {
            if (line.StartsWith('#') || line.StartsWith('^')) continue;
            var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[1], reference, StringComparison.Ordinal)) return parts[0];
        }

        return null;
    }
}

public sealed record CodeSymbol(string Name, string Kind, int StartLine, int EndLine);

public interface ICodeStructureProvider
{
    IReadOnlyList<CodeSymbol> Extract(string content, string extension);
}

public sealed class BasicCodeStructureProvider : ICodeStructureProvider
{
    private static readonly Regex SymbolPattern = new(
        @"(?m)^(?:\s*)(?:(?:public|private|protected|internal|static|async|abstract|sealed|partial|override|virtual|readonly|export|pub)\s+)*(class|record|struct|interface|enum|module|namespace|fn|function|def|type|trait|impl|property)\s+([A-Za-z_][A-Za-z0-9_<>.]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<CodeSymbol> Extract(string content, string extension)
    {
        var symbols = new List<CodeSymbol>();
        foreach (Match match in SymbolPattern.Matches(content))
        {
            var kind = match.Groups[1].Value;
            var name = match.Groups[2].Value;
            var line = 1 + content.AsSpan(0, match.Index).Count('\n');
            symbols.Add(new CodeSymbol(name, kind, line, line));
        }

        if (extension.Equals(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            var classMatch = Regex.Match(content, @"x:Class\s*=\s*""([^""]+)""", RegexOptions.CultureInvariant);
            if (classMatch.Success) symbols.Add(new CodeSymbol(classMatch.Groups[1].Value, "xaml-class", 1, 1));
        }

        return symbols;
    }
}

public sealed class RepositoryIngestionService(
    IMemoryStore store,
    IEmbeddingProvider? embeddings = null,
    ICodeStructureProvider? structure = null,
    IGitMetadataProvider? git = null)
{
    private readonly IMemoryStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IEmbeddingProvider _embeddings = embeddings ?? new HashEmbeddingProvider();
    private readonly ICodeStructureProvider _structure = structure ?? new BasicCodeStructureProvider();
    private readonly IGitMetadataProvider _git = git ?? new RepositoryGitMetadataProvider();

    public async ValueTask<RepositoryIngestionResult> IngestAsync(RepositoryIngestionOptions options, IProgress<IngestionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Directory.Exists(options.RootPath)) throw new DirectoryNotFoundException(options.RootPath);
        await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var gitMetadata = _git.Read(options.RootPath);
        var started = Stopwatch.GetTimestamp();
        var ignored = options.IgnoredDirectoryNames ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", "bin", "obj", ".vs", "node_modules", ".idea", "packages" };
        var extensions = options.IncludedExtensions ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".cs", ".rs", ".ts", ".tsx", ".js", ".jsx", ".py", ".lua", ".luau", ".json", ".yaml", ".yml", ".md", ".xaml", ".xml", ".toml" };
        var files = Directory.EnumerateFiles(options.RootPath, "*", SearchOption.AllDirectories)
            .Where(path => extensions.Contains(Path.GetExtension(path)) && !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(ignored.Contains))
            .ToArray();
        var processed = 0; var indexed = 0; var skipped = 0; var failed = 0; long bytes = 0;
        var errors = new ConcurrentQueue<string>();
        progress?.Report(new IngestionProgress(files.Length, 0, 0, 0, 0));
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(options.MaxConcurrency, 1, 16), CancellationToken = cancellationToken }, async (path, token) =>
        {
            try
            {
                var relative = Path.GetRelativePath(options.RootPath, path).Replace(Path.DirectorySeparatorChar, '/');
                var info = new FileInfo(path);
                if (info.Length > options.MaxFileBytes) { Interlocked.Increment(ref skipped); return; }
                var text = await File.ReadAllTextAsync(path, token).ConfigureAwait(false);
                Interlocked.Add(ref bytes, info.Length);
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
                var previous = await _store.GetIndexedFileAsync(options.ProjectKey, relative, token).ConfigureAwait(false);
                if (previous?.ContentHash.Equals(hash, StringComparison.OrdinalIgnoreCase) == true)
                {
                    Interlocked.Increment(ref skipped);
                    return;
                }

                if (previous is not null)
                {
                    foreach (var oldId in previous.MemoryIds) await _store.ForgetAsync(oldId, token).ConfigureAwait(false);
                }

                var extension = Path.GetExtension(path);
                var symbols = _structure.Extract(text, extension);
                var chunks = StructureAwareChunker.Chunk(text, symbols, options.MaxChunkCharacters);
                var ids = new List<MemoryId>(chunks.Count);
                for (var index = 0; index < chunks.Count; index++)
                {
                    var chunk = chunks[index];
                    var id = MemoryIds.Stable($"source|{options.ProjectKey}|{relative}|{chunk.SymbolName}|{index}|{hash}");
                    var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["path"] = relative,
                        ["language"] = extension.TrimStart('.').ToLowerInvariant(),
                        ["chunk"] = index.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    };
                    if (chunk.SymbolName is not null) metadata["symbol"] = chunk.SymbolName;
                    var immutableMetadata = metadata.ToImmutableDictionary(StringComparer.Ordinal);
                    if (gitMetadata.Branch is not null) immutableMetadata = immutableMetadata.SetItem("branch", gitMetadata.Branch);
                    var entry = MemoryEntry.Create(MemoryKind.Source, MemoryScopeKind.Project, options.ProjectKey, chunk.SymbolName ?? relative, chunk.Text, new MemoryProvenance(MemorySourceKind.SourceCode, 0.98, DateTimeOffset.UtcNow, SourceCommit: gitMetadata.Commit, SourcePath: relative, SourceHash: hash), id: id) with
                    {
                        Metadata = immutableMetadata
                    };
                    await _store.UpsertAsync(entry, token).ConfigureAwait(false);
                    await _store.AddChunkAsync(new MemoryChunk(ChunkId.New(), id, chunk.Text, index, relative, chunk.SymbolName, chunk.SymbolKind, extension, chunk.StartLine, chunk.EndLine, hash), token).ConfigureAwait(false);
                    var fileNode = new KnowledgeNode(
                        new KnowledgeNodeId(MemoryIds.Stable($"node|file|{options.ProjectKey}|{relative}|{index}").Value),
                        id,
                        "file",
                        relative,
                        relative,
                        options.ProjectKey);
                    var contentNode = new KnowledgeNode(
                        new KnowledgeNodeId(MemoryIds.Stable($"node|content|{options.ProjectKey}|{relative}|{index}|{chunk.SymbolName}").Value),
                        id,
                        chunk.SymbolKind ?? "chunk",
                        chunk.SymbolName ?? $"{relative}#{index}",
                        chunk.SymbolName ?? relative,
                        options.ProjectKey);
                    await _store.AddNodeAsync(fileNode, token).ConfigureAwait(false);
                    await _store.AddNodeAsync(contentNode, token).ConfigureAwait(false);
                    await _store.AddEdgeAsync(new KnowledgeEdge(
                        new KnowledgeEdgeId(MemoryIds.Stable($"edge|defines|{fileNode.Id}|{contentNode.Id}").Value),
                        fileNode.Id,
                        KnowledgeRelationType.Defines,
                        contentNode.Id,
                        0.98,
                        DateTimeOffset.UtcNow), token).ConfigureAwait(false);
                    if (options.GenerateEmbeddings)
                    {
                        var vector = await _embeddings.EmbedAsync(chunk.Text, token).ConfigureAwait(false);
                        if (vector is not null) await _store.AddEmbeddingAsync(EmbeddingId.New(), id, vector, token).ConfigureAwait(false);
                    }

                    ids.Add(id);
                }

                await _store.UpsertIndexedFileAsync(new IndexedFileRecord(options.ProjectKey, relative, hash, info.Length, DateTimeOffset.UtcNow, ids, gitMetadata.Branch, gitMetadata.Commit), token).ConfigureAwait(false);
                Interlocked.Increment(ref indexed);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var failureNumber = Interlocked.Increment(ref failed);
                if (failureNumber <= 256) errors.Enqueue($"{path}: {exception.GetType().Name}: {exception.Message}");
            }
            finally
            {
                var done = Interlocked.Increment(ref processed);
                progress?.Report(new IngestionProgress(files.Length, done, Volatile.Read(ref indexed), Volatile.Read(ref skipped), Volatile.Read(ref failed), path));
            }
        }).ConfigureAwait(false);

        return new RepositoryIngestionResult(files.Length, indexed, skipped, failed, bytes, Stopwatch.GetElapsedTime(started), errors.ToArray());
    }
}

public static class StructureAwareChunker
{
    public static IReadOnlyList<ChunkSlice> Chunk(string content, IReadOnlyList<CodeSymbol> symbols, int maxCharacters)
    {
        maxCharacters = Math.Clamp(maxCharacters, 256, 32_000);
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var chunks = new List<ChunkSlice>();
        if (symbols.Count == 0)
        {
            for (var start = 0; start < lines.Length; start += Math.Max(1, maxCharacters / 80))
            {
                var end = Math.Min(lines.Length, start + Math.Max(1, maxCharacters / 80));
                var text = string.Join('\n', lines[start..end]);
                if (text.Trim().Length > 0) chunks.Add(new ChunkSlice(text, null, "text", start + 1, end));
            }

            return chunks;
        }

        for (var index = 0; index < symbols.Count; index++)
        {
            var symbol = symbols[index];
            var start = Math.Clamp(symbol.StartLine - 1, 0, lines.Length);
            var end = index + 1 < symbols.Count ? Math.Clamp(symbols[index + 1].StartLine - 1, start + 1, lines.Length) : lines.Length;
            var text = string.Join('\n', lines[start..end]);
            if (text.Length <= maxCharacters)
            {
                if (text.Trim().Length > 0) chunks.Add(new ChunkSlice(text, symbol.Name, symbol.Kind, start + 1, end));
                continue;
            }

            for (var sliceStart = 0; sliceStart < text.Length; sliceStart += maxCharacters)
            {
                var slice = text[sliceStart..Math.Min(text.Length, sliceStart + maxCharacters)];
                if (slice.Trim().Length > 0) chunks.Add(new ChunkSlice(slice, symbol.Name, symbol.Kind, start + 1, end));
            }
        }

        return chunks;
    }
}

public sealed record ChunkSlice(string Text, string? SymbolName, string? SymbolKind, int StartLine, int EndLine);

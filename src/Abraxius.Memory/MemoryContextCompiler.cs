using System.Security.Cryptography;
using System.Text;
using Abraxius.Axl;
using Abraxius.Protocol;

namespace Abraxius.Memory;

public sealed class MemoryContextCompiler(IHybridMemoryRetriever retriever) 
{
    private readonly IHybridMemoryRetriever _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));

    public async ValueTask<MemoryContextPackage> CompileAsync(ContextCompilationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _retriever.RetrieveAsync(request.Query with { Limit = Math.Max(request.Query.Limit, 16) }, cancellationToken).ConfigureAwait(false);
        var sections = new List<ContextSection>();
        AddSection(sections, "OBJECTIVE", request.Objective, 100);
        AddSection(sections, "CONSTRAINTS", request.Constraints, 95);
        AddSection(sections, "CURRENT STATE", request.CurrentState, 90);
        AddSection(sections, "PRIOR ATTEMPTS", request.PriorAttempts, 80);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in result.Hits.OrderByDescending(static hit => hit.Score))
        {
            var key = hit.Entry.Content.Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;
            var evidence = request.IncludeEvidenceReferences
                ? string.Join(' ', hit.Entry.Evidence.Where(static link => link.EvidenceId.HasValue).Select(static link => $"e#{link.EvidenceId!.Value}"))
                : string.Empty;
            var source = hit.Entry.Provenance.SourcePath is { Length: > 0 } path ? $" source={path}" : string.Empty;
            var stale = hit.IsStale ? " stale=true" : string.Empty;
            var conflict = hit.IsConflict ? " conflict=true" : string.Empty;
            var content = $"[{hit.Entry.Id}] {hit.Entry.Title}{source}{stale}{conflict}\n{hit.Entry.Content}\nwhy={hit.Explanation}{(evidence.Length == 0 ? string.Empty : $" evidence={evidence}")}";
            AddSection(sections, "RELEVANT KNOWLEDGE", content, 70);
        }

        var availableTokens = Math.Max(256, request.ContextWindow - Math.Max(0, request.ReservedOutputTokens));
        var builder = new StringBuilder();
        var includedMemories = new List<MemoryId>();
        var includedEvidence = new List<EvidenceId>();
        var includedSections = new List<ContextSection>();
        var usedTokens = 0;
        foreach (var section in sections.OrderByDescending(static section => section.Priority))
        {
            var addition = $"\n## {section.Name}\n{section.Content}\n";
            var tokens = EstimateTokens(addition);
            if (usedTokens + tokens > availableTokens) continue;
            builder.Append(addition);
            usedTokens += tokens;
            includedSections.Add(section);
        }

        foreach (var hit in result.Hits)
        {
            if (includedSections.Any(section => section.Content.Contains(hit.Entry.Id.ToString(), StringComparison.Ordinal)))
            {
                includedMemories.Add(hit.Entry.Id);
                includedEvidence.AddRange(hit.Entry.Evidence.Where(static link => link.EvidenceId.HasValue).Select(static link => link.EvidenceId!.Value));
            }
        }

        var text = builder.ToString().Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        var projection = AxlFormatter.Compact(new AxlDocument(AxlVersion.Current, [new AxlMemoryQuery(request.Query.Text, request.Query.Limit, request.Query.Scope is { } scope ? [scope.ToString()] : [])]));
        return new MemoryContextPackage(text, includedMemories, includedEvidence.Distinct().ToArray(), includedSections, result.Conflicts, usedTokens, hash, projection);
    }

    private static void AddSection(List<ContextSection> sections, string name, string? content, int priority)
    {
        if (!string.IsNullOrWhiteSpace(content)) AddSection(sections, name, new[] { content }, priority);
    }

    private static void AddSection(List<ContextSection> sections, string name, IReadOnlyList<string>? content, int priority)
    {
        if (content is null || content.Count == 0) return;
        var text = string.Join('\n', content.Where(static value => !string.IsNullOrWhiteSpace(value)));
        if (text.Length == 0) return;
        sections.Add(new ContextSection(name, text, priority, EstimateTokens(text)));
    }

    private static int EstimateTokens(string value) => Math.Max(1, (value.Length + 3) / 4);
}

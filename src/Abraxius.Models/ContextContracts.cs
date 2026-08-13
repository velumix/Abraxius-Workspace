using System.Security.Cryptography;
using System.Text;
using Abraxius.Protocol;

namespace Abraxius.Models;

public sealed record ContextEvidenceItem(
    EvidenceId Id,
    string Content,
    int Priority = 0,
    string? Source = null);

public sealed record ContextBudgetRequest(
    string Objective,
    IReadOnlyList<ContextEvidenceItem> Evidence,
    IReadOnlyList<string>? RecentHistory = null,
    int ContextWindow = 32_000,
    int ReservedOutputTokens = 4_000);

public sealed record ContextPackage(
    string Text,
    IReadOnlyList<EvidenceId> IncludedEvidence,
    int EstimatedTokens,
    string ContentHash);

/// <summary>Builds minimal sufficient context before a request reaches a paid or quota-limited route.</summary>
public interface IContextBudgeter
{
    ContextPackage Build(ContextBudgetRequest request);
}

public sealed class DefaultContextBudgeter : IContextBudgeter
{
    public ContextPackage Build(ContextBudgetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var availableTokens = Math.Max(1_000, request.ContextWindow - request.ReservedOutputTokens);
        var builder = new StringBuilder(request.Objective);
        var included = new List<EvidenceId>();
        var estimatedTokens = EstimateTokens(builder.Length);
        if (request.RecentHistory is not null)
        {
            foreach (var history in request.RecentHistory)
            {
                var addition = $"\n{history}";
                var additionTokens = EstimateTokens(addition.Length);
                if (estimatedTokens + additionTokens > availableTokens)
                {
                    break;
                }

                builder.Append(addition);
                estimatedTokens += additionTokens;
            }
        }

        foreach (var evidence in request.Evidence.OrderByDescending(static item => item.Priority))
        {
            var addition = $"\n[{evidence.Id}] {evidence.Content}";
            var additionTokens = EstimateTokens(addition.Length);
            if (estimatedTokens + additionTokens > availableTokens)
            {
                continue;
            }

            builder.Append(addition);
            included.Add(evidence.Id);
            estimatedTokens += additionTokens;
        }

        var text = builder.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new ContextPackage(text, included, estimatedTokens, hash);
    }

    private static int EstimateTokens(int characterCount) => Math.Max(1, (characterCount + 3) / 4);
}

public sealed record CompressionResult(string Content, int InputBytes, int OutputBytes, bool Lossless, string? Algorithm = null);

public interface IContextCompressor
{
    ValueTask<CompressionResult> CompressAsync(string content, CancellationToken cancellationToken = default);
}

public interface IModelResponseCache
{
    ValueTask<ModelResult?> GetAsync(string key, CancellationToken cancellationToken = default);
    ValueTask SetAsync(string key, ModelResult result, CancellationToken cancellationToken = default);
}

public static class ModelCacheKeys
{
    public static string Create(ModelRequest request, ContextPackage? context = null)
    {
        var value = $"{request.Model}|{request.TaskClass}|{request.ExpectedJsonSchema}|{context?.ContentHash}|{request.Prompt}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}

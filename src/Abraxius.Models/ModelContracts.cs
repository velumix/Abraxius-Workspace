using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Diagnostics;
using Abraxius.Protocol;

namespace Abraxius.Models;

public enum ModelOutputFormat
{
    Text,
    Json,
    Axl
}

public sealed record ModelRequest(
    string Prompt,
    string? Model = null,
    string? SystemPrompt = null,
    string? ExpectedJsonSchema = null,
    WorkPriority Priority = WorkPriority.Normal,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    /// <summary>Stable request identity used for routing and provider telemetry.</summary>
    public ModelRequestId RequestId { get; init; } = ModelRequestId.New();

    /// <summary>Execution identity used to enforce per-execution model budgets.</summary>
    public ExecutionId? ExecutionId { get; init; }

    /// <summary>Task identity used to correlate provider activity with the execution graph.</summary>
    public TaskId? TaskId { get; init; }

    /// <summary>Classification used by the transparent route scorer.</summary>
    public IntelligenceTaskClass TaskClass { get; init; } = IntelligenceTaskClass.General;

    /// <summary>Coarse complexity estimate. It is a routing hint, not a model judgment.</summary>
    public IntelligenceComplexity Complexity { get; init; } = IntelligenceComplexity.Simple;

    /// <summary>Capabilities that a candidate must provide before it can be scored.</summary>
    public IReadOnlyList<ModelCapability> RequiredCapabilities { get; init; } = [];

    /// <summary>Routing and budget policy for this request.</summary>
    public IntelligenceRequestPolicy Policy { get; init; } = new();

    /// <summary>Minimum context capacity needed by the assembled prompt.</summary>
    public int? RequiredContextTokens { get; init; }

    /// <summary>Optional stable session key for route affinity and prompt-cache locality.</summary>
    public string? SessionKey { get; init; }

    public int? MaxOutputTokens { get; init; }
    public decimal? Temperature { get; init; }
    public bool Stream { get; init; }
    public ModelOutputFormat OutputFormat { get; init; } = ModelOutputFormat.Text;
    public decimal? ExecutionMaximumCost { get; init; }
    public int? ExecutionMaximumCalls { get; init; }
    public IReadOnlyList<ModelToolDefinition> Tools { get; init; } = [];
    public IReadOnlyList<EvidenceId> Evidence { get; init; } = [];
    public DataClassification DataClassification { get; init; } = DataClassification.Internal;
}

public sealed record ModelToolDefinition(string Name, string Description, string JsonSchema);

public sealed record ModelUsage(
    int InputTokens,
    int OutputTokens,
    decimal? EstimatedCost = null);

public sealed record ModelResult(
    string Text,
    string? StructuredJson,
    string Model,
    ModelUsage? Usage,
    TimeSpan Latency,
    string? Provider = null,
    RouteDecision? Route = null,
    IReadOnlyList<ModelActionProposal>? Actions = null);

public abstract record ModelStreamEvent(DateTimeOffset Timestamp)
{
    public sealed record Started(DateTimeOffset Timestamp, string? Model) : ModelStreamEvent(Timestamp);
    public sealed record Token(DateTimeOffset Timestamp, string Text) : ModelStreamEvent(Timestamp);
    public sealed record Completed(DateTimeOffset Timestamp, ModelResult Result) : ModelStreamEvent(Timestamp);
}

public interface IModelProvider
{
    ValueTask<ModelResult> InferAsync(ModelRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, CancellationToken cancellationToken = default);
}

public sealed class MockModelProvider : IModelProvider
{
    private readonly TimeSpan _latency;
    private readonly string _model;

    public MockModelProvider(TimeSpan? latency = null, string model = "mock-reasoner")
    {
        _latency = latency ?? TimeSpan.FromMilliseconds(200);
        _model = model;
    }

    public async ValueTask<ModelResult> InferAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        var text = BuildResponse(request);
        return new ModelResult(text, request.ExpectedJsonSchema is null ? null : JsonSerializer.Serialize(new { answer = text }), _model, new ModelUsage(32, 16), Stopwatch.GetElapsedTime(started), "mock");
    }

    private static string BuildResponse(ModelRequest request)
    {
        if (request.Metadata?.TryGetValue("surface", out var surface) == true &&
            string.Equals(surface, "chat-room", StringComparison.OrdinalIgnoreCase))
        {
            var message = ExtractLatestUserMessage(request.Prompt);
            return string.IsNullOrWhiteSpace(message)
                ? "I’m ready. Send a message to begin."
                : $"Offline demo response: I received \"{message}\". Configure a model provider for full AI answers.";
        }

        return $"Deterministic synthesis for: {request.Prompt}";
    }

    private static string ExtractLatestUserMessage(string prompt)
    {
        var lines = prompt.Split('\n');
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            const string marker = "USER: ";
            if (!lines[index].StartsWith(marker, StringComparison.Ordinal))
            {
                continue;
            }

            var message = lines[index][marker.Length..].Trim();
            return message.Length > 320 ? message[..320] + "…" : message;
        }

        return string.Empty;
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await InferAsync(request, cancellationToken).ConfigureAwait(false);
        foreach (var token in result.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new ModelStreamEvent.Token(DateTimeOffset.UtcNow, token + " ");
        }

        yield return new ModelStreamEvent.Completed(DateTimeOffset.UtcNow, result);
    }
}

using System.Diagnostics;
using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Abraxius.Protocol;

namespace Abraxius.Models;

/// <summary>
/// Small provider-neutral adapter for gateways exposing the OpenAI chat-completions shape.
/// Authentication is supplied by the host and is never included in exceptions or telemetry.
/// </summary>
public class OpenAiCompatibleModelProvider : IIntelligenceGatewayProvider, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Uri _chatCompletionsEndpoint;
    private readonly string _defaultModel;
    private readonly string? _apiKey;

    public OpenAiCompatibleModelProvider(
        HttpClient httpClient,
        Uri endpoint,
        string defaultModel = "default",
        string? apiKey = null,
        bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _chatCompletionsEndpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _defaultModel = string.IsNullOrWhiteSpace(defaultModel) ? "default" : defaultModel;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        _ownsHttpClient = ownsHttpClient;
    }

    public virtual IntelligenceGateway Gateway => IntelligenceGateway.Local;
    public virtual string ProviderKey => "openai-compatible";
    public Uri Endpoint => _chatCompletionsEndpoint;

    public async ValueTask<ModelResult> InferAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        using var httpRequest = CreateRequest(request, stream: false);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            throw CreateCredentialException();
        }
        catch (UnauthorizedAccessException)
        {
            throw CreateCredentialException();
        }

        using (response)
        {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateProviderException(response.StatusCode, body);
        }

        return ParseResult(body, request, Stopwatch.GetElapsedTime(started), ProviderKey);
        }
    }

    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(
        ModelRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var started = Stopwatch.GetTimestamp();
        using var httpRequest = CreateRequest(request, stream: true);
        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            throw CreateCredentialException();
        }
        catch (UnauthorizedAccessException)
        {
            throw CreateCredentialException();
        }

        using (response)
        {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw CreateProviderException(response.StatusCode, body);
        }

        yield return new ModelStreamEvent.Started(DateTimeOffset.UtcNow, request.Model ?? _defaultModel);
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var nonStreamingResult = ParseResult(body, request, Stopwatch.GetElapsedTime(started), ProviderKey);
            if (!string.IsNullOrEmpty(nonStreamingResult.Text))
            {
                yield return new ModelStreamEvent.Token(DateTimeOffset.UtcNow, nonStreamingResult.Text);
            }

            yield return new ModelStreamEvent.Completed(DateTimeOffset.UtcNow, nonStreamingResult);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        var model = request.Model ?? _defaultModel;
        ModelUsage? usage = null;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (data is "[DONE]" or "[done]")
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(data))
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String)
            {
                model = modelElement.GetString() ?? model;
            }

            if (root.TryGetProperty("usage", out var usageElement))
            {
                usage = ParseUsage(usageElement);
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                continue;
            }

            var choice = choices[0];
            if (!choice.TryGetProperty("delta", out var delta) || !delta.TryGetProperty("content", out var content))
            {
                continue;
            }

            var token = content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            if (string.IsNullOrEmpty(token))
            {
                continue;
            }

            text.Append(token);
            yield return new ModelStreamEvent.Token(DateTimeOffset.UtcNow, token);
        }

        var result = new ModelResult(
            text.ToString(),
            request.ExpectedJsonSchema is null ? null : text.ToString(),
            model,
            usage,
            Stopwatch.GetElapsedTime(started),
            ProviderKey);
        yield return new ModelStreamEvent.Completed(DateTimeOffset.UtcNow, result);
        }
    }

    public async ValueTask<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, GetModelsEndpoint());
            AddAuthentication(request);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var status = response.StatusCode == HttpStatusCode.TooManyRequests
                ? ProviderHealthStatus.RateLimited
                : response.IsSuccessStatusCode
                    ? ProviderHealthStatus.Healthy
                    : response.StatusCode is >= HttpStatusCode.InternalServerError
                        ? ProviderHealthStatus.Unavailable
                        : ProviderHealthStatus.Degraded;
            return new ProviderHealth
            {
                Status = status,
                ObservedAt = DateTimeOffset.UtcNow,
                EstimatedLatency = Stopwatch.GetElapsedTime(started)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (KeyNotFoundException)
        {
            return new ProviderHealth
            {
                Status = ProviderHealthStatus.Unavailable,
                ObservedAt = DateTimeOffset.UtcNow,
                EstimatedLatency = Stopwatch.GetElapsedTime(started),
                Detail = "credential_unavailable"
            };
        }
        catch (UnauthorizedAccessException)
        {
            return new ProviderHealth
            {
                Status = ProviderHealthStatus.Unavailable,
                ObservedAt = DateTimeOffset.UtcNow,
                EstimatedLatency = Stopwatch.GetElapsedTime(started),
                Detail = "credential_denied"
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return new ProviderHealth
            {
                Status = ProviderHealthStatus.Unavailable,
                ObservedAt = DateTimeOffset.UtcNow,
                EstimatedLatency = Stopwatch.GetElapsedTime(started),
                Detail = exception.GetType().Name
            };
        }
    }

    public async ValueTask<IReadOnlyList<ModelDescriptor>> DiscoverModelsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GetModelsEndpoint());
        AddAuthentication(request);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateProviderException(response.StatusCode, body);
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var models = new List<ModelDescriptor>();
        foreach (var element in data.EnumerateArray())
        {
            if (!element.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var modelId = idElement.GetString();
            if (string.IsNullOrWhiteSpace(modelId))
            {
                continue;
            }

            models.Add(new ModelDescriptor
            {
                ModelId = modelId,
                DisplayName = modelId,
                Provider = ProviderKey,
                Gateway = Gateway,
                CostClass = ModelCostClass.Unknown,
                Tier = IntelligenceTier.Standard,
                Health = new ProviderHealth { Status = ProviderHealthStatus.Healthy, ObservedAt = DateTimeOffset.UtcNow }
            });
        }

        return models;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    protected virtual HttpRequestMessage CreateRequest(ModelRequest request, bool stream)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model ?? _defaultModel,
            ["messages"] = new[]
            {
                new { role = "system", content = request.SystemPrompt ?? "You are a structured reasoning engine." },
                new { role = "user", content = request.Prompt }
            },
            ["stream"] = stream
        };
        if (request.ExpectedJsonSchema is not null)
        {
            payload["response_format"] = new { type = "json_object" };
        }
        if (request.MaxOutputTokens is { } maxOutputTokens)
        {
            payload["max_tokens"] = maxOutputTokens;
        }
        if (request.Temperature is { } temperature)
        {
            payload["temperature"] = temperature;
        }
        if (request.Tools.Count > 0)
        {
            payload["tools"] = request.Tools.Select(static tool => new
            {
                type = "function",
                function = new { name = tool.Name, description = tool.Description, parameters = JsonSerializer.Deserialize<JsonElement>(tool.JsonSchema) }
            }).ToArray();
        }

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _chatCompletionsEndpoint)
        {
            Content = JsonContent.Create(payload)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(stream ? "text/event-stream" : "application/json"));
        AddAuthentication(httpRequest);
        return httpRequest;
    }

    protected virtual void AddAuthentication(HttpRequestMessage request)
    {
        if (_apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }
    }

    private Uri GetModelsEndpoint()
    {
        var builder = new UriBuilder(_chatCompletionsEndpoint);
        var path = builder.Path.TrimEnd('/');
        const string completions = "/chat/completions";
        builder.Path = path.EndsWith(completions, StringComparison.OrdinalIgnoreCase)
            ? path[..^completions.Length] + "/models"
            : path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? path + "/models" : path + "/models";
        return builder.Uri;
    }

    private static ModelResult ParseResult(string body, ModelRequest request, TimeSpan latency, string provider)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var model = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
            ? modelElement.GetString()
            : request.Model;
        model ??= "unknown";
        var text = string.Empty;
        IReadOnlyList<ModelActionProposal>? actions = null;
        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var message = choices[0].TryGetProperty("message", out var messageElement) ? messageElement : default;
            if (message.ValueKind != JsonValueKind.Undefined && message.TryGetProperty("content", out var content))
            {
                text = ReadContent(content);
            }

            actions = ReadActions(message);
        }

        var usage = root.TryGetProperty("usage", out var usageElement) ? ParseUsage(usageElement) : null;
        return new ModelResult(
            text,
            request.ExpectedJsonSchema is null ? null : text,
            model,
            usage,
            latency,
            provider,
            Actions: actions);
    }

    private static string ReadContent(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? string.Empty,
        JsonValueKind.Array => string.Join(string.Empty, content.EnumerateArray().Select(static item => item.TryGetProperty("text", out var text) ? text.GetString() : null).Where(static value => value is not null)),
        _ => content.ToString()
    };

    private static ModelUsage ParseUsage(JsonElement usage)
    {
        var input = usage.TryGetProperty("prompt_tokens", out var prompt) ? prompt.GetInt32() : 0;
        var output = usage.TryGetProperty("completion_tokens", out var completion) ? completion.GetInt32() : 0;
        return new ModelUsage(input, output);
    }

    private static List<ModelActionProposal>? ReadActions(JsonElement message)
    {
        if (!message.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var actions = new List<ModelActionProposal>();
        foreach (var toolCall in toolCalls.EnumerateArray())
        {
            if (!toolCall.TryGetProperty("function", out var function) ||
                !function.TryGetProperty("name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var name = nameElement.GetString();
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            if (function.TryGetProperty("arguments", out var arguments) && arguments.ValueKind == JsonValueKind.String)
            {
                var rawArguments = arguments.GetString();
                if (!string.IsNullOrWhiteSpace(rawArguments))
                {
                    try
                    {
                        using var argumentsDocument = JsonDocument.Parse(rawArguments);
                        if (argumentsDocument.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var property in argumentsDocument.RootElement.EnumerateObject())
                            {
                                parameters[property.Name] = property.Value.ValueKind == JsonValueKind.String
                                    ? property.Value.GetString() ?? string.Empty
                                    : property.Value.GetRawText();
                            }
                        }
                    }
                    catch (JsonException)
                    {
                        // A malformed tool call is returned as data for policy/repair; it is never executed here.
                    }
                }
            }

            var target = parameters.TryGetValue("target", out var targetValue) ? targetValue : string.Empty;
            actions.Add(new ModelActionProposal(name, name, target, parameters));
        }

        return actions;
    }

    private ModelProviderException CreateProviderException(HttpStatusCode statusCode, string body)
    {
        var transient = statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500 || statusCode == HttpStatusCode.RequestTimeout;
        var category = statusCode == HttpStatusCode.TooManyRequests ? Abraxius.Protocol.ErrorCategory.Transport : Abraxius.Protocol.ErrorCategory.Model;
        var detail = body.Length > 512 ? body[..512] : body;
        return new ModelProviderException(new Abraxius.Protocol.RuntimeError(
            category,
            statusCode == HttpStatusCode.TooManyRequests ? "provider_rate_limited" : "provider_http_error",
            $"{ProviderKey} returned HTTP {(int)statusCode}.",
            detail,
            transient));
    }

    private ModelProviderException CreateCredentialException() => new(new Abraxius.Protocol.RuntimeError(
        Abraxius.Protocol.ErrorCategory.Configuration,
        "provider_credential_unavailable",
        $"{ProviderKey} credentials are not configured for this gateway.",
        IsTransient: false));
}

public sealed class OmniRouteModelProvider : OpenAiCompatibleModelProvider
{
    public OmniRouteModelProvider(HttpClient httpClient, Uri endpoint, string defaultRoute = "auto/coding:free", string? apiKey = null, bool ownsHttpClient = false)
        : base(httpClient, endpoint, defaultRoute, apiKey, ownsHttpClient)
    {
    }

    public override IntelligenceGateway Gateway => IntelligenceGateway.OmniRoute;
    public override string ProviderKey => "omniroute";
}

public sealed class LiteLlmModelProvider : OpenAiCompatibleModelProvider
{
    public LiteLlmModelProvider(HttpClient httpClient, Uri endpoint, string defaultModel = "general-low-cost", string? apiKey = null, bool ownsHttpClient = false)
        : base(httpClient, endpoint, defaultModel, apiKey, ownsHttpClient)
    {
    }

    public override IntelligenceGateway Gateway => IntelligenceGateway.LiteLlm;
    public override string ProviderKey => "litellm";
}

public sealed class FrontierModelProvider : OpenAiCompatibleModelProvider
{
    public FrontierModelProvider(HttpClient httpClient, Uri endpoint, string defaultModel, string? apiKey = null, bool ownsHttpClient = false)
        : base(httpClient, endpoint, defaultModel, apiKey, ownsHttpClient)
    {
    }

    public override IntelligenceGateway Gateway => IntelligenceGateway.Frontier;
    public override string ProviderKey => "frontier";
}

public sealed class ModelProviderException(Abraxius.Protocol.RuntimeError error) : Exception(error.Message)
{
    public Abraxius.Protocol.RuntimeError Error { get; } = error;
}

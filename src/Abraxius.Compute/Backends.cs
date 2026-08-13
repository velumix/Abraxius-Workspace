using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.AI;

namespace Abraxius.Compute;

public sealed class OllamaBackend : ILocalInferenceBackend
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    public OllamaBackend(HttpClient http, Uri? endpoint = null, bool userManaged = true)
    {
        _http = http; _endpoint = endpoint ?? new Uri("http://127.0.0.1:11434/");
        if (!_endpoint.IsLoopback) throw new ArgumentException("Ollama backend must bind through loopback. Remote inference uses Fabric.", nameof(endpoint));
        Descriptor = new(new("ollama"), "discovered", ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "gguf"),
            ImmutableHashSet.Create(ComputeDeviceClass.Cpu, ComputeDeviceClass.Gpu), true, true, true, true, true, true, true, false,
            ImmutableHashSet<string>.Empty, BackendHealth.Unknown, userManaged);
    }
    public InferenceBackendDescriptor Descriptor { get; private set; }
    public IChatClient? ChatClient => null;

    public async ValueTask<ImmutableArray<ModelVariantDescriptor>> DiscoverModelsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(new Uri(_endpoint, "api/tags"), cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
            var payload = await response.Content.ReadFromJsonAsync<OllamaTags>(cancellationToken: cancellationToken).ConfigureAwait(false);
            Descriptor = Descriptor with { Health = BackendHealth.Healthy };
            return [.. (payload?.Models ?? []).Select(ToVariant)];
        }
        catch (HttpRequestException) { Descriptor = Descriptor with { Health = BackendHealth.Unavailable }; return []; }
    }

    public async ValueTask<ImmutableArray<ResidentModelInstance>> GetResidencyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var payload = await _http.GetFromJsonAsync<OllamaPs>(new Uri(_endpoint, "api/ps"), cancellationToken).ConfigureAwait(false);
            return [.. (payload?.Models ?? []).Select(model => new ResidentModelInstance(ModelInstanceId.New(), VariantId(model.Model, model.Digest), Descriptor.Id, [], ModelResidencyState.IdleResident,
                Math.Max(0, model.Size - model.SizeVram), model.SizeVram, model.ContextLength, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.Zero, false, 0, model.ExpiresAt))];
        }
        catch (HttpRequestException) { return []; }
    }

    public async ValueTask<ResidentModelInstance> LoadAsync(ModelVariantDescriptor variant, LocalInferenceExecutionPlan plan, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp(); using var response = await _http.PostAsJsonAsync(new Uri(_endpoint, "api/generate"), new { model = BackendName(variant), prompt = "", stream = false, keep_alive = "5m" }, cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        return new(ModelInstanceId.New(), variant.Id, Descriptor.Id, plan.Devices, ModelResidencyState.Resident, plan.Memory.HostRamBytes, plan.Memory.DeviceMemoryBytes, plan.ContextTokens, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(started), false, 0, DateTimeOffset.UtcNow.AddMinutes(5));
    }

    public async ValueTask UnloadAsync(ModelVariantDescriptor variant, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync(new Uri(_endpoint, "api/generate"), new { model = BackendName(variant), prompt = "", stream = false, keep_alive = 0 }, cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
    }

    public async IAsyncEnumerable<BackendInferenceEvent> InferAsync(ModelVariantDescriptor variant, LocalInferenceRequest request, LocalInferenceExecutionPlan plan, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "api/generate")) { Content = JsonContent.Create(new { model = BackendName(variant), prompt = request.Prompt, stream = true, keep_alive = "5m", options = new { num_ctx = plan.ContextTokens, num_predict = request.ExpectedOutputTokens } }) };
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false); using var reader = new StreamReader(body);
        var text = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var item = JsonSerializer.Deserialize<OllamaGenerate>(line); if (item is null) continue;
            if (!string.IsNullOrEmpty(item.Response)) { text.Append(item.Response); yield return new BackendInferenceEvent.Token(item.Response); }
            if (item.Done) yield return new BackendInferenceEvent.Completed(text.ToString(), item.LoadDuration, item.PromptEvalCount, item.PromptEvalDuration, item.EvalCount, item.EvalDuration);
        }
    }

    private ModelVariantDescriptor ToVariant(OllamaModel model)
    {
        var logical = new LogicalModelId(model.Model.Split(':', 2)[0]); var revision = new ModelRevisionId(model.Digest);
        return new(VariantId(model.Model, model.Digest), logical, revision, model.Model, model.Details?.Format ?? "unknown", model.Details?.QuantizationLevel ?? "unknown",
            ParseParameters(model.Details?.ParameterSize), model.Details?.Family ?? "unknown", 4096, model.Size,
            ImmutableDictionary<string, string>.Empty.Add("digest", model.Digest), ImmutableHashSet.Create(ModelCapabilityKind.Chat), [], ImmutableHashSet.Create(Descriptor.Id),
            new(null, null, false, LicenseAcceptance.NotRequired), new("Ollama", model.Model, model.Digest, Abraxius.Models.DataClassification.Internal), ModelValidationState.Unvalidated,
            ModelStorageKind.BackendManaged, model.Model);
    }
    private static ModelVariantId VariantId(string model, string digest) => new($"ollama/{model}/{digest[..Math.Min(16, digest.Length)]}");
    private static string BackendName(ModelVariantDescriptor variant) => variant.StorageKind == ModelStorageKind.BackendManaged ? variant.StorageReference : variant.DisplayName;
    private static long? ParseParameters(string? value) { if (string.IsNullOrWhiteSpace(value)) return null; var suffix = value[^1]; if (!double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return null; return checked((long)(number * (suffix is 'B' or 'b' ? 1_000_000_000 : suffix is 'M' or 'm' ? 1_000_000 : 1))); }
    private sealed record OllamaTags([property: System.Text.Json.Serialization.JsonPropertyName("models")] OllamaModel[] Models);
    private sealed record OllamaPs([property: System.Text.Json.Serialization.JsonPropertyName("models")] OllamaRunningModel[] Models);
    private sealed record OllamaModel([property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name, [property: System.Text.Json.Serialization.JsonPropertyName("model")] string Model, [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size, [property: System.Text.Json.Serialization.JsonPropertyName("digest")] string Digest, [property: System.Text.Json.Serialization.JsonPropertyName("details")] OllamaDetails? Details);
    private sealed record OllamaRunningModel([property: System.Text.Json.Serialization.JsonPropertyName("model")] string Model, [property: System.Text.Json.Serialization.JsonPropertyName("size")] long Size, [property: System.Text.Json.Serialization.JsonPropertyName("digest")] string Digest, [property: System.Text.Json.Serialization.JsonPropertyName("size_vram")] long SizeVram, [property: System.Text.Json.Serialization.JsonPropertyName("context_length")] int ContextLength, [property: System.Text.Json.Serialization.JsonPropertyName("expires_at")] DateTimeOffset? ExpiresAt);
    private sealed record OllamaDetails([property: System.Text.Json.Serialization.JsonPropertyName("format")] string Format, [property: System.Text.Json.Serialization.JsonPropertyName("family")] string Family, [property: System.Text.Json.Serialization.JsonPropertyName("parameter_size")] string ParameterSize, [property: System.Text.Json.Serialization.JsonPropertyName("quantization_level")] string QuantizationLevel);
    private sealed record OllamaGenerate([property: System.Text.Json.Serialization.JsonPropertyName("response")] string Response, [property: System.Text.Json.Serialization.JsonPropertyName("done")] bool Done, [property: System.Text.Json.Serialization.JsonPropertyName("load_duration")] long LoadDuration, [property: System.Text.Json.Serialization.JsonPropertyName("prompt_eval_count")] int PromptEvalCount, [property: System.Text.Json.Serialization.JsonPropertyName("prompt_eval_duration")] long PromptEvalDuration, [property: System.Text.Json.Serialization.JsonPropertyName("eval_count")] int EvalCount, [property: System.Text.Json.Serialization.JsonPropertyName("eval_duration")] long EvalDuration);
}

public sealed class UnavailableInferenceBackend(InferenceBackendDescriptor descriptor) : ILocalInferenceBackend
{
    public InferenceBackendDescriptor Descriptor { get; } = descriptor with { Health = BackendHealth.Unavailable };
    public IChatClient? ChatClient => null;
    public ValueTask<ImmutableArray<ModelVariantDescriptor>> DiscoverModelsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ImmutableArray<ModelVariantDescriptor>.Empty);
    public ValueTask<ImmutableArray<ResidentModelInstance>> GetResidencyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(ImmutableArray<ResidentModelInstance>.Empty);
    public ValueTask<ResidentModelInstance> LoadAsync(ModelVariantDescriptor variant, LocalInferenceExecutionPlan plan, CancellationToken cancellationToken = default) => ValueTask.FromException<ResidentModelInstance>(new InvalidOperationException($"Backend {Descriptor.Id} is unavailable."));
    public ValueTask UnloadAsync(ModelVariantDescriptor variant, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public async IAsyncEnumerable<BackendInferenceEvent> InferAsync(ModelVariantDescriptor variant, LocalInferenceRequest request, LocalInferenceExecutionPlan plan, [EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
}

public static class BackendBoundaries
{
    public static ILocalInferenceBackend LlamaCpp() => Boundary("llamacpp", ["gguf"], [ComputeDeviceClass.Cpu, ComputeDeviceClass.Gpu], ["Q4_K_M", "Q8_0"]);
    public static ILocalInferenceBackend WindowsMl() => Boundary("windows-ml", ["onnx"], [ComputeDeviceClass.Cpu, ComputeDeviceClass.Gpu, ComputeDeviceClass.Npu], []);
    public static ILocalInferenceBackend OnnxExperimental() => Boundary("onnx-genai-preview", ["onnx"], [ComputeDeviceClass.Cpu, ComputeDeviceClass.Gpu, ComputeDeviceClass.Npu], []);
    public static ILocalInferenceBackend Mlx() => Boundary("mlx", ["safetensors", "mlx"], [ComputeDeviceClass.Cpu, ComputeDeviceClass.Gpu], ["int4", "int8"]);
    private static UnavailableInferenceBackend Boundary(string id, string[] formats, ComputeDeviceClass[] devices, string[] quantization) => new(new(new(id), "not-discovered", formats.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase), devices.ToImmutableHashSet(), true, false, false, false, false, false, false, false, quantization.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase), BackendHealth.Unavailable));
}

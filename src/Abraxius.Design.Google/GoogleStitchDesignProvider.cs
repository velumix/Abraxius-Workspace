using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Abraxius.Design;
using Abraxius.Security;

namespace Abraxius.Design.Google;

public sealed record GoogleStitchOptions(
    string BaseUrl = "https://stitch.googleapis.com/mcp",
    string? ProjectId = null,
    string? AccessTokenSecret = "secret://google/stitch/access-token",
    string? ApiKeySecret = "secret://google/stitch/api-key",
    string? OAuthClientId = null,
    string OAuthAuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
    string OAuthTokenEndpoint = "https://oauth2.googleapis.com/token",
    string OAuthScope = "https://www.googleapis.com/auth/cloud-platform",
    TimeSpan? RequestTimeout = null);

public sealed record StitchCredential(string? ApiKey, string? AccessToken, string? QuotaProjectId);

public interface IGoogleStitchCredentialProvider
{
    ValueTask<StitchCredential?> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads credentials only through the Phase 17 broker. The provider never receives a persisted credential object.</summary>
public sealed class SecretBrokerGoogleStitchCredentialProvider(
    ISecretBroker broker,
    GoogleStitchOptions options,
    SecuritySubject? subject = null,
    IGoogleStitchTokenRefresher? refresher = null) : IGoogleStitchCredentialProvider
{
    private readonly SecuritySubject _subject = subject ?? SecuritySubject.System("design-studio");
    public async ValueTask<StitchCredential?> GetAsync(CancellationToken cancellationToken = default)
    {
        var metadata = await broker.ListAsync(cancellationToken).ConfigureAwait(false);
        var accessReference = ParseReference(options.AccessTokenSecret);
        var accessMetadata = accessReference is { } access ? metadata.FirstOrDefault(item => item.Reference == access) : null;
        if (accessMetadata?.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow.AddMinutes(1) && refresher is not null)
        {
            await refresher.RefreshAsync(cancellationToken).ConfigureAwait(false);
            metadata = await broker.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        var apiKey = await ReadAsync(options.ApiKeySecret, metadata, SecurityActions.NetworkHttpPost, cancellationToken).ConfigureAwait(false);
        var accessToken = await ReadAsync(options.AccessTokenSecret, metadata, SecurityActions.NetworkHttpPost, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey) && string.IsNullOrWhiteSpace(accessToken)) return null;
        return new StitchCredential(apiKey, accessToken, options.ProjectId);
    }

    private static SecretReference? ParseReference(string? value) =>
        value is not null && SecretReference.TryParse(value, out var reference) ? reference : null;

    private async ValueTask<string?> ReadAsync(string? value, IReadOnlyList<SecretMetadata> metadata, string operation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value) || !SecretReference.TryParse(value, out var reference) || metadata.All(item => item.Reference != reference)) return null;
        return await broker.UseAsync(new SecretUseRequest(_subject, reference, "https://stitch.googleapis.com", operation,
            new AuthorizationContext(Classification: DataClassification.Internal, UserPresent: false)),
            static (chars, _) => ValueTask.FromResult(chars.ToString()), cancellationToken).ConfigureAwait(false);
    }
}

public sealed class GoogleStitchNotConfiguredException(string message) : InvalidOperationException(message);
public sealed class GoogleStitchProviderException(string message, bool retryable = false) : InvalidOperationException(message)
{
    public bool Retryable { get; } = retryable;
}

/// <summary>
/// Small C# client for Stitch's documented Streamable HTTP MCP endpoint. It intentionally
/// exposes only the typed design operations Abraxius needs; MCP and provider JSON never leak
/// into the provider-neutral design domain.
/// </summary>
internal sealed class StitchMcpHttpClient(HttpClient httpClient, GoogleStitchOptions options, IGoogleStitchCredentialProvider credentials) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _requestId;
    private string? _sessionId;
    private bool _initialized;
    public void Dispose() => _gate.Dispose();

    public async ValueTask<JsonElement> CallToolAsync(string name, object arguments, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await PostAsync(new { jsonrpc = "2.0", id = Interlocked.Increment(ref _requestId), method = "tools/call", @params = new { name, arguments } }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var result = await PostAsync(new
            {
                jsonrpc = "2.0",
                id = Interlocked.Increment(ref _requestId),
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "abraxius-design", version = "1.0" }
                }
            }, cancellationToken, initialize: true).ConfigureAwait(false);
            _initialized = result.ValueKind != JsonValueKind.Undefined;
            await PostNotificationAsync("notifications/initialized", cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async ValueTask PostNotificationAsync(string method, CancellationToken cancellationToken)
    {
        _ = await PostAsync(new { jsonrpc = "2.0", method, @params = new { } }, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<JsonElement> PostAsync(object payload, CancellationToken cancellationToken, bool initialize = false)
    {
        var credential = await credentials.GetAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new GoogleStitchNotConfiguredException("Google Stitch is not connected. Configure a brokered API key or complete Google OAuth first.");
        using var request = new HttpRequestMessage(HttpMethod.Post, options.BaseUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrWhiteSpace(credential.ApiKey)) request.Headers.TryAddWithoutValidation("X-Goog-Api-Key", credential.ApiKey);
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        if (!string.IsNullOrWhiteSpace(credential.QuotaProjectId)) request.Headers.TryAddWithoutValidation("X-Goog-User-Project", credential.QuotaProjectId);
        if (!string.IsNullOrWhiteSpace(_sessionId)) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout ?? TimeSpan.FromMinutes(5));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues)) _sessionId = sessionValues.FirstOrDefault();
        var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new GoogleStitchProviderException($"Stitch MCP returned {(int)response.StatusCode} {response.ReasonPhrase}.", response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError);
        var envelope = ParseJsonOrSse(body);
        if (envelope.TryGetProperty("error", out var error)) throw new GoogleStitchProviderException(error.ToString());
        if (!envelope.TryGetProperty("result", out var result)) return envelope;
        if (result.TryGetProperty("isError", out var isError) && isError.ValueKind == JsonValueKind.True)
            throw new GoogleStitchProviderException(result.TryGetProperty("content", out var content) ? content.ToString() : "Stitch tool call failed.");
        if (result.TryGetProperty("structuredContent", out var structured)) return structured.Clone();
        if (result.TryGetProperty("content", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("text", out var text)) continue;
                try { return JsonDocument.Parse(text.GetString() ?? "null").RootElement.Clone(); } catch (JsonException) { return text.Clone(); }
            }
        }
        return result.Clone();
    }

    private static JsonElement ParseJsonOrSse(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return JsonDocument.Parse("{}").RootElement.Clone();
        try { return JsonDocument.Parse(body).RootElement.Clone(); }
        catch (JsonException)
        {
            var data = body.Split('\n').Select(static line => line.Trim()).Where(static line => line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)).Select(static line => line[5..].Trim()).LastOrDefault();
            return data is null ? throw new GoogleStitchProviderException("Stitch returned an invalid MCP response.") : JsonDocument.Parse(data).RootElement.Clone();
        }
    }
}

public sealed class GoogleStitchDesignProvider : IDesignGenerationProvider, IDisposable
{
    private readonly GoogleStitchOptions _options;
    private readonly IGoogleStitchCredentialProvider _credentials;
    private readonly StitchMcpHttpClient _client;
    private readonly HttpClient _httpClient;
    private static readonly string[] VariantAspects = ["LAYOUT", "COLOR_SCHEME", "TEXT_FONT"];
    public GoogleStitchDesignProvider(HttpClient httpClient, GoogleStitchOptions options, IGoogleStitchCredentialProvider credentials)
    {
        _options = options;
        _credentials = credentials;
        _httpClient = httpClient;
        _client = new StitchMcpHttpClient(httpClient, options, credentials);
    }
    public DesignProviderId Id => new("google.stitch");
    public void Dispose()
    {
        _client.Dispose();
        _httpClient.Dispose();
    }

    public async ValueTask<DesignProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await _credentials.GetAsync(cancellationToken).ConfigureAwait(false) is null)
                return new(Id, DesignProviderConnectionState.NeedsConfiguration, "Connect Google Stitch or configure a brokered credential.", DateTimeOffset.UtcNow, false);
            _ = await _client.CallToolAsync("list_projects", new { }, cancellationToken).ConfigureAwait(false);
            return new(Id, DesignProviderConnectionState.Connected, "Google Stitch is reachable.", DateTimeOffset.UtcNow, true);
        }
        catch (GoogleStitchNotConfiguredException exception) { return new(Id, DesignProviderConnectionState.NeedsConfiguration, exception.Message, DateTimeOffset.UtcNow, false); }
        catch (Exception exception) { return new(Id, DesignProviderConnectionState.Degraded, $"Google Stitch health check failed: {exception.Message}", DateTimeOffset.UtcNow, false); }
    }

    public async ValueTask<DesignProjectRef> EnsureProjectAsync(DesignProjectRequest request, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_options.ProjectId)) return new(Id, new DesignProjectId(_options.ProjectId), request.Title, $"projects/{_options.ProjectId}");
        var projects = await _client.CallToolAsync("list_projects", new { }, cancellationToken).ConfigureAwait(false);
        if (projects.TryGetProperty("projects", out var items))
        {
            foreach (var project in items.EnumerateArray())
            {
                var title = GetString(project, "title") ?? GetString(project, "displayName");
                var projectId = GetString(project, "projectId") ?? GetString(project, "name")?.Split('/').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(projectId) && string.Equals(title, request.Title, StringComparison.OrdinalIgnoreCase))
                    return new(Id, new DesignProjectId(projectId), title ?? request.Title, $"projects/{projectId}");
            }
        }
        var created = await _client.CallToolAsync("create_project", new { title = request.Title }, cancellationToken).ConfigureAwait(false);
        var id = GetString(created, "projectId") ?? GetString(created, "name")?.Split('/').LastOrDefault() ?? throw new GoogleStitchProviderException("Stitch created a project without an identifier.");
        return new(Id, new DesignProjectId(id), request.Title, $"projects/{id}");
    }

    public async ValueTask<DesignGenerationResult> GenerateAsync(DesignGenerationRequest request, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var prompt = request.Context.Brief;
        var generated = await _client.CallToolAsync("generate_screen_from_text", new { projectId = request.Project.ProjectId.Value, prompt, deviceType = "DESKTOP" }, cancellationToken).ConfigureAwait(false);
        var first = await ReadNewestCandidateAsync(request.Context, request.Project, generated, "Conversation First", cancellationToken).ConfigureAwait(false);
        var candidates = ImmutableArray.CreateBuilder<DesignCandidate>();
        candidates.Add(first);
        if (request.VariantCount > 1 && first.ProviderScreenRef is not null)
        {
            var variantPrompt = request.Strategy ?? "Create meaningful alternatives: Conversation First, Balanced Workstation, and Dense Expert. Preserve all required interactions and responsive behavior.";
            var variants = await GenerateVariantsAsync(new DesignVariantRequest(request.GenerationId, request.Context, request.Project, first, request.VariantCount - 1, variantPrompt), cancellationToken).ConfigureAwait(false);
            candidates.AddRange(variants);
        }
        return new DesignGenerationResult(request.GenerationId, Id, request.Project, candidates.ToImmutable(), prompt, request.Context.Source, ImmutableDictionary<string, string>.Empty.Add("source", "Google Stitch MCP"), DateTimeOffset.UtcNow - started);
    }

    public async ValueTask<IReadOnlyList<DesignCandidate>> GenerateVariantsAsync(DesignVariantRequest request, CancellationToken cancellationToken = default)
    {
        if (request.BaseCandidate.ProviderScreenRef is null) return [];
        var prompt = request.Strategy ?? "Explore distinct layout and hierarchy directions while preserving required behavior.";
        var result = await _client.CallToolAsync("generate_variants", new
        {
            projectId = request.Project.ProjectId.Value,
            selectedScreenIds = new[] { request.BaseCandidate.ProviderScreenRef },
            prompt,
            deviceType = "DESKTOP",
            variantOptions = new { variantCount = Math.Clamp(request.VariantCount, 1, 5), creativeRange = "EXPLORE", aspects = VariantAspects }
        }, cancellationToken).ConfigureAwait(false);
        return await ReadNewCandidatesAsync(request.Context, request.Project, result, "Variant", request.BaseCandidate.Id, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<DesignGenerationResult> RefineAsync(DesignRefinementRequest request, CancellationToken cancellationToken = default)
    {
        if (request.BaseCandidate.ProviderScreenRef is null) throw new GoogleStitchProviderException("The selected candidate has no Stitch screen reference.");
        var started = DateTimeOffset.UtcNow;
        var result = await _client.CallToolAsync("edit_screens", new { projectId = request.Project.ProjectId.Value, selectedScreenIds = new[] { request.BaseCandidate.ProviderScreenRef }, prompt = request.Instruction, deviceType = "DESKTOP" }, cancellationToken).ConfigureAwait(false);
        var candidate = await ReadNewestCandidateAsync(request.Context, request.Project, result, "Refined", cancellationToken).ConfigureAwait(false);
        return new DesignGenerationResult(request.GenerationId, Id, request.Project, [candidate with { DerivedFrom = request.BaseCandidate.Id, References = request.References.Select(static item => item.Id).ToImmutableArray() }], request.Context.Brief, request.Context.Source, ImmutableDictionary<string, string>.Empty.Add("derivedFrom", request.BaseCandidate.Id.ToString()), DateTimeOffset.UtcNow - started);
    }

    private async ValueTask<DesignCandidate> ReadNewestCandidateAsync(DesignGenerationContext context, DesignProjectRef project, JsonElement generated, string fallbackTitle, CancellationToken cancellationToken)
    {
        var candidates = await ReadNewCandidatesAsync(context, project, generated, fallbackTitle, null, cancellationToken).ConfigureAwait(false);
        return candidates.Count > 0 ? candidates[0] : new DesignCandidate(DesignCandidateId.New(), fallbackTitle, null, null, null, context.Brief, context.Source, context.CaptureRequest, ImmutableDictionary<string, string>.Empty, DateTimeOffset.UtcNow);
    }

    private async ValueTask<IReadOnlyList<DesignCandidate>> ReadNewCandidatesAsync(DesignGenerationContext context, DesignProjectRef project, JsonElement operationResult, string fallbackTitle, DesignCandidateId? derivedFrom, CancellationToken cancellationToken)
    {
        var screens = await _client.CallToolAsync("list_screens", new { projectId = project.ProjectId.Value }, cancellationToken).ConfigureAwait(false);
        if (!screens.TryGetProperty("screens", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        var list = items.EnumerateArray().ToArray();
        var count = Math.Max(1, operationResult.TryGetProperty("sessionId", out _) ? 3 : 1);
        var selected = list[Math.Max(0, list.Length - count)..];
        var result = new List<DesignCandidate>();
        foreach (var screen in selected)
        {
            var id = GetString(screen, "id") ?? GetString(screen, "screenId") ?? GetString(screen, "name")?.Split('/').LastOrDefault();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var detail = await _client.CallToolAsync("get_screen", new { name = $"projects/{project.ProjectId.Value}/screens/{id}", projectId = project.ProjectId.Value, screenId = id }, cancellationToken).ConfigureAwait(false);
            var html = ReadFile(detail, "htmlCode");
            var image = await ReadImageAsync(detail, cancellationToken).ConfigureAwait(false);
            result.Add(new DesignCandidate(DesignCandidateId.New(), GetString(detail, "title") ?? GetString(screen, "title") ?? $"{fallbackTitle} {result.Count + 1}", id, html, image,
                context.Brief, context.Source, context.CaptureRequest, ImmutableDictionary<string, string>.Empty.Add("provider", Id.Value), DateTimeOffset.UtcNow, derivedFrom));
        }
        return result;
    }

    private async ValueTask<byte[]?> ReadImageAsync(JsonElement detail, CancellationToken cancellationToken)
    {
        if (!detail.TryGetProperty("screenshot", out var file)) return null;
        if (file.TryGetProperty("fileContentBase64", out var base64) && !string.IsNullOrWhiteSpace(base64.GetString())) return Convert.FromBase64String(base64.GetString()!);
        var url = GetString(file, "downloadUrl");
        return string.IsNullOrWhiteSpace(url) ? null : await _credentialsDownloadAsync(url, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<byte[]?> _credentialsDownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false) : null;
    }

    private static string? ReadFile(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var file)) return null;
        if (file.ValueKind == JsonValueKind.String) return file.GetString();
        if (file.TryGetProperty("fileContentBase64", out var encoded) && encoded.ValueKind == JsonValueKind.String)
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded.GetString()!));
        return GetString(file, "downloadUrl");
    }

    private static string? GetString(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class GoogleStitchProjectResolver(GoogleStitchDesignProvider provider) : IDesignProjectResolver
{
    public ValueTask<DesignProjectRef> ResolveAsync(DesignProjectRequest request, CancellationToken cancellationToken = default) => provider.EnsureProjectAsync(request, cancellationToken);
}

public sealed record GoogleStitchOAuthOptions(
    string ClientId,
    string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
    string TokenEndpoint = "https://oauth2.googleapis.com/token",
    string Scope = "https://www.googleapis.com/auth/cloud-platform",
    string? ProjectId = null);

public sealed record OAuthAuthorizationAttempt(string State, string CodeVerifier, Uri AuthorizationUri, string RedirectUri);
public sealed record OAuthTokenResult(string AccessToken, string? RefreshToken, int ExpiresInSeconds);
public interface IGoogleStitchTokenRefresher
{
    ValueTask<OAuthTokenResult?> RefreshAsync(CancellationToken cancellationToken = default);
}

/// <summary>Installed-app OAuth plumbing. The browser is external and tokens are written only to the Phase 17 secret store.</summary>
public sealed class GoogleStitchOAuthClient(
    ISecretStore secretStore,
    HttpClient httpClient,
    GoogleStitchOAuthOptions options,
    IAuthorizationGrantStore? grants = null,
    ISecretBroker? broker = null) : IGoogleStitchTokenRefresher, IDisposable
{
    public OAuthAuthorizationAttempt BeginAuthorization(int port)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId)) throw new GoogleStitchNotConfiguredException("Google OAuth client ID is not configured.");
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirect = $"http://127.0.0.1:{port}/oauth/callback/";
        var builder = new UriBuilder(options.AuthorizationEndpoint);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = options.ClientId, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = options.Scope,
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["access_type"] = "offline", ["prompt"] = "consent"
        };
        builder.Query = string.Join('&', query.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        return new OAuthAuthorizationAttempt(state, verifier, builder.Uri, redirect);
    }

    public async ValueTask<OAuthTokenResult> ExchangeAsync(OAuthAuthorizationAttempt attempt, string code, string returnedState, CancellationToken cancellationToken = default)
    {
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(attempt.State), Encoding.UTF8.GetBytes(returnedState))) throw new UnauthorizedAccessException("Google OAuth state validation failed.");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = options.ClientId, ["code"] = code, ["code_verifier"] = attempt.CodeVerifier, ["redirect_uri"] = attempt.RedirectUri, ["grant_type"] = "authorization_code" });
        using var response = await httpClient.PostAsync(options.TokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new GoogleStitchProviderException("Google OAuth token exchange failed.");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var access = root.GetProperty("access_token").GetString() ?? throw new GoogleStitchProviderException("OAuth response did not include an access token.");
        var refresh = root.TryGetProperty("refresh_token", out var refreshElement) ? refreshElement.GetString() : null;
        var expires = root.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds) ? seconds : 3600;
        await StoreAsync("secret://google/stitch/access-token", access, "Google Stitch access token", DateTimeOffset.UtcNow.AddSeconds(expires), options.TokenEndpoint, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(refresh)) await StoreAsync("secret://google/stitch/refresh-token", refresh!, "Google Stitch refresh token", null, options.TokenEndpoint, cancellationToken).ConfigureAwait(false);
        IssueSessionGrant("secret://google/stitch/access-token");
        if (!string.IsNullOrWhiteSpace(refresh)) IssueSessionGrant("secret://google/stitch/refresh-token");
        return new OAuthTokenResult(access, refresh, expires);
    }

    public async ValueTask<OAuthTokenResult?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!SecretReference.TryParse("secret://google/stitch/refresh-token", out var reference)) return null;
        async ValueTask<OAuthTokenResult?> Exchange(ReadOnlyMemory<char> chars, CancellationToken token)
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId,
                ["refresh_token"] = chars.ToString(),
                ["grant_type"] = "refresh_token"
            });
            using var response = await httpClient.PostAsync(options.TokenEndpoint, content, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var access = json.RootElement.TryGetProperty("access_token", out var accessElement) ? accessElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(access)) return null;
            var expires = json.RootElement.TryGetProperty("expires_in", out var expiresElement) && expiresElement.TryGetInt32(out var seconds) ? seconds : 3600;
            await StoreAsync("secret://google/stitch/access-token", access, "Google Stitch access token", DateTimeOffset.UtcNow.AddSeconds(expires), options.TokenEndpoint, token).ConfigureAwait(false);
            IssueSessionGrant("secret://google/stitch/access-token");
            return new OAuthTokenResult(access, null, expires);
        }

        if (broker is not null)
        {
            return await broker.UseAsync(new SecretUseRequest(SecuritySubject.System("design-studio"), reference,
                options.TokenEndpoint, "GoogleStitch.Refresh", new AuthorizationContext(Classification: DataClassification.Internal)), Exchange, cancellationToken).ConfigureAwait(false);
        }
        return await secretStore.UseAsync(reference, Exchange, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the installed-application flow using the system browser and a one-shot loopback
    /// callback. No token is placed in a command line, URL after exchange, or log.
    /// </summary>
    public async ValueTask<OAuthTokenResult> ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var attempt = BeginAuthorization(endpoint.Port);
        await SystemBrowserLauncher.OpenAsync(attempt.AuthorizationUri, cancellationToken).ConfigureAwait(false);
        using var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(requestLine)) throw new UnauthorizedAccessException("Google OAuth callback was empty.");
        var requestParts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestParts.Length < 2) throw new UnauthorizedAccessException("Google OAuth callback was malformed.");
        var callback = new Uri($"http://127.0.0.1:{endpoint.Port}{requestParts[1]}");
        var query = ParseQuery(callback.Query);
        var html = query.TryGetValue("error", out var error)
            ? $"<html><body>Abraxius could not connect Google Stitch: {WebUtility.HtmlEncode(error)}</body></html>"
            : "<html><body>Abraxius is connected to Google Stitch. You can close this window.</body></html>";
        var responseBytes = Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {responseBytesLength(html)}\r\nConnection: close\r\n\r\n{html}");
        await stream.WriteAsync(responseBytes, cancellationToken).ConfigureAwait(false);
        if (!query.TryGetValue("code", out var code) || !query.TryGetValue("state", out var state))
            throw new UnauthorizedAccessException($"Google OAuth callback failed: {error ?? "missing code or state"}.");
        return await ExchangeAsync(attempt, code, state, cancellationToken).ConfigureAwait(false);

        static int responseBytesLength(string content) => Encoding.UTF8.GetByteCount(content);
    }

    private ValueTask StoreAsync(string referenceText, string value, string display, DateTimeOffset? expiresAt, string destination, CancellationToken cancellationToken)
    {
        if (!SecretReference.TryParse(referenceText, out var reference)) throw new InvalidOperationException("Invalid OAuth secret reference.");
        return secretStore.StoreAsync(new SecretMetadata(reference, display, "Google Stitch", [new Uri(destination).GetLeftPart(UriPartial.Authority)], DateTimeOffset.UtcNow, expiresAt), value.AsMemory(), cancellationToken);
    }

    private void IssueSessionGrant(string referenceText)
    {
        if (grants is null || !SecretReference.TryParse(referenceText, out var reference)) return;
        grants.Issue(new AuthorizationGrant(AuthorizationGrantId.New(), SecuritySubject.System("design-studio"),
            ImmutableHashSet.Create(SecurityActions.SecretUse), reference.Value, GrantScope.Session,
            DateTimeOffset.UtcNow, DateTimeOffset.MaxValue, "google-stitch-connect", "User explicitly connected Google Stitch."));
    }

    private static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(static pair => pair.Split('=', 2))
        .Where(static pair => pair.Length == 2)
        .ToDictionary(static pair => Uri.UnescapeDataString(pair[0]), static pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

    private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => httpClient.Dispose();
}

public static class SystemBrowserLauncher
{
    public static ValueTask OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        return ValueTask.CompletedTask;
    }
}

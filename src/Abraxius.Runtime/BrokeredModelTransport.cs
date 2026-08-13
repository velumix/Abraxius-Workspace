using System.Collections.Immutable;
using System.Net.Http.Headers;
using Abraxius.Models;
using Abraxius.Security;
using Abraxius.Voice;

namespace Abraxius.Runtime;

internal sealed class BrokeredBearerAuthenticationHandler : DelegatingHandler
{
    private readonly ISecretBroker _secrets;
    private readonly ISecretRedactor _redactor;
    private readonly SecretReference _reference;
    private readonly SecuritySubject _subject;

    public BrokeredBearerAuthenticationHandler(
        ISecretBroker secrets,
        ISecretRedactor redactor,
        SecretReference reference,
        SecuritySubject subject)
        : base(new HttpClientHandler())
    {
        _secrets = secrets;
        _redactor = redactor;
        _reference = reference;
        _subject = subject;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var destination = request.RequestUri?.GetLeftPart(UriPartial.Authority)
            ?? throw new InvalidOperationException("Model transport requires an absolute destination URI.");
        var use = new SecretUseRequest(_subject, _reference, destination, SecurityActions.ModelEgress, new AuthorizationContext());
        return _secrets.UseAsync(use, SendAuthorizedAsync, cancellationToken).AsTask();

        async ValueTask<HttpResponseMessage> SendAuthorizedAsync(ReadOnlyMemory<char> value, CancellationToken token)
        {
            _redactor.RegisterSensitiveValue(value.Span);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value.ToString());
            try
            {
                return await base.SendAsync(request, token).ConfigureAwait(false);
            }
            finally
            {
                request.Headers.Authorization = null;
            }
        }
    }
}

internal sealed record ModelSecretBootstrap(
    ISecretStore Store,
    ImmutableDictionary<IntelligenceGateway, SecretReference> References,
    ImmutableDictionary<string, SecretReference> VoiceReferences,
    SecuritySubject Subject);

internal sealed class BrokeredSpeechCredentialProvider(
    ISecretBroker secrets,
    ISecretRedactor redactor,
    SecretReference reference,
    SecuritySubject subject,
    string destination) : ISpeechCredentialProvider
{
    public ValueTask<T> UseAsync<T>(Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> transport, CancellationToken cancellationToken = default) =>
        secrets.UseAsync(new SecretUseRequest(subject, reference, destination, "Voice.Transport", new AuthorizationContext()),
            async (value, token) =>
            {
                redactor.RegisterSensitiveValue(value.Span);
                return await transport(value, token).ConfigureAwait(false);
            }, cancellationToken);
}

internal static class ModelSecretBootstrapFactory
{
    public static ModelSecretBootstrap Create(IntelligenceFabricOptions options, InMemorySecretStore writableStore)
    {
        var mappings = new Dictionary<SecretReference, (string Variable, SecretMetadata Metadata)>();
        var references = ImmutableDictionary.CreateBuilder<IntelligenceGateway, SecretReference>();
        var voiceReferences = ImmutableDictionary.CreateBuilder<string, SecretReference>();
        Add(options.OmniRoute, IntelligenceGateway.OmniRoute);
        Add(options.LiteLlm, IntelligenceGateway.LiteLlm);
        Add(options.Frontier, IntelligenceGateway.Frontier);
        AddVoice(SpeechCredentialNames.Deepgram, "ABRAXIUS_DEEPGRAM_API_KEY", "Deepgram speech credential", "wss://api.deepgram.com");
        AddVoice(SpeechCredentialNames.ElevenLabs, "ABRAXIUS_ELEVENLABS_API_KEY", "ElevenLabs speech credential", "wss://api.elevenlabs.io");
        AddDesignCredential("ABRAXIUS_STITCH_API_KEY", "Google Stitch API key", "secret://google/stitch/api-key");
        AddDesignCredential("ABRAXIUS_STITCH_ACCESS_TOKEN", "Google Stitch access token", "secret://google/stitch/access-token");

        ISecretStore store = mappings.Count == 0
            ? writableStore
            : new CompositeSecretStore(writableStore, new EnvironmentSecretStore(mappings));
        return new ModelSecretBootstrap(store, references.ToImmutable(), voiceReferences.ToImmutable(), SecuritySubject.System("brokered-transport"));

        void Add(GatewayConnectionOptions gateway, IntelligenceGateway kind)
        {
            if (!gateway.Enabled || string.IsNullOrWhiteSpace(gateway.ApiKeyEnvironmentVariable) ||
                !Uri.TryCreate(gateway.Endpoint, UriKind.Absolute, out var endpoint)) return;

            var reference = new SecretReference($"secret://model/{kind.ToString().ToLowerInvariant()}");
            var destination = endpoint.GetLeftPart(UriPartial.Authority);
            var metadata = new SecretMetadata(reference, $"{kind} model credential", "environment",
                [destination], DateTimeOffset.UtcNow, RequiresApproval: false);
            mappings[reference] = (gateway.ApiKeyEnvironmentVariable, metadata);
            references[kind] = reference;
        }

        void AddVoice(string name, string variable, string displayName, string destination)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))) return;
            var reference = new SecretReference($"secret://voice/{name}");
            var metadata = new SecretMetadata(reference, displayName, "environment", [destination], DateTimeOffset.UtcNow, RequiresApproval: false);
            mappings[reference] = (variable, metadata);
            voiceReferences[name] = reference;
        }

        void AddDesignCredential(string variable, string displayName, string referenceText)
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)) || !SecretReference.TryParse(referenceText, out var reference)) return;
            mappings[reference] = (variable, new SecretMetadata(reference, displayName, "environment", ["https://stitch.googleapis.com"], DateTimeOffset.UtcNow, RequiresApproval: false));
        }
    }
}

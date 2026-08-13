using System.Collections.Immutable;

namespace Abraxius.Voice;

public interface ISpeechRouteEngine
{
    ValueTask<SpeechRouteDecision> SelectSttAsync(
        SpeechRouteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<SpeechRouteDecision> SelectTtsAsync(
        SpeechRouteRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class SpeechRouteEngine : ISpeechRouteEngine
{
    private readonly ImmutableArray<SpeechProviderDescriptor> _providers;

    public SpeechRouteEngine(IEnumerable<SpeechProviderDescriptor> providers)
    {
        _providers = providers.ToImmutableArray();
    }

    public ValueTask<SpeechRouteDecision> SelectSttAsync(SpeechRouteRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Select(request with { Kind = SpeechRouteKind.Stt }));

    public ValueTask<SpeechRouteDecision> SelectTtsAsync(SpeechRouteRequest request, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Select(request with { Kind = SpeechRouteKind.Tts }));

    private SpeechRouteDecision Select(SpeechRouteRequest request)
    {
        var candidates = new List<SpeechRouteCandidate>(_providers.Length);
        SpeechProviderDescriptor? selected = null;
        var selectedScore = int.MinValue;

        foreach (var provider in _providers)
        {
            var rejection = Reject(provider, request);
            if (rejection is not null)
            {
                candidates.Add(new SpeechRouteCandidate(provider, int.MinValue, rejection));
                continue;
            }

            var score = Score(provider, request);
            candidates.Add(new SpeechRouteCandidate(provider, score));
            if (selected is null || score > selectedScore || (score == selectedScore && string.CompareOrdinal(provider.Id.Value, selected.Id.Value) < 0))
            {
                selected = provider;
                selectedScore = score;
            }
        }

        if (selected is null)
        {
            throw new SpeechProviderException(new SpeechError(
                request.Kind == SpeechRouteKind.Stt ? SpeechErrorCode.SttUnavailable : SpeechErrorCode.TtsUnavailable,
                "No speech provider satisfies the current capability, privacy, health, and cost policy."));
        }

        var reason = request.PreferredProvider is not null && selected.Id.Value.Equals(request.PreferredProvider, StringComparison.OrdinalIgnoreCase)
            ? "Explicit provider preference satisfied."
            : $"Selected {selected.Id} with score {selectedScore} under {request.Mode} speech routing.";
        return new SpeechRouteDecision(selected.Id, request.Kind, request.Mode, reason, candidates, selected.CostClass, DateTimeOffset.UtcNow);
    }

    private static string? Reject(SpeechProviderDescriptor provider, SpeechRouteRequest request)
    {
        if (request.PreferredProvider is not null && !provider.Id.Value.Equals(request.PreferredProvider, StringComparison.OrdinalIgnoreCase))
        {
            return "Not the manually selected provider.";
        }

        if (!provider.Supports(request.RequiredCapabilities))
        {
            return "Required speech capability is missing.";
        }

        if (request.Streaming && !provider.Supports(SpeechCapabilities.Streaming))
        {
            return "Streaming is required.";
        }

        if (request.RequireLocal && provider.Type is not SpeechProviderType.Local and not SpeechProviderType.Sidecar and not SpeechProviderType.Mock)
        {
            return "Private mode requires a local or explicitly supervised sidecar provider.";
        }

        if (request.Context.PrivateMode && provider.Type == SpeechProviderType.Cloud)
        {
            return "Private mode blocks cloud speech.";
        }

        if (provider.Health.Status is SpeechProviderHealthStatus.Unavailable or SpeechProviderHealthStatus.RateLimited)
        {
            return $"Provider is {provider.Health.Status}.";
        }

        if (request.MaximumCost is 0 && provider.CostClass is not SpeechCostClass.Zero and not SpeechCostClass.Included)
        {
            return "Provider exceeds the zero-cost policy.";
        }

        if (request.Language is not null && provider.Languages.Count > 0 && !provider.Languages.Contains(request.Language, StringComparer.OrdinalIgnoreCase))
        {
            return "Provider does not advertise the requested language.";
        }

        return null;
    }

    private static int Score(SpeechProviderDescriptor provider, SpeechRouteRequest request)
    {
        var score = provider.Health.Status == SpeechProviderHealthStatus.Healthy ? 100 : 60;
        score += request.Mode switch
        {
            SpeechRoutingMode.Quality => provider.Type == SpeechProviderType.Cloud ? 40 : 20,
            SpeechRoutingMode.BalancedQuality => provider.Type == SpeechProviderType.Cloud ? 30 : 25,
            SpeechRoutingMode.LocalFirst => provider.Type is SpeechProviderType.Local or SpeechProviderType.Sidecar ? 50 : 0,
            SpeechRoutingMode.Private => provider.Type is SpeechProviderType.Local or SpeechProviderType.Sidecar ? 80 : 0,
            SpeechRoutingMode.Manual => 20,
            _ => 0
        };
        score += request.Kind == SpeechRouteKind.Tts && provider.Supports(SpeechCapabilities.Expressive) ? 15 : 0;
        score += request.Kind == SpeechRouteKind.Stt && provider.Supports(SpeechCapabilities.NoiseRobust) ? 15 : 0;
        score += provider.CostClass switch
        {
            SpeechCostClass.Zero => request.Mode == SpeechRoutingMode.LocalFirst ? 25 : 5,
            SpeechCostClass.Included => 4,
            SpeechCostClass.Low => 1,
            SpeechCostClass.Premium => request.Mode == SpeechRoutingMode.Quality ? 8 : -10,
            _ => 0
        };
        return score;
    }
}

public sealed class SpeechProviderRegistry
{
    private readonly Dictionary<SpeechProviderId, ISpeechToTextProvider> _stt = [];
    private readonly Dictionary<SpeechProviderId, ITextToSpeechProvider> _tts = [];

    public void Register(ISpeechToTextProvider provider) => _stt[provider.Descriptor.Id] = provider;
    public void Register(ITextToSpeechProvider provider) => _tts[provider.Descriptor.Id] = provider;

    public ISpeechToTextProvider GetStt(SpeechProviderId id) =>
        _stt.TryGetValue(id, out var provider)
            ? provider
            : throw new SpeechProviderException(new SpeechError(SpeechErrorCode.SttUnavailable, $"STT provider '{id}' is not registered.", id));

    public ITextToSpeechProvider GetTts(SpeechProviderId id) =>
        _tts.TryGetValue(id, out var provider)
            ? provider
            : throw new SpeechProviderException(new SpeechError(SpeechErrorCode.TtsUnavailable, $"TTS provider '{id}' is not registered.", id));
}

public sealed class RoutedSpeechToTextProvider(
    ISpeechRouteEngine routes,
    SpeechProviderRegistry registry,
    SpeechRouteRequest request) : ISpeechToTextProvider
{
    public SpeechProviderDescriptor Descriptor => new()
    {
        Id = new SpeechProviderId("routed-stt"),
        Type = SpeechProviderType.Mock,
        Capabilities = SpeechCapabilities.Streaming,
        Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
        CostClass = SpeechCostClass.Unknown
    };

    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        TranscriptionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var decision = await routes.SelectSttAsync(request with
        {
            Mode = context.RoutingMode,
            Context = context.Speech,
            Language = context.Language
        }, cancellationToken).ConfigureAwait(false);
        await using var replayable = new ReplayableAudioStream(audio, 12_000);
        var candidates = decision.Candidates
            .Where(static candidate => candidate.RejectionReason is null)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Provider.Id.Value, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            var provider = registry.GetStt(candidate.Provider.Id);
            var transientFailure = (SpeechProviderException?)null;
            var completed = false;
            var enumerator = provider.TranscribeAsync(replayable.ReadFromStart(cancellationToken), context, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (SpeechProviderException exception) when (exception.Error.IsTransient && !cancellationToken.IsCancellationRequested)
                    {
                        transientFailure = exception;
                        break;
                    }

                    if (!moved)
                    {
                        completed = true;
                        break;
                    }

                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (completed) yield break;
            if (transientFailure is not null && index + 1 < candidates.Length)
            {
                yield return new TranscriptionEvent.ProviderChanged(DateTimeOffset.UtcNow, candidate.Provider.Id.Value, candidates[index + 1].Provider.Id.Value, transientFailure.Error.Message);
                continue;
            }

            if (transientFailure is not null) throw transientFailure;
        }

        throw new SpeechProviderException(new SpeechError(SpeechErrorCode.SttUnavailable, "All eligible streaming STT providers failed."));
    }
}

public sealed class RoutedTextToSpeechProvider(
    ISpeechRouteEngine routes,
    SpeechProviderRegistry registry,
    SpeechRouteRequest routeRequest) : ITextToSpeechProvider
{
    public SpeechProviderDescriptor Descriptor => new()
    {
        Id = new SpeechProviderId("routed-tts"),
        Type = SpeechProviderType.Mock,
        Capabilities = SpeechCapabilities.Streaming,
        Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
        CostClass = SpeechCostClass.Unknown
    };

    public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        SpeechSynthesisRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var decision = await routes.SelectTtsAsync(routeRequest with
        {
            Mode = request.RoutingMode,
            Context = request.Context ?? new SpeechContext(),
            Language = request.Language
        }, cancellationToken).ConfigureAwait(false);
        var candidates = decision.Candidates
            .Where(static candidate => candidate.RejectionReason is null)
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Provider.Id.Value, StringComparer.Ordinal)
            .ToArray();
        for (var index = 0; index < candidates.Length; index++)
        {
            var provider = registry.GetTts(candidates[index].Provider.Id);
            var emitted = false;
            var transientFailure = (SpeechProviderException?)null;
            var completed = false;
            var enumerator = provider.SynthesizeAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (SpeechProviderException exception) when (exception.Error.IsTransient && !emitted && !cancellationToken.IsCancellationRequested)
                    {
                        transientFailure = exception;
                        break;
                    }

                    if (!moved)
                    {
                        completed = true;
                        break;
                    }

                    emitted = true;
                    yield return enumerator.Current;
                }
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (completed) yield break;
            if (transientFailure is not null && !emitted && index + 1 < candidates.Length)
            {
                // A segment has not reached playback yet, so the next provider can
                // synthesize it without repeating audible content.
                continue;
            }

            if (transientFailure is not null) throw transientFailure;
        }

        throw new SpeechProviderException(new SpeechError(SpeechErrorCode.TtsUnavailable, "All eligible streaming TTS providers failed."));
    }
}

using System.Runtime.CompilerServices;

namespace Abraxius.Voice;

public sealed class FallbackSpeechToTextProvider(
    ISpeechToTextProvider primary,
    ISpeechToTextProvider fallback,
    int maxReplayFrames = 12_000) : ISpeechToTextProvider
{
    private readonly int _maxReplayFrames = maxReplayFrames > 0
        ? maxReplayFrames
        : throw new ArgumentOutOfRangeException(nameof(maxReplayFrames));

    public SpeechProviderDescriptor Descriptor => primary.Descriptor;

    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        TranscriptionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var replayable = new ReplayableAudioStream(audio, _maxReplayFrames);
        var useFallback = false;
        var enumerator = primary.TranscribeAsync(replayable.ReadFromStart(cancellationToken), context, cancellationToken).GetAsyncEnumerator(cancellationToken);
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
                    useFallback = true;
                    break;
                }

                if (!moved) yield break;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (!useFallback) yield break;
        // The replayable stream retains only frames already consumed by the primary
        // provider and continues from the same source for the fallback provider.
        await foreach (var item in fallback.TranscribeAsync(replayable.ReadFromStart(cancellationToken), context, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }
}

public sealed class FallbackTextToSpeechProvider(
    ITextToSpeechProvider primary,
    ITextToSpeechProvider fallback) : ITextToSpeechProvider
{
    public SpeechProviderDescriptor Descriptor => primary.Descriptor;

    public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        SpeechSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var emittedAudio = false;
        var useFallback = false;
        var enumerator = primary.SynthesizeAsync(request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try
                {
                    moved = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (SpeechProviderException exception) when (exception.Error.IsTransient && !emittedAudio && !cancellationToken.IsCancellationRequested)
                {
                    useFallback = true;
                    break;
                }

                if (!moved) yield break;
                emittedAudio = true;
                yield return enumerator.Current;
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }

        if (!useFallback) yield break;
        // Switching after audio has been heard would repeat the segment. The
        // orchestrator can retry the next segment with a newly selected route.
        if (emittedAudio)
        {
            throw new SpeechProviderException(new SpeechError(
                SpeechErrorCode.TtsUnavailable,
                "The primary TTS provider failed after audio had started; the segment was not replayed.",
                primary.Descriptor.Id,
                IsTransient: true));
        }

        await foreach (var item in fallback.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false))
        {
            yield return item;
        }
    }
}

/// <summary>
/// Keeps a bounded-in-practice replay window for a single utterance while allowing
/// the primary STT provider to consume the source immediately. It does not
/// materialize the audio before the primary provider starts.
/// </summary>
internal sealed class ReplayableAudioStream(IAsyncEnumerable<AudioFrame> source, int maxReplayFrames) : IAsyncDisposable
{
    private readonly IAsyncEnumerable<AudioFrame> _source = source;
    private readonly int _maxReplayFrames = maxReplayFrames;
    private readonly List<AudioFrame> _frames = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IAsyncEnumerator<AudioFrame>? _sourceEnumerator;
    private bool _completed;
    private bool _disposed;

    public async IAsyncEnumerable<AudioFrame> ReadFromStart(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var index = 0;
        while (true)
        {
            AudioFrame frame = default;
            var hasFrame = false;

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (index < _frames.Count)
                {
                    frame = _frames[index++];
                    hasFrame = true;
                }
                else if (!_completed)
                {
                    if (_frames.Count >= _maxReplayFrames)
                    {
                        throw new SpeechProviderException(new SpeechError(
                            SpeechErrorCode.SttUnavailable,
                            $"Speech failover replay window reached {_maxReplayFrames} frames; the utterance was not replayed.",
                            IsTransient: false));
                    }

                    _sourceEnumerator ??= _source.GetAsyncEnumerator(cancellationToken);
                    if (await _sourceEnumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        frame = _sourceEnumerator.Current;
                        _frames.Add(frame);
                        index++;
                        hasFrame = true;
                    }
                    else
                    {
                        _completed = true;
                        await _sourceEnumerator.DisposeAsync().ConfigureAwait(false);
                        _sourceEnumerator = null;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }

            if (!hasFrame)
            {
                yield break;
            }

            yield return frame;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_sourceEnumerator is not null)
            {
                await _sourceEnumerator.DisposeAsync().ConfigureAwait(false);
                _sourceEnumerator = null;
            }

            _completed = true;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Abraxius.Models;

namespace Abraxius.Voice;

public sealed class SpeechSegmenter
{
    private readonly int _minimumCharacters;
    private readonly int _maximumCharacters;

    public SpeechSegmenter(int minimumCharacters = 24, int maximumCharacters = 240)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, minimumCharacters);
        _minimumCharacters = minimumCharacters;
        _maximumCharacters = maximumCharacters;
    }

    public async IAsyncEnumerable<string> SegmentAsync(
        IAsyncEnumerable<string> text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var buffer = new System.Text.StringBuilder();
        await foreach (var delta in text.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrEmpty(delta)) continue;
            buffer.Append(delta);
            while (TryTakeSegment(buffer, out var segment))
            {
                yield return segment;
            }
        }

        if (buffer.Length > 0)
        {
            yield return buffer.ToString().Trim();
        }
    }

    private bool TryTakeSegment(System.Text.StringBuilder buffer, out string segment)
    {
        segment = string.Empty;
        if (buffer.Length < _minimumCharacters) return false;

        var boundary = -1;
        for (var index = 0; index < buffer.Length; index++)
        {
            var character = buffer[index];
            if (character is '.' or '!' or '?' or ';' or ':' || character is '\n' or '\r')
            {
                boundary = index + 1;
                break;
            }
        }

        if (boundary < 0 && buffer.Length >= _maximumCharacters)
        {
            boundary = buffer.ToString(0, _maximumCharacters).LastIndexOf(' ') + 1;
            if (boundary <= 0) boundary = _maximumCharacters;
        }

        if (boundary <= 0) return false;
        segment = buffer.ToString(0, boundary).Trim();
        buffer.Remove(0, boundary);
        return segment.Length > 0;
    }
}

public sealed class ModelVoiceResponseGenerator(IModelProvider modelProvider) : IVoiceResponseGenerator
{
    public async IAsyncEnumerable<string> GenerateAsync(
        string transcript,
        VoiceTurnId turnId,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new ModelRequest(
            $"Respond briefly and naturally to this spoken Abraxius request. Prefer an actionable summary and refer the user to the workstation for details:\n{transcript}",
            Priority: Abraxius.Protocol.WorkPriority.Interactive)
        {
            TaskClass = IntelligenceTaskClass.General,
            Complexity = IntelligenceComplexity.Simple,
            Policy = new IntelligenceRequestPolicy { Mode = IntelligenceRoutingMode.Balanced },
            SessionKey = $"voice:{turnId}",
            DataClassification = DataClassification.Internal
        };

        await foreach (var item in modelProvider.StreamAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is ModelStreamEvent.Token token)
            {
                yield return token.Text;
            }
        }
    }
}

public sealed class VoiceConversationOrchestrator : IAsyncDisposable
{
    private readonly IAudioCaptureService _capture;
    private readonly IVoiceActivityDetector _vad;
    private readonly ISpeechToTextProvider _stt;
    private readonly ITextToSpeechProvider _tts;
    private readonly IAudioPlaybackService _playback;
    private readonly ISpeechVocabularyProvider _vocabulary;
    private readonly IVoiceResponseGenerator _responseGenerator;
    private readonly IVoiceIntentSink? _intentSink;
    private readonly IWakeWordDetector _wakeWord;
    private readonly IVoiceEventSource _events;
    private readonly VoiceEventHub? _eventHub;
    private readonly SpeechSegmenter _segmenter;
    private readonly AudioGenerationGate _generationGate = new();
    private readonly object _speechGate = new();
    private CancellationTokenSource? _speechCancellation;
    private VoiceGenerationId _activeGeneration;
    private int _disposed;

    public VoiceConversationOrchestrator(
        IAudioCaptureService capture,
        IVoiceActivityDetector vad,
        ISpeechToTextProvider stt,
        ITextToSpeechProvider tts,
        IAudioPlaybackService playback,
        ISpeechVocabularyProvider vocabulary,
        IVoiceResponseGenerator responseGenerator,
        IVoiceEventSource events,
        SpeechSegmenter? segmenter = null,
        IVoiceIntentSink? intentSink = null,
        IWakeWordDetector? wakeWord = null)
    {
        _capture = capture;
        _vad = vad;
        _stt = stt;
        _tts = tts;
        _playback = playback;
        _vocabulary = vocabulary;
        _responseGenerator = responseGenerator;
        _intentSink = intentSink;
        _wakeWord = wakeWord ?? new DisabledWakeWordDetector();
        _events = events;
        _eventHub = events as VoiceEventHub;
        _segmenter = segmenter ?? new SpeechSegmenter();
    }

    public VoiceTurnState State { get; private set; } = VoiceTurnState.Idle;
    public VoiceGenerationId ActiveGeneration => _activeGeneration;

    public Task SpeakAsync(
        string transcript,
        VoiceSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var turnId = VoiceTurnId.New();
        return SpeakResponseAsync(transcript, turnId, NewGeneration(), options ?? new VoiceSessionOptions(), null, cancellationToken);
    }

    public async Task RunAsync(
        VoiceSessionOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var permission = await _capture.GetPermissionAsync(cancellationToken).ConfigureAwait(false);
        if (permission != AudioPermissionStatus.Granted)
        {
            State = VoiceTurnState.Error;
            throw new SpeechProviderException(new SpeechError(SpeechErrorCode.PermissionDenied, $"Microphone permission is {permission}."));
        }

        var turnId = VoiceTurnId.New();
        var generation = NewGeneration();
        await PublishAsync(new VoiceEvent.SessionStarted(turnId, generation, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
        State = VoiceTurnState.Listening;
        await PublishAsync(new VoiceEvent.ListeningStarted(turnId, generation, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);

        var captureOptions = options.Capture ?? new AudioCaptureOptions(AudioFormat.NormalizedSpeech, TimeSpan.FromMilliseconds(20));
        var activityOptions = options.ActivityDetection ?? new VoiceActivityOptions();
        var preprocessor = options.EchoCanceller ?? options.Preprocessor ?? AudioPreprocessorFactory.Create(captureOptions.Preprocessing);
        var utterances = Channel.CreateBounded<ImmutableArray<AudioFrame>>(new BoundedChannelOptions(8)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });

        async Task CaptureLoopAsync()
        {
            var preRoll = new PreRollAudioBuffer(activityOptions.PreRollFrames);
            var utterance = new List<AudioFrame>(64);
            var speaking = false;
            var postRollRemaining = 0;
            var wakeActive = options.Mode != VoiceMode.WakeWord;
            try
            {
                await foreach (var capturedFrame in _capture.CaptureAsync(captureOptions, cancellationToken).ConfigureAwait(false))
                {
                    var frame = preprocessor.Process(capturedFrame);
                    if (!wakeActive)
                    {
                        wakeActive = await _wakeWord.ProcessAsync(frame, cancellationToken).ConfigureAwait(false);
                        if (!wakeActive) continue;
                        _wakeWord.Reset();
                    }

                    var vad = _vad.Process(frame);
                    _eventHub?.PublishTelemetry(new VoiceTelemetry(DateTimeOffset.UtcNow, turnId, _activeGeneration, vad.Level, vad.State, utterance.Count));
                    if (!speaking) preRoll.Add(frame);

                    if (vad.Activity == SpeechActivityKind.SpeechStarted)
                    {
                        if (State == VoiceTurnState.Speaking && options.BargeInEnabled)
                        {
                            await InterruptAsync("user speech detected", turnId, cancellationToken).ConfigureAwait(false);
                        }

                        if (!speaking)
                        {
                            speaking = true;
                            State = VoiceTurnState.SpeechDetected;
                            utterance.AddRange(preRoll.Snapshot());
                            await PublishAsync(new VoiceEvent.SpeechDetected(turnId, _activeGeneration, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                        }

                        postRollRemaining = 0;
                        utterance.Add(frame);
                    }
                    else if (speaking)
                    {
                        utterance.Add(frame);
                        if (vad.Activity == SpeechActivityKind.SpeechEnded)
                        {
                            await PublishAsync(new VoiceEvent.SpeechEnded(turnId, _activeGeneration, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
                            postRollRemaining = activityOptions.PostRollFrames;
                            if (postRollRemaining == 0)
                            {
                                speaking = false;
                                var completed = utterance.ToImmutableArray();
                                utterance.Clear();
                                preRoll = new PreRollAudioBuffer(activityOptions.PreRollFrames);
                                if (options.Mode == VoiceMode.WakeWord) wakeActive = false;
                                await utterances.Writer.WriteAsync(completed, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        else if (postRollRemaining > 0 && --postRollRemaining == 0)
                        {
                            speaking = false;
                            var completed = utterance.ToImmutableArray();
                            utterance.Clear();
                            preRoll = new PreRollAudioBuffer(activityOptions.PreRollFrames);
                            if (options.Mode == VoiceMode.WakeWord) wakeActive = false;
                            await utterances.Writer.WriteAsync(completed, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                if (utterance.Count > 0)
                {
                    await utterances.Writer.WriteAsync(utterance.ToImmutableArray(), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                utterances.Writer.TryComplete();
            }
        }

        async Task ProcessLoopAsync()
        {
            await foreach (var utterance in utterances.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    State = VoiceTurnState.Transcribing;
                    await ProcessUtteranceAsync(utterance, turnId, options, cancellationToken).ConfigureAwait(false);
                    if (State == VoiceTurnState.Transcribing) State = VoiceTurnState.Listening;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // A barge-in cancels only the stale speech generation. The session remains live.
                    State = VoiceTurnState.Listening;
                }
                catch (SpeechProviderException exception)
                {
                    State = VoiceTurnState.Error;
                    await PublishAsync(new VoiceEvent.ErrorEvent(turnId, _activeGeneration, DateTimeOffset.UtcNow, exception.Error), cancellationToken).ConfigureAwait(false);
                    State = VoiceTurnState.Listening;
                }
            }
        }

        try
        {
            await Task.WhenAll(CaptureLoopAsync(), ProcessLoopAsync()).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            State = VoiceTurnState.Interrupted;
            await StopSpeechAsync("session cancelled", cancellationToken).ConfigureAwait(false);
        }
        catch (SpeechProviderException exception)
        {
            State = VoiceTurnState.Error;
            await PublishAsync(new VoiceEvent.ErrorEvent(turnId, _activeGeneration, DateTimeOffset.UtcNow, exception.Error), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            await PublishAsync(new VoiceEvent.SessionCompleted(turnId, _activeGeneration, DateTimeOffset.UtcNow), CancellationToken.None).ConfigureAwait(false);
            State = VoiceTurnState.Idle;
        }
    }

    public async ValueTask InterruptAsync(string reason = "barge-in", CancellationToken cancellationToken = default)
    {
        var turnId = VoiceTurnId.New();
        await InterruptAsync(reason, turnId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask InterruptAsync(string reason, VoiceTurnId turnId, CancellationToken cancellationToken)
    {
        VoiceGenerationId generation;
        lock (_speechGate)
        {
            generation = _activeGeneration;
            _speechCancellation?.Cancel();
            _speechCancellation = null;
            _activeGeneration = NewGeneration();
            State = VoiceTurnState.Interrupted;
        }

        await _playback.StopAsync(generation, cancellationToken).ConfigureAwait(false);
        await PublishAsync(new VoiceEvent.VoiceInterrupted(turnId, generation, DateTimeOffset.UtcNow, reason), cancellationToken).ConfigureAwait(false);
        await PublishAsync(new VoiceEvent.PlaybackStopped(turnId, generation, DateTimeOffset.UtcNow, reason), cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessUtteranceAsync(
        IReadOnlyList<AudioFrame> frames,
        VoiceTurnId turnId,
        VoiceSessionOptions options,
        CancellationToken cancellationToken)
    {
        if (frames.Count == 0) return;
        var vocabulary = await _vocabulary.BuildAsync(options.Context ?? new SpeechContext(PrivateMode: options.PrivateMode), cancellationToken).ConfigureAwait(false);
        var context = new TranscriptionContext(
            frames[0].Format,
            (options.Context ?? new SpeechContext(PrivateMode: options.PrivateMode)) with { Vocabulary = vocabulary },
            options.Language,
            RoutingMode: options.RoutingMode);
        async IAsyncEnumerable<AudioFrame> ReadFrames([EnumeratorCancellation] CancellationToken token = default)
        {
            foreach (var frame in frames)
            {
                token.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Yield();
            }
        }

        string? finalText = null;
        await foreach (var item in _stt.TranscribeAsync(ReadFrames(cancellationToken), context, cancellationToken).ConfigureAwait(false))
        {
            switch (item)
            {
                case TranscriptionEvent.PartialTranscript partial:
                    await PublishAsync(new VoiceEvent.PartialTranscriptUpdated(turnId, _activeGeneration, item.Timestamp, partial.Text), cancellationToken).ConfigureAwait(false);
                    break;
                case TranscriptionEvent.Final final:
                    finalText = final.Text;
                    await PublishAsync(new VoiceEvent.TranscriptFinalized(turnId, _activeGeneration, item.Timestamp, final.Text), cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(finalText) || !options.AutoSubmitFinalTranscript) return;
        var response = _intentSink is null
            ? null
            : await _intentSink.SubmitAsync(finalText, turnId, cancellationToken).ConfigureAwait(false);
        State = VoiceTurnState.Processing;
        var generation = NewGeneration();
        await SpeakResponseAsync(finalText, turnId, generation, options, response, cancellationToken).ConfigureAwait(false);
    }

    private async Task SpeakResponseAsync(
        string transcript,
        VoiceTurnId turnId,
        VoiceGenerationId generation,
        VoiceSessionOptions options,
        VoiceResponse? response,
        CancellationToken cancellationToken)
    {
        using var speechCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_speechGate)
        {
            _speechCancellation?.Cancel();
            _speechCancellation = speechCts;
            _activeGeneration = generation;
            State = VoiceTurnState.Speaking;
        }

        try
        {
            await PublishAsync(new VoiceEvent.SpeechGenerationStarted(turnId, generation, DateTimeOffset.UtcNow, transcript), cancellationToken).ConfigureAwait(false);
            var text = response?.StreamingText ??
                (response is not null ? OneResponseAsync(response.Text, speechCts.Token) : _responseGenerator.GenerateAsync(transcript, turnId, speechCts.Token));
            await foreach (var segment in _segmenter.SegmentAsync(text, speechCts.Token).ConfigureAwait(false))
            {
                speechCts.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(generation)) return;
                var request = new SpeechSynthesisRequest(
                    segment,
                    options.Voice,
                    options.Language,
                    OutputFormat: AudioFormat.NormalizedSpeech,
                    Context: options.Context ?? new SpeechContext(PrivateMode: options.PrivateMode),
                    RoutingMode: options.RoutingMode);
                await PublishAsync(new VoiceEvent.PlaybackStarted(turnId, generation, DateTimeOffset.UtcNow), speechCts.Token).ConfigureAwait(false);
                await _playback.PlayAsync(_tts.SynthesizeAsync(request, speechCts.Token), options.Playback ?? new AudioPlaybackOptions(AudioFormat.NormalizedSpeech), generation, speechCts.Token).ConfigureAwait(false);
                if (!IsCurrent(generation)) return;
            }

            await PublishAsync(new VoiceEvent.PlaybackStopped(turnId, generation, DateTimeOffset.UtcNow, "completed"), CancellationToken.None).ConfigureAwait(false);
            State = VoiceTurnState.Listening;
        }
        finally
        {
            lock (_speechGate)
            {
                if (ReferenceEquals(_speechCancellation, speechCts))
                {
                    _speechCancellation = null;
                }
            }
        }
    }

    private async ValueTask StopSpeechAsync(string reason, CancellationToken cancellationToken)
    {
        VoiceGenerationId generation;
        lock (_speechGate)
        {
            generation = _activeGeneration;
            _speechCancellation?.Cancel();
        }

        await _playback.StopAsync(generation, cancellationToken).ConfigureAwait(false);
    }

    private VoiceGenerationId NewGeneration() => _generationGate.Advance();
    private bool IsCurrent(VoiceGenerationId generation) => _generationGate.IsCurrent(generation);

    private ValueTask PublishAsync(VoiceEvent value, CancellationToken cancellationToken) =>
        _eventHub?.PublishAsync(value, cancellationToken) ?? ValueTask.CompletedTask;

    private static async IAsyncEnumerable<string> OneResponseAsync(
        string text,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return text;
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            await StopSpeechAsync("disposed", CancellationToken.None).ConfigureAwait(false);
            if (_eventHub is not null)
            {
                await _eventHub.DisposeAsync().ConfigureAwait(false);
            }
        }

        GC.SuppressFinalize(this);
    }
}

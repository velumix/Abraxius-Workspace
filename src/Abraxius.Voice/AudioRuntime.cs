using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Abraxius.Voice;

public sealed class VoiceEventHub : IVoiceEventSource, IAsyncDisposable
{
    private readonly Channel<VoiceEvent> _events = Channel.CreateBounded<VoiceEvent>(new BoundedChannelOptions(512)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = false,
        SingleWriter = false
    });
    private readonly Channel<VoiceTelemetry> _telemetry = Channel.CreateBounded<VoiceTelemetry>(new BoundedChannelOptions(128)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = false,
        SingleWriter = false
    });
    private int _completed;

    public async ValueTask PublishAsync(VoiceEvent value, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _completed) != 0)
        {
            return;
        }

        await _events.Writer.WriteAsync(value, cancellationToken).ConfigureAwait(false);
    }

    public void PublishTelemetry(VoiceTelemetry value)
    {
        if (Volatile.Read(ref _completed) == 0)
        {
            _telemetry.Writer.TryWrite(value);
        }
    }

    public IAsyncEnumerable<VoiceEvent> ReadEventsAsync(CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    public IAsyncEnumerable<VoiceTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken = default) =>
        _telemetry.Reader.ReadAllAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _events.Writer.TryComplete();
            _telemetry.Writer.TryComplete();
        }

        return ValueTask.CompletedTask;
    }
}

public sealed class PreRollAudioBuffer
{
    private readonly AudioFrame[] _frames;
    private int _count;
    private int _next;

    public PreRollAudioBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        _frames = new AudioFrame[capacity];
    }

    public int Count => _count;

    public void Add(AudioFrame frame)
    {
        if (_frames.Length == 0) return;
        _frames[_next] = frame;
        _next = (_next + 1) % _frames.Length;
        _count = Math.Min(_count + 1, _frames.Length);
    }

    public ImmutableArray<AudioFrame> Snapshot()
    {
        if (_count == 0) return ImmutableArray<AudioFrame>.Empty;
        var builder = ImmutableArray.CreateBuilder<AudioFrame>(_count);
        var start = (_next - _count + _frames.Length) % _frames.Length;
        for (var index = 0; index < _count; index++)
        {
            builder.Add(_frames[(start + index) % _frames.Length]);
        }

        return builder.MoveToImmutable();
    }
}

public sealed class AudioGenerationGate
{
    private long _generation;

    public VoiceGenerationId Advance() => new(Interlocked.Increment(ref _generation));

    public bool IsCurrent(VoiceGenerationId generation) => generation.Value == Volatile.Read(ref _generation);
}

public sealed class PassthroughAudioPreprocessor : IAudioPreprocessor
{
    public AudioFrame Process(AudioFrame frame) => frame;
}

/// <summary>
/// Small deterministic host-side gate for explicitly noisy profiles. It is not a
/// replacement for a platform AEC/NS implementation; it only suppresses samples
/// below a configured floor before VAD/STT receive them.
/// </summary>
public sealed class PcmNoiseGateAudioPreprocessor(short threshold = 320) : IAudioPreprocessor
{
    private readonly short _threshold = ValidateThreshold(threshold);

    public AudioFrame Process(AudioFrame frame)
    {
        if (frame.Format.SampleType != AudioSampleType.Pcm16 || frame.Data.IsEmpty) return frame;
        var data = frame.Data.ToArray();
        for (var index = 0; index + 1 < data.Length; index += 2)
        {
            var sample = BitConverter.ToInt16(data, index);
            if (Math.Abs((int)sample) < _threshold)
            {
                data[index] = 0;
                data[index + 1] = 0;
            }
        }

        return frame with { Data = data };
    }

    private static short ValidateThreshold(short value) =>
        value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
}

public static class AudioPreprocessorFactory
{
    public static IAudioPreprocessor Create(AudioPreprocessingProfile profile) =>
        profile == AudioPreprocessingProfile.NoisyEnvironment
            ? new PcmNoiseGateAudioPreprocessor()
            : new PassthroughAudioPreprocessor();
}

public sealed class EnergyVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly VoiceActivityOptions _options;
    private VadState _state;
    private int _speechFrames;
    private int _silenceFrames;

    public EnergyVoiceActivityDetector(VoiceActivityOptions? options = null)
    {
        _options = options ?? new VoiceActivityOptions();
    }

    public VadResult Process(AudioFrame frame)
    {
        var level = CalculateLevel(frame);
        var aboveThreshold = level >= _options.SpeechThreshold;
        var activity = SpeechActivityKind.None;

        switch (_state)
        {
            case VadState.Silence when aboveThreshold:
                _speechFrames = 1;
                _silenceFrames = 0;
                _state = _options.StartFrames == 1 ? VadState.Speech : VadState.PossibleSpeech;
                if (_state == VadState.Speech) activity = SpeechActivityKind.SpeechStarted;
                break;
            case VadState.PossibleSpeech when aboveThreshold:
                _speechFrames++;
                if (_speechFrames >= _options.StartFrames)
                {
                    _state = VadState.Speech;
                    activity = SpeechActivityKind.SpeechStarted;
                }
                break;
            case VadState.PossibleSpeech:
                _speechFrames = 0;
                _state = VadState.Silence;
                break;
            case VadState.Speech when aboveThreshold:
                _silenceFrames = 0;
                activity = SpeechActivityKind.SpeechContinued;
                break;
            case VadState.Speech:
                _silenceFrames = 1;
                _state = _options.EndFrames == 1 ? VadState.Silence : VadState.PossibleEnd;
                if (_state == VadState.Silence) activity = SpeechActivityKind.SpeechEnded;
                break;
            case VadState.PossibleEnd when !aboveThreshold:
                _silenceFrames++;
                if (_silenceFrames >= _options.EndFrames)
                {
                    _state = VadState.Silence;
                    _speechFrames = 0;
                    activity = SpeechActivityKind.SpeechEnded;
                }
                break;
            case VadState.PossibleEnd:
                _silenceFrames = 0;
                _state = VadState.Speech;
                activity = SpeechActivityKind.SpeechContinued;
                break;
        }

        return new VadResult(_state, activity, level, frame);
    }

    public void Reset()
    {
        _state = VadState.Silence;
        _speechFrames = 0;
        _silenceFrames = 0;
    }

    private static float CalculateLevel(AudioFrame frame)
    {
        if (frame.Format.SampleType != AudioSampleType.Pcm16 || frame.Data.IsEmpty)
        {
            return 0;
        }

        var samples = frame.Data.Span;
        double sum = 0;
        var count = samples.Length / 2;
        for (var index = 0; index < count; index++)
        {
            var sample = BitConverter.ToInt16(samples.Slice(index * 2, 2));
            var normalized = sample / 32768d;
            sum += normalized * normalized;
        }

        return count == 0 ? 0 : (float)Math.Sqrt(sum / count);
    }
}

public sealed class InMemoryAudioCaptureService(IEnumerable<AudioFrame> frames) : IAudioCaptureService
{
    private readonly ImmutableArray<AudioFrame> _frames = frames.ToImmutableArray();

    public ValueTask<AudioPermissionStatus> GetPermissionAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AudioPermissionStatus.Granted);

    public ValueTask<AudioPermissionStatus> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AudioPermissionStatus.Granted);

    public ValueTask<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<AudioDevice>>([]);

    public async IAsyncEnumerable<AudioFrame> CaptureAsync(
        AudioCaptureOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var frame in _frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return frame;
            await Task.Yield();
        }
    }
}

public sealed class InMemoryAudioPlaybackService : IAudioPlaybackService
{
    private readonly ConcurrentQueue<AudioFrame> _frames = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _stopRequested;

    public IReadOnlyCollection<AudioFrame> Frames => _frames.ToArray();
    public Task Started => _started.Task;

    public ValueTask<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<AudioDevice>>([]);

    public async ValueTask PlayAsync(
        IAsyncEnumerable<AudioFrame> audio,
        AudioPlaybackOptions options,
        VoiceGenerationId generation,
        CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _stopRequested, 0);
        await foreach (var frame in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (Volatile.Read(ref _stopRequested) != 0) break;
            _frames.Enqueue(frame);
            _started.TrySetResult();
        }
    }

    public ValueTask StopAsync(VoiceGenerationId generation, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _stopRequested, 1);
        return ValueTask.CompletedTask;
    }
}

public sealed class UnavailableAudioCaptureService(SpeechErrorCode code = SpeechErrorCode.MicrophoneUnavailable) : IAudioCaptureService
{
    public ValueTask<AudioPermissionStatus> GetPermissionAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AudioPermissionStatus.Denied);

    public ValueTask<AudioPermissionStatus> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(AudioPermissionStatus.Denied);

    public ValueTask<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<AudioDevice>>([]);

    public async IAsyncEnumerable<AudioFrame> CaptureAsync(
        AudioCaptureOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) yield break;
        await Task.CompletedTask;
        throw new SpeechProviderException(new SpeechError(code, "No microphone capture backend is registered for this host."));
    }
}

public sealed class UnavailableAudioPlaybackService : IAudioPlaybackService
{
    public ValueTask<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<AudioDevice>>([]);

    public async ValueTask PlayAsync(
        IAsyncEnumerable<AudioFrame> audio,
        AudioPlaybackOptions options,
        VoiceGenerationId generation,
        CancellationToken cancellationToken = default) =>
        throw new SpeechProviderException(new SpeechError(SpeechErrorCode.PlaybackFailure, "No audio playback backend is registered for this host."));

    public ValueTask StopAsync(VoiceGenerationId generation, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

public sealed class InMemorySpeechToTextProvider : ISpeechToTextProvider
{
    private readonly string _finalText;
    private readonly ImmutableArray<string> _partials;

    public InMemorySpeechToTextProvider(
        string finalText,
        IEnumerable<string>? partials = null,
        string id = "mock-stt")
    {
        _finalText = finalText;
        _partials = (partials ?? SplitPartials(finalText)).ToImmutableArray();
        Descriptor = new SpeechProviderDescriptor
        {
            Id = new SpeechProviderId(id),
            Type = SpeechProviderType.Mock,
            Capabilities = SpeechCapabilities.Streaming | SpeechCapabilities.PartialTranscripts | SpeechCapabilities.Multilingual | SpeechCapabilities.KeytermPrompting,
            Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Healthy),
            CostClass = SpeechCostClass.Zero,
            Languages = ["en", "fr"]
        };
    }

    public SpeechProviderDescriptor Descriptor { get; }

    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        TranscriptionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new TranscriptionEvent.SessionStarted(DateTimeOffset.UtcNow, Descriptor.Id.Value);
        await foreach (var _ in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            foreach (var partial in _partials)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new TranscriptionEvent.PartialTranscript(DateTimeOffset.UtcNow, partial, partial);
                await Task.Yield();
            }

            break;
        }

        yield return new TranscriptionEvent.Final(DateTimeOffset.UtcNow, _finalText, context.Language, 1);
        yield return new TranscriptionEvent.SessionCompleted(DateTimeOffset.UtcNow);
    }

    private static IEnumerable<string> SplitPartials(string value)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 1; index <= words.Length; index++)
        {
            yield return string.Join(' ', words.Take(index));
        }
    }
}

public sealed class InMemoryTextToSpeechProvider : ITextToSpeechProvider
{
    private readonly TimeSpan _delay;

    public InMemoryTextToSpeechProvider(TimeSpan? delay = null, string id = "mock-tts")
    {
        _delay = delay ?? TimeSpan.FromMilliseconds(1);
        Descriptor = new SpeechProviderDescriptor
        {
            Id = new SpeechProviderId(id),
            Type = SpeechProviderType.Mock,
            Capabilities = SpeechCapabilities.Streaming | SpeechCapabilities.Multilingual | SpeechCapabilities.Expressive | SpeechCapabilities.Offline,
            Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Healthy),
            CostClass = SpeechCostClass.Zero,
            Languages = ["en", "fr"]
        };
    }

    public SpeechProviderDescriptor Descriptor { get; }

    public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        SpeechSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var format = request.OutputFormat ?? AudioFormat.NormalizedSpeech;
        var bytes = new byte[Math.Max(2, format.BytesPerSecond / 20)];
        var sequence = 0L;
        var timestamp = TimeSpan.Zero;
        foreach (var character in request.Text)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frequency = 180 + (character % 40) * 4;
            for (var index = 0; index < bytes.Length; index += 2)
            {
                var sampleIndex = index / 2;
                var sample = (short)(Math.Sin((sampleIndex / (double)format.SampleRate) * frequency * Math.PI * 2) * 1800);
                BitConverter.TryWriteBytes(bytes.AsSpan(index, 2), sample);
            }

            await Task.Delay(_delay, cancellationToken).ConfigureAwait(false);
            yield return new AudioFrame(bytes.ToArray(), format, sequence++, timestamp);
            timestamp += format.DurationForBytes(bytes.Length);
        }
    }
}

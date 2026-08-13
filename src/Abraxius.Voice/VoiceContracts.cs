using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Abraxius.Voice;

public enum AudioSampleType
{
    Pcm16,
    FloatingPoint
}

public sealed record AudioFormat(
    int SampleRate,
    int Channels,
    AudioSampleType SampleType = AudioSampleType.Pcm16)
{
    public static AudioFormat NormalizedSpeech { get; } = new(16_000, 1, AudioSampleType.Pcm16);

    public int BytesPerSample => SampleType == AudioSampleType.Pcm16 ? 2 : 4;
    public int BytesPerSecond => checked(SampleRate * Channels * BytesPerSample);

    public TimeSpan DurationForBytes(int byteCount) =>
        TimeSpan.FromSeconds(byteCount / (double)BytesPerSecond);
}

public readonly record struct AudioFrame(
    ReadOnlyMemory<byte> Data,
    AudioFormat Format,
    long Sequence,
    TimeSpan Timestamp)
{
    public TimeSpan Duration => Format.DurationForBytes(Data.Length);
}

public enum AudioDeviceKind
{
    Input,
    Output
}

public sealed record AudioDevice(
    string Id,
    string Name,
    AudioDeviceKind Kind,
    AudioFormat DefaultFormat,
    bool IsDefault = false);

public enum AudioPermissionStatus
{
    Granted,
    Denied,
    Restricted,
    NotRequested,
    Unknown
}

public sealed record AudioCaptureOptions(
    AudioFormat Format,
    TimeSpan FrameDuration,
    string? DeviceId = null,
    AudioPreprocessingProfile Preprocessing = AudioPreprocessingProfile.Automatic,
    bool IncludeSilence = true);

public sealed record AudioPlaybackOptions(
    AudioFormat Format,
    string? DeviceId = null,
    bool Interruptible = true);

public enum AudioPreprocessingProfile
{
    Raw,
    VoiceChat,
    NoisyEnvironment,
    Automatic
}

public interface IAudioCaptureService
{
    ValueTask<AudioPermissionStatus> GetPermissionAsync(CancellationToken cancellationToken = default);

    ValueTask<AudioPermissionStatus> RequestPermissionAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<AudioFrame> CaptureAsync(
        AudioCaptureOptions options,
        CancellationToken cancellationToken = default);
}

public interface IAudioPlaybackService
{
    ValueTask<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);

    ValueTask PlayAsync(
        IAsyncEnumerable<AudioFrame> audio,
        AudioPlaybackOptions options,
        VoiceGenerationId generation,
        CancellationToken cancellationToken = default);

    ValueTask StopAsync(VoiceGenerationId generation, CancellationToken cancellationToken = default);
}

public interface IAudioPreprocessor
{
    AudioFrame Process(AudioFrame frame);
}

public interface IAcousticEchoCanceller : IAudioPreprocessor
{
    void SetReference(AudioFrame frame);
}

public enum VadState
{
    Silence,
    PossibleSpeech,
    Speech,
    PossibleEnd
}

public enum SpeechActivityKind
{
    None,
    SpeechStarted,
    SpeechContinued,
    SpeechEnded
}

public readonly record struct VadResult(
    VadState State,
    SpeechActivityKind Activity,
    float Level,
    AudioFrame Frame);

public sealed record VoiceActivityOptions
{
    public VoiceActivityOptions(
        float speechThreshold = 0.018f,
        int startFrames = 2,
        int endFrames = 8,
        int preRollFrames = 8,
        int postRollFrames = 3)
    {
        if (speechThreshold <= 0 || speechThreshold >= 1) throw new ArgumentOutOfRangeException(nameof(speechThreshold));
        ArgumentOutOfRangeException.ThrowIfLessThan(startFrames, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(endFrames, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(preRollFrames);
        ArgumentOutOfRangeException.ThrowIfNegative(postRollFrames);
        SpeechThreshold = speechThreshold;
        StartFrames = startFrames;
        EndFrames = endFrames;
        PreRollFrames = preRollFrames;
        PostRollFrames = postRollFrames;
    }

    public float SpeechThreshold { get; }
    public int StartFrames { get; }
    public int EndFrames { get; }
    public int PreRollFrames { get; }
    public int PostRollFrames { get; }
}

public interface IVoiceActivityDetector
{
    VadResult Process(AudioFrame frame);
    void Reset();
}

public enum VoiceMode
{
    PushToTalk,
    WakeWord,
    AlwaysListening,
    Manual
}

public sealed record VoiceSettings(
    string? InputDeviceId = null,
    string? OutputDeviceId = null,
    VoiceMode Mode = VoiceMode.AlwaysListening,
    SpeechRoutingMode RoutingMode = SpeechRoutingMode.BalancedQuality,
    string Voice = "default",
    string? Language = null,
    bool WakeWordEnabled = false,
    bool BargeInEnabled = true,
    bool PrivateMode = false,
    bool AutoSubmitFinalTranscript = true,
    AudioPreprocessingProfile Preprocessing = AudioPreprocessingProfile.Automatic)
{
    public static VoiceSettings Default { get; } = new();
}

public interface IWakeWordDetector
{
    ValueTask<bool> ProcessAsync(AudioFrame frame, CancellationToken cancellationToken = default);
    void Reset();
}

public sealed class DisabledWakeWordDetector : IWakeWordDetector
{
    public ValueTask<bool> ProcessAsync(AudioFrame frame, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public void Reset() { }
}

public sealed record SpeechVocabularyContext(
    IReadOnlyList<string> Terms,
    string? LanguageHint = null,
    IReadOnlyDictionary<string, string>? Pronunciations = null);

public sealed record SpeechContext(
    string? ProjectName = null,
    string? Mission = null,
    string? Language = null,
    SpeechVocabularyContext? Vocabulary = null,
    bool PrivateMode = false);

public interface ISpeechVocabularyProvider
{
    ValueTask<SpeechVocabularyContext> BuildAsync(
        SpeechContext context,
        CancellationToken cancellationToken = default);
}

public sealed class StaticSpeechVocabularyProvider(IEnumerable<string> terms) : ISpeechVocabularyProvider
{
    private readonly ImmutableArray<string> _terms = terms
        .Where(static term => !string.IsNullOrWhiteSpace(term))
        .Select(static term => term.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(128)
        .ToImmutableArray();

    public ValueTask<SpeechVocabularyContext> BuildAsync(SpeechContext context, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new SpeechVocabularyContext(_terms, context.Language));
}

public enum TranscriptionEventKind
{
    SessionStarted,
    PartialTranscript,
    Final,
    LanguageDetected,
    WordTiming,
    SpeechStarted,
    SpeechEnded,
    ProviderChanged,
    ErrorEvent,
    SessionCompleted
}

public abstract record TranscriptionEvent(TranscriptionEventKind Kind, DateTimeOffset Timestamp)
{
    public sealed record SessionStarted(DateTimeOffset Timestamp, string Provider) : TranscriptionEvent(TranscriptionEventKind.SessionStarted, Timestamp);
    public sealed record PartialTranscript(DateTimeOffset Timestamp, string Text, string? StableText = null) : TranscriptionEvent(TranscriptionEventKind.PartialTranscript, Timestamp);
    public sealed record Final(DateTimeOffset Timestamp, string Text, string? Language = null, double? Confidence = null) : TranscriptionEvent(TranscriptionEventKind.Final, Timestamp);
    public sealed record LanguageDetected(DateTimeOffset Timestamp, string Language) : TranscriptionEvent(TranscriptionEventKind.LanguageDetected, Timestamp);
    public sealed record WordTiming(DateTimeOffset Timestamp, string Word, TimeSpan Start, TimeSpan End) : TranscriptionEvent(TranscriptionEventKind.WordTiming, Timestamp);
    public sealed record SpeechStarted(DateTimeOffset Timestamp) : TranscriptionEvent(TranscriptionEventKind.SpeechStarted, Timestamp);
    public sealed record SpeechEnded(DateTimeOffset Timestamp) : TranscriptionEvent(TranscriptionEventKind.SpeechEnded, Timestamp);
    public sealed record ProviderChanged(DateTimeOffset Timestamp, string From, string To, string Reason) : TranscriptionEvent(TranscriptionEventKind.ProviderChanged, Timestamp);
    public sealed record ErrorEvent(DateTimeOffset Timestamp, SpeechError ErrorInfo) : TranscriptionEvent(TranscriptionEventKind.ErrorEvent, Timestamp);
    public sealed record SessionCompleted(DateTimeOffset Timestamp) : TranscriptionEvent(TranscriptionEventKind.SessionCompleted, Timestamp);
}

public sealed record TranscriptionContext(
    AudioFormat Format,
    SpeechContext Speech,
    string? Language = null,
    bool Streaming = true,
    TimeSpan? Timeout = null,
    SpeechRoutingMode RoutingMode = SpeechRoutingMode.BalancedQuality);

public interface ISpeechToTextProvider
{
    SpeechProviderDescriptor Descriptor { get; }

    IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        TranscriptionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record SpeechSynthesisRequest(
    string Text,
    string Voice,
    string? Language = null,
    string? Style = null,
    double Speed = 1,
    bool Interruptible = true,
    AudioFormat? OutputFormat = null,
    SpeechContext? Context = null,
    SpeechRoutingMode RoutingMode = SpeechRoutingMode.BalancedQuality);

public interface ITextToSpeechProvider
{
    SpeechProviderDescriptor Descriptor { get; }

    IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        SpeechSynthesisRequest request,
        CancellationToken cancellationToken = default);
}

[Flags]
public enum SpeechCapabilities
{
    None = 0,
    Streaming = 1,
    PartialTranscripts = 2,
    Timestamps = 4,
    WordTimestamps = 8,
    SpeakerDiarization = 16,
    LanguageDetection = 32,
    Multilingual = 64,
    CodeSwitching = 128,
    KeytermPrompting = 256,
    NoiseRobust = 512,
    Offline = 1024,
    ToolVoice = 2048,
    VoiceCloning = 4096,
    Expressive = 8192,
    Ssml = 16384
}

public enum SpeechProviderType
{
    Local,
    Cloud,
    Sidecar,
    Mock
}

public enum SpeechProviderHealthStatus
{
    Healthy,
    Degraded,
    RateLimited,
    Unavailable,
    Unknown
}

public enum SpeechCostClass
{
    Zero,
    Included,
    Low,
    Standard,
    Premium,
    Unknown
}

public sealed record SpeechProviderHealth(
    SpeechProviderHealthStatus Status,
    DateTimeOffset? LastChecked = null,
    string? Message = null);

public readonly record struct SpeechProviderId(string Value)
{
    public override string ToString() => Value;
}

public sealed record SpeechProviderDescriptor
{
    public required SpeechProviderId Id { get; init; }
    public required SpeechProviderType Type { get; init; }
    public required SpeechCapabilities Capabilities { get; init; }
    public required SpeechProviderHealth Health { get; init; }
    public required SpeechCostClass CostClass { get; init; }
    public string? Version { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];
    public string? Endpoint { get; init; }

    public bool Supports(SpeechCapabilities capability) => (Capabilities & capability) == capability;
}

public enum SpeechRoutingMode
{
    Quality,
    BalancedQuality,
    LocalFirst,
    Private,
    Manual
}

public enum SpeechRouteKind
{
    Stt,
    Tts
}

public sealed record SpeechRouteRequest(
    SpeechRouteKind Kind,
    SpeechRoutingMode Mode,
    SpeechContext Context,
    string? Language,
    SpeechCapabilities RequiredCapabilities,
    bool Streaming,
    bool RequireLocal,
    string? PreferredProvider = null,
    decimal? MaximumCost = null);

public sealed record SpeechRouteCandidate(
    SpeechProviderDescriptor Provider,
    int Score,
    string? RejectionReason = null);

public sealed record SpeechRouteDecision(
    SpeechProviderId Provider,
    SpeechRouteKind Kind,
    SpeechRoutingMode Mode,
    string Reason,
    IReadOnlyList<SpeechRouteCandidate> Candidates,
    SpeechCostClass CostClass,
    DateTimeOffset Timestamp);

public enum SpeechErrorCode
{
    MicrophoneUnavailable,
    PermissionDenied,
    CaptureFailure,
    PlaybackFailure,
    VadFailure,
    SttUnavailable,
    SttTimeout,
    TtsUnavailable,
    TtsTimeout,
    UnsupportedLanguage,
    ProviderRateLimited,
    NetworkLost,
    SidecarUnavailable,
    Cancelled,
    InvalidAudio,
    PrivateModeBlocked
}

public sealed record SpeechError(
    SpeechErrorCode Code,
    string Message,
    SpeechProviderId? Provider = null,
    bool IsTransient = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed class SpeechProviderException(SpeechError error) : Exception(error.Message)
{
    public SpeechError Error { get; } = error;
}

public readonly record struct VoiceTurnId(Guid Value)
{
    public static VoiceTurnId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct VoiceGenerationId(long Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public enum VoiceTurnState
{
    Idle,
    Listening,
    SpeechDetected,
    Transcribing,
    Processing,
    Speaking,
    Interrupted,
    Error
}

public enum VoiceEventKind
{
    SessionStarted,
    ListeningStarted,
    SpeechDetected,
    SpeechEnded,
    PartialTranscriptUpdated,
    TranscriptFinalized,
    SpeechGenerationStarted,
    PlaybackStarted,
    PlaybackStopped,
    VoiceInterrupted,
    VoiceProviderChanged,
    SessionCompleted,
    Error
}

public abstract record VoiceEvent(
    VoiceEventKind Kind,
    VoiceTurnId TurnId,
    VoiceGenerationId Generation,
    DateTimeOffset Timestamp)
{
    public sealed record SessionStarted(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp) : VoiceEvent(VoiceEventKind.SessionStarted, TurnId, Generation, Timestamp);
    public sealed record ListeningStarted(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp) : VoiceEvent(VoiceEventKind.ListeningStarted, TurnId, Generation, Timestamp);
    public sealed record SpeechDetected(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp) : VoiceEvent(VoiceEventKind.SpeechDetected, TurnId, Generation, Timestamp);
    public sealed record SpeechEnded(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp) : VoiceEvent(VoiceEventKind.SpeechEnded, TurnId, Generation, Timestamp);
    public sealed record PartialTranscriptUpdated(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp, string Text) : VoiceEvent(VoiceEventKind.PartialTranscriptUpdated, TurnId, Generation, Timestamp);
    public sealed record TranscriptFinalized(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp, string Text) : VoiceEvent(VoiceEventKind.TranscriptFinalized, TurnId, Generation, Timestamp);
    public sealed record SpeechGenerationStarted(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp, string Text) : VoiceEvent(VoiceEventKind.SpeechGenerationStarted, TurnId, Generation, Timestamp);
    public sealed record PlaybackStarted(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp) : VoiceEvent(VoiceEventKind.PlaybackStarted, TurnId, Generation, Timestamp);
    public sealed record PlaybackStopped(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp, string Reason) : VoiceEvent(VoiceEventKind.PlaybackStopped, TurnId, Generation, Timestamp);
    public sealed record VoiceInterrupted(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp, string Reason) : VoiceEvent(VoiceEventKind.VoiceInterrupted, TurnId, Generation, Timestamp);
    public sealed record VoiceProviderChanged(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp, SpeechRouteKind RouteKind, SpeechProviderId From, SpeechProviderId To, string Reason) : VoiceEvent(VoiceEventKind.VoiceProviderChanged, TurnId, Generation, Timestamp);
    public sealed record SessionCompleted(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp) : VoiceEvent(VoiceEventKind.SessionCompleted, TurnId, Generation, Timestamp);
    public sealed record ErrorEvent(VoiceTurnId TurnId, VoiceGenerationId Generation, DateTimeOffset Timestamp, SpeechError ErrorInfo) : VoiceEvent(VoiceEventKind.Error, TurnId, Generation, Timestamp);
}

public interface IVoiceEventSource
{
    IAsyncEnumerable<VoiceEvent> ReadEventsAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<VoiceTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken = default);
}

public sealed record VoiceTelemetry(
    DateTimeOffset Timestamp,
    VoiceTurnId TurnId,
    VoiceGenerationId Generation,
    float InputLevel,
    VadState VadState,
    int AudioBufferDepth,
    TimeSpan? FirstPartialLatency = null,
    TimeSpan? FinalTranscriptLatency = null,
    TimeSpan? FirstAudioLatency = null,
    TimeSpan? InterruptLatency = null);

public sealed record VoiceSessionOptions(
    VoiceMode Mode = VoiceMode.PushToTalk,
    SpeechRoutingMode RoutingMode = SpeechRoutingMode.BalancedQuality,
    AudioCaptureOptions? Capture = null,
    AudioPlaybackOptions? Playback = null,
    SpeechContext? Context = null,
    string Voice = "default",
    string? Language = null,
    bool AutoSubmitFinalTranscript = true,
    bool BargeInEnabled = true,
    bool PrivateMode = false,
    VoiceActivityOptions? ActivityDetection = null,
    IAudioPreprocessor? Preprocessor = null,
    IAcousticEchoCanceller? EchoCanceller = null);

public sealed record VoiceResponse(
    string Text,
    IAsyncEnumerable<string>? StreamingText = null);

public interface IVoiceIntentSink
{
    ValueTask<VoiceResponse?> SubmitAsync(string transcript, VoiceTurnId turnId, CancellationToken cancellationToken = default);
}

public interface IVoiceResponseGenerator
{
    IAsyncEnumerable<string> GenerateAsync(
        string transcript,
        VoiceTurnId turnId,
        CancellationToken cancellationToken = default);
}

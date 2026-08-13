namespace Abraxius.Voice;

public sealed record VoiceMetricsSnapshot(
    long SpeechStarts,
    long SpeechEnds,
    long PartialTranscripts,
    long FinalTranscripts,
    long BargeIns,
    long ProviderChanges,
    long TelemetrySamples,
    int MaximumObservedBufferDepth,
    float LastInputLevel);

public sealed class VoiceMetricsCollector
{
    private long _speechStarts;
    private long _speechEnds;
    private long _partialTranscripts;
    private long _finalTranscripts;
    private long _bargeIns;
    private long _providerChanges;
    private long _telemetrySamples;
    private int _maximumObservedBufferDepth;
    private float _lastInputLevel;

    public void Observe(VoiceEvent item)
    {
        switch (item)
        {
            case VoiceEvent.SpeechDetected:
                Interlocked.Increment(ref _speechStarts);
                break;
            case VoiceEvent.SpeechEnded:
                Interlocked.Increment(ref _speechEnds);
                break;
            case VoiceEvent.PartialTranscriptUpdated:
                Interlocked.Increment(ref _partialTranscripts);
                break;
            case VoiceEvent.TranscriptFinalized:
                Interlocked.Increment(ref _finalTranscripts);
                break;
            case VoiceEvent.VoiceInterrupted:
                Interlocked.Increment(ref _bargeIns);
                break;
            case VoiceEvent.VoiceProviderChanged:
                Interlocked.Increment(ref _providerChanges);
                break;
        }
    }

    public void Observe(VoiceTelemetry item)
    {
        Interlocked.Increment(ref _telemetrySamples);
        Volatile.Write(ref _lastInputLevel, item.InputLevel);
        var current = Volatile.Read(ref _maximumObservedBufferDepth);
        while (item.AudioBufferDepth > current)
        {
            var observed = Interlocked.CompareExchange(ref _maximumObservedBufferDepth, item.AudioBufferDepth, current);
            if (observed == current) break;
            current = observed;
        }
    }

    public VoiceMetricsSnapshot Snapshot() => new(
        Volatile.Read(ref _speechStarts),
        Volatile.Read(ref _speechEnds),
        Volatile.Read(ref _partialTranscripts),
        Volatile.Read(ref _finalTranscripts),
        Volatile.Read(ref _bargeIns),
        Volatile.Read(ref _providerChanges),
        Volatile.Read(ref _telemetrySamples),
        Volatile.Read(ref _maximumObservedBufferDepth),
        Volatile.Read(ref _lastInputLevel));
}

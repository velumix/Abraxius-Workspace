using BenchmarkDotNet.Attributes;
using Abraxius.Voice;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class VoiceBenchmarks
{
    private readonly EnergyVoiceActivityDetector _vad = new();
    private readonly AudioFrame _frame = CreateFrame();
    private readonly AudioGenerationGate _gate = new();
    private readonly SpeechSegmenter _segmenter = new();

    [Benchmark]
    public VadResult VadFrame() => _vad.Process(_frame);

    [Benchmark]
    public VoiceGenerationId AdvanceGeneration() => _gate.Advance();

    [Benchmark]
    public async Task<int> SegmentResponse()
    {
        var count = 0;
        await foreach (var _ in _segmenter.SegmentAsync(Text())) count++;
        return count;
    }

    private static AudioFrame CreateFrame()
    {
        var data = new byte[640];
        for (var index = 0; index < data.Length; index += 2)
        {
            BitConverter.TryWriteBytes(data.AsSpan(index, 2), (short)1200);
        }

        return new AudioFrame(data, AudioFormat.NormalizedSpeech, 1, TimeSpan.Zero);
    }

    private static async IAsyncEnumerable<string> Text()
    {
        yield return "The scheduler found the root cause. ";
        await Task.Yield();
        yield return "The detailed evidence is open in the workstation.";
    }
}

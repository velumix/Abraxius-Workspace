using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Abraxius.Voice;
using Xunit;

namespace Abraxius.Voice.Tests;

public sealed class VoicePipelineTests
{
    [Fact]
    public void EnergyVadUsesHysteresisAndEmitsOneStartAndEnd()
    {
        var detector = new EnergyVoiceActivityDetector(new VoiceActivityOptions(
            speechThreshold: 0.02f,
            startFrames: 2,
            endFrames: 3));
        var silence = Frame(0, 0);
        var speech = Frame(0.2f, 1);

        Assert.Equal(SpeechActivityKind.None, detector.Process(silence).Activity);
        Assert.Equal(SpeechActivityKind.None, detector.Process(speech).Activity);
        Assert.Equal(SpeechActivityKind.SpeechStarted, detector.Process(Frame(0.2f, 2)).Activity);
        Assert.Equal(SpeechActivityKind.SpeechContinued, detector.Process(Frame(0.2f, 3)).Activity);
        Assert.Equal(SpeechActivityKind.None, detector.Process(Frame(0, 4)).Activity);
        Assert.Equal(SpeechActivityKind.None, detector.Process(Frame(0, 5)).Activity);
        Assert.Equal(SpeechActivityKind.SpeechEnded, detector.Process(Frame(0, 6)).Activity);
    }

    [Fact]
    public void PreRollRetainsTheFramesBeforeSpeechStart()
    {
        var buffer = new PreRollAudioBuffer(3);
        buffer.Add(Frame(0, 0));
        buffer.Add(Frame(0, 1));
        buffer.Add(Frame(0, 2));
        buffer.Add(Frame(0, 3));

        Assert.Equal([1L, 2L, 3L], buffer.Snapshot().Select(static frame => frame.Sequence));
    }

    [Fact]
    public async Task PrivateRoutingRejectsCloudAndChoosesLocal()
    {
        var cloud = Descriptor("cloud", SpeechProviderType.Cloud, SpeechCostClass.Premium, SpeechCapabilities.Streaming | SpeechCapabilities.Multilingual);
        var local = Descriptor("local", SpeechProviderType.Local, SpeechCostClass.Zero, SpeechCapabilities.Streaming | SpeechCapabilities.Multilingual | SpeechCapabilities.Offline);
        var engine = new SpeechRouteEngine([cloud, local]);
        var request = new SpeechRouteRequest(
            SpeechRouteKind.Stt,
            SpeechRoutingMode.Private,
            new SpeechContext(PrivateMode: true),
            "en",
            SpeechCapabilities.Streaming,
            Streaming: true,
            RequireLocal: true);

        var decision = await engine.SelectSttAsync(request);

        Assert.Equal(new SpeechProviderId("local"), decision.Provider);
        Assert.Contains(decision.Candidates, candidate => candidate.Provider.Id.Value == "cloud" && candidate.RejectionReason is not null);
    }

    [Fact]
    public async Task SegmenterStartsSpeechBeforeTheWholeResponseExists()
    {
        var segmenter = new SpeechSegmenter(minimumCharacters: 8, maximumCharacters: 80);
        var segments = new List<string>();
        await foreach (var segment in segmenter.SegmentAsync(TextDeltas()))
        {
            segments.Add(segment);
        }

        Assert.Equal(["The first sentence.", "The second sentence."], segments);
    }

    [Fact]
    public void GenerationGateRejectsLateAudio()
    {
        var gate = new AudioGenerationGate();
        var first = gate.Advance();
        var second = gate.Advance();

        Assert.False(gate.IsCurrent(first));
        Assert.True(gate.IsCurrent(second));
    }

    [Fact]
    public void NoisyAudioProfileAppliesTheConfiguredPreprocessor()
    {
        var frame = Frame(0.005f, 1);
        var processed = new PcmNoiseGateAudioPreprocessor().Process(frame);

        Assert.NotEqual(frame.Data.ToArray(), processed.Data.ToArray());
        Assert.All(processed.Data.ToArray().Chunk(2), static bytes => Assert.Equal(0, BitConverter.ToInt16(bytes)));
    }

    [Fact]
    public async Task BargeInCancelsTheCurrentSpeechGeneration()
    {
        await using var events = new VoiceEventHub();
        var playback = new InMemoryAudioPlaybackService();
        var orchestrator = new VoiceConversationOrchestrator(
            new InMemoryAudioCaptureService([]),
            new EnergyVoiceActivityDetector(),
            new InMemorySpeechToTextProvider("unused"),
            new SlowTextToSpeechProvider(),
            playback,
            new StaticSpeechVocabularyProvider(["Avalonia", "ExecutionGraph"]),
            new OneSentenceResponseGenerator(),
            events,
            new SpeechSegmenter(8, 80));

        var speaking = orchestrator.SpeakAsync("The scheduler is speaking a deliberately long response.");
        await playback.Started.WaitAsync(TimeSpan.FromSeconds(2));
        await orchestrator.InterruptAsync("user spoke");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => speaking);
        Assert.Equal(VoiceTurnState.Interrupted, orchestrator.State);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var observed = new List<VoiceEvent>();
        await foreach (var item in events.ReadEventsAsync(timeout.Token))
        {
            observed.Add(item);
            if (item is VoiceEvent.VoiceInterrupted) break;
        }

        Assert.Contains(observed, static item => item is VoiceEvent.VoiceInterrupted);
    }

    [Fact]
    public async Task CompletedSpeechGenerationDoesNotPoisonTheNextGeneration()
    {
        await using var events = new VoiceEventHub();
        var orchestrator = new VoiceConversationOrchestrator(
            new InMemoryAudioCaptureService([]),
            new EnergyVoiceActivityDetector(),
            new InMemorySpeechToTextProvider("unused"),
            new InMemoryTextToSpeechProvider(),
            new InMemoryAudioPlaybackService(),
            new StaticSpeechVocabularyProvider(["Abraxius"]),
            new OneSentenceResponseGenerator(),
            events,
            new SpeechSegmenter(8, 80));

        await orchestrator.SpeakAsync("First completed response.");
        await orchestrator.SpeakAsync("Second completed response.");

        Assert.Equal(VoiceTurnState.Listening, orchestrator.State);
    }

    [Fact]
    public async Task RoutedSttReplaysBufferedAudioAfterTransientProviderFailure()
    {
        var primary = new TransientSttProvider("primary-cloud", SpeechProviderType.Cloud);
        var fallback = new InMemorySpeechToTextProvider("fallback result", id: "fallback-local");
        var registry = new SpeechProviderRegistry();
        registry.Register(primary);
        registry.Register(fallback);
        var routes = new SpeechRouteEngine([primary.Descriptor, fallback.Descriptor]);
        var routed = new RoutedSpeechToTextProvider(
            routes,
            registry,
            new SpeechRouteRequest(SpeechRouteKind.Stt, SpeechRoutingMode.Quality, new SpeechContext(), "en", SpeechCapabilities.Streaming | SpeechCapabilities.PartialTranscripts, true, false));

        var events = new List<TranscriptionEvent>();
        await foreach (var item in routed.TranscribeAsync(AudioFrames(2), new TranscriptionContext(AudioFormat.NormalizedSpeech, new SpeechContext(), "en", RoutingMode: SpeechRoutingMode.Quality)))
        {
            events.Add(item);
        }

        Assert.Contains(events, static item => item is TranscriptionEvent.ProviderChanged);
        Assert.Contains(events, static item => item is TranscriptionEvent.Final final && final.Text == "fallback result");
        Assert.Equal(1, primary.FramesObserved);
    }

    [Fact]
    public async Task RoutedTtsFailsOverBeforeAnyAudioIsEmitted()
    {
        var primary = new TransientTtsProvider("primary-cloud", SpeechProviderType.Cloud);
        var fallback = new InMemoryTextToSpeechProvider(id: "fallback-local");
        var registry = new SpeechProviderRegistry();
        registry.Register(primary);
        registry.Register(fallback);
        var routes = new SpeechRouteEngine([primary.Descriptor, fallback.Descriptor]);
        var routed = new RoutedTextToSpeechProvider(
            routes,
            registry,
            new SpeechRouteRequest(SpeechRouteKind.Tts, SpeechRoutingMode.Quality, new SpeechContext(), "en", SpeechCapabilities.Streaming, true, false));

        var audio = new List<AudioFrame>();
        await foreach (var frame in routed.SynthesizeAsync(new SpeechSynthesisRequest("hello", "default", "en", RoutingMode: SpeechRoutingMode.Quality)))
        {
            audio.Add(frame);
            if (audio.Count == 1) break;
        }

        Assert.NotEmpty(audio);
        Assert.True(primary.WasAttempted);
    }

    private static SpeechProviderDescriptor Descriptor(string id, SpeechProviderType type, SpeechCostClass cost, SpeechCapabilities capabilities) => new()
    {
        Id = new SpeechProviderId(id),
        Type = type,
        CostClass = cost,
        Capabilities = capabilities,
        Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Healthy),
        Languages = ["en"]
    };

    private static AudioFrame Frame(float amplitude, long sequence)
    {
        var data = new byte[640];
        for (var index = 0; index < data.Length; index += 2)
        {
            var sample = (short)(amplitude * short.MaxValue);
            BitConverter.TryWriteBytes(data.AsSpan(index, 2), sample);
        }

        return new AudioFrame(data, AudioFormat.NormalizedSpeech, sequence, TimeSpan.FromMilliseconds(sequence * 20));
    }

    private static async IAsyncEnumerable<string> TextDeltas([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in new[] { "The ", "first ", "sentence. ", "The ", "second ", "sentence." })
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<AudioFrame> AudioFrames(int count, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Frame(0.1f, index);
            await Task.Yield();
        }
    }

    private sealed class OneSentenceResponseGenerator : IVoiceResponseGenerator
    {
        public async IAsyncEnumerable<string> GenerateAsync(string transcript, VoiceTurnId turnId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return transcript;
            await Task.Yield();
        }
    }

    private sealed class SlowTextToSpeechProvider : ITextToSpeechProvider
    {
        public SpeechProviderDescriptor Descriptor { get; } = new()
        {
            Id = new SpeechProviderId("slow-tts"),
            Type = SpeechProviderType.Mock,
            Capabilities = SpeechCapabilities.Streaming,
            Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Healthy),
            CostClass = SpeechCostClass.Zero
        };

        public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(SpeechSynthesisRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var frame = Frame(0.01f, 0);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return frame;
                await Task.Delay(10, cancellationToken);
            }
        }
    }

    private sealed class TransientSttProvider(string id, SpeechProviderType type) : ISpeechToTextProvider
    {
        public int FramesObserved { get; private set; }
        public SpeechProviderDescriptor Descriptor { get; } = new()
        {
            Id = new SpeechProviderId(id),
            Type = type,
            Capabilities = SpeechCapabilities.Streaming | SpeechCapabilities.PartialTranscripts,
            Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Healthy),
            CostClass = SpeechCostClass.Premium,
            Languages = ["en"]
        };

        public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(IAsyncEnumerable<AudioFrame> audio, TranscriptionContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var _ in audio.WithCancellation(cancellationToken))
            {
                FramesObserved++;
                throw new SpeechProviderException(new SpeechError(SpeechErrorCode.NetworkLost, "simulated transient STT failure", Descriptor.Id, IsTransient: true));
            }

            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class TransientTtsProvider(string id, SpeechProviderType type) : ITextToSpeechProvider
    {
        public bool WasAttempted { get; private set; }
        public SpeechProviderDescriptor Descriptor { get; } = new()
        {
            Id = new SpeechProviderId(id),
            Type = type,
            Capabilities = SpeechCapabilities.Streaming | SpeechCapabilities.Expressive,
            Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Healthy),
            CostClass = SpeechCostClass.Premium,
            Languages = ["en"]
        };

        public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(SpeechSynthesisRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WasAttempted = true;
            await Task.Yield();
            if (cancellationToken.IsCancellationRequested) yield break;
            throw new SpeechProviderException(new SpeechError(SpeechErrorCode.NetworkLost, "simulated transient TTS failure", Descriptor.Id, IsTransient: true));
        }
    }
}

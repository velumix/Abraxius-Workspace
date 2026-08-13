namespace Abraxius.Voice;

public static class SpeechProviderFactory
{
    public static ISpeechToTextProvider CreateConfiguredStt(IReadOnlyDictionary<string, ISpeechCredentialProvider>? credentials = null) => CreateRoutedStt(CreateSttProviders(credentials));

    public static ITextToSpeechProvider CreateConfiguredTts(IReadOnlyDictionary<string, ISpeechCredentialProvider>? credentials = null) => CreateRoutedTts(CreateTtsProviders(credentials));

    public static IReadOnlyList<SpeechProviderDescriptor> GetConfiguredSttDescriptors() =>
        CreateSttProviders(null).Select(static provider => provider.Descriptor).ToArray();

    public static IReadOnlyList<SpeechProviderDescriptor> GetConfiguredTtsDescriptors() =>
        CreateTtsProviders(null).Select(static provider => provider.Descriptor).ToArray();

    private static List<ISpeechToTextProvider> CreateSttProviders(IReadOnlyDictionary<string, ISpeechCredentialProvider>? credentials)
    {
        var providers = new List<ISpeechToTextProvider>
        {
            new SherpaOnnxSpeechProvider()
        };

        if (credentials?.TryGetValue(SpeechCredentialNames.Deepgram, out var deepgram) == true) providers.Add(new DeepgramRealtimeSpeechToTextProvider(deepgram));
        if (credentials?.TryGetValue(SpeechCredentialNames.ElevenLabs, out var elevenLabs) == true) providers.Add(new ElevenLabsRealtimeSpeechToTextProvider(elevenLabs));
        return providers;
    }

    private static List<ITextToSpeechProvider> CreateTtsProviders(IReadOnlyDictionary<string, ISpeechCredentialProvider>? credentials)
    {
        var providers = new List<ITextToSpeechProvider>
        {
            new KokoroTextToSpeechProvider()
        };

        var voice = Environment.GetEnvironmentVariable("ABRAXIUS_ELEVENLABS_VOICE_ID");
        if (credentials?.TryGetValue(SpeechCredentialNames.ElevenLabs, out var elevenLabs) == true && !string.IsNullOrWhiteSpace(voice))
            providers.Add(new ElevenLabsRealtimeTextToSpeechProvider(elevenLabs, voice));
        return providers;
    }

    private static RoutedSpeechToTextProvider CreateRoutedStt(IReadOnlyList<ISpeechToTextProvider> providers)
    {
        var registry = new SpeechProviderRegistry();
        foreach (var provider in providers) registry.Register(provider);
        var routes = new SpeechRouteEngine(providers.Select(static provider => provider.Descriptor));
        return new RoutedSpeechToTextProvider(
            routes,
            registry,
            new SpeechRouteRequest(
                SpeechRouteKind.Stt,
                SpeechRoutingMode.BalancedQuality,
                new SpeechContext(),
                null,
                SpeechCapabilities.Streaming | SpeechCapabilities.PartialTranscripts,
                Streaming: true,
                RequireLocal: false));
    }

    private static RoutedTextToSpeechProvider CreateRoutedTts(IReadOnlyList<ITextToSpeechProvider> providers)
    {
        var registry = new SpeechProviderRegistry();
        foreach (var provider in providers) registry.Register(provider);
        var routes = new SpeechRouteEngine(providers.Select(static provider => provider.Descriptor));
        return new RoutedTextToSpeechProvider(
            routes,
            registry,
            new SpeechRouteRequest(
                SpeechRouteKind.Tts,
                SpeechRoutingMode.BalancedQuality,
                new SpeechContext(),
                null,
                SpeechCapabilities.Streaming,
                Streaming: true,
                RequireLocal: false));
    }
}

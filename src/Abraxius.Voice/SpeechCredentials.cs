namespace Abraxius.Voice;

/// <summary>
/// Supplies a credential only inside the transport operation that consumes it. Implementations
/// may bridge to a platform keychain or to the Abraxius Secret Broker.
/// </summary>
public interface ISpeechCredentialProvider
{
    ValueTask<T> UseAsync<T>(
        Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> transport,
        CancellationToken cancellationToken = default);
}

public static class SpeechCredentialNames
{
    public const string Deepgram = "deepgram";
    public const string ElevenLabs = "elevenlabs";
}

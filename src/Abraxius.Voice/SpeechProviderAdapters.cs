using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Abraxius.Voice;

public sealed class ElevenLabsRealtimeSpeechToTextProvider : ISpeechToTextProvider
{
    private readonly Uri _endpoint;
    private readonly ISpeechCredentialProvider _credential;
    private readonly string _model;

    public ElevenLabsRealtimeSpeechToTextProvider(
        ISpeechCredentialProvider credential,
        Uri? endpoint = null,
        string model = "scribe_v2_realtime")
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _endpoint = endpoint ?? new Uri("wss://api.elevenlabs.io/v1/speech-to-text/realtime");
        _model = model;
        Descriptor = new SpeechProviderDescriptor
        {
            Id = new SpeechProviderId("elevenlabs-scribe-realtime"),
            Type = SpeechProviderType.Cloud,
            Capabilities = SpeechCapabilities.Streaming | SpeechCapabilities.PartialTranscripts | SpeechCapabilities.Timestamps |
                SpeechCapabilities.Multilingual | SpeechCapabilities.CodeSwitching | SpeechCapabilities.KeytermPrompting | SpeechCapabilities.NoiseRobust,
            Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
            CostClass = SpeechCostClass.Premium,
            Endpoint = _endpoint.ToString(),
            Version = model
        };
    }

    public SpeechProviderDescriptor Descriptor { get; }

    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        TranscriptionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var socket = await _credential.UseAsync(async (value, token) =>
        {
            var connected = new ClientWebSocket();
            connected.Options.SetRequestHeader("xi-api-key", value.ToString());
            try { await connected.ConnectAsync(_endpoint, token).ConfigureAwait(false); return connected; }
            catch { connected.Dispose(); throw; }
        }, cancellationToken).ConfigureAwait(false);
        yield return new TranscriptionEvent.SessionStarted(DateTimeOffset.UtcNow, Descriptor.Id.Value);

        var events = Channel.CreateBounded<TranscriptionEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        using var receiverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiver = ReceiveTranscriptionEventsAsync(socket, events.Writer, receiverCancellation.Token);

        try
        {
            var initialize = new
            {
                message_type = "session_started",
                model_id = _model,
                audio_format = "pcm_16000",
                language_code = context.Language,
                keyterms = context.Speech.Vocabulary?.Terms
            };
            await SendJsonAsync(socket, initialize, cancellationToken).ConfigureAwait(false);

            await foreach (var frame in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var message = new
                {
                    message_type = "input_audio_chunk",
                    audio_base_64 = Convert.ToBase64String(frame.Data.Span),
                    commit = false
                };
                await SendJsonAsync(socket, message, cancellationToken).ConfigureAwait(false);
                while (events.Reader.TryRead(out var item)) yield return item;
            }

            await SendJsonAsync(socket, new { message_type = "input_audio_chunk", audio_base_64 = string.Empty, commit = true }, cancellationToken).ConfigureAwait(false);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "audio complete", cancellationToken).ConfigureAwait(false);
            await receiver.ConfigureAwait(false);
            while (events.Reader.TryRead(out var item)) yield return item;
            yield return new TranscriptionEvent.SessionCompleted(DateTimeOffset.UtcNow);
        }
        finally
        {
            receiverCancellation.Cancel();
            try { await receiver.ConfigureAwait(false); } catch (OperationCanceledException) when (receiverCancellation.IsCancellationRequested) { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "cancelled", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReceiveTranscriptionEventsAsync(ClientWebSocket socket, ChannelWriter<TranscriptionEvent> writer, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                using var output = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    output.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var value = ParseTranscription(output.GetBuffer().AsSpan(0, checked((int)output.Length)));
                if (value is not null) await writer.WriteAsync(value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static TranscriptionEvent? ParseTranscription(ReadOnlySpan<byte> payload)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        var type = root.TryGetProperty("message_type", out var typeProperty) ? typeProperty.GetString() : null;
        var text = GetString(root, "text") ?? GetString(root, "transcript");
        if (string.IsNullOrWhiteSpace(text)) return null;
        return type switch
        {
            "partial_transcript" or "partial" => new TranscriptionEvent.PartialTranscript(DateTimeOffset.UtcNow, text),
            "committed_transcript" or "final" => new TranscriptionEvent.Final(DateTimeOffset.UtcNow, text),
            _ => null
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class DeepgramRealtimeSpeechToTextProvider : ISpeechToTextProvider
{
    private readonly Uri _endpoint;
    private readonly ISpeechCredentialProvider _credential;

    public DeepgramRealtimeSpeechToTextProvider(ISpeechCredentialProvider credential, Uri? endpoint = null)
    {
        _endpoint = endpoint ?? new Uri("wss://api.deepgram.com/v1/listen?encoding=linear16&sample_rate=16000&channels=1&interim_results=true");
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        Descriptor = new SpeechProviderDescriptor
        {
            Id = new SpeechProviderId("deepgram-realtime"),
            Type = SpeechProviderType.Cloud,
            Capabilities = SpeechCapabilities.Streaming | SpeechCapabilities.PartialTranscripts | SpeechCapabilities.Timestamps |
                SpeechCapabilities.Multilingual | SpeechCapabilities.CodeSwitching | SpeechCapabilities.NoiseRobust,
            Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
            CostClass = SpeechCostClass.Premium,
            Endpoint = _endpoint.ToString(),
            Version = "nova-3"
        };
    }

    public SpeechProviderDescriptor Descriptor { get; }

    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        TranscriptionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var socket = await _credential.UseAsync(async (value, token) =>
        {
            var connected = new ClientWebSocket();
            connected.Options.SetRequestHeader("Authorization", $"Token {value}");
            try { await connected.ConnectAsync(_endpoint, token).ConfigureAwait(false); return connected; }
            catch { connected.Dispose(); throw; }
        }, cancellationToken).ConfigureAwait(false);
        yield return new TranscriptionEvent.SessionStarted(DateTimeOffset.UtcNow, Descriptor.Id.Value);

        var events = Channel.CreateBounded<TranscriptionEvent>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        using var receiverCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var receiver = ReceiveDeepgramEventsAsync(socket, events.Writer, receiverCancellation.Token);
        try
        {
            await foreach (var frame in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await socket.SendAsync(frame.Data, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
                while (events.Reader.TryRead(out var item)) yield return item;
            }

            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"CloseStream\"}"), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "audio complete", cancellationToken).ConfigureAwait(false);
            await receiver.ConfigureAwait(false);
            while (events.Reader.TryRead(out var item)) yield return item;
            yield return new TranscriptionEvent.SessionCompleted(DateTimeOffset.UtcNow);
        }
        finally
        {
            receiverCancellation.Cancel();
            try { await receiver.ConfigureAwait(false); } catch (OperationCanceledException) when (receiverCancellation.IsCancellationRequested) { }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "cancelled", CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReceiveDeepgramEventsAsync(ClientWebSocket socket, ChannelWriter<TranscriptionEvent> writer, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                using var output = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    output.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                using var document = JsonDocument.Parse(output.ToArray());
                var root = document.RootElement;
                if (!string.Equals(GetString(root, "type"), "Results", StringComparison.OrdinalIgnoreCase)) continue;
                if (!root.TryGetProperty("channel", out var channel) || !channel.TryGetProperty("alternatives", out var alternatives) || alternatives.GetArrayLength() == 0) continue;
                var alternative = alternatives[0];
                var transcript = GetString(alternative, "transcript");
                if (string.IsNullOrWhiteSpace(transcript)) continue;
                var isFinal = root.TryGetProperty("is_final", out var finalProperty) && finalProperty.ValueKind == JsonValueKind.True;
                await writer.WriteAsync(isFinal
                    ? new TranscriptionEvent.Final(DateTimeOffset.UtcNow, transcript)
                    : new TranscriptionEvent.PartialTranscript(DateTimeOffset.UtcNow, transcript), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public sealed class ElevenLabsRealtimeTextToSpeechProvider : ITextToSpeechProvider
{
    private readonly Uri _endpoint;
    private readonly ISpeechCredentialProvider _credential;
    private readonly string _voiceId;
    private readonly string _model;

    public ElevenLabsRealtimeTextToSpeechProvider(
        ISpeechCredentialProvider credential,
        string voiceId,
        string model = "eleven_flash_v2_5",
        Uri? endpoint = null)
    {
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _voiceId = string.IsNullOrWhiteSpace(voiceId) ? throw new ArgumentException("A voice ID is required.", nameof(voiceId)) : voiceId;
        _model = model;
        _endpoint = endpoint ?? new Uri($"wss://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}/stream-input?model_id={Uri.EscapeDataString(model)}&output_format=pcm_16000");
        Descriptor = new SpeechProviderDescriptor
        {
            Id = new SpeechProviderId("elevenlabs-realtime-tts"),
            Type = SpeechProviderType.Cloud,
            Capabilities = SpeechCapabilities.Streaming | SpeechCapabilities.Multilingual | SpeechCapabilities.Expressive | SpeechCapabilities.VoiceCloning,
            Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
            CostClass = SpeechCostClass.Premium,
            Endpoint = _endpoint.ToString(),
            Version = model
        };
    }

    public SpeechProviderDescriptor Descriptor { get; }

    public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        SpeechSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var socket = await _credential.UseAsync(async (value, token) =>
        {
            var connected = new ClientWebSocket();
            connected.Options.SetRequestHeader("xi-api-key", value.ToString());
            try { await connected.ConnectAsync(_endpoint, token).ConfigureAwait(false); return connected; }
            catch { connected.Dispose(); throw; }
        }, cancellationToken).ConfigureAwait(false);

        var initialize = new
        {
            text = " ",
            voice_settings = new { stability = 0.5, similarity_boost = 0.75 },
            generation_config = new { chunk_length_schedule = new[] { 50, 120, 240, 320 } },
            flush = false
        };
        await SendJsonAsync(socket, initialize, cancellationToken).ConfigureAwait(false);
        await SendJsonAsync(socket, new { text = request.Text, try_trigger_generation = true }, cancellationToken).ConfigureAwait(false);
        await SendJsonAsync(socket, new { text = string.Empty, flush = true }, cancellationToken).ConfigureAwait(false);

        var buffer = new byte[16 * 1024];
        var sequence = 0L;
        var timestamp = TimeSpan.Zero;
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            using var output = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) yield break;
                output.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            using var document = JsonDocument.Parse(output.ToArray());
            var root = document.RootElement;
            var audio = root.TryGetProperty("audio", out var audioProperty) ? audioProperty.GetString() :
                root.TryGetProperty("audioChunk", out var chunkProperty) ? chunkProperty.GetString() : null;
            if (!string.IsNullOrWhiteSpace(audio))
            {
                var data = Convert.FromBase64String(audio);
                var format = request.OutputFormat ?? AudioFormat.NormalizedSpeech;
                yield return new AudioFrame(data, format, sequence++, timestamp);
                timestamp += format.DurationForBytes(data.Length);
            }

            if (root.TryGetProperty("isFinal", out var final) && final.ValueKind == JsonValueKind.True) break;
        }

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "audio complete", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(json, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class OpenAiCompatibleSpeechToTextProvider : ISpeechToTextProvider
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly ISpeechCredentialProvider _credential;
    private readonly string _model;

    public OpenAiCompatibleSpeechToTextProvider(HttpClient http, Uri endpoint, ISpeechCredentialProvider credential, string model = "gpt-4o-transcribe")
    {
        _http = http;
        _endpoint = endpoint;
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _model = model;
        Descriptor = new SpeechProviderDescriptor
        {
            Id = new SpeechProviderId("openai-compatible-transcription"),
            Type = SpeechProviderType.Cloud,
            Capabilities = SpeechCapabilities.Multilingual | SpeechCapabilities.NoiseRobust,
            Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
            CostClass = SpeechCostClass.Premium,
            Endpoint = endpoint.ToString(),
            Version = model
        };
    }

    public SpeechProviderDescriptor Descriptor { get; }

    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        TranscriptionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var pcm = new MemoryStream();
        await foreach (var frame in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            await pcm.WriteAsync(frame.Data, cancellationToken).ConfigureAwait(false);
        }

        await using var wav = new MemoryStream();
        await WriteWaveHeaderAsync(wav, context.Format, checked((int)pcm.Length), cancellationToken).ConfigureAwait(false);
        pcm.Position = 0;
        await pcm.CopyToAsync(wav, cancellationToken).ConfigureAwait(false);
        wav.Position = 0;
        using var content = new MultipartFormDataContent();
        using var file = new StreamContent(wav);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "abraxius.wav");
        content.Add(new StringContent(_model), "model");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_endpoint, "audio/transcriptions")) { Content = content };
        using var response = await _credential.UseAsync(async (value, token) =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value.ToString());
            try { return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false); }
            finally { request.Headers.Authorization = null; }
        }, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SpeechProviderException(new SpeechError(SpeechErrorCode.SttUnavailable, $"Transcription provider returned HTTP {(int)response.StatusCode}.", Descriptor.Id, (int)response.StatusCode >= 500));
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var text = document.RootElement.TryGetProperty("text", out var textProperty) ? textProperty.GetString() : null;
        yield return new TranscriptionEvent.SessionStarted(DateTimeOffset.UtcNow, Descriptor.Id.Value);
        if (!string.IsNullOrWhiteSpace(text)) yield return new TranscriptionEvent.Final(DateTimeOffset.UtcNow, text);
        yield return new TranscriptionEvent.SessionCompleted(DateTimeOffset.UtcNow);
    }

    private static async Task WriteWaveHeaderAsync(Stream stream, AudioFormat format, int dataLength, CancellationToken cancellationToken)
    {
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BitConverter.TryWriteBytes(header.AsSpan(4), 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BitConverter.TryWriteBytes(header.AsSpan(16), 16);
        BitConverter.TryWriteBytes(header.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(header.AsSpan(22), (short)format.Channels);
        BitConverter.TryWriteBytes(header.AsSpan(24), format.SampleRate);
        BitConverter.TryWriteBytes(header.AsSpan(28), format.BytesPerSecond);
        BitConverter.TryWriteBytes(header.AsSpan(32), (short)(format.Channels * format.BytesPerSample));
        BitConverter.TryWriteBytes(header.AsSpan(34), (short)(format.BytesPerSample * 8));
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BitConverter.TryWriteBytes(header.AsSpan(40), dataLength);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
    }
}

public abstract class UnavailableLocalSpeechProvider : ISpeechToTextProvider, ITextToSpeechProvider
{
    protected UnavailableLocalSpeechProvider(SpeechProviderDescriptor descriptor) => Descriptor = descriptor;
    public SpeechProviderDescriptor Descriptor { get; }

    public async IAsyncEnumerable<TranscriptionEvent> TranscribeAsync(
        IAsyncEnumerable<AudioFrame> audio,
        TranscriptionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) yield break;
        await Task.CompletedTask;
        throw new SpeechProviderException(new SpeechError(SpeechErrorCode.SidecarUnavailable, $"Local speech runtime '{Descriptor.Id}' is not installed.", Descriptor.Id));
    }

    public async IAsyncEnumerable<AudioFrame> SynthesizeAsync(
        SpeechSynthesisRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) yield break;
        await Task.CompletedTask;
        throw new SpeechProviderException(new SpeechError(SpeechErrorCode.SidecarUnavailable, $"Local speech runtime '{Descriptor.Id}' is not installed.", Descriptor.Id));
    }
}

public sealed class SherpaOnnxSpeechProvider() : UnavailableLocalSpeechProvider(new SpeechProviderDescriptor
{
    Id = new SpeechProviderId("sherpa-onnx"),
    Type = SpeechProviderType.Local,
    Capabilities = SpeechCapabilities.Streaming | SpeechCapabilities.PartialTranscripts | SpeechCapabilities.Offline | SpeechCapabilities.Multilingual,
    Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
    CostClass = SpeechCostClass.Zero,
    Version = "external native model runtime"
});

public sealed class WhisperCppSpeechToTextProvider() : UnavailableLocalSpeechProvider(new SpeechProviderDescriptor
{
    Id = new SpeechProviderId("whisper-cpp"),
    Type = SpeechProviderType.Sidecar,
    Capabilities = SpeechCapabilities.Offline | SpeechCapabilities.Multilingual | SpeechCapabilities.NoiseRobust,
    Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
    CostClass = SpeechCostClass.Zero,
    Version = "external sidecar"
});

public sealed class KokoroTextToSpeechProvider() : UnavailableLocalSpeechProvider(new SpeechProviderDescriptor
{
    Id = new SpeechProviderId("kokoro"),
    Type = SpeechProviderType.Local,
    Capabilities = SpeechCapabilities.Offline | SpeechCapabilities.Multilingual | SpeechCapabilities.Streaming,
    Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
    CostClass = SpeechCostClass.Zero,
    Version = "external native model runtime"
});

public sealed class ChatterboxTextToSpeechProvider() : UnavailableLocalSpeechProvider(new SpeechProviderDescriptor
{
    Id = new SpeechProviderId("chatterbox"),
    Type = SpeechProviderType.Sidecar,
    Capabilities = SpeechCapabilities.Offline | SpeechCapabilities.Multilingual | SpeechCapabilities.Streaming | SpeechCapabilities.Expressive,
    Health = new SpeechProviderHealth(SpeechProviderHealthStatus.Unknown),
    CostClass = SpeechCostClass.Zero,
    Version = "external supervised sidecar"
});

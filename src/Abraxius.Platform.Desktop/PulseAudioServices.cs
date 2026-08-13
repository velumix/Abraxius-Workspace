using System.Diagnostics;
using Abraxius.Voice;

namespace Abraxius.Platform.Desktop;

/// <summary>
/// Linux desktop adapter for the normalized voice contracts. It deliberately uses direct
/// executable invocation rather than a shell, and remains outside Abraxius.Voice so other
/// hosts can select browser/mobile/native backends without conditional code in the pipeline.
/// </summary>
public sealed class PulseAudioCaptureService : IAudioCaptureService
{
    private readonly string _captureExecutable;

    public PulseAudioCaptureService(string captureExecutable = "parec") => _captureExecutable = captureExecutable;

    public async ValueTask<AudioPermissionStatus> GetPermissionAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || !ExecutableOnPath(_captureExecutable)) return AudioPermissionStatus.Denied;
        try
        {
            using var process = Start("pactl", "info");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode == 0 ? AudioPermissionStatus.Granted : AudioPermissionStatus.Unknown;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return AudioPermissionStatus.Unknown;
        }
    }

    public ValueTask<AudioPermissionStatus> RequestPermissionAsync(CancellationToken cancellationToken = default) =>
        GetPermissionAsync(cancellationToken);

    public ValueTask<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<AudioDevice>>(
            OperatingSystem.IsLinux() && ExecutableOnPath(_captureExecutable)
                ? [new AudioDevice("pulse-default-input", "PulseAudio default input", AudioDeviceKind.Input, AudioFormat.NormalizedSpeech, true)]
                : []);

    public async IAsyncEnumerable<AudioFrame> CaptureAsync(
        AudioCaptureOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || !ExecutableOnPath(_captureExecutable))
        {
            throw new SpeechProviderException(new SpeechError(SpeechErrorCode.MicrophoneUnavailable, "PulseAudio capture is unavailable on this host."));
        }

        var process = Start(_captureExecutable,
            "--raw",
            "--format=s16le",
            $"--rate={options.Format.SampleRate}",
            $"--channels={options.Format.Channels}");
        try
        {
            var bytesPerFrame = checked((int)(options.Format.BytesPerSecond * options.FrameDuration.TotalSeconds));
            var buffer = new byte[Math.Max(options.Format.BytesPerSample, bytesPerFrame)];
            var sequence = 0L;
            var timestamp = TimeSpan.Zero;
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await ReadAtMostAsync(process.StandardOutput.BaseStream, buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                var frameData = buffer.AsMemory(0, read).ToArray();
                yield return new AudioFrame(frameData, options.Format, sequence++, timestamp);
                timestamp += options.Format.DurationForBytes(read);
            }
        }
        finally
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            process.Dispose();
        }
    }

    private static async ValueTask<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            read += count;
            if (stream is not FileStream) break;
        }

        return read;
    }

    internal static Process Start(string executable, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var process = Process.Start(info);
        return process ?? throw new InvalidOperationException($"Could not start '{executable}'.");
    }

    internal static bool ExecutableOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return false;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, executable))) return true;
        }

        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed class PulseAudioPlaybackService : IAudioPlaybackService
{
    private readonly string _playbackExecutable;

    public PulseAudioPlaybackService(string playbackExecutable = "pacat") => _playbackExecutable = playbackExecutable;

    public ValueTask<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<AudioDevice>>(
            OperatingSystem.IsLinux() && PulseAudioCaptureService.ExecutableOnPath(_playbackExecutable)
                ? [new AudioDevice("pulse-default-output", "PulseAudio default output", AudioDeviceKind.Output, AudioFormat.NormalizedSpeech, true)]
                : []);

    public async ValueTask PlayAsync(
        IAsyncEnumerable<AudioFrame> audio,
        AudioPlaybackOptions options,
        VoiceGenerationId generation,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || !PulseAudioCaptureService.ExecutableOnPath(_playbackExecutable))
        {
            throw new SpeechProviderException(new SpeechError(SpeechErrorCode.PlaybackFailure, "PulseAudio playback is unavailable on this host."));
        }

        using var process = PulseAudioCaptureService.Start(
            _playbackExecutable,
            "--raw",
            "--format=s16le",
            $"--rate={options.Format.SampleRate}",
            $"--channels={options.Format.Channels}");
        try
        {
            await foreach (var frame in audio.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await process.StandardInput.BaseStream.WriteAsync(frame.Data, cancellationToken).ConfigureAwait(false);
            }

            await process.StandardInput.BaseStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
        }
    }

    public ValueTask StopAsync(VoiceGenerationId generation, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

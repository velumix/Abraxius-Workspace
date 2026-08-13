using System.ComponentModel;
using System.Diagnostics;
using Abraxius.Platform;

namespace Abraxius.Platform.Desktop;

public sealed class DesktopProcessExecutionService : IProcessExecutionService
{
    public async ValueTask<PlatformOperationResult<ProcessExecutionResult>> ExecuteAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Executable))
        {
            return PlatformOperationResult.Failure<ProcessExecutionResult>(new PlatformError(
                PlatformErrorCode.InvalidReference,
                "An executable is required."));
        }

        var started = Stopwatch.GetTimestamp();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.Executable,
                WorkingDirectory = request.WorkingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        foreach (var argument in request.Arguments.IsDefault ? [] : request.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var pair in request.Environment)
            {
                process.StartInfo.Environment[pair.Key] = pair.Value;
            }
        }

        try
        {
            if (!process.Start())
            {
                return PlatformOperationResult.Failure<ProcessExecutionResult>(new PlatformError(
                    PlatformErrorCode.PlatformServiceUnavailable,
                    $"The platform could not start '{request.Executable}'."));
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            var waitTask = process.WaitForExitAsync(cancellationToken);
            var timeoutTask = request.Timeout is { } timeout ? Task.Delay(timeout, cancellationToken) : Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(waitTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                await waitTask.ConfigureAwait(false);
                return PlatformOperationResult.Success(new ProcessExecutionResult(
                    -1,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false),
                    Stopwatch.GetElapsedTime(started),
                    TimedOut: true));
            }

            await waitTask.ConfigureAwait(false);
            return PlatformOperationResult.Success(new ProcessExecutionResult(
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false),
                Stopwatch.GetElapsedTime(started)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return PlatformOperationResult.Failure<ProcessExecutionResult>(new PlatformError(
                PlatformErrorCode.PlatformServiceUnavailable,
                "Process execution was cancelled.",
                IsTransient: true));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return PlatformOperationResult.Failure<ProcessExecutionResult>(new PlatformError(
                PlatformErrorCode.PlatformServiceUnavailable,
                $"Process execution failed for '{request.Executable}'.",
                Metadata: new Dictionary<string, string> { ["exception"] = exception.GetType().Name }));
        }
    }
}

public sealed class DesktopFileSystem : IPlatformFileSystem
{
    public ValueTask<PlatformOperationResult<PlatformFileReference>> PickFileAsync(
        FilePickerRequest? request = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlatformOperationResult.Failure<PlatformFileReference>(new PlatformError(
            PlatformErrorCode.PlatformServiceUnavailable,
            "File picking is owned by the Avalonia desktop host and requires a view owner.")));

    public async ValueTask<PlatformOperationResult<Stream>> OpenReadAsync(
        PlatformFileReference reference,
        CancellationToken cancellationToken = default)
    {
        if (reference is not PlatformFileReference.LocalPath local || string.IsNullOrWhiteSpace(local.Path))
        {
            return PlatformOperationResult.Failure<Stream>(new PlatformError(
                PlatformErrorCode.InvalidReference,
                "A local path file reference is required for desktop file access."));
        }

        try
        {
            return PlatformOperationResult.Success<Stream>(new FileStream(
                Path.GetFullPath(local.Path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PlatformOperationResult.Failure<Stream>(new PlatformError(
                PlatformErrorCode.PlatformServiceUnavailable,
                "The desktop file could not be opened.",
                Metadata: new Dictionary<string, string> { ["exception"] = exception.GetType().Name }));
        }
    }

    public async ValueTask<PlatformOperationResult<PlatformFileReference>> WriteAsync(
        PlatformFileReference reference,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (reference is not PlatformFileReference.LocalPath local || string.IsNullOrWhiteSpace(local.Path))
        {
            return PlatformOperationResult.Failure<PlatformFileReference>(new PlatformError(
                PlatformErrorCode.InvalidReference,
                "A local path file reference is required for desktop file access."));
        }

        try
        {
            var path = Path.GetFullPath(local.Path);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllBytesAsync(path, data.ToArray(), cancellationToken).ConfigureAwait(false);
            return PlatformOperationResult.Success<PlatformFileReference>(new PlatformFileReference.LocalPath(path, UserGranted: true));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PlatformOperationResult.Failure<PlatformFileReference>(new PlatformError(
                PlatformErrorCode.PlatformServiceUnavailable,
                "The desktop file could not be written.",
                Metadata: new Dictionary<string, string> { ["exception"] = exception.GetType().Name }));
        }
    }
}

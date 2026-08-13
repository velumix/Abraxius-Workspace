using System.Collections.Immutable;
using Abraxius.Protocol;

namespace Abraxius.Platform;

public abstract record PlatformFileReference
{
    public sealed record LocalPath(string Path, bool UserGranted = false) : PlatformFileReference;
    public sealed record SandboxDocument(string Identifier, string Name) : PlatformFileReference;
    public sealed record BrowserFile(string Name, string Token, long? SizeBytes = null) : PlatformFileReference;
    public sealed record RemoteArtifact(ArtifactId ArtifactId, RemoteHostId? HostId = null) : PlatformFileReference;
    public sealed record LatticeResource(string ResourceId, RemoteHostId? HostId = null) : PlatformFileReference;
}

public sealed record FilePickerRequest(
    string? Title = null,
    ImmutableArray<string> AllowedContentTypes = default,
    bool AllowMultiple = false);

public sealed record PlatformError(
    PlatformErrorCode Code,
    string Message,
    bool IsTransient = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record PlatformOperationResult<T>(T? Value, PlatformError? Error = null)
{
    public bool Succeeded => Error is null;
}

public static class PlatformOperationResult
{
    public static PlatformOperationResult<T> Success<T>(T value) => new(value);
    public static PlatformOperationResult<T> Failure<T>(PlatformError error) => new(default, error);
}

public interface IPlatformFileSystem
{
    ValueTask<PlatformOperationResult<PlatformFileReference>> PickFileAsync(
        FilePickerRequest? request = null,
        CancellationToken cancellationToken = default);

    ValueTask<PlatformOperationResult<Stream>> OpenReadAsync(
        PlatformFileReference reference,
        CancellationToken cancellationToken = default);

    ValueTask<PlatformOperationResult<PlatformFileReference>> WriteAsync(
        PlatformFileReference reference,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default);
}

public sealed record ProcessRequest(
    string Executable,
    ImmutableArray<string> Arguments = default,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? Environment = null,
    TimeSpan? Timeout = null);

public sealed record ProcessExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut = false);

public interface IProcessExecutionService
{
    ValueTask<PlatformOperationResult<ProcessExecutionResult>> ExecuteAsync(
        ProcessRequest request,
        CancellationToken cancellationToken = default);
}

public interface ISecureStorage
{
    ValueTask<PlatformOperationResult<ReadOnlyMemory<byte>>> GetAsync(string key, CancellationToken cancellationToken = default);
    ValueTask<PlatformOperationResult<bool>> SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken cancellationToken = default);
    ValueTask<PlatformOperationResult<bool>> DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface IClipboardService
{
    ValueTask<PlatformOperationResult<string>> GetTextAsync(CancellationToken cancellationToken = default);
    ValueTask<PlatformOperationResult<bool>> SetTextAsync(string text, CancellationToken cancellationToken = default);
}

public interface INotificationService
{
    ValueTask<PlatformOperationResult<bool>> NotifyAsync(string title, string message, CancellationToken cancellationToken = default);
}

public interface IOpenUriService
{
    ValueTask<PlatformOperationResult<bool>> OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

public enum PermissionKind
{
    FileSystem,
    Notifications,
    Clipboard,
    Network,
    Camera,
    Microphone
}

public enum PermissionState
{
    Granted,
    Denied,
    PromptRequired,
    Restricted,
    Unknown
}

public interface IPermissionService
{
    ValueTask<PermissionState> CheckAsync(PermissionKind permission, CancellationToken cancellationToken = default);
    ValueTask<PermissionState> RequestAsync(PermissionKind permission, CancellationToken cancellationToken = default);
}

public enum AppLifecycleState
{
    Starting,
    Active,
    Suspended,
    Resuming,
    Stopping,
    Stopped
}

public sealed record AppLifecycleEvent(AppLifecycleState State, DateTimeOffset Timestamp, string? Reason = null);

public interface IAppLifecycleService
{
    AppLifecycleState CurrentState { get; }
    IAsyncEnumerable<AppLifecycleEvent> ObserveAsync(CancellationToken cancellationToken = default);
}

public interface IPlatformNetworkInformation
{
    ConnectivityState CurrentState { get; }
    event EventHandler<ConnectivityState>? StateChanged;
}

public interface IPlatformPathProvider
{
    string ApplicationDataDirectory { get; }
    string CacheDirectory { get; }
    string? GetKnownDirectory(string name);
}

public sealed class DefaultPlatformPathProvider(IPlatformEnvironment environment) : IPlatformPathProvider
{
    public string ApplicationDataDirectory => GetBaseDirectory("data");
    public string CacheDirectory => GetBaseDirectory("cache");

    public string? GetKnownDirectory(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return environment.Platform.Family is PlatformFamily.Browser
            ? null
            : Path.Combine(ApplicationDataDirectory, name);
    }

    private string GetBaseDirectory(string suffix)
    {
        if (environment.Platform.Family == PlatformFamily.Browser)
        {
            return Path.Combine(AppContext.BaseDirectory, ".abraxius", suffix);
        }

        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = AppContext.BaseDirectory;
        }

        return Path.Combine(baseDirectory, "Abraxius", suffix);
    }
}

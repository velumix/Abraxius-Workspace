using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Abraxius.Distribution;

public enum UpdateChannel
{
    Stable,
    Beta,
    Development
}

public enum InstallationKind
{
    Developer,
    VelopackDirect,
    AppImageManaged,
    Portable,
    PackageManager,
    AppStore,
    PlayStore,
    Browser,
    Unknown
}

public enum UpdateState
{
    Idle,
    Checking,
    Available,
    Downloading,
    Downloaded,
    Applying,
    RestartRequired,
    UpToDate,
    Unavailable,
    Failed,
    RollbackRequired
}

public enum UpdateApplyMode
{
    NotifyOnly,
    ApplyOnExit,
    RestartNow
}

public enum UpdateErrorCode
{
    None,
    UpdateSourceUnavailable,
    NotInstalled,
    NoCompatibleRelease,
    DownloadFailed,
    IntegrityFailed,
    SignatureInvalid,
    UnsupportedPlatform,
    UnsupportedArchitecture,
    InsufficientDiskSpace,
    ApplyFailed,
    RestartFailed,
    MigrationFailed,
    RollbackFailed,
    WrongChannel,
    DowngradeRejected,
    Cancelled,
    Unknown
}

public enum ReleaseSeverity
{
    Normal,
    Recommended,
    Critical
}

public sealed record BuildInfo(
    string ProductName,
    string ProductVersion,
    string BuildVersion,
    string GitCommit,
    UpdateChannel ReleaseChannel,
    DateTimeOffset? BuildTimestamp,
    string PackId,
    string Repository)
{
    public static BuildInfo Current { get; } = FromAssembly();

    private static BuildInfo FromAssembly()
    {
        var assembly = typeof(BuildInfo).Assembly;
        var assemblyVersion = assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var buildVersion = string.IsNullOrWhiteSpace(informational) ? assemblyVersion : informational;
        var metadata = buildVersion.Split('+', 2, StringSplitOptions.TrimEntries);
        var gitCommit = Environment.GetEnvironmentVariable("ABRAXIUS_GIT_COMMIT")
            ?? (metadata.Length == 2 && !string.IsNullOrWhiteSpace(metadata[1]) ? metadata[1] : "unknown");
        var channel = ParseChannel(Environment.GetEnvironmentVariable("ABRAXIUS_RELEASE_CHANNEL"));
        var timestamp = ParseTimestamp(Environment.GetEnvironmentVariable("ABRAXIUS_BUILD_TIMESTAMP"));

        return new BuildInfo(
            Environment.GetEnvironmentVariable("ABRAXIUS_PRODUCT_NAME") ?? "Abraxius",
            assemblyVersion,
            buildVersion,
            gitCommit,
            channel,
            timestamp,
            Environment.GetEnvironmentVariable("ABRAXIUS_PACK_ID") ?? "Abraxius",
            Environment.GetEnvironmentVariable("ABRAXIUS_REPOSITORY") ?? "velumix/Abraxius");
    }

    public static UpdateChannel ParseChannel(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "beta" => UpdateChannel.Beta,
        "development" or "dev" or "nightly" => UpdateChannel.Development,
        _ => UpdateChannel.Stable
    };

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;
}

public sealed record UpdateError(
    UpdateErrorCode Code,
    string Message,
    bool IsTransient = false,
    Exception? Exception = null);

public sealed record UpdateProgress(
    long? BytesDownloaded,
    long? TotalBytes,
    int? Percent,
    string? Phase = null)
{
    public static UpdateProgress Indeterminate(string? phase = null) => new(null, null, null, phase);
}

public sealed record UpdateInfo(
    string UpdateId,
    string Version,
    UpdateChannel Channel,
    ReleaseSeverity Severity,
    string ReleaseNotes,
    long? PackageSize,
    string? Sha256,
    string? PackageFileName,
    bool IsDowngrade = false,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record UpdateCheckResult(
    UpdateState State,
    UpdateInfo? Update,
    string CurrentVersion,
    DateTimeOffset CheckedAt,
    UpdateError? Error = null)
{
    public bool IsAvailable => State == UpdateState.Available && Update is not null;
}

public sealed record UpdateDownloadResult(
    UpdateState State,
    UpdateInfo? Update,
    UpdateError? Error = null)
{
    public bool IsReady => State is UpdateState.Downloaded or UpdateState.RestartRequired;
}

public sealed record UpdateApplyResult(
    UpdateState State,
    UpdateInfo? Update,
    UpdateApplyMode Mode,
    UpdateError? Error = null);

public sealed class UpdateStateChangedEventArgs(
    UpdateState previous,
    UpdateState current,
    UpdateInfo? update,
    UpdateError? error) : EventArgs
{
    public UpdateState Previous { get; } = previous;
    public UpdateState Current { get; } = current;
    public UpdateInfo? Update { get; } = update;
    public UpdateError? Error { get; } = error;
}

public sealed record IntegrationState(
    InstallationKind InstallationKind,
    bool IsInstalled,
    bool IsHealthy,
    string? LauncherPath,
    string? DesktopEntryPath,
    IReadOnlyList<string> Issues);

public interface IPlatformInstallationIntegration
{
    ValueTask<IntegrationState> InspectAsync(CancellationToken cancellationToken = default);

    ValueTask<IntegrationState> ReconcileAsync(CancellationToken cancellationToken = default);

    ValueTask<IntegrationState> RemoveAsync(CancellationToken cancellationToken = default);
}

public sealed record ShortcutSpec(
    string Id,
    string Name,
    string? Arguments = null,
    bool EnabledByDefault = true);

public static class AbraxiusShortcutManifest
{
    public const string ApplicationId = "com.abraxius.Abraxius";
    public const string UriScheme = "abraxius";

    public static IReadOnlyList<ShortcutSpec> ManagedShortcuts { get; } =
    [
        new("main", "Abraxius"),
        new("safe-mode", "Abraxius Safe Mode", "--safe-mode", false),
        new("diagnostics", "Abraxius Diagnostics", "diagnostics", false)
    ];
}

public sealed record UpdatePolicy(
    UpdateChannel Channel = UpdateChannel.Stable,
    bool AutomaticChecks = true,
    bool AutomaticDownload = true,
    TimeSpan? CheckInterval = null,
    UpdateApplyMode ApplyMode = UpdateApplyMode.ApplyOnExit,
    bool AllowPrerelease = false,
    bool AllowDowngrade = false,
    bool AdministratorLocked = false)
{
    public TimeSpan EffectiveCheckInterval => CheckInterval is { } interval && interval >= TimeSpan.FromMinutes(15)
        ? interval
        : TimeSpan.FromHours(6);
}

public interface IUpdateService : IAsyncDisposable
{
    BuildInfo Build { get; }
    InstallationKind InstallationKind { get; }
    UpdateChannel Channel { get; }
    UpdateState State { get; }
    UpdateInfo? AvailableUpdate { get; }
    UpdateInfo? DownloadedUpdate { get; }
    UpdateError? LastError { get; }
    DateTimeOffset? LastCheckedAt { get; }
    bool IsSupported { get; }
    event EventHandler<UpdateStateChangedEventArgs>? StateChanged;

    ValueTask<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    ValueTask<UpdateDownloadResult> DownloadAsync(
        UpdateInfo update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask<UpdateApplyResult> ApplyAsync(
        UpdateInfo update,
        UpdateApplyMode mode,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SetChannelAsync(UpdateChannel channel, CancellationToken cancellationToken = default);
}

public interface IUpdateShutdownParticipant
{
    ValueTask PrepareForUpdateAsync(CancellationToken cancellationToken = default);
}

public interface IUpdateCoordinator
{
    ValueTask<UpdateApplyResult> ApplyAsync(
        UpdateInfo update,
        UpdateApplyMode mode,
        CancellationToken cancellationToken = default);
}

public interface IUpdateRecoveryService
{
    ValueTask<UpdateApplyResult> RollbackAsync(CancellationToken cancellationToken = default);
}

public sealed class UpdateCoordinator(
    IUpdateService updateService,
    IEnumerable<IUpdateShutdownParticipant>? participants = null,
    Func<CancellationToken, ValueTask>? shutdown = null) : IUpdateCoordinator
{
    private readonly IReadOnlyList<IUpdateShutdownParticipant> _participants = (participants ?? []).ToArray();

    public async ValueTask<UpdateApplyResult> ApplyAsync(
        UpdateInfo update,
        UpdateApplyMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        foreach (var participant in _participants)
        {
            await participant.PrepareForUpdateAsync(cancellationToken).ConfigureAwait(false);
        }

        var result = await updateService.ApplyAsync(update, mode, cancellationToken).ConfigureAwait(false);
        if (result.Error is null && mode == UpdateApplyMode.ApplyOnExit && shutdown is not null)
        {
            await shutdown(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}

public sealed class UnavailableUpdateService(
    BuildInfo? build = null,
    InstallationKind installationKind = InstallationKind.Developer) : IUpdateService
{
    public BuildInfo Build { get; } = build ?? BuildInfo.Current;
    public InstallationKind InstallationKind { get; } = installationKind;
    public UpdateChannel Channel => Build.ReleaseChannel;
    public UpdateState State => UpdateState.Unavailable;
    public UpdateInfo? AvailableUpdate => null;
    public UpdateInfo? DownloadedUpdate => null;
    public UpdateError? LastError => new(UpdateErrorCode.NotInstalled, "Updates are available only for an installed desktop build.");
    public DateTimeOffset? LastCheckedAt => null;
    public bool IsSupported => false;
    public event EventHandler<UpdateStateChangedEventArgs>? StateChanged
    {
        add { }
        remove { }
    }

    public ValueTask<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new UpdateCheckResult(State, null, Build.ProductVersion, DateTimeOffset.UtcNow, LastError));

    public ValueTask<UpdateDownloadResult> DownloadAsync(UpdateInfo update, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new UpdateDownloadResult(State, null, LastError));

    public ValueTask<UpdateApplyResult> ApplyAsync(UpdateInfo update, UpdateApplyMode mode, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new UpdateApplyResult(State, null, mode, LastError));

    public ValueTask<bool> SetChannelAsync(UpdateChannel channel, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryUpdateService(
    UpdateInfo? available = null,
    BuildInfo? build = null,
    InstallationKind installationKind = InstallationKind.VelopackDirect) : IUpdateService
{
    private UpdateInfo? _available = available;
    private UpdateInfo? _downloaded;
    private UpdateState _state = UpdateState.Idle;
    private UpdateError? _lastError;
    private DateTimeOffset? _lastChecked;

    public BuildInfo Build { get; } = build ?? BuildInfo.Current;
    public InstallationKind InstallationKind { get; } = installationKind;
    public UpdateChannel Channel { get; private set; } = (available?.Channel).GetValueOrDefault((build ?? BuildInfo.Current).ReleaseChannel);
    public UpdateState State => _state;
    public UpdateInfo? AvailableUpdate => _available;
    public UpdateInfo? DownloadedUpdate => _downloaded;
    public UpdateError? LastError => _lastError;
    public DateTimeOffset? LastCheckedAt => _lastChecked;
    public bool IsSupported => true;
    public event EventHandler<UpdateStateChangedEventArgs>? StateChanged;

    public ValueTask<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _lastChecked = DateTimeOffset.UtcNow;
        SetState(_available is null ? UpdateState.UpToDate : UpdateState.Available, null);
        return ValueTask.FromResult(new UpdateCheckResult(State, _available, Build.ProductVersion, _lastChecked.Value, _lastError));
    }

    public ValueTask<UpdateDownloadResult> DownloadAsync(UpdateInfo update, IProgress<UpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_available?.UpdateId != update.UpdateId)
        {
            var error = new UpdateError(UpdateErrorCode.NoCompatibleRelease, "The update is not the current available release.");
            SetState(UpdateState.Failed, error);
            return ValueTask.FromResult(new UpdateDownloadResult(State, null, error));
        }

        progress?.Report(new UpdateProgress(0, update.PackageSize, 0, "download"));
        _downloaded = update;
        progress?.Report(new UpdateProgress(update.PackageSize, update.PackageSize, 100, "verified"));
        SetState(UpdateState.Downloaded, null);
        return ValueTask.FromResult(new UpdateDownloadResult(State, _downloaded));
    }

    public ValueTask<UpdateApplyResult> ApplyAsync(UpdateInfo update, UpdateApplyMode mode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_downloaded?.UpdateId != update.UpdateId)
        {
            var error = new UpdateError(UpdateErrorCode.ApplyFailed, "The update has not been downloaded.");
            SetState(UpdateState.Failed, error);
            return ValueTask.FromResult(new UpdateApplyResult(State, null, mode, error));
        }

        SetState(mode == UpdateApplyMode.NotifyOnly ? UpdateState.RestartRequired : UpdateState.Applying, null);
        return ValueTask.FromResult(new UpdateApplyResult(State, update, mode));
    }

    public ValueTask<bool> SetChannelAsync(UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Channel = channel;
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        StateChanged = null;
        return ValueTask.CompletedTask;
    }

    private void SetState(UpdateState state, UpdateError? error)
    {
        var previous = _state;
        _state = state;
        _lastError = error;
        if (previous != state || error is not null)
        {
            StateChanged?.Invoke(this, new UpdateStateChangedEventArgs(previous, state, _available ?? _downloaded, error));
        }
    }
}

public sealed record ReleaseManifest(
    string Version,
    UpdateChannel Channel,
    string MinimumProtocol,
    DateTimeOffset ReleasedAt,
    ReleaseSeverity Severity,
    IReadOnlyDictionary<string, ReleasePlatformManifest> Platforms,
    IReadOnlyList<string>? Highlights = null,
    IReadOnlyList<string>? BreakingChanges = null);

public sealed record ReleasePlatformManifest(
    string RuntimeIdentifier,
    string? MinimumOsVersion = null,
    string? MinimumLatticeVersion = null,
    string? MinimumSidecarVersion = null,
    string? PackageSha256 = null,
    long? PackageSize = null);

public static class ReleaseManifestJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string Serialize(ReleaseManifest manifest) => JsonSerializer.Serialize(manifest, Options);

    public static ReleaseManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<ReleaseManifest>(json, Options)
        ?? throw new InvalidDataException("The release manifest was empty.");
}

public sealed record StartupHealthState(
    string Version,
    DateTimeOffset StartedAt,
    DateTimeOffset? HealthyAt,
    int ConsecutiveUnhealthyStarts,
    bool RecoveryRequired);

/// <summary>
/// Small, crash-tolerant startup marker used to detect a release that repeatedly fails before
/// the application can report itself healthy. It deliberately does not perform a rollback: the
/// platform updater owns binary replacement, while recovery policy remains explicit and testable.
/// </summary>
public sealed class StartupHealthMarker
{
    private readonly string _path;
    private readonly object _gate = new();

    public StartupHealthMarker(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public StartupHealthState? Current => Load();

    public StartupHealthState BeginStartup(BuildInfo? build = null)
    {
        var currentBuild = build ?? BuildInfo.Current;
        var previous = Load();
        var unhealthyStarts = previous is not null && previous.HealthyAt is null
            ? previous.ConsecutiveUnhealthyStarts + 1
            : 1;
        var state = new StartupHealthState(
            currentBuild.ProductVersion,
            DateTimeOffset.UtcNow,
            null,
            unhealthyStarts,
            unhealthyStarts >= 2);
        Save(state);
        return state;
    }

    public StartupHealthState MarkHealthy(BuildInfo? build = null)
    {
        var currentBuild = build ?? BuildInfo.Current;
        var state = new StartupHealthState(
            currentBuild.ProductVersion,
            Load()?.StartedAt ?? DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            0,
            false);
        Save(state);
        return state;
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
    }

    public static string DefaultPath()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support")
                : Environment.GetEnvironmentVariable("XDG_STATE_HOME")
                    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "state");
        return Path.Combine(root, "Abraxius", "startup-health.json");
    }

    private StartupHealthState? Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<StartupHealthState>(File.ReadAllText(_path));
            }
            catch (IOException)
            {
                return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    private void Save(StartupHealthState state)
    {
        lock (_gate)
        {
            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Startup health path must have a parent directory.");
            }

            Directory.CreateDirectory(directory);
            var temporary = $"{_path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, _path, overwrite: true);
        }
    }
}

public sealed record UpdateMonitorOptions(
    TimeSpan? InitialDelay = null,
    TimeSpan? CheckInterval = null,
    bool DownloadAutomatically = true);

/// <summary>Runs conservative, cancellable background checks without coupling the UI to a timer.</summary>
public sealed class UpdateMonitor(IUpdateService updateService, UpdateMonitorOptions? options = null)
{
    private readonly UpdateMonitorOptions _options = options ?? new UpdateMonitorOptions();

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var initialDelay = _options.InitialDelay ?? TimeSpan.FromSeconds(Random.Shared.Next(5, 31));
        await Task.Delay(initialDelay, cancellationToken).ConfigureAwait(false);
        var interval = _options.CheckInterval ?? TimeSpan.FromHours(6);
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await updateService.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (_options.DownloadAutomatically && result.Update is not null)
            {
                await updateService.DownloadAsync(result.Update, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }
}

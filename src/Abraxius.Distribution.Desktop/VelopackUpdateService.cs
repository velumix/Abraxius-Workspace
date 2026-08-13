using Abraxius.Distribution;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace Abraxius.Distribution.Desktop;

public sealed record DesktopUpdateOptions(
    string Repository = "velumix/Abraxius",
    UpdateChannel? Channel = null,
    bool? IncludePrereleases = null,
    int MaximumDeltasBeforeFallback = 10,
    InstallationKind? InstallationKind = null,
    string? ChannelStatePath = null,
    BuildInfo? Build = null);

/// <summary>
/// The only assembly that knows about Velopack. Core, Runtime, and the Avalonia view models use
/// IUpdateService instead, which keeps update ownership testable and platform-neutral.
/// </summary>
public sealed class VelopackUpdateService : IUpdateService, IUpdateRecoveryService
{
    private readonly object _gate = new();
    private readonly DesktopUpdateOptions _options;
    private readonly string _repository;
    private readonly string _channelStatePath;
    private UpdateManager? _manager;
    private Velopack.UpdateInfo? _providerUpdate;
    private UpdateInfo? _availableUpdate;
    private UpdateInfo? _downloadedUpdate;
    private UpdateState _state = UpdateState.Idle;
    private UpdateError? _lastError;
    private DateTimeOffset? _lastCheckedAt;
    private UpdateChannel _channel;
    private int _disposed;

    public VelopackUpdateService(DesktopUpdateOptions? options = null)
    {
        _options = options ?? new DesktopUpdateOptions();
        Build = _options.Build ?? BuildInfo.Current;
        _repository = NormalizeRepository(_options.Repository);
        _channelStatePath = _options.ChannelStatePath ?? DistributionStatePaths.DefaultChannelStatePath();
        _channel = _options.Channel ?? UpdateChannelStateStore.Load(_channelStatePath, Build.ReleaseChannel);
        InstallationKind = _options.InstallationKind ?? DesktopInstallationDetector.Detect(_repository, _channel);
        RecreateManager();
    }

    public BuildInfo Build { get; }
    public InstallationKind InstallationKind { get; }
    public UpdateChannel Channel => _channel;
    public UpdateState State => _state;
    public UpdateInfo? AvailableUpdate => _availableUpdate;
    public UpdateInfo? DownloadedUpdate => _downloadedUpdate;
    public UpdateError? LastError => _lastError;
    public DateTimeOffset? LastCheckedAt => _lastCheckedAt;
    public bool IsSupported => (InstallationKind is InstallationKind.VelopackDirect or InstallationKind.AppImageManaged) && _manager is not null;

    public event EventHandler<UpdateStateChangedEventArgs>? StateChanged;

    public async ValueTask<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        _lastCheckedAt = DateTimeOffset.UtcNow;

        if (!IsSupported || _manager is null || !_manager.IsInstalled)
        {
            var error = new UpdateError(UpdateErrorCode.NotInstalled, "This developer or portable build is not managed by Velopack.");
            SetState(UpdateState.Unavailable, error);
            return new UpdateCheckResult(State, null, Build.ProductVersion, _lastCheckedAt.Value, error);
        }

        SetState(UpdateState.Checking, null);
        try
        {
            var update = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (update is null)
            {
                _availableUpdate = null;
                SetState(UpdateState.UpToDate, null);
                return new UpdateCheckResult(State, null, CurrentVersion(), _lastCheckedAt.Value);
            }

            var targetVersion = update.TargetFullRelease.Version.ToString();
            if (update.IsDowngrade || SemanticVersionComparer.Compare(targetVersion, CurrentVersion()) <= 0)
            {
                var error = new UpdateError(UpdateErrorCode.DowngradeRejected, $"Release {targetVersion} is not newer than the installed release.");
                _availableUpdate = null;
                SetState(UpdateState.UpToDate, error);
                return new UpdateCheckResult(State, null, CurrentVersion(), _lastCheckedAt.Value, error);
            }

            _providerUpdate = update;
            _availableUpdate = ToUpdateInfo(update);
            SetState(UpdateState.Available, null);
            return new UpdateCheckResult(State, _availableUpdate, CurrentVersion(), _lastCheckedAt.Value);
        }
        catch (OperationCanceledException)
        {
            var error = new UpdateError(UpdateErrorCode.Cancelled, "Update check was cancelled.", true);
            SetState(UpdateState.Idle, error);
            return new UpdateCheckResult(State, null, CurrentVersion(), _lastCheckedAt.Value, error);
        }
        catch (Exception exception)
        {
            var error = MapError(exception, UpdateErrorCode.UpdateSourceUnavailable);
            SetState(UpdateState.Failed, error);
            return new UpdateCheckResult(State, null, CurrentVersion(), _lastCheckedAt.Value, error);
        }
    }

    public async ValueTask<UpdateDownloadResult> DownloadAsync(
        UpdateInfo update,
        IProgress<UpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported || _manager is null || _providerUpdate is null || _availableUpdate?.UpdateId != update.UpdateId)
        {
            var error = new UpdateError(UpdateErrorCode.NoCompatibleRelease, "The requested update is not the currently selected release.");
            SetState(UpdateState.Failed, error);
            return new UpdateDownloadResult(State, null, error);
        }

        SetState(UpdateState.Downloading, null);
        try
        {
            await _manager.DownloadUpdatesAsync(
                _providerUpdate,
                percent => progress?.Report(new UpdateProgress(null, update.PackageSize, percent, "download")),
                cancellationToken).ConfigureAwait(false);
            progress?.Report(new UpdateProgress(update.PackageSize, update.PackageSize, 100, "verified"));
            _downloadedUpdate = update;
            SetState(UpdateState.Downloaded, null);
            return new UpdateDownloadResult(State, update);
        }
        catch (OperationCanceledException)
        {
            var error = new UpdateError(UpdateErrorCode.Cancelled, "Update download was cancelled.", true);
            SetState(UpdateState.Available, error);
            return new UpdateDownloadResult(State, update, error);
        }
        catch (Exception exception)
        {
            var error = MapError(exception, UpdateErrorCode.DownloadFailed);
            SetState(UpdateState.Failed, error);
            return new UpdateDownloadResult(State, update, error);
        }
    }

    public ValueTask<UpdateApplyResult> ApplyAsync(
        UpdateInfo update,
        UpdateApplyMode mode,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported || _manager is null || _providerUpdate is null || _downloadedUpdate?.UpdateId != update.UpdateId)
        {
            var error = new UpdateError(UpdateErrorCode.ApplyFailed, "The requested update has not been downloaded.");
            SetState(UpdateState.Failed, error);
            return ValueTask.FromResult(new UpdateApplyResult(State, null, mode, error));
        }

        try
        {
            if (mode == UpdateApplyMode.NotifyOnly)
            {
                SetState(UpdateState.RestartRequired, null);
                return ValueTask.FromResult(new UpdateApplyResult(State, update, mode));
            }

            SetState(UpdateState.Applying, null);
            if (mode == UpdateApplyMode.ApplyOnExit)
            {
                // The coordinator must flush application state and then close the process. Velopack
                // waits for that graceful exit and restarts with the downloaded release.
                _manager.WaitExitThenApplyUpdates(_providerUpdate.TargetFullRelease, silent: true, restart: true);
            }
            else
            {
                _manager.ApplyUpdatesAndRestart(_providerUpdate.TargetFullRelease);
            }

            return ValueTask.FromResult(new UpdateApplyResult(UpdateState.Applying, update, mode));
        }
        catch (Exception exception)
        {
            var error = MapError(exception, UpdateErrorCode.ApplyFailed);
            SetState(UpdateState.Failed, error);
            return ValueTask.FromResult(new UpdateApplyResult(State, update, mode, error));
        }
    }

    public async ValueTask<UpdateApplyResult> RollbackAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported || _manager is null)
        {
            var unavailable = new UpdateError(UpdateErrorCode.RollbackFailed, "Rollback is available only for an installed Velopack build.");
            SetState(UpdateState.RollbackRequired, unavailable);
            return new UpdateApplyResult(State, null, UpdateApplyMode.RestartNow, unavailable);
        }

        try
        {
            var locator = VelopackLocator.Current;
            var current = CurrentVersion();
            if (string.IsNullOrWhiteSpace(locator.PackagesDir) || !Directory.Exists(locator.PackagesDir))
            {
                var missingPackages = new UpdateError(UpdateErrorCode.RollbackFailed, "The installed updater package cache is unavailable.");
                SetState(UpdateState.RollbackRequired, missingPackages);
                return new UpdateApplyResult(State, null, UpdateApplyMode.RestartNow, missingPackages);
            }

            var package = Directory.EnumerateFiles(locator.PackagesDir, "*.nupkg", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: path, Version: TryGetPackageVersion(path)))
                .Where(item => item.Version is not null && SemanticVersionComparer.Compare(item.Version, current) < 0)
                .OrderByDescending(item => item.Version, Comparer<string?>.Create((left, right) => SemanticVersionComparer.Compare(left ?? "0.0.0", right ?? "0.0.0")))
                .FirstOrDefault();
            if (package.Path is null || package.Version is null)
            {
                var missing = new UpdateError(UpdateErrorCode.RollbackFailed, "No retained signed full package older than the current release was found.");
                SetState(UpdateState.RollbackRequired, missing);
                return new UpdateApplyResult(State, null, UpdateApplyMode.RestartNow, missing);
            }

            var asset = await VelopackAsset.FromNupkgGenerateChecksumAsync(package.Path).WaitAsync(cancellationToken).ConfigureAwait(false);
            var update = new UpdateInfo(
                $"rollback|{asset.Version}|{asset.SHA256}",
                asset.Version.ToString(),
                Channel,
                ReleaseSeverity.Critical,
                "Explicit rollback to the retained previous package.",
                asset.Size,
                asset.SHA256,
                asset.FileName,
                IsDowngrade: true,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["reason"] = "explicit recovery action",
                    ["package"] = package.Path
                });
            var recoveryManager = new UpdateManager(
                new GithubSource(_repository, accessToken: null, prerelease: _channel != UpdateChannel.Stable),
                new UpdateOptions
                {
                    ExplicitChannel = ChannelName(_channel),
                    AllowVersionDowngrade = true,
                    MaximumDeltasBeforeFallback = 0
                });
            SetState(UpdateState.Applying, null);
            recoveryManager.ApplyUpdatesAndRestart(asset);
            return new UpdateApplyResult(State, update, UpdateApplyMode.RestartNow);
        }
        catch (OperationCanceledException)
        {
            var cancelled = new UpdateError(UpdateErrorCode.Cancelled, "Rollback was cancelled.", true);
            SetState(UpdateState.RollbackRequired, cancelled);
            return new UpdateApplyResult(State, null, UpdateApplyMode.RestartNow, cancelled);
        }
        catch (Exception exception)
        {
            var error = MapError(exception, UpdateErrorCode.RollbackFailed);
            SetState(UpdateState.RollbackRequired, error);
            return new UpdateApplyResult(State, null, UpdateApplyMode.RestartNow, error);
        }
    }

    public ValueTask<bool> SetChannelAsync(UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Channel is not null && _options.Channel != channel)
        {
            return ValueTask.FromResult(false);
        }

        _channel = channel;
        UpdateChannelStateStore.Save(_channelStatePath, channel);
        lock (_gate)
        {
            RecreateManager();
        }

        _availableUpdate = null;
        _downloadedUpdate = null;
        _providerUpdate = null;
        SetState(UpdateState.Idle, null);
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            StateChanged = null;
        }

        return ValueTask.CompletedTask;
    }

    private void RecreateManager()
    {
        try
        {
            var repository = new GithubSource(_repository, accessToken: null, prerelease: _options.IncludePrereleases ?? _channel != UpdateChannel.Stable);
            var options = new UpdateOptions
            {
                ExplicitChannel = ChannelName(_channel),
                AllowVersionDowngrade = false,
                MaximumDeltasBeforeFallback = Math.Max(0, _options.MaximumDeltasBeforeFallback)
            };
            _manager = new UpdateManager(repository, options);
        }
        catch (InvalidOperationException)
        {
            // CLI/developer processes do not call VelopackApp.Build().Run(). They must remain
            // usable and report a non-installed update state rather than failing to start.
            _manager = null;
        }
    }

    private string CurrentVersion() => _manager?.CurrentVersion?.ToString() ?? Build.ProductVersion;

    private UpdateInfo ToUpdateInfo(Velopack.UpdateInfo update)
    {
        var asset = update.TargetFullRelease;
        return new UpdateInfo(
            $"{asset.Version}|{asset.FileName}|{asset.SHA256}",
            asset.Version.ToString(),
            Channel,
            ReleaseSeverity.Normal,
            asset.NotesMarkdown ?? string.Empty,
            asset.Size,
            asset.SHA256,
            asset.FileName,
            update.IsDowngrade,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["packageId"] = asset.PackageId,
                ["channel"] = ChannelName(Channel),
                ["gateway"] = "GitHub Releases / Velopack"
            });
    }

    private void SetState(UpdateState state, UpdateError? error)
    {
        var previous = _state;
        _state = state;
        _lastError = error;
        if (previous != state || error is not null)
        {
            StateChanged?.Invoke(this, new UpdateStateChangedEventArgs(previous, state, _availableUpdate ?? _downloadedUpdate, error));
        }
    }

    private static UpdateError MapError(Exception exception, UpdateErrorCode fallback) =>
        new(fallback, exception.Message, IsTransientException(exception), exception);

    private static bool IsTransientException(Exception exception) => exception is HttpRequestException or IOException or TimeoutException;

    private string? TryGetPackageVersion(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        var prefix = $"{Build.PackId}-";
        var suffix = $"-{ChannelName(Channel)}-full";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var version = fileName[prefix.Length..^suffix.Length];
        return string.IsNullOrWhiteSpace(version) ? null : version;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    internal static string ChannelName(UpdateChannel channel) => channel switch
    {
        UpdateChannel.Beta => "beta",
        UpdateChannel.Development => "development",
        _ => "stable"
    };

    internal static string NormalizeRepository(string repository)
    {
        var value = repository.Trim();
        return value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? value.TrimEnd('/')
            : $"https://github.com/{value.Trim('/') }";
    }
}

public static class DesktopInstallationDetector
{
    public static InstallationKind Detect(string repository, UpdateChannel channel)
    {
        if (OperatingSystem.IsBrowser())
        {
            return InstallationKind.Browser;
        }

        var explicitKind = Environment.GetEnvironmentVariable("ABRAXIUS_INSTALLATION_KIND");
        if (Enum.TryParse<InstallationKind>(explicitKind, ignoreCase: true, out var kind))
        {
            return kind;
        }

        if (Environment.GetEnvironmentVariable("ABRAXIUS_PORTABLE") == "1")
        {
            return InstallationKind.Portable;
        }

        if (OperatingSystem.IsLinux() && IsManagedAppImagePath())
        {
            return InstallationKind.AppImageManaged;
        }

        try
        {
            var manager = new UpdateManager(
                new GithubSource(VelopackUpdateService.NormalizeRepository(repository), null, channel != UpdateChannel.Stable),
                new UpdateOptions { ExplicitChannel = VelopackUpdateService.ChannelName(channel) });
            if (manager.IsInstalled)
            {
                return InstallationKind.VelopackDirect;
            }
        }
        catch
        {
            // Detection must not prevent the application from starting in developer mode.
        }

        return InstallationKind.Developer;
    }

    private static bool IsManagedAppImagePath()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var expected = LinuxInstallationIntegration.CreateDefaultOptions().AppImagePath;
        return string.Equals(
            Path.GetFullPath(processPath),
            Path.GetFullPath(expected),
            StringComparison.Ordinal);
    }
}

internal static class DistributionStatePaths
{
    public static string DefaultChannelStatePath()
    {
        var basePath = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support")
            : OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".config");
        return Path.Combine(basePath, "Abraxius", "update-channel.json");
    }
}

internal static class UpdateChannelStateStore
{
    public static UpdateChannel Load(string path, UpdateChannel fallback)
    {
        try
        {
            if (File.Exists(path))
            {
                var value = File.ReadAllText(path).Trim();
                return Enum.TryParse<UpdateChannel>(value, ignoreCase: true, out var channel) ? channel : fallback;
            }
        }
        catch
        {
            // A failed preference read should never block startup.
        }

        return fallback;
    }

    public static void Save(string path, UpdateChannel channel)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temporary, channel.ToString());
        File.Move(temporary, path, overwrite: true);
    }
}

public static class SemanticVersionComparer
{
    public static int Compare(string left, string right)
    {
        var leftVersion = Parse(left);
        var rightVersion = Parse(right);
        var core = leftVersion.Core.CompareTo(rightVersion.Core);
        if (core != 0)
        {
            return core;
        }

        if (leftVersion.PreRelease.Length == 0 && rightVersion.PreRelease.Length == 0)
        {
            return 0;
        }

        if (leftVersion.PreRelease.Length == 0)
        {
            return 1;
        }

        if (rightVersion.PreRelease.Length == 0)
        {
            return -1;
        }

        for (var index = 0; index < Math.Min(leftVersion.PreRelease.Length, rightVersion.PreRelease.Length); index++)
        {
            var comparison = CompareIdentifier(leftVersion.PreRelease[index], rightVersion.PreRelease[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftVersion.PreRelease.Length.CompareTo(rightVersion.PreRelease.Length);
    }

    private static (Version Core, string[] PreRelease) Parse(string value)
    {
        var withoutMetadata = value.Split('+', 2)[0];
        var parts = withoutMetadata.Split('-', 2);
        var numeric = parts[0].Split('.').Select(static item => int.TryParse(item, out var number) ? number : 0).ToArray();
        var core = new Version(
            numeric.ElementAtOrDefault(0),
            numeric.ElementAtOrDefault(1),
            numeric.ElementAtOrDefault(2),
            numeric.ElementAtOrDefault(3));
        var prerelease = parts.Length == 2 ? parts[1].Split('.', StringSplitOptions.RemoveEmptyEntries) : [];
        return (core, prerelease);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(left, out var leftNumber);
        var rightNumeric = int.TryParse(right, out var rightNumber);
        if (leftNumeric && rightNumeric)
        {
            return leftNumber.CompareTo(rightNumber);
        }

        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }

        return string.Compare(left, right, StringComparison.Ordinal);
    }
}

using System.Collections.Concurrent;
using System.Collections.Immutable;
using Abraxius.Plugin.Contracts;

namespace Abraxius.Plugins;

public interface IPluginSupervisor : IAsyncDisposable
{
    ImmutableArray<PluginHostSnapshot> Hosts { get; }
    ValueTask<PluginInstallation> StartAsync(PluginInstallation installation, CancellationToken cancellationToken = default);
    ValueTask<PluginInstallation> StopAsync(PluginInstallation installation, string reason, CancellationToken cancellationToken = default);
    ValueTask<PluginInvocationResult> InvokeAsync(PluginId pluginId, PluginVersion version, PluginInvocation invocation, CancellationToken cancellationToken = default);
}

public sealed record PluginHostSnapshot(PluginId PluginId, PluginVersion Version, PluginHostId HostId, PluginHealthState Health, int CrashCount, DateTimeOffset StartedAt);

public sealed class PluginSupervisor(IPluginHostLauncher launcher, PluginHostLaunchOptions launchOptions, IPluginRegistry registry, IPluginContributionRegistry contributions, PluginViewValidator views) : IPluginSupervisor
{
    private sealed record Active(IPluginHostSession Session, DateTimeOffset StartedAt);
    private readonly ConcurrentDictionary<(PluginId, PluginVersion), Active> _active = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private int _monitorStarted;
    private int _disposed;
    private Task? _monitorTask;
    public ImmutableArray<PluginHostSnapshot> Hosts => _active.Select(item => new PluginHostSnapshot(item.Key.Item1, item.Key.Item2, item.Value.Session.HostId, item.Value.Session.Health, registry.Find(item.Key.Item1, item.Key.Item2)?.CrashCount ?? 0, item.Value.StartedAt)).OrderBy(static item => item.PluginId.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray();

    public async ValueTask<PluginInstallation> StartAsync(PluginInstallation installation, CancellationToken cancellationToken = default)
    {
        if (installation.State == PluginLifecycleState.Quarantined) throw new InvalidOperationException("Quarantined plugin must be reviewed before restart.");
        if (_active.ContainsKey((installation.Package.PluginId, installation.Package.Version))) return installation;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active.ContainsKey((installation.Package.PluginId, installation.Package.Version))) return installation;
            var starting = installation with { State = PluginLifecycleState.Starting, Health = PluginHealthState.Starting, LastError = null };
            await registry.UpsertAsync(starting, cancellationToken).ConfigureAwait(false);
            IPluginHostSession? session = null;
            try
            {
                session = await launcher.LaunchAsync(starting, launchOptions, cancellationToken).ConfigureAwait(false);
                ValidateRegistration(starting, session.Registration);
                contributions.Register(starting.Package.PluginId, starting.Package.Version, session.Registration);
                _active[(starting.Package.PluginId, starting.Package.Version)] = new(session, DateTimeOffset.UtcNow);
                var running = starting with { State = PluginLifecycleState.Running, Health = PluginHealthState.Healthy, HostId = session.HostId };
                await registry.UpsertAsync(running, cancellationToken).ConfigureAwait(false);
                EnsureMonitor();
                return running;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (session is not null) await session.DisposeAsync().ConfigureAwait(false);
                var failed = starting with { State = PluginLifecycleState.Crashed, Health = PluginHealthState.Crashed, LastError = exception.Message, CrashCount = starting.CrashCount + 1, LastCrashAt = DateTimeOffset.UtcNow };
                await registry.UpsertAsync(failed, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PluginInstallation> StopAsync(PluginInstallation installation, string reason, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            contributions.Unregister(installation.Package.PluginId, installation.Package.Version);
            if (_active.TryRemove((installation.Package.PluginId, installation.Package.Version), out var active))
            {
                await active.Session.StopAsync(reason, cancellationToken).ConfigureAwait(false);
                await active.Session.DisposeAsync().ConfigureAwait(false);
            }
            var stopped = installation with { State = PluginLifecycleState.Disabled, Health = PluginHealthState.Stopped, HostId = null };
            await registry.UpsertAsync(stopped, cancellationToken).ConfigureAwait(false);
            return stopped;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask<PluginInvocationResult> InvokeAsync(PluginId pluginId, PluginVersion version, PluginInvocation invocation, CancellationToken cancellationToken = default)
    {
        if (!_active.TryGetValue((pluginId, version), out var active)) return new(invocation.InvocationId, PluginInvocationStatus.HostUnavailable, ErrorCode: "plugin-not-running", ErrorMessage: "The exact plugin version is not running.");
        return await active.Session.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateRegistration(PluginInstallation installation, PluginRegistration registration)
    {
        var declared = installation.Manifest.Contributions.Select(static item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = registration.Capabilities.Select(static item => item.Id).Concat(registration.Commands.Select(static item => item.Id)).Concat(registration.Navigation.Select(static item => item.Id)).Concat(registration.ArtifactKinds.Select(static item => item.Id)).Concat(registration.Views.Select(static item => item.Id)).Concat(registration.Settings.Select(static item => item.Id)).Concat(registration.Other.Select(static item => item.Id));
        var undeclared = actual.Where(id => !declared.Contains(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (undeclared.Length > 0) throw new InvalidOperationException($"PluginHost registered undeclared contributions: {string.Join(", ", undeclared)}.");
        foreach (var view in registration.Views)
        {
            var errors = views.Validate(view); if (errors.Length > 0) throw new InvalidOperationException($"Plugin view '{view.Id}' is invalid: {string.Join("; ", errors)}");
        }
    }

    private void EnsureMonitor()
    {
        if (Interlocked.Exchange(ref _monitorStarted, 1) != 0) return;
        _monitorTask = MonitorAsync(_lifetime.Token);
    }
    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var pair in _active.ToArray())
                {
                    var health = await pair.Value.Session.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                    if (health is PluginHealthState.Healthy or PluginHealthState.Degraded) continue;
                    if (!_active.TryRemove(pair.Key, out var failed)) continue;
                    contributions.Unregister(pair.Key.Item1, pair.Key.Item2);
                    await failed.Session.DisposeAsync().ConfigureAwait(false);
                    var installation = registry.Find(pair.Key.Item1, pair.Key.Item2); if (installation is null) continue;
                    var recent = installation.LastCrashAt is { } last && DateTimeOffset.UtcNow - last < TimeSpan.FromMinutes(5);
                    var count = recent ? installation.CrashCount + 1 : 1;
                    var quarantined = count >= 3;
                    var updated = installation with { State = quarantined ? PluginLifecycleState.Quarantined : PluginLifecycleState.Crashed, Health = quarantined ? PluginHealthState.Quarantined : PluginHealthState.Crashed, CrashCount = count, LastCrashAt = DateTimeOffset.UtcNow, LastError = "PluginHost stopped responding or exited." };
                    await registry.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
                    if (!quarantined)
                    {
                        try { await Task.Delay(TimeSpan.FromMilliseconds(250 * count), cancellationToken).ConfigureAwait(false); await StartAsync(updated, cancellationToken).ConfigureAwait(false); }
                        catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        if (_monitorTask is not null) await _monitorTask.ConfigureAwait(false);
        foreach (var pair in _active.ToArray()) if (_active.TryRemove(pair.Key, out var active)) await active.Session.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose(); _gate.Dispose();
    }
}

using System.Collections.Immutable;
using Abraxius.Plugin.Contracts;

namespace Abraxius.Plugins;

public sealed class PluginRuntime : IAsyncDisposable
{
    private int _disposed;
    public PluginRuntime(string rootPath, IPluginHostLauncher launcher, PluginHostCommand hostCommand)
    {
        RootPath = Path.GetFullPath(rootPath); Directory.CreateDirectory(RootPath);
        ManifestParser = new StaticPluginManifestParser(); ManifestValidator = new PluginManifestValidator(); SignatureVerifier = new PolicyPluginPackageSignatureVerifier();
        Inspector = new PluginPackageInspector(ManifestParser, ManifestValidator, SignatureVerifier); Store = new FilePluginStore(RootPath);
        Registry = new FilePluginRegistry(Path.Combine(RootPath, "registry.json")); Contributions = new PluginContributionRegistry(); Storage = new NamespacedPluginStorage(); Views = new PluginViewValidator();
        Supervisor = new PluginSupervisor(launcher, PluginHostLaunchOptions.Create(hostCommand, Path.Combine(RootPath, "ipc")), Registry, Contributions, Views);
    }
    public string RootPath { get; }
    public IPluginManifestParser ManifestParser { get; }
    public PluginManifestValidator ManifestValidator { get; }
    public IPluginPackageSignatureVerifier SignatureVerifier { get; }
    public IPluginPackageInspector Inspector { get; }
    public IPluginStore Store { get; }
    public IPluginRegistry Registry { get; }
    public IPluginContributionRegistry Contributions { get; }
    public IPluginStorage Storage { get; }
    public PluginViewValidator Views { get; }
    public IPluginSupervisor Supervisor { get; }

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => Registry.LoadAsync(cancellationToken);
    public ValueTask<PluginPackageInspection> ValidateAsync(string packagePath, bool developerMode = false, CancellationToken cancellationToken = default) => Inspector.InspectAsync(packagePath, new(developerMode), cancellationToken);
    public async ValueTask<PluginInstallation> InstallAsync(PluginInstallRequest request, CancellationToken cancellationToken = default)
    {
        var inspection = await Inspector.InspectAsync(request.PackagePath, new(request.DeveloperMode), cancellationToken).ConfigureAwait(false);
        if (!inspection.Valid) throw new InvalidOperationException(string.Join("; ", inspection.Errors));
        var installation = await Store.InstallAsync(request, inspection, cancellationToken).ConfigureAwait(false);
        await Registry.UpsertAsync(installation, cancellationToken).ConfigureAwait(false); return installation;
    }
    public async ValueTask<PluginInstallation> EnableAsync(PluginId id, PluginVersion? version = null, CancellationToken cancellationToken = default)
    {
        var installation = Registry.Find(id, version) ?? throw new KeyNotFoundException("Exact plugin installation was not found.");
        return await Supervisor.StartAsync(installation, cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask<PluginInstallation> DisableAsync(PluginId id, PluginVersion? version = null, CancellationToken cancellationToken = default)
    {
        var installation = Registry.Find(id, version) ?? throw new KeyNotFoundException("Exact plugin installation was not found.");
        return await Supervisor.StopAsync(installation, "user-disabled", cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask UninstallAsync(PluginId id, PluginVersion version, bool keepData, CancellationToken cancellationToken = default)
    {
        var installation = Registry.Find(id, version) ?? throw new KeyNotFoundException("Exact plugin installation was not found.");
        if (installation.State == PluginLifecycleState.Running) installation = await Supervisor.StopAsync(installation, "uninstall", cancellationToken).ConfigureAwait(false);
        await Registry.RemoveAsync(installation.InstallationId, cancellationToken).ConfigureAwait(false);
        await Store.RemovePayloadAsync(installation, cancellationToken).ConfigureAwait(false);
        if (!keepData)
        {
            // Namespaced state removal is intentionally a separate future streaming operation; callers
            // cannot reach another plugin namespace and package payload remains recoverable.
        }
    }
    public ImmutableArray<PluginInstallation> List() => Registry.Installations.OrderBy(static item => item.Package.PluginId.Value, StringComparer.OrdinalIgnoreCase).ThenByDescending(static item => item.Package.Version).ToImmutableArray();
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await Supervisor.DisposeAsync().ConfigureAwait(false);
        if (Registry is IDisposable disposable) disposable.Dispose();
    }
}

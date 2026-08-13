using System.Reflection;
using System.Runtime.Loader;
using Abraxius.Plugin.Contracts;
using Abraxius.Plugin.Managed;

namespace Abraxius.PluginHost;

internal sealed class PluginLoadContext(string entryAssemblyPath) : AssemblyLoadContext(isCollectible: true)
{
    private readonly AssemblyDependencyResolver _resolver = new(entryAssemblyPath);
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is "Abraxius.Plugin.Contracts" or "Abraxius.Plugin.Managed.Abstractions") return null;
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? nint.Zero : LoadUnmanagedDllFromPath(path);
    }
}

internal sealed class LoadedPlugin : IAsyncDisposable
{
    private readonly PluginLoadContext _context;
    private readonly IAbraxiusPlugin _plugin;
    private LoadedPlugin(PluginLoadContext context, IAbraxiusPlugin plugin) { _context = context; _plugin = plugin; }
    public IAbraxiusPlugin Instance => _plugin;
    public static LoadedPlugin Load(PluginHostBootstrap bootstrap)
    {
        var entry = bootstrap.Manifest.Entrypoints.Single(item => item.Tier == PluginExecutionTier.ManagedOutOfProcess && (item.RuntimeIdentifier is null || item.RuntimeIdentifier.Equals(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, StringComparison.OrdinalIgnoreCase)));
        var root = Path.GetFullPath(bootstrap.PackageDirectory); var path = Path.GetFullPath(Path.Combine(root, entry.Path));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path)) throw new InvalidDataException("Managed plugin entrypoint is outside the approved package or missing.");
        var context = new PluginLoadContext(path);
        try
        {
            var assembly = context.LoadFromAssemblyPath(path);
            var type = assembly.GetType(entry.Type!, throwOnError: true, ignoreCase: false)!;
            if (!typeof(IAbraxiusPlugin).IsAssignableFrom(type)) throw new InvalidDataException("Managed plugin entrypoint does not implement IAbraxiusPlugin.");
            var instance = Activator.CreateInstance(type) as IAbraxiusPlugin ?? throw new InvalidDataException("Managed plugin entrypoint could not be constructed.");
            return new(context, instance);
        }
        catch { context.Unload(); throw; }
    }
    public async ValueTask DisposeAsync() { await _plugin.DisposeAsync().ConfigureAwait(false); _context.Unload(); }
}

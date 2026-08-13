using System.Collections.Immutable;

namespace Abraxius.Plugins;

public sealed record PluginSourceDescriptor(string Id, string DisplayName, Uri Location, bool FirstParty, bool Enabled);
public sealed record PluginSourcePackage(string SourceId, string PackagePath, string? ExpectedHash = null);

public interface IPluginSourceProvider
{
    PluginSourceDescriptor Descriptor { get; }
    IAsyncEnumerable<PluginSourcePackage> EnumerateAsync(CancellationToken cancellationToken = default);
}

public sealed class LocalPluginSourceProvider(string id, string directory, bool developerSource) : IPluginSourceProvider
{
    private readonly string _directory = Path.GetFullPath(directory);
    public PluginSourceDescriptor Descriptor { get; } = new(id, developerSource ? "Developer packages" : "Local packages", new Uri(Path.GetFullPath(directory)), false, true);

    public async IAsyncEnumerable<PluginSourcePackage> EnumerateAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory)) yield break;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.nupkg", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new(Descriptor.Id, path);
            await Task.Yield();
        }
    }
}

public enum WasiPluginAvailability { Unavailable, Experimental, Stable }
public sealed record WasiPluginLimits(long MemoryBytes, long Fuel, TimeSpan Timeout);

public interface IWasiPluginRuntime
{
    WasiPluginAvailability Availability { get; }
    ImmutableArray<string> SupportedFeatures { get; }
    ValueTask<PluginWasiExecutionResult> ExecuteAsync(string componentPath, string operation, ReadOnlyMemory<byte> input, WasiPluginLimits limits, CancellationToken cancellationToken = default);
}

public sealed record PluginWasiExecutionResult(bool Succeeded, ReadOnlyMemory<byte> Output, string? ErrorCode = null, string? ErrorMessage = null);

public sealed class UnavailableWasiPluginRuntime : IWasiPluginRuntime
{
    public WasiPluginAvailability Availability => WasiPluginAvailability.Unavailable;
    public ImmutableArray<string> SupportedFeatures => [];
    public ValueTask<PluginWasiExecutionResult> ExecuteAsync(string componentPath, string operation, ReadOnlyMemory<byte> input, WasiPluginLimits limits, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginWasiExecutionResult(false, ReadOnlyMemory<byte>.Empty, "wasi-unavailable", "No approved WASI runtime is installed."));
    }
}

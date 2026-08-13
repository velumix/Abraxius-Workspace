using System.Collections.Concurrent;
using Abraxius.Plugin.Contracts;
using Abraxius.Plugin.Managed;

namespace Abraxius.PluginHost;

internal sealed class HostPluginContext(PluginManifest manifest) : IPluginContext
{
    public PluginId PluginId => manifest.PluginId;
    public PluginVersion Version => manifest.PluginVersion;
    public IPluginLogger Logger { get; } = new HostPluginLogger();
    public IPluginCapabilityBroker Capabilities { get; } = new DenyByDefaultCapabilityBroker();
    public IPluginStorageClient Storage { get; } = new HostSessionStorage();
}

internal sealed class HostPluginLogger : IPluginLogger
{
    private long _remaining = 4L * 1024 * 1024;
    public ValueTask WriteAsync(PluginLogLevel level, string eventName, string message, IReadOnlyDictionary<string, string>? safeProperties = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounded = message.Length > 16_384 ? message[..16_384] : message; var bytes = System.Text.Encoding.UTF8.GetByteCount(bounded);
        if (Interlocked.Add(ref _remaining, -bytes) < 0) return ValueTask.CompletedTask;
        Console.Error.WriteLine($"[{level}] {eventName}: {bounded}"); return ValueTask.CompletedTask;
    }
}

internal sealed class DenyByDefaultCapabilityBroker : IPluginCapabilityBroker
{
    public ValueTask<PluginInvocationResult> InvokeAsync(string declaredPermission, string capability, string payloadJson, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new PluginInvocationResult(Guid.NewGuid().ToString("N"), PluginInvocationStatus.Denied, ErrorCode: "broker-not-granted", ErrorMessage: "PluginHost has no direct authority. Capability use must be brokered by the authenticated Abraxius session."));
    }
}

internal sealed class HostSessionStorage : IPluginStorageClient
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
    public ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_values.GetValueOrDefault(key)); }
    public ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); _values[key] = value; return ValueTask.CompletedTask; }
    public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_values.TryRemove(key, out _)); }
}

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using Abraxius.Plugin.Contracts;

namespace Abraxius.Plugins;

public sealed record RegisteredPluginContribution(PluginId PluginId, PluginVersion PluginVersion, PluginContributionKind Kind, string LocalId, object Descriptor)
{
    public string GlobalId => $"{PluginId.Value}/{LocalId}";
}

public interface IPluginContributionRegistry
{
    ImmutableArray<RegisteredPluginContribution> Contributions { get; }
    void Register(PluginId pluginId, PluginVersion version, PluginRegistration registration);
    void Unregister(PluginId pluginId, PluginVersion version);
}

public sealed class PluginContributionRegistry : IPluginContributionRegistry
{
    private readonly object _gate = new();
    private ImmutableDictionary<string, RegisteredPluginContribution> _values = ImmutableDictionary<string, RegisteredPluginContribution>.Empty.WithComparers(StringComparer.OrdinalIgnoreCase);
    public ImmutableArray<RegisteredPluginContribution> Contributions => Volatile.Read(ref _values).Values.OrderBy(static item => item.GlobalId, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
    public void Register(PluginId pluginId, PluginVersion version, PluginRegistration registration)
    {
        var pending = new List<RegisteredPluginContribution>();
        pending.AddRange(registration.Capabilities.Select(value => new RegisteredPluginContribution(pluginId, version, PluginContributionKind.CapabilityProvider, value.Id, value)));
        pending.AddRange(registration.Commands.Select(value => new RegisteredPluginContribution(pluginId, version, PluginContributionKind.Command, value.Id, value)));
        pending.AddRange(registration.Navigation.Select(value => new RegisteredPluginContribution(pluginId, version, PluginContributionKind.Navigation, value.Id, value)));
        pending.AddRange(registration.ArtifactKinds.Select(value => new RegisteredPluginContribution(pluginId, version, PluginContributionKind.ArtifactKind, value.Id, value)));
        pending.AddRange(registration.Views.Select(value => new RegisteredPluginContribution(pluginId, version, PluginContributionKind.InspectorPanel, value.Id, value)));
        pending.AddRange(registration.Settings.Select(value => new RegisteredPluginContribution(pluginId, version, PluginContributionKind.SettingsSection, value.Id, value)));
        pending.AddRange(registration.EventSubscriptions.Select(value => new RegisteredPluginContribution(pluginId, version, PluginContributionKind.EventSubscription, $"{value.EventType}:{value.HandlerContributionId}", value)));
        pending.AddRange(registration.Other.Select(value => new RegisteredPluginContribution(pluginId, version, value.Kind, value.Id, value)));
        if (pending.Select(static item => item.GlobalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pending.Count) throw new InvalidOperationException("Plugin registration contains duplicate contribution IDs.");
        lock (_gate)
        {
            var next = _values;
            foreach (var item in pending)
            {
                if (next.ContainsKey(item.GlobalId)) throw new InvalidOperationException($"Contribution '{item.GlobalId}' is already registered.");
                next = next.Add(item.GlobalId, item);
            }
            Volatile.Write(ref _values, next);
        }
    }
    public void Unregister(PluginId pluginId, PluginVersion version)
    {
        lock (_gate) Volatile.Write(ref _values, _values.RemoveRange(_values.Where(item => item.Value.PluginId == pluginId && item.Value.PluginVersion == version).Select(static item => item.Key)));
    }
}

public interface IPluginStorage
{
    ValueTask<string?> GetAsync(PluginId pluginId, string key, CancellationToken cancellationToken = default);
    ValueTask SetAsync(PluginId pluginId, string key, string value, CancellationToken cancellationToken = default);
    ValueTask<bool> RemoveAsync(PluginId pluginId, string key, CancellationToken cancellationToken = default);
}

public sealed class NamespacedPluginStorage(long quotaBytes = 16L * 1024 * 1024) : IPluginStorage
{
    private readonly ConcurrentDictionary<PluginId, ConcurrentDictionary<string, string>> _values = new();
    public ValueTask<string?> GetAsync(PluginId pluginId, string key, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_values.TryGetValue(pluginId, out var store) && store.TryGetValue(ValidateKey(key), out var value) ? value : null); }
    public ValueTask SetAsync(PluginId pluginId, string key, string value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); key = ValidateKey(key); var store = _values.GetOrAdd(pluginId, static _ => new(StringComparer.Ordinal));
        var existing = store.GetValueOrDefault(key); var current = store.Sum(static item => Encoding.UTF8.GetByteCount(item.Key) + Encoding.UTF8.GetByteCount(item.Value));
        var projected = current - (existing is null ? 0 : Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(existing)) + Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
        if (projected > quotaBytes) throw new InvalidOperationException("Plugin storage quota exceeded."); store[key] = value; return ValueTask.CompletedTask;
    }
    public ValueTask<bool> RemoveAsync(PluginId pluginId, string key, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_values.TryGetValue(pluginId, out var store) && store.TryRemove(ValidateKey(key), out _)); }
    private static string ValidateKey(string key) { ArgumentException.ThrowIfNullOrWhiteSpace(key); if (key.Length > 256 || key.Contains("..", StringComparison.Ordinal) || key.Contains('/') || key.Contains('\\')) throw new ArgumentException("Plugin storage keys must be flat, bounded identifiers.", nameof(key)); return key; }
}

public sealed record PluginLogEntry(DateTimeOffset Timestamp, string Level, string EventName, string Message, ImmutableDictionary<string, string> Properties);

public sealed class BoundedPluginLog(long maximumBytes, int maximumEntries = 10_000)
{
    private readonly object _gate = new(); private readonly Queue<(PluginLogEntry Entry, long Bytes)> _entries = new(); private long _bytes;
    public bool Truncated { get; private set; }
    public void Write(PluginLogEntry entry)
    {
        var safeMessage = entry.Message.Length > 16_384 ? entry.Message[..16_384] : entry.Message; var safe = entry with { Message = safeMessage };
        var size = Encoding.UTF8.GetByteCount(safeMessage) + safe.Properties.Sum(static item => Encoding.UTF8.GetByteCount(item.Key) + Encoding.UTF8.GetByteCount(item.Value));
        lock (_gate)
        {
            _entries.Enqueue((safe, size)); _bytes += size;
            while (_entries.Count > maximumEntries || _bytes > maximumBytes) { var removed = _entries.Dequeue(); _bytes -= removed.Bytes; Truncated = true; }
        }
    }
    public ImmutableArray<PluginLogEntry> Snapshot() { lock (_gate) return _entries.Select(static item => item.Entry).ToImmutableArray(); }
}

public sealed class PluginViewValidator(int maximumComponents = 2_000, int maximumDepth = 32, int maximumTextLength = 128 * 1024)
{
    public ImmutableArray<string> Validate(PluginViewDescriptor view)
    {
        var errors = ImmutableArray.CreateBuilder<string>(); var count = 0; Visit(view.Root, 0, errors, ref count);
        if (view.MaximumRows is < 1 or > 100_000) errors.Add("View row bound is outside the permitted range.");
        if (view.PageSize is < 1 or > 1_000) errors.Add("View page size is outside the permitted range."); return errors.ToImmutable();
    }
    private void Visit(PluginViewComponent component, int depth, ImmutableArray<string>.Builder errors, ref int count)
    {
        count++; if (count > maximumComponents) { if (count == maximumComponents + 1) errors.Add("View contains too many components."); return; }
        if (depth > maximumDepth) { errors.Add("View nesting exceeds the permitted depth."); return; }
        if (component.Text?.Length > maximumTextLength) errors.Add($"Component '{component.Id}' text exceeds the permitted bound.");
        if (component.Kind == PluginViewComponentKind.Button && string.IsNullOrWhiteSpace(component.CommandId)) errors.Add($"Button '{component.Id}' must invoke a registered CommandId.");
        if (component.Kind == PluginViewComponentKind.Markdown && ContainsUnsafeMarkup(component.Text)) errors.Add($"Markdown component '{component.Id}' contains embedded HTML or script markup.");
        foreach (var child in component.SafeChildren) Visit(child, depth + 1, errors, ref count);
    }
    private static bool ContainsUnsafeMarkup(string? text) => text is not null && (text.Contains("<script", StringComparison.OrdinalIgnoreCase) || text.Contains("javascript:", StringComparison.OrdinalIgnoreCase) || text.Contains("<iframe", StringComparison.OrdinalIgnoreCase) || text.Contains("<object", StringComparison.OrdinalIgnoreCase));
}

using System.Collections.Immutable;
using Abraxius.Plugin.Contracts;

namespace Abraxius.Plugin.Managed;

/// <summary>The only executable entry contract implemented by managed third-party plugins.</summary>
public interface IAbraxiusPlugin : IAsyncDisposable
{
    ValueTask<PluginRegistration> InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);
    ValueTask<PluginInvocationResult> InvokeAsync(PluginInvocation invocation, CancellationToken cancellationToken = default);
}

public interface IPluginContext
{
    PluginId PluginId { get; }
    PluginVersion Version { get; }
    IPluginLogger Logger { get; }
    IPluginCapabilityBroker Capabilities { get; }
    IPluginStorageClient Storage { get; }
}

public interface IPluginLogger
{
    ValueTask WriteAsync(PluginLogLevel level, string eventName, string message, IReadOnlyDictionary<string, string>? safeProperties = null, CancellationToken cancellationToken = default);
}

public enum PluginLogLevel { Trace, Debug, Information, Warning, Error, Critical }

public interface IPluginCapabilityBroker
{
    ValueTask<PluginInvocationResult> InvokeAsync(string declaredPermission, string capability, string payloadJson, CancellationToken cancellationToken = default);
}

public interface IPluginStorageClient
{
    ValueTask<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    ValueTask SetAsync(string key, string value, CancellationToken cancellationToken = default);
    ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Generated plugin registration tables implement this contract; the host never scans arbitrary types.</summary>
public interface IGeneratedPluginRegistration
{
    static abstract PluginRegistration Create();
}

public sealed class PluginRegistrationBuilder
{
    private readonly ImmutableArray<PluginCapabilityDescriptor>.Builder _capabilities = ImmutableArray.CreateBuilder<PluginCapabilityDescriptor>();
    private readonly ImmutableArray<PluginCommandDescriptor>.Builder _commands = ImmutableArray.CreateBuilder<PluginCommandDescriptor>();
    private readonly ImmutableArray<PluginNavigationDescriptor>.Builder _navigation = ImmutableArray.CreateBuilder<PluginNavigationDescriptor>();
    private readonly ImmutableArray<PluginArtifactKindDescriptor>.Builder _artifacts = ImmutableArray.CreateBuilder<PluginArtifactKindDescriptor>();
    private readonly ImmutableArray<PluginViewDescriptor>.Builder _views = ImmutableArray.CreateBuilder<PluginViewDescriptor>();
    private readonly ImmutableArray<PluginSettingsSchema>.Builder _settings = ImmutableArray.CreateBuilder<PluginSettingsSchema>();
    private readonly ImmutableArray<PluginEventSubscriptionDescriptor>.Builder _events = ImmutableArray.CreateBuilder<PluginEventSubscriptionDescriptor>();
    private readonly ImmutableArray<PluginContributionDeclaration>.Builder _other = ImmutableArray.CreateBuilder<PluginContributionDeclaration>();

    public PluginRegistrationBuilder AddCapability(PluginCapabilityDescriptor value) { _capabilities.Add(value); return this; }
    public PluginRegistrationBuilder AddCommand(PluginCommandDescriptor value) { _commands.Add(value); return this; }
    public PluginRegistrationBuilder AddNavigation(PluginNavigationDescriptor value) { _navigation.Add(value); return this; }
    public PluginRegistrationBuilder AddArtifactKind(PluginArtifactKindDescriptor value) { _artifacts.Add(value); return this; }
    public PluginRegistrationBuilder AddView(PluginViewDescriptor value) { _views.Add(value); return this; }
    public PluginRegistrationBuilder AddSettings(PluginSettingsSchema value) { _settings.Add(value); return this; }
    public PluginRegistrationBuilder AddEventSubscription(PluginEventSubscriptionDescriptor value) { _events.Add(value); return this; }
    public PluginRegistrationBuilder AddOther(PluginContributionDeclaration value) { _other.Add(value); return this; }
    public PluginRegistration Build() => new(_capabilities.ToImmutable(), _commands.ToImmutable(), _navigation.ToImmutable(), _artifacts.ToImmutable(), _views.ToImmutable(), _settings.ToImmutable(), _events.ToImmutable(), _other.ToImmutable());
}

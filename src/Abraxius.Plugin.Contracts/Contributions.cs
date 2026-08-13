using System.Collections.Immutable;

namespace Abraxius.Plugin.Contracts;

public enum PluginCapabilitySideEffect { ReadOnly, Mutation, ExternalSideEffect, Destructive }
public enum PluginInvocationStatus { Succeeded, Failed, Denied, Cancelled, TimedOut, HostUnavailable }
public enum PluginNavigationLocation { PrimaryRail, ToolsGroup, ProjectGroup, Settings }

public sealed record PluginCapabilityDescriptor(string Id, string Name, string InputSchema, string OutputSchema, PluginCapabilitySideEffect SideEffect, TimeSpan Timeout, bool Idempotent, ImmutableArray<string> RequiredPermissions);
public sealed record PluginCommandDescriptor(string Id, string Title, string Description, string ContextPredicate, string ActionRef);
public sealed record PluginNavigationDescriptor(string Id, string Title, PluginNavigationLocation Location, string ViewId, int Order = 0);
public sealed record PluginArtifactKindDescriptor(string Id, string Name, ImmutableArray<string> MediaTypes);
public sealed record PluginSettingsField(string Id, string Label, string Description, string Type, bool Required, string? DefaultValue = null, string? SecretPurpose = null, ImmutableArray<string>? AllowedValues = null)
{
    public ImmutableArray<string> SafeAllowedValues => AllowedValues ?? [];
}
public sealed record PluginSettingsSchema(string Id, string Title, ImmutableArray<PluginSettingsField> Fields);
public sealed record PluginEventSubscriptionDescriptor(string EventType, string HandlerContributionId, bool Background);

public sealed record PluginRegistration(
    ImmutableArray<PluginCapabilityDescriptor> Capabilities,
    ImmutableArray<PluginCommandDescriptor> Commands,
    ImmutableArray<PluginNavigationDescriptor> Navigation,
    ImmutableArray<PluginArtifactKindDescriptor> ArtifactKinds,
    ImmutableArray<PluginViewDescriptor> Views,
    ImmutableArray<PluginSettingsSchema> Settings,
    ImmutableArray<PluginEventSubscriptionDescriptor> EventSubscriptions,
    ImmutableArray<PluginContributionDeclaration> Other)
{
    public static PluginRegistration Empty { get; } = new([], [], [], [], [], [], [], []);
}

public sealed record PluginInvocation(string InvocationId, string ContributionId, string PayloadJson, TimeSpan Timeout, string? TraceParent = null);
public sealed record PluginInvocationResult(string InvocationId, PluginInvocationStatus Status, string PayloadJson = "{}", string? ErrorCode = null, string? ErrorMessage = null);

public sealed record PluginHostBootstrap(
    string EndpointKind,
    string EndpointAddress,
    string SessionId,
    string Nonce,
    string PackageDirectory,
    string ExpectedPackageHash,
    PluginManifest Manifest,
    string HostId);

using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Abraxius.Plugin.Contracts;

public enum PluginExecutionTier { BuiltIn, ManagedOutOfProcess, WasiSandboxed, TrustedInProcess }
public enum PluginActivationMode { OnDemand, Startup, Project, Event }
public enum PluginContributionKind { CapabilityProvider, SkillProvider, SpecialistProfile, ArtifactKind, ArtifactPreview, ArtifactDiff, Command, Navigation, InspectorPanel, SettingsSection, MemoryIndexer, ProjectRecognizer, ModelBackend, ModelSource, ComputeTelemetry, EvalSuite, EventSubscription }
public enum PluginSandboxRequirement { ProcessIsolation, WorkspaceIsolation, NetworkIsolation, StrongSandbox, WasiCapabilitySandbox }
public enum PluginPermissionRisk { ReadOnly, Mutation, External, Credential, Process, Background, Compute, UserInterface }

public sealed record PluginCompatibility(
    string AbraxiusVersionRange,
    PluginApiVersion PluginApi,
    ImmutableArray<string> Platforms,
    ImmutableArray<string> Architectures);

public sealed record PluginEntrypoint(
    PluginExecutionTier Tier,
    string Path,
    string? Type,
    string? RuntimeIdentifier = null);

public sealed record PluginPermissionDeclaration(
    string Id,
    PluginPermissionRisk Risk,
    string Reason,
    ImmutableArray<string> ResourceScopes,
    bool Required = true);

public sealed record PluginContributionDeclaration(
    string Id,
    PluginContributionKind Kind,
    string DisplayName,
    string Version,
    ImmutableArray<string> RequiredPermissions,
    ImmutableDictionary<string, string>? Metadata = null);

public sealed record PluginDependency(string PluginId, string VersionRange, bool Optional = false);

public sealed record PluginManifest(
    int SchemaVersion,
    string Id,
    string Version,
    string Name,
    string Description,
    string Publisher,
    PluginCompatibility Requires,
    ImmutableArray<PluginEntrypoint> Entrypoints,
    ImmutableArray<PluginPermissionDeclaration> Permissions,
    ImmutableArray<PluginContributionDeclaration> Contributions,
    PluginActivationMode ActivationMode = PluginActivationMode.OnDemand,
    PluginSandboxRequirement MinimumSandboxLevel = PluginSandboxRequirement.ProcessIsolation,
    ImmutableArray<PluginDependency> Dependencies = default,
    string? License = null,
    string? ProjectUrl = null)
{
    public PluginId PluginId => new(Id);
    public PluginVersion PluginVersion => PluginVersion.Parse(Version);
    public ImmutableArray<PluginDependency> SafeDependencies => Dependencies.IsDefault ? [] : Dependencies;
}

[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginRegistration))]
[JsonSerializable(typeof(PluginHostBootstrap))]
[JsonSerializable(typeof(PluginViewDescriptor))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
public partial class PluginContractJsonContext : JsonSerializerContext;

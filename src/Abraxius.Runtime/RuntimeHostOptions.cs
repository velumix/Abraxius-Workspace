using Abraxius.Scheduler;
using Abraxius.Platform;

namespace Abraxius.Runtime;

public sealed record RuntimeHostOptions(
    string? LedgerPath = null,
    string? EvidencePath = null,
    string? MemoryDatabasePath = null,
    string? AgentMissionPath = null,
    string? DebriefSessionPath = null,
    string? SkillRegistryPath = null,
    string? ProgressionDatabasePath = null,
    string? PresenceDatabasePath = null,
    string? PresenceSettingsPath = null,
    string? SecurityDatabasePath = null,
    string? ArtifactDatabasePath = null,
    string? ArtifactContentPath = null,
    string? EvaluationDatabasePath = null,
    string? FabricIdentityPath = null,
    string? ComputeRootPath = null,
    string? PluginRootPath = null,
    string? PluginHostPath = null,
    bool UseFileEvidence = true,
    bool UseFileLedger = true,
    bool UseFileProgression = true,
    bool UseFilePresence = true,
    bool UseFileSecurity = true,
    bool UseFileArtifacts = true,
    bool UseFileEvaluation = true,
    bool UseFileFabric = true,
    bool UsePlugins = true,
    int EventBufferCapacity = 8192,
    SchedulerOptions? Scheduler = null,
    IntelligenceFabricOptions? Intelligence = null)
{
    public string EffectiveLedgerPath => LedgerPath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "runtime-events.jsonl");

    public string EffectiveEvidencePath => EvidencePath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "evidence");

    public string EffectiveMemoryDatabasePath => MemoryDatabasePath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "memory", "knowledge.db");
    public string EffectiveAgentMissionPath => AgentMissionPath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "agents", "missions.json");
    public string EffectiveDebriefSessionPath => DebriefSessionPath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "debrief", "sessions.json");
    public string EffectiveSkillRegistryPath => SkillRegistryPath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "skills", "registry.json");
    public string EffectiveProgressionDatabasePath => ProgressionDatabasePath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "progression", "progression.db");
    public string EffectivePresenceDatabasePath => PresenceDatabasePath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "presence", "presence.db");
    public string EffectivePresenceSettingsPath => PresenceSettingsPath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "presence", "settings.json");
    public string EffectiveSecurityDatabasePath => SecurityDatabasePath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "security", "security.db");
    public string EffectiveArtifactDatabasePath => ArtifactDatabasePath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "artifacts", "artifacts.db");
    public string EffectiveArtifactContentPath => ArtifactContentPath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "artifacts", "content");
    public string EffectiveEvaluationDatabasePath => EvaluationDatabasePath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "evaluation", "evaluation.db");
    public string EffectiveFabricIdentityPath => FabricIdentityPath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "fabric", "identity.json");
    public string EffectiveComputeRootPath => ComputeRootPath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "compute");
    public string EffectivePluginRootPath => PluginRootPath ?? Path.Combine(
        new DefaultPlatformPathProvider(PlatformEnvironmentFactory.CreateCurrent()).ApplicationDataDirectory,
        "plugins");
    public string EffectivePluginHostPath => PluginHostPath ?? Path.Combine(AppContext.BaseDirectory, "Abraxius.PluginHost.dll");

    public SchedulerOptions EffectiveScheduler => Scheduler ?? new SchedulerOptions();
    public IntelligenceFabricOptions EffectiveIntelligence => Intelligence ?? new IntelligenceFabricOptions();
}

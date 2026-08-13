using System.Collections.Immutable;
using Abraxius.Core;
using Abraxius.Agents;
using Abraxius.Debrief;
using Abraxius.Lattice;
using Abraxius.Ledger;
using Abraxius.Memory;
using Abraxius.Models;
using Abraxius.Protocol;
using Abraxius.Platform;
using Abraxius.Scheduler;
using Abraxius.Telemetry;
using Abraxius.Voice;
using Abraxius.Skills;
using Abraxius.Progression;
using Abraxius.Presence;
using Abraxius.Security;
using Abraxius.Artifacts;
using Abraxius.Evaluation;
using Abraxius.Fabric;
using Abraxius.Compute;
using Abraxius.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Abraxius.Runtime;

public sealed class AbraxiusRuntimeHost : IAsyncDisposable
{
    private static readonly Action<ILogger, Exception?> LedgerStoppedLog =
        LoggerMessage.Define(LogLevel.Debug, new EventId(100, "LedgerStopped"), "Ledger pump stopped during runtime shutdown.");
    private readonly RuntimeHostOptions _options;
    private readonly RuntimeEventHub _events;
    private readonly IEventLedger _ledger;
    private readonly IEvidenceStore _evidence;
    private readonly IMemoryStore _memoryStore;
    private readonly IModelProvider _model;
    private readonly IntelligenceFabric _intelligence;
    private readonly IMemoryProvider _memory;
    private readonly LatticeExecutor _lattice;
    private readonly IWorkExecutorRegistry _executors;
    private readonly DagScheduler _scheduler;
    private readonly ILogger<AbraxiusRuntimeHost> _logger;
    private readonly IPlatformEnvironment _environment;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _runGate = new();
    private readonly ISkillCandidateExtractor _skillCandidateExtractor;
    private RuntimeEventHub.RuntimeEventSubscription? _ledgerSubscription;
    private RuntimeEventHub.RuntimeEventSubscription? _memorySubscription;
    private Task? _ledgerPump;
    private Task? _memoryPump;
    private Task? _intelligenceDiscovery;
    private Task? _computeDiscovery;
    private CancellationTokenSource? _activeExecution;
    private int _started;
    private int _disposed;

    private AbraxiusRuntimeHost(
        RuntimeHostOptions options,
        RuntimeEventHub events,
        IEventLedger ledger,
        IEvidenceStore evidence,
        IMemoryStore memoryStore,
        IModelProvider model,
        IntelligenceFabric intelligence,
        IMemoryProvider memory,
        LatticeExecutor lattice,
        IWorkExecutorRegistry executors,
        DagScheduler scheduler,
        AgentKernel agents,
        DebriefEngine debrief,
        SkillEngine skills,
        SkillCandidateStore skillCandidates,
        ISkillCandidateExtractor skillCandidateExtractor,
        ProgressionService progression,
        PresenceRuntime presence,
        SecurityRuntime security,
        ArtifactRuntime artifacts,
        EvaluationRuntime evaluation,
        ComputeRuntime compute,
        FabricRuntime fabric,
        PluginRuntime plugins,
        IReadOnlyDictionary<string, ISpeechCredentialProvider> voiceCredentials,
        ILogger<AbraxiusRuntimeHost> logger,
        IPlatformEnvironment environment)
    {
        _options = options;
        _events = events;
        _ledger = ledger;
        _evidence = evidence;
        _memoryStore = memoryStore;
        _model = model;
        _intelligence = intelligence;
        _memory = memory;
        _lattice = lattice;
        _executors = executors;
        _scheduler = scheduler;
        Agents = agents;
        Debrief = debrief;
        Skills = skills;
        SkillCandidates = skillCandidates;
        _skillCandidateExtractor = skillCandidateExtractor;
        Progression = progression;
        Presence = presence;
        Security = security;
        Artifacts = artifacts;
        Evaluation = evaluation;
        Compute = compute;
        Fabric = fabric;
        Plugins = plugins;
        VoiceCredentials = voiceCredentials;
        _logger = logger;
        _environment = environment;
    }

    public RuntimeEventHub Events => _events;
    public IEvidenceStore Evidence => _evidence;
    public IMemoryStore Memory => _memoryStore;
    public IRuntimeMetricsSource Metrics => _scheduler.Metrics;
    public IPlatformEnvironment Environment => _environment;
    public IntelligenceFabric Intelligence => _intelligence;
    public AgentKernel Agents { get; }
    public DebriefEngine Debrief { get; }
    public SkillEngine Skills { get; }
    public SkillCandidateStore SkillCandidates { get; }
    public ProgressionService Progression { get; }
    public PresenceRuntime Presence { get; }
    public SecurityRuntime Security { get; }
    public ArtifactRuntime Artifacts { get; }
    public EvaluationRuntime Evaluation { get; }
    public ComputeRuntime Compute { get; }
    public FabricRuntime Fabric { get; }
    public PluginRuntime Plugins { get; }
    public IReadOnlyDictionary<string, ISpeechCredentialProvider> VoiceCredentials { get; }
    public IModelProvider Model => _model;
    public ExecutionResult? LastExecution { get; private set; }

    public static AbraxiusRuntimeHost CreateDefault(RuntimeHostOptions? options = null)
    {
        var effectiveOptions = options ?? RuntimeConfigurationLoader.ToHostOptions(RuntimeConfigurationLoader.Load());
        var environment = PlatformEnvironmentFactory.CreateCurrent();
        if (!environment.Capabilities.LocalFileSystem)
        {
            effectiveOptions = effectiveOptions with { UseFileEvidence = false, UseFileLedger = false, UseFileProgression = false, UseFilePresence = false, UseFileSecurity = false, UseFileArtifacts = false, UseFileEvaluation = false, UseFileFabric = false, UsePlugins = false };
        }
        var services = new ServiceCollection();
        services.AddSingleton(effectiveOptions);
        services.AddSingleton<IPlatformEnvironment>(environment);
        services.AddSingleton<RuntimeEventHub>();
        services.AddSingleton<IEventLedger>(effectiveOptions.UseFileLedger
            ? new BufferedEventLedger(effectiveOptions.EffectiveLedgerPath, effectiveOptions.EventBufferCapacity)
            : new InMemoryEventLedger());
        services.AddSingleton<IEvidenceStore>(effectiveOptions.UseFileEvidence
            ? new FileEvidenceStore(effectiveOptions.EffectiveEvidencePath)
            : new InMemoryEvidenceStore());
        var securityResources = new ResourceCanonicalizer();
        var securityRisk = new DeterministicRiskClassifier();
        var securityPolicies = new DeterministicPolicyEngine(preset: PolicyPreset.Balanced);
        var securityGrants = new InMemoryAuthorizationGrantStore();
        ISecurityAuditStore securityAudit = effectiveOptions.UseFileSecurity && environment.Capabilities.LocalFileSystem
            ? new SqliteSecurityAuditStore(effectiveOptions.EffectiveSecurityDatabasePath)
            : new InMemorySecurityAuditStore();
        var securityKernel = new SecurityKernel(securityPolicies, securityRisk, securityGrants, securityAudit, securityResources);
        var writableSecrets = new InMemorySecretStore();
        var modelSecrets = ModelSecretBootstrapFactory.Create(effectiveOptions.EffectiveIntelligence, writableSecrets);
        var securityRedactor = new SecretRedactor();
        var secretBroker = new SecretBroker(modelSecrets.Store, securityKernel, securityAudit, securityResources);
        var fabricIdentity = effectiveOptions.UseFileFabric && environment.Capabilities.LocalFileSystem
            ? FabricIdentityPersistence.LoadOrCreate(effectiveOptions.EffectiveFabricIdentityPath)
            : new FabricIdentity(FabricId.New(), FabricNodeId.New());
        var fabricRegistry = new InMemoryFabricNodeRegistry(fabricIdentity.FabricId, new FabricEpoch(1));
        var fabricTransport = new InMemoryFabricTransport();
        services.AddSingleton(_ => new ComputeRuntime(effectiveOptions.EffectiveComputeRootPath));
        services.AddSingleton(_ => new PluginRuntime(
            effectiveOptions.EffectivePluginRootPath,
            new LocalGrpcPluginHostLauncher(),
            PluginHostCommand.ForManagedEntry(effectiveOptions.EffectivePluginHostPath)));
        services.AddSingleton<IFabricNodeRegistry>(fabricRegistry);
        services.AddSingleton<IFabricTransport>(fabricTransport);
        services.AddSingleton<IExecutionPlacementEngine, DeterministicPlacementEngine>();
        services.AddSingleton<IRemoteAuthorizationProvider>(provider => new SecurityKernelRemoteAuthorizationProvider(
            provider.GetRequiredService<ISecurityKernel>(), provider.GetRequiredService<IResourceCanonicalizer>(), fabricIdentity.NodeId));
        services.AddSingleton<FabricRuntime>(provider => new FabricRuntime(
            FabricNodeFactory.CreateLocal(fabricIdentity.NodeId, environment).WithCompute(provider.GetRequiredService<ComputeRuntime>()),
            provider.GetRequiredService<IFabricTransport>(), provider.GetRequiredService<IFabricNodeRegistry>(),
            provider.GetRequiredService<IExecutionPlacementEngine>(), provider.GetRequiredService<IRemoteAuthorizationProvider>()));
        foreach (var reference in modelSecrets.References.Values.Concat(modelSecrets.VoiceReferences.Values).Distinct())
        {
            securityGrants.Issue(new AuthorizationGrant(
                AuthorizationGrantId.New(), modelSecrets.Subject,
                ImmutableHashSet.Create(SecurityActions.SecretUse), reference.Value,
                GrantScope.Session, DateTimeOffset.UtcNow, DateTimeOffset.MaxValue,
                "runtime-configuration", "Credential use is limited to the explicitly enabled model transport."));
        }
        var intelligence = IntelligenceFabricFactory.Create(effectiveOptions.EffectiveIntelligence, secretBroker, securityRedactor,
            modelSecrets.References, modelSecrets.Subject);
        var voiceCredentials = modelSecrets.VoiceReferences.ToDictionary(
            static pair => pair.Key,
            pair => (ISpeechCredentialProvider)new BrokeredSpeechCredentialProvider(
                secretBroker, securityRedactor, pair.Value, modelSecrets.Subject,
                pair.Key == SpeechCredentialNames.Deepgram ? "wss://api.deepgram.com" : "wss://api.elevenlabs.io"),
            StringComparer.OrdinalIgnoreCase);
        services.AddSingleton(intelligence);
        services.AddSingleton<IModelProvider>(intelligence.Provider);
        IMemoryStore memoryStore = environment.Capabilities.LocalFileSystem
            ? new SqliteMemoryStore(effectiveOptions.EffectiveMemoryDatabasePath)
            : new InMemoryKnowledgeStore();
        var memoryRetriever = new HybridMemoryRetriever(memoryStore, new HashEmbeddingProvider());
        services.AddSingleton<IMemoryStore>(memoryStore);
        services.AddSingleton<IHybridMemoryRetriever>(memoryRetriever);
        services.AddSingleton<IMemoryProvider>(_ => new PersistentMemoryProvider(memoryRetriever));
        services.AddSingleton<IResourceCanonicalizer>(securityResources);
        services.AddSingleton<IRiskClassifier>(securityRisk);
        services.AddSingleton<IPolicyEngine>(securityPolicies);
        services.AddSingleton<IAuthorizationGrantStore>(securityGrants);
        services.AddSingleton<ISecurityAuditStore>(securityAudit);
        services.AddSingleton<ISecurityKernel>(securityKernel);
        services.AddSingleton<ConfigurableSecurityApprovalSink>();
        services.AddSingleton<ILatticePolicy, SecurityLatticePolicy>();
        services.AddSingleton<ILatticeCapability>(_ => new MockLatticeCapability());
        services.AddSingleton<LatticeExecutor>();
        services.AddSingleton<IWorkExecutorRegistry>(provider => RuntimeWorkExecutorFactory.Create(
            provider.GetRequiredService<IModelProvider>(),
            provider.GetRequiredService<IMemoryProvider>(),
            provider.GetRequiredService<IHybridMemoryRetriever>(),
            provider.GetRequiredService<LatticeExecutor>(),
            provider.GetRequiredService<IEvidenceStore>(),
            provider.GetRequiredService<RuntimeEventHub>()));
        services.AddSingleton<SchedulerMetrics>();
        services.AddSingleton<DagScheduler>(provider => new DagScheduler(
            effectiveOptions.EffectiveScheduler,
            provider.GetRequiredService<RuntimeEventHub>(),
            provider.GetRequiredService<SchedulerMetrics>(),
            provider.GetRequiredService<IWorkExecutorRegistry>()));
        services.AddSingleton<ISpecialistRegistry>(_ => new SpecialistRegistry());
        services.AddSingleton<IAgentMessageBus>(provider => new AgentMessageBus(effectiveOptions.EventBufferCapacity));
        services.AddSingleton<IAgentMissionStore>(_ => effectiveOptions.UseFileLedger
            ? new JsonAgentMissionStore(effectiveOptions.EffectiveAgentMissionPath)
            : new InMemoryAgentMissionStore());
        services.AddSingleton<IAgentAssignmentRunner>(provider => new SchedulerAgentAssignmentRunner(
            provider.GetRequiredService<DagScheduler>(),
            provider.GetRequiredService<IWorkExecutorRegistry>(),
            provider.GetRequiredService<IEvidenceStore>(),
            provider.GetRequiredService<IPlatformEnvironment>(),
            provider.GetRequiredService<FabricRuntime>()));
        services.AddSingleton<AgentKernel>(provider => new AgentKernel(
            provider.GetRequiredService<ISpecialistRegistry>(),
            provider.GetRequiredService<IAgentAssignmentRunner>(),
            messages: provider.GetRequiredService<IAgentMessageBus>(),
            memory: new MemoryContextCompiler(provider.GetRequiredService<IHybridMemoryRetriever>()),
            missionStore: provider.GetRequiredService<IAgentMissionStore>()));
        services.AddSingleton<ISkillRegistryStore>(_ => effectiveOptions.UseFileLedger
            ? new JsonSkillRegistryStore(effectiveOptions.EffectiveSkillRegistryPath)
            : new InMemorySkillRegistryStore());
        services.AddSingleton<SkillEngineOptions>();
        services.AddSingleton<SkillCandidateStore>();
        services.AddSingleton<ISkillCandidateExtractor, DeterministicSkillCandidateExtractor>();
        services.AddSingleton<SkillRegistry>(provider =>
        {
            var registry = new SkillRegistry(provider.GetRequiredService<ISkillRegistryStore>(), provider.GetRequiredService<SkillEngineOptions>());
            foreach (var skill in BuiltInSkills.CreateAll()) registry.Register(skill);
            return registry;
        });
        services.AddSingleton<ISkillRegistry>(provider => provider.GetRequiredService<SkillRegistry>());
        services.AddSingleton<ISkillMatcher>(provider => new DeterministicSkillMatcher(provider.GetRequiredService<ISkillRegistry>()));
        services.AddSingleton<ISkillValidator, SkillValidator>();
        services.AddSingleton<ISkillPromotionPolicy, SkillPromotionPolicy>();
        services.AddSingleton<ISkillModelOperator>(provider => new RuntimeSkillModelOperator(provider.GetRequiredService<IModelProvider>()));
        services.AddSingleton<ISkillStepRunner>(provider => new RuntimeSkillStepRunner(
            provider.GetRequiredService<AgentKernel>(),
            provider.GetRequiredService<IHybridMemoryRetriever>(),
            provider.GetRequiredService<DagScheduler>(),
            provider.GetRequiredService<IWorkExecutorRegistry>(),
            provider.GetRequiredService<IEvidenceStore>(),
            provider.GetRequiredService<IPlatformEnvironment>(),
            provider.GetRequiredService<FabricRuntime>(),
            provider.GetRequiredService<ISkillModelOperator>()));
        services.AddSingleton<ISkillExecutor>(provider => new SkillExecutor(
            provider.GetRequiredService<ISkillValidator>(),
            provider.GetRequiredService<ISkillStepRunner>(),
            provider.GetRequiredService<SkillEngineOptions>(),
            provider.GetRequiredService<ISkillRegistry>()));
        services.AddSingleton<SkillEngine>(provider => new SkillEngine(
            provider.GetRequiredService<ISkillRegistry>(),
            provider.GetRequiredService<ISkillMatcher>(),
            provider.GetRequiredService<ISkillValidator>(),
            provider.GetRequiredService<ISkillExecutor>(),
            provider.GetRequiredService<ISkillPromotionPolicy>(),
            provider.GetRequiredService<SkillEngineOptions>()));
        services.AddSingleton<IProgressionStore>(_ => effectiveOptions.UseFileProgression && environment.Capabilities.LocalFileSystem
            ? new SqliteProgressionStore(effectiveOptions.EffectiveProgressionDatabasePath)
            : new InMemoryProgressionStore());
        services.AddSingleton<IProgressionRules, ProgressionRulesV1>();
        services.AddSingleton<ProgressionService>(provider => new ProgressionService(
            provider.GetRequiredService<IProgressionStore>(),
            provider.GetRequiredService<IProgressionRules>()));
        services.AddSingleton<INeedsYouStore>(_ => effectiveOptions.UseFilePresence && environment.Capabilities.LocalFileSystem
            ? new SqliteNeedsYouStore(effectiveOptions.EffectivePresenceDatabasePath)
            : new InMemoryNeedsYouStore());
        services.AddSingleton<IPresenceSettingsStore>(_ => effectiveOptions.UseFilePresence && environment.Capabilities.LocalFileSystem
            ? new JsonPresenceSettingsStore(effectiveOptions.EffectivePresenceSettingsPath)
            : new InMemoryPresenceSettingsStore());
        services.AddSingleton<PresenceRuntime>(provider => new PresenceRuntime(
            provider.GetRequiredService<AgentKernel>(),
            provider.GetRequiredService<INeedsYouStore>(),
            provider.GetRequiredService<IPresenceSettingsStore>()));
        services.AddSingleton<ISecretStore>(modelSecrets.Store);
        services.AddSingleton<ISecretRedactor>(securityRedactor);
        services.AddSingleton<ISecretBroker>(secretBroker);
        services.AddSingleton<ISandboxService>(_ => new WorkspaceSandboxService());
        services.AddSingleton<IModelEgressPolicy, ModelEgressPolicy>();
        services.AddSingleton<ISecurityApprovalService>(provider => new SecurityApprovalService(
            provider.GetRequiredService<PresenceRuntime>().NeedsYou,
            provider.GetRequiredService<IAuthorizationGrantStore>(),
            provider.GetRequiredService<ISecurityAuditStore>()));
        services.AddSingleton<SecurityRuntime>(provider =>
        {
            provider.GetRequiredService<ConfigurableSecurityApprovalSink>().Configure(new PresenceSecurityApprovalSink(
                provider.GetRequiredService<ISecurityApprovalService>(), provider.GetRequiredService<PresenceRuntime>()));
            return new SecurityRuntime(provider.GetRequiredService<ISecurityKernel>(), provider.GetRequiredService<IPolicyEngine>(),
                provider.GetRequiredService<IAuthorizationGrantStore>(), provider.GetRequiredService<ISecurityAuditStore>(),
                provider.GetRequiredService<ISecretBroker>(), provider.GetRequiredService<ISecretStore>(),
                provider.GetRequiredService<ISecurityApprovalService>(), provider.GetRequiredService<ISandboxService>(),
                provider.GetRequiredService<IModelEgressPolicy>());
        });
        services.AddSingleton<IArtifactStore>(_ => effectiveOptions.UseFileArtifacts && environment.Capabilities.LocalFileSystem
            ? new SqliteArtifactStore(effectiveOptions.EffectiveArtifactDatabasePath)
            : new InMemoryArtifactStore());
        services.AddSingleton<IArtifactContentStore>(_ => effectiveOptions.UseFileArtifacts && environment.Capabilities.LocalFileSystem
            ? new FileArtifactContentStore(effectiveOptions.EffectiveArtifactContentPath)
            : new InMemoryArtifactContentStore());
        services.AddSingleton<IArtifactService, ArtifactService>();
        services.AddSingleton<IArtifactReviewService>(provider => new ArtifactReviewService(
            provider.GetRequiredService<IArtifactStore>(), provider.GetRequiredService<PresenceRuntime>().NeedsYou));
        services.AddSingleton<IArtifactTargetAdapter, AtomicFileArtifactTargetAdapter>();
        services.AddSingleton<ArtifactIntegrationService>();
        services.AddSingleton<IArtifactSecretScanner, PatternArtifactSecretScanner>();
        services.AddSingleton<IArtifactPublisher, UnavailableArtifactPublisher>();
        services.AddSingleton<ArtifactPublicationService>();
        services.AddSingleton<IArtifactDiffProvider, LinearTextDiffProvider>();
        services.AddSingleton<IArtifactDiffProvider, BinaryMetadataDiffProvider>();
        services.AddSingleton<ArtifactDiffProviderRegistry>();
        services.AddSingleton<IArtifactPreviewProvider, SafeTextPreviewProvider>();
        services.AddSingleton<IArtifactPreviewProvider, SafeMetadataPreviewProvider>();
        services.AddSingleton<ArtifactRuntime>(provider => new ArtifactRuntime(
            provider.GetRequiredService<IArtifactStore>(), provider.GetRequiredService<IArtifactContentStore>(),
            provider.GetRequiredService<IArtifactService>(), provider.GetRequiredService<IArtifactReviewService>(),
            provider.GetRequiredService<ArtifactIntegrationService>(), provider.GetRequiredService<ArtifactPublicationService>(),
            provider.GetRequiredService<ArtifactDiffProviderRegistry>(), provider.GetServices<IArtifactPreviewProvider>().ToArray()));
        services.AddSingleton<IEvalStore>(_ => effectiveOptions.UseFileEvaluation && environment.Capabilities.LocalFileSystem
            ? new SqliteEvalStore(effectiveOptions.EffectiveEvaluationDatabasePath)
            : new InMemoryEvalStore());
        services.AddSingleton<IEvalVerifier, DeterministicEvalVerifier>();
        services.AddSingleton<IEvalCaseExecutor>(provider => new BuiltInEvalCaseExecutor(
            provider.GetRequiredService<IModelEgressPolicy>(), provider.GetRequiredService<ISecurityKernel>(), provider.GetRequiredService<IArtifactService>()));
        services.AddSingleton<IEvalRunner>(provider => new EvalRunner(
            provider.GetRequiredService<DagScheduler>(), provider.GetRequiredService<IEvidenceStore>(), provider.GetRequiredService<IEvalStore>(),
            provider.GetRequiredService<IEvalCaseExecutor>(), provider.GetRequiredService<IEvalVerifier>(), provider.GetRequiredService<IArtifactService>()));
        services.AddSingleton<EvaluationRuntime>();
        services.AddSingleton<IDebriefSourceResolver>(provider => new MemoryDebriefSourceResolver(provider.GetRequiredService<IHybridMemoryRetriever>()));
        services.AddSingleton<IDebriefPlanner>(provider => new DeterministicDebriefPlanner(provider.GetRequiredService<IDebriefSourceResolver>()));
        services.AddSingleton<GroundedDebriefDialogueComposer>();
        services.AddSingleton<IDebriefDialogueComposer>(provider => new Phase6DebriefDialogueComposer(
            provider.GetRequiredService<GroundedDebriefDialogueComposer>(),
            provider.GetRequiredService<IModelProvider>()));
        services.AddSingleton<IDebriefGroundingPolicy>(_ => new DeterministicDebriefGroundingPolicy());
        services.AddSingleton<IDebriefAudioCache, InMemoryDebriefAudioCache>();
        services.AddSingleton<IDebriefSessionStore>(_ => effectiveOptions.UseFileLedger
            ? new JsonDebriefSessionStore(effectiveOptions.EffectiveDebriefSessionPath)
            : new InMemoryDebriefSessionStore());
        services.AddSingleton<DebriefEngine>(provider => new DebriefEngine(
            provider.GetRequiredService<IDebriefPlanner>(),
            provider.GetRequiredService<IDebriefDialogueComposer>(),
            provider.GetRequiredService<IDebriefGroundingPolicy>(),
            provider.GetRequiredService<IDebriefAudioCache>(),
            provider.GetRequiredService<IDebriefSessionStore>(),
            provider.GetRequiredService<AgentKernel>()));
        services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<AbraxiusRuntimeHost>(provider => new AbraxiusRuntimeHost(
            effectiveOptions,
            provider.GetRequiredService<RuntimeEventHub>(),
            provider.GetRequiredService<IEventLedger>(),
            provider.GetRequiredService<IEvidenceStore>(),
            provider.GetRequiredService<IMemoryStore>(),
            provider.GetRequiredService<IModelProvider>(),
            provider.GetRequiredService<IntelligenceFabric>(),
            provider.GetRequiredService<IMemoryProvider>(),
            provider.GetRequiredService<LatticeExecutor>(),
            provider.GetRequiredService<IWorkExecutorRegistry>(),
            provider.GetRequiredService<DagScheduler>(),
            provider.GetRequiredService<AgentKernel>(),
            provider.GetRequiredService<DebriefEngine>(),
            provider.GetRequiredService<SkillEngine>(),
            provider.GetRequiredService<SkillCandidateStore>(),
            provider.GetRequiredService<ISkillCandidateExtractor>(),
            provider.GetRequiredService<ProgressionService>(),
            provider.GetRequiredService<PresenceRuntime>(),
            provider.GetRequiredService<SecurityRuntime>(),
            provider.GetRequiredService<ArtifactRuntime>(),
            provider.GetRequiredService<EvaluationRuntime>(),
            provider.GetRequiredService<ComputeRuntime>(),
            provider.GetRequiredService<FabricRuntime>(),
            provider.GetRequiredService<PluginRuntime>(),
            voiceCredentials,
            provider.GetRequiredService<ILogger<AbraxiusRuntimeHost>>(),
            provider.GetRequiredService<IPlatformEnvironment>()));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        var host = provider.GetRequiredService<AbraxiusRuntimeHost>();
        host._serviceProvider = provider;
        return host;
    }

    private IServiceProvider? _serviceProvider;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (_ledger is BufferedEventLedger bufferedLedger)
        {
            bufferedLedger.Start();
        }

        _ledgerSubscription = _events.Subscribe(_options.EventBufferCapacity, lossy: false, _lifetime.Token);
        _ledgerPump = Task.Run(LedgerPumpAsync, CancellationToken.None);
        await _memoryStore.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Agents.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Skills.Registry.LoadAsync(cancellationToken).ConfigureAwait(false);
        await Progression.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Presence.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Security.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Artifacts.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await Evaluation.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (_options.UsePlugins) await Plugins.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _memorySubscription = _events.Subscribe(Math.Max(128, _options.EventBufferCapacity / 4), lossy: true, _lifetime.Token);
        _memoryPump = Task.Run(MemoryPumpAsync, CancellationToken.None);
        _intelligenceDiscovery = RefreshIntelligenceAsync();
        _computeDiscovery = RefreshComputeAsync();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task<ExecutionResult> ExecuteIntentAsync(Intent intent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        lock (_runGate)
        {
            _activeExecution?.Dispose();
            _activeExecution = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        }

        try
        {
            var graph = DemoExecutionGraphFactory.Create(intent);
            var executionContext = new SchedulerExecutionContext
            {
                ExecutionId = graph.Source.ExecutionId,
                CorrelationId = graph.Source.CorrelationId,
                Environment = _environment,
                Executors = _executors,
                Constraints = intent.Constraints,
                RemoteExecutor = Fabric.RemoteExecutor,
                RemoteHosts = Fabric.RemoteHosts,
                SecurityContext = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["principal.type"] = PrincipalType.System.ToString(),
                    ["principal.id"] = "system:mission-runtime"
                }
            };
            var result = await _scheduler.ExecuteAsync(graph, executionContext, _evidence, _activeExecution.Token).ConfigureAwait(false);
            LastExecution = result;
            await PersistExecutionMemoryAsync(result, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        finally
        {
            lock (_runGate)
            {
                _activeExecution?.Dispose();
                _activeExecution = null;
            }
        }
    }

    public Task<ExecutionResult> RunDemoAsync(CancellationToken cancellationToken = default) =>
        ExecuteIntentAsync(new Intent("Analyze the repository and validate the execution result.", CorrelationId.New()), cancellationToken);

    public async ValueTask<EvalRun> RunEvaluationAsync(EvalRunRequest request, CancellationToken cancellationToken = default)
    {
        await StartAsync(cancellationToken).ConfigureAwait(false);
        return await Evaluation.Runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MissionResult> RunMissionAsync(Intent intent, MissionSuccessContract? successContract = null, SpecialistRole? explicitRole = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        await StartAsync(cancellationToken).ConfigureAwait(false);
        var matches = Skills.Match(new SkillMatchRequest(intent.Objective, explicitRole, intent.Scope, AllowMutation: intent.Constraints.AllowMutation), 1);
        if (matches.Count > 0 && matches[0].Score >= 0.58)
        {
            var skillResult = await Skills.TryExecuteMatchedAsync(new SkillMatchRequest(intent.Objective, explicitRole, intent.Scope, AllowMutation: intent.Constraints.AllowMutation), missionId: null, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (skillResult is { Succeeded: true })
            {
                var contract = successContract ?? new MissionSuccessContract(intent.Objective, ["Skill procedure completed."], ["Skill verification passed."], ["Current policy remains authoritative."]);
                var specialist = explicitRole ?? matches[0].Skill.SpecialistPolicy.PreferredRole ?? SpecialistRole.Coordinator;
                var definition = Agents.Registry.Definitions.First(item => item.Role == specialist);
                var mission = new Mission(MissionId.New(), intent, contract, intent.Priority, definition.CognitiveBudget, definition.AutonomyBudget, definition.WorkspacePolicy.Mode, MissionState.Succeeded, Evidence: skillResult.SafeEvidence.ToImmutableArray(), CreatedAt: DateTimeOffset.UtcNow, CompletedAt: DateTimeOffset.UtcNow);
                var result = new MissionResult(mission, $"Used {skillResult.SkillId}/{skillResult.Version}: {skillResult.Summary}", new Dictionary<AssignmentId, AgentAssignmentResult>(), skillResult.Duration);
                result = await CaptureMissionArtifactAsync(result, ArtifactProducerKind.Skill, cancellationToken).ConfigureAwait(false);
                _ = AwardMissionAsync(result, skillResult, matches[0].Skill);
                return result;
            }
        }

        var missionResult = await Agents.RunMissionAsync(intent, successContract, explicitRole, cancellationToken).ConfigureAwait(false);
        missionResult = await CaptureMissionArtifactAsync(missionResult, explicitRole switch
        {
            SpecialistRole.Investigator => ArtifactProducerKind.Orion,
            SpecialistRole.Builder => ArtifactProducerKind.Daedalus,
            SpecialistRole.Verifier => ArtifactProducerKind.Argus,
            _ => ArtifactProducerKind.Athena
        }, cancellationToken).ConfigureAwait(false);
        if (missionResult.Succeeded)
        {
            _ = ExtractSkillCandidateAsync(missionResult, explicitRole, cancellationToken);
        }
        _ = AwardMissionAsync(missionResult);
        return missionResult;
    }

    private async ValueTask<MissionResult> CaptureMissionArtifactAsync(MissionResult result, ArtifactProducerKind producerKind, CancellationToken cancellationToken)
    {
        var evidence = result.Mission.SafeEvidence.Concat(result.AssignmentResults.Values.SelectMany(static value => value.SafeEvidence)).Distinct().ToImmutableArray();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = "abraxius.mission-report/1",
            missionId = result.Mission.Id.ToString(),
            objective = result.Mission.Intent.Objective,
            state = result.Mission.State.ToString(),
            result.Summary,
            assignments = result.AssignmentResults.Select(static pair => new { assignmentId = pair.Key.ToString(), pair.Value.Succeeded, pair.Value.Summary, verification = pair.Value.Verification?.ToString(), evidence = pair.Value.SafeEvidence.Select(static id => id.ToString()).ToArray() }).ToArray()
        });
        await using var stream = new MemoryStream(payload, writable: false);
        var artifact = await Artifacts.Service.CreateAsync(new CreateArtifactRequest(
            ArtifactKind.Report,
            $"Mission result — {result.Mission.Intent.Objective}",
            new ArtifactProducer(new PrincipalId($"specialist:{producerKind.ToString().ToLowerInvariant()}"), producerKind, producerKind.ToString()),
            new ArtifactProvenance(result.Mission.Id, TrajectoryId: result.Mission.Intent.CorrelationId.ToString(), SourceEvidenceIds: evidence),
            ArtifactClassification.Internal,
            "application/json",
            "mission-result.json",
            result.Succeeded ? ArtifactState.Verified : ArtifactState.VerificationFailed,
            EvidenceRefs: evidence,
            TypedMetadata: ImmutableDictionary<string, string>.Empty
                .Add("mission.state", result.Mission.State.ToString())
                .Add("mission.durationMs", result.Duration.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))), stream, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            var verification = new ArtifactVerification(ArtifactVerificationId.New(), artifact.CurrentRevision.Id,
                new ArtifactProducer(new PrincipalId("specialist:argus"), ArtifactProducerKind.Argus, "Argus"),
                "Mission success contract and assignment outcomes", [new("Mission success contract", "Succeeded", result.Mission.State.ToString(), evidence, ArtifactVerificationResult.Passed)],
                evidence, ArtifactVerificationResult.Passed, DateTimeOffset.UtcNow, _environment.Platform.Family.ToString());
            artifact = await Artifacts.Service.AttachVerificationAsync(artifact.Descriptor.Id, verification, cancellationToken).ConfigureAwait(false);
        }
        var reference = new ArtifactReference(artifact.Descriptor.Id, artifact.Descriptor.Title, artifact.CurrentRevision.Content.MediaType,
            artifact.CurrentRevision.Content.Length, artifact.CurrentRevision.Content.ContentHash, artifact.CurrentRevision.Id.ToString(),
            artifact.CurrentRevision.RevisionHash, artifact.Descriptor.Kind.Value);
        return result with { Artifacts = result.SafeArtifacts.Append(reference).ToArray() };
    }

    private async Task AwardMissionAsync(MissionResult result, SkillExecutionResult? skillResult = null, SkillDefinition? skill = null)
    {
        try
        {
            var trajectory = RuntimeProgressionProjection.FromMission(result, Agents.Assignments, _intelligence.Snapshot.LastDecision?.Tier, skillResult, skill);
            await Progression.ProcessAsync(trajectory, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await _events.PublishAsync(new RuntimeWarningEvent(DateTimeOffset.UtcNow, ExecutionId.Empty, null, result.Mission.Intent.CorrelationId,
                "runtime.progression", $"Progression reward unavailable: {exception.GetType().Name}"), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ExtractSkillCandidateAsync(MissionResult result, SpecialistRole? preferredRole, CancellationToken cancellationToken)
    {
        try
        {
            var evidence = result.Mission.SafeEvidence
                .Concat(result.AssignmentResults.Values.SelectMany(static item => item.SafeEvidence))
                .Distinct()
                .ToArray();
            if (evidence.Length == 0) return;
            var steps = result.AssignmentResults.Values
                .Where(static item => item.Succeeded)
                .Select(static item => item.Summary)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
            var candidate = await _skillCandidateExtractor.ExtractAsync(new SkillExtractionRequest(
                result.Mission.Id,
                result.Mission.Intent.Objective,
                steps,
                evidence,
                result.Mission.SuccessContract.VerificationRequirements,
                result.Mission.Intent.Scope,
                preferredRole), cancellationToken).ConfigureAwait(false);
            if (candidate is null) return;
            SkillCandidates.Add(candidate);
            if (!Skills.Registry.TryGet(candidate.Definition.Id, candidate.Definition.Version, out _))
            {
                Skills.Registry.Register(candidate.Definition with { Enabled = false });
                await Skills.Registry.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Candidate extraction is best-effort and never changes the mission outcome.
        }
        catch
        {
            // Candidate extraction is advisory; the completed mission remains authoritative.
        }
    }

    public ValueTask<DebriefSession> CreateDebriefAsync(DebriefRequest request, CancellationToken cancellationToken = default) =>
        Debrief.CreateAsync(request, cancellationToken);

    public void ConfigureDebriefAudio(ITextToSpeechProvider tts, IAudioPlaybackService? playback = null) =>
        Debrief.ConfigureAudio(tts, playback);

    public void CancelActiveExecution()
    {
        lock (_runGate)
        {
            _activeExecution?.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();

        CancelActiveExecution();
        if (_ledgerSubscription is not null)
        {
            await _ledgerSubscription.DisposeAsync().ConfigureAwait(false);
        }

        if (_memorySubscription is not null)
        {
            await _memorySubscription.DisposeAsync().ConfigureAwait(false);
        }

        await Agents.DisposeAsync().ConfigureAwait(false);
        await Debrief.DisposeAsync().ConfigureAwait(false);
        await Security.DisposeAsync().ConfigureAwait(false);
        await Artifacts.DisposeAsync().ConfigureAwait(false);
        await Evaluation.DisposeAsync().ConfigureAwait(false);
        await Fabric.DisposeAsync().ConfigureAwait(false);
        await Plugins.DisposeAsync().ConfigureAwait(false);
        await Presence.DisposeAsync().ConfigureAwait(false);

        if (_ledgerPump is not null)
        {
            try
            {
                await _ledgerPump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Shutdown cancellation is expected.
            }
        }

        if (_memoryPump is not null)
        {
            try { await _memoryPump.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        if (_intelligenceDiscovery is not null)
        {
            try
            {
                await _intelligenceDiscovery.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Gateway discovery is optional during shutdown.
            }
        }

        if (_computeDiscovery is not null)
        {
            try { await _computeDiscovery.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }

        await Compute.DisposeAsync().ConfigureAwait(false);

        await _ledger.FlushAsync().ConfigureAwait(false);
        await _ledger.DisposeAsync().ConfigureAwait(false);
        await _events.DisposeAsync().ConfigureAwait(false);
        await _scheduler.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        if (_serviceProvider is IAsyncDisposable asyncProvider)
        {
            await asyncProvider.DisposeAsync().ConfigureAwait(false);
        }
        else if (_serviceProvider is IDisposable provider)
        {
            provider.Dispose();
        }
    }

    private async Task LedgerPumpAsync()
    {
        if (_ledgerSubscription is null)
        {
            return;
        }

        try
        {
            await foreach (var runtimeEvent in ((IAsyncEnumerable<RuntimeEvent>)_ledgerSubscription).ConfigureAwait(false))
            {
                await _ledger.AppendAsync(runtimeEvent, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            LedgerStoppedLog(_logger, null);
        }
    }

    private async Task RefreshIntelligenceAsync()
    {
        try
        {
            await _intelligence.RefreshHealthAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await _events.PublishAsync(new RuntimeWarningEvent(
                DateTimeOffset.UtcNow,
                ExecutionId.Empty,
                null,
                CorrelationId.New(),
                "runtime.intelligence",
                $"Gateway discovery unavailable: {exception.GetType().Name}")).ConfigureAwait(false);
        }
    }

    private async Task RefreshComputeAsync()
    {
        try
        {
            await Compute.RefreshAsync(_lifetime.Token).ConfigureAwait(false);
            Fabric.UpdateLocalNode(node => node.WithCompute(Compute));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            await _events.PublishAsync(new RuntimeWarningEvent(DateTimeOffset.UtcNow, ExecutionId.Empty, null, CorrelationId.New(),
                "runtime.compute", $"Compute discovery unavailable: {exception.GetType().Name}"), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task MemoryPumpAsync()
    {
        if (_memorySubscription is null) return;
        try
        {
            await foreach (var runtimeEvent in ((IAsyncEnumerable<RuntimeEvent>)_memorySubscription).ConfigureAwait(false))
            {
                MemoryEntry? entry = runtimeEvent switch
                {
                    ExecutionCompletedEvent completed => MemoryEntry.Create(
                        MemoryKind.Episodic, MemoryScopeKind.Execution, completed.ExecutionId.ToString(),
                        $"Execution {completed.ExecutionId}", completed.Summary,
                        new MemoryProvenance(MemorySourceKind.VerifiedExecution, completed.Succeeded ? 1 : 0.7, completed.Timestamp, SourceCommit: null),
                        [new MemoryEvidenceLink(ExecutionId: completed.ExecutionId)], MemoryIds.Stable($"execution|{completed.ExecutionId}")) with
                        { LastVerifiedAt = completed.Succeeded ? completed.Timestamp : null, Metadata = new Dictionary<string, string> { ["succeeded"] = completed.Succeeded.ToString() }.ToImmutableDictionary(StringComparer.Ordinal) },
                    ExecutionFailedEvent failed => MemoryEntry.Create(
                        MemoryKind.Episodic, MemoryScopeKind.Execution, failed.ExecutionId.ToString(),
                        $"Failed execution {failed.ExecutionId}", failed.Error.Message,
                        new MemoryProvenance(MemorySourceKind.VerifiedExecution, 0.9, failed.Timestamp),
                        [new MemoryEvidenceLink(ExecutionId: failed.ExecutionId)], MemoryIds.Stable($"execution|{failed.ExecutionId}")),
                    ExecutionCancelledEvent cancelled => MemoryEntry.Create(
                        MemoryKind.Episodic, MemoryScopeKind.Execution, cancelled.ExecutionId.ToString(),
                        $"Cancelled execution {cancelled.ExecutionId}", cancelled.Reason,
                        new MemoryProvenance(MemorySourceKind.VerifiedExecution, 0.85, cancelled.Timestamp),
                        [new MemoryEvidenceLink(ExecutionId: cancelled.ExecutionId)], MemoryIds.Stable($"execution|{cancelled.ExecutionId}")),
                    _ => null
                };
                if (entry is not null) await _memoryStore.UpsertAsync(entry, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    private async ValueTask PersistExecutionMemoryAsync(ExecutionResult result, CancellationToken cancellationToken)
    {
        var evidence = result.Results.Values
            .SelectMany(static item => item.Evidence)
            .Distinct()
            .Select(static id => new MemoryEvidenceLink(EvidenceId: id, ExecutionId: null))
            .ToArray();
        var summary = $"{(result.Succeeded ? "Succeeded" : result.Cancelled ? "Cancelled" : "Failed")} execution with {result.Tasks.Count} tasks, {result.Results.Count} results, and {result.Errors.Count} errors. Elapsed {result.Elapsed.TotalMilliseconds:0} ms.";
        var entry = MemoryEntry.Create(
            MemoryKind.Episodic,
            MemoryScopeKind.Execution,
            result.ExecutionId.ToString(),
            $"Execution {result.ExecutionId}",
            summary,
            new MemoryProvenance(MemorySourceKind.VerifiedExecution, result.Succeeded ? 1 : 0.85, DateTimeOffset.UtcNow),
            evidence,
            MemoryIds.Stable($"execution|{result.ExecutionId}")) with
        {
            LastVerifiedAt = result.Succeeded ? DateTimeOffset.UtcNow : null,
            Metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["succeeded"] = result.Succeeded.ToString(),
                ["cancelled"] = result.Cancelled.ToString(),
                ["task_count"] = result.Tasks.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["error_count"] = result.Errors.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }.ToImmutableDictionary(StringComparer.Ordinal)
        };
        await _memoryStore.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}

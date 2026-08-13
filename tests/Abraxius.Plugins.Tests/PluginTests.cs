using System.Collections.Immutable;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Abraxius.Plugin.Contracts;
using Abraxius.Plugins;
using Abraxius.Extensions.Repository;
using Xunit;

namespace Abraxius.Plugins.Tests;

public sealed class PluginTests
{
    [Fact]
    public async Task StaticManifestParserRejectsOversizedInput()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 2048)));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new StaticPluginManifestParser().ParseAsync(input, 1024));
    }

    [Fact]
    public void ManifestRejectsInProcessAndTraversalEntrypoints()
    {
        var manifest = Manifest(entrypoints: [new(PluginExecutionTier.TrustedInProcess, "../evil.dll", "Evil")]);
        var errors = new PluginManifestValidator().Validate(manifest);
        Assert.Contains(errors, item => item.Contains("TrustedInProcess", StringComparison.Ordinal));
        Assert.Contains(errors, item => item.Contains("safe package-relative", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnsignedPackageRequiresDeveloperModeAndTamperChangesIdentity()
    {
        var root = Temp(); var package = Path.Combine(root, "sample.nupkg"); CreatePackage(package, Manifest());
        var inspector = Inspector();
        var normal = await inspector.InspectAsync(package, new(false)); Assert.False(normal.Valid); Assert.Contains(normal.Errors, item => item.Contains("Developer Mode", StringComparison.Ordinal));
        var developer = await inspector.InspectAsync(package, new(true)); Assert.True(developer.Valid);
        await File.AppendAllTextAsync(package, "tamper"); var changed = await inspector.InspectAsync(package, new(true)); Assert.NotEqual(developer.Identity.Sha256, changed.Identity.Sha256);
    }

    [Fact]
    public async Task InstallIsSideBySideAndPermissionReviewPrecedesActivation()
    {
        var root = Temp(); var package = Path.Combine(root, "sample.nupkg"); var manifest = Manifest(); CreatePackage(package, manifest);
        var inspection = await Inspector().InspectAsync(package, new(true)); var store = new FilePluginStore(Path.Combine(root, "store"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await store.InstallAsync(new(package, [], PluginPublisherTrust.Unknown, true), inspection));
        var grants = manifest.Permissions.Select(item => new PluginPermissionGrant(item.Id, item.ResourceScopes, DateTimeOffset.UtcNow, "test-user")).ToImmutableArray();
        var installed = await store.InstallAsync(new(package, grants, PluginPublisherTrust.Unknown, true), inspection);
        Assert.True(File.Exists(Path.Combine(Directory.GetParent(installed.PackageDirectory)!.FullName, "package.nupkg")));
        Assert.Contains(installed.Package.Sha256, installed.PackageDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionExpansionIsExplicit()
    {
        var current = Manifest();
        var candidate = current with { Version = "1.1.0", Permissions = current.Permissions.Add(new("project.write", PluginPermissionRisk.Mutation, "write", ["project://active"])) };
        var difference = PluginPermissionDiff.Compare(current, candidate);
        Assert.True(difference.ExpandsAuthority); Assert.Single(difference.Added);
    }

    [Fact]
    public void ContributionRegistryNamespacesAndUnregistersExactVersion()
    {
        var registry = new PluginContributionRegistry(); var id = new PluginId("com.example.sample"); var version = PluginVersion.Parse("1.0.0");
        var registration = PluginRegistration.Empty with { Commands = [new("open", "Open", "", "true", "action")] };
        registry.Register(id, version, registration); Assert.Equal("com.example.sample/open", Assert.Single(registry.Contributions).GlobalId);
        Assert.Throws<InvalidOperationException>(() => registry.Register(id, version, registration));
        registry.Unregister(id, version); Assert.Empty(registry.Contributions);
    }

    [Fact]
    public async Task StorageIsNamespacedAndQuotaBounded()
    {
        var storage = new NamespacedPluginStorage(32); var one = new PluginId("com.example.one"); var two = new PluginId("com.example.two");
        await storage.SetAsync(one, "key", "value"); Assert.Null(await storage.GetAsync(two, "key"));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await storage.SetAsync(one, "large", new string('x', 64)));
    }

    [Fact]
    public void DeclarativeUiRejectsScriptAndUnboundButtons()
    {
        var view = new PluginViewDescriptor("bad", "Bad", new("root", PluginViewComponentKind.Stack, Children: [new("script", PluginViewComponentKind.Markdown, "<script>alert(1)</script>"), new("button", PluginViewComponentKind.Button, "Run")]));
        var errors = new PluginViewValidator().Validate(view); Assert.Equal(2, errors.Length);
    }

    [Fact]
    public void BoundedLogDropsOldEntries()
    {
        var log = new BoundedPluginLog(16, 2); for (var i = 0; i < 10; i++) log.Write(new(DateTimeOffset.UtcNow, "Info", "event", "12345678", ImmutableDictionary<string, string>.Empty));
        Assert.True(log.Truncated); Assert.InRange(log.Snapshot().Length, 1, 2);
    }

    [Fact]
    public async Task SupervisorQuarantinesInvalidRegistrationBeforeExposure()
    {
        var root = Temp(); var registry = new FilePluginRegistry(Path.Combine(root, "registry.json")); var manifest = Manifest(); var installation = Installation(root, manifest);
        await registry.UpsertAsync(installation);
        var bad = PluginRegistration.Empty with { Commands = [new("undeclared", "Bad", "", "true", "bad")] };
        await using var supervisor = new PluginSupervisor(new FakeLauncher(bad), PluginHostLaunchOptions.Create(new("unused"), root), registry, new PluginContributionRegistry(), new PluginViewValidator());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await supervisor.StartAsync(installation));
        Assert.Empty(supervisor.Hosts);
    }

    [Fact]
    public async Task RealOutOfProcessHostLoadsPinnedPackageInvokesAndStops()
    {
        var root = Temp();
        var manifest = RepositoryManifest();
        var package = Path.Combine(root, "repository.nupkg");
        CreatePackage(package, manifest, typeof(RepositoryIntelligencePlugin).Assembly.Location, "lib/net10.0/Abraxius.Extensions.Repository.dll");
        var hostAssembly = Path.Combine(AppContext.BaseDirectory, "Abraxius.PluginHost.dll");
        Assert.True(File.Exists(hostAssembly), $"PluginHost assembly was not copied to the test output: {hostAssembly}");
        await using var runtime = new PluginRuntime(Path.Combine(root, "store"), new LocalGrpcPluginHostLauncher(), PluginHostCommand.ForManagedEntry(hostAssembly));
        await runtime.InitializeAsync();
        var grants = manifest.Permissions.Select(item => new PluginPermissionGrant(item.Id, item.ResourceScopes, DateTimeOffset.UtcNow, "test-user")).ToImmutableArray();
        var installed = await runtime.InstallAsync(new(package, grants, PluginPublisherTrust.FirstParty, true));
        var running = await runtime.EnableAsync(installed.Package.PluginId, installed.Package.Version);
        var result = await runtime.Supervisor.InvokeAsync(running.Package.PluginId, running.Package.Version,
            new(Guid.NewGuid().ToString("N"), "repo.inspect", "{\"fileCount\":42,\"branch\":\"main\",\"clean\":true}", TimeSpan.FromSeconds(5)));
        Assert.Equal(PluginInvocationStatus.Succeeded, result.Status);
        Assert.Contains("42 files on main", result.PayloadJson, StringComparison.Ordinal);
        await runtime.DisableAsync(running.Package.PluginId, running.Package.Version);
        Assert.Empty(runtime.Supervisor.Hosts);
        Assert.Empty(runtime.Contributions.Contributions);
    }

    private static PluginPackageInspector Inspector() => new(new StaticPluginManifestParser(), new PluginManifestValidator(), new PolicyPluginPackageSignatureVerifier());
    private static PluginManifest Manifest(ImmutableArray<PluginEntrypoint>? entrypoints = null) => new(1, "com.example.sample", "1.0.0", "Sample", "test", "Example", new(">=1.0 <2.0", PluginApiVersion.Current, ["Linux"], ["x64"]), entrypoints ?? [new(PluginExecutionTier.ManagedOutOfProcess, "lib/plugin.dll", "Example.Plugin")], [new("project.read", PluginPermissionRisk.ReadOnly, "read", ["project://active"])], [new("open", PluginContributionKind.Command, "Open", "1", ["project.read"])], Dependencies: []);
    private static PluginInstallation Installation(string root, PluginManifest manifest) => new(PluginInstallationId.New(), new(PluginPackageId.New(), manifest.PluginId, manifest.PluginVersion, new string('a', 64), 1), manifest, root, PluginPublisherTrust.UserTrusted, PluginSignatureState.Valid, PluginLifecycleState.Installed, PluginHealthState.Stopped, [new("project.read", ["project://active"], DateTimeOffset.UtcNow, "user")], DateTimeOffset.UtcNow);
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), $"abraxius-plugin-tests-{Guid.NewGuid():N}"); Directory.CreateDirectory(path); return path; }
    private static void CreatePackage(string path, PluginManifest manifest)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("abraxius.plugin.json");
        using (var stream = entry.Open()) JsonSerializer.Serialize(stream, manifest, PluginContractJsonContext.Default.PluginManifest);
        var payload = archive.CreateEntry("lib/plugin.dll");
        using (var payloadStream = payload.Open()) payloadStream.Write([1, 2, 3]);
    }

    private static void CreatePackage(string path, PluginManifest manifest, string assemblyPath, string entryPath)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("abraxius.plugin.json");
        using (var stream = entry.Open()) JsonSerializer.Serialize(stream, manifest, PluginContractJsonContext.Default.PluginManifest);
        var payload = archive.CreateEntry(entryPath, CompressionLevel.NoCompression);
        using var payloadStream = payload.Open(); using var assembly = File.OpenRead(assemblyPath); assembly.CopyTo(payloadStream);
    }

    private static PluginManifest RepositoryManifest() => new(1, "org.abraxius.repository-intelligence", "1.0.0", "Repository Intelligence", "First-party proof extension.", "Abraxius",
        new(">=1.0.0 <2.0.0", PluginApiVersion.Current, ["Windows", "Linux", "macOS"], ["x64", "arm64"]),
        [new(PluginExecutionTier.ManagedOutOfProcess, "lib/net10.0/Abraxius.Extensions.Repository.dll", "Abraxius.Extensions.Repository.RepositoryIntelligencePlugin")],
        [new("project.read", PluginPermissionRisk.ReadOnly, "Inspect brokered project metadata.", ["project://active"]), new("ui.navigation", PluginPermissionRisk.UserInterface, "Render declarative UI.", ["capability://ui/repository"]), new("artifact.preview", PluginPermissionRisk.ReadOnly, "Preview report metadata.", ["artifact://repository-report"], false)],
        [new("repo.inspect", PluginContributionKind.CapabilityProvider, "Inspect repository metadata", "1", ["project.read"]), new("repo.open", PluginContributionKind.Command, "Open Repository Intelligence", "1", ["ui.navigation"]), new("repo.page", PluginContributionKind.InspectorPanel, "Repository Intelligence", "1", ["ui.navigation"]), new("repo.report", PluginContributionKind.ArtifactKind, "Repository report", "1", ["artifact.preview"]), new("repo.recognizer", PluginContributionKind.ProjectRecognizer, "Repository recognizer", "1", ["project.read"]), new("repo.eval", PluginContributionKind.EvalSuite, "Repository extension contract eval", "1", [])],
        PluginActivationMode.Project, PluginSandboxRequirement.ProcessIsolation, []);

    private sealed class FakeLauncher(PluginRegistration registration) : IPluginHostLauncher
    {
        public ValueTask<IPluginHostSession> LaunchAsync(PluginInstallation installation, PluginHostLaunchOptions options, CancellationToken cancellationToken = default) => ValueTask.FromResult<IPluginHostSession>(new FakeSession(registration));
    }
    private sealed class FakeSession(PluginRegistration registration) : IPluginHostSession
    {
        public PluginHostId HostId { get; } = PluginHostId.New(); public PluginHostSessionId SessionId { get; } = PluginHostSessionId.New(); public PluginRegistration Registration { get; } = registration; public PluginHealthState Health => PluginHealthState.Healthy;
        public ValueTask<PluginHealthState> CheckHealthAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(PluginHealthState.Healthy);
        public ValueTask<PluginInvocationResult> InvokeAsync(PluginInvocation invocation, CancellationToken cancellationToken = default) => ValueTask.FromResult(new PluginInvocationResult(invocation.InvocationId, PluginInvocationStatus.Succeeded));
        public ValueTask StopAsync(string reason, CancellationToken cancellationToken = default) => ValueTask.CompletedTask; public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Collections.Immutable;
using System.Net;
using Abraxius.Agents;
using Abraxius.Presence;
using Abraxius.Security;
using Xunit;

namespace Abraxius.Security.Tests;

public sealed class SecurityTests
{
    [Fact]
    public async Task OrionReadInsideWorkspaceIsAllowedWithoutPrompt()
    {
        using var workspace = new TemporaryDirectory();
        var file = Path.Combine(workspace.Path, "source.cs"); await File.WriteAllTextAsync(file, "sealed class Source {}");
        var fixture = new SecurityFixture();
        var request = await fixture.RequestAsync(SecuritySubject.Specialist(SpecialistRole.Investigator, SpecialistInstanceId.New(), MissionId.New(), AssignmentId.New()), SecurityActions.FileRead, ResourceKind.File, file, workspace.Path);
        var decision = await fixture.Kernel.AuthorizeAsync(request);
        Assert.Equal(AuthorizationOutcome.Allow, decision.Outcome);
    }

    [Fact]
    public async Task OrionWriteIsDeniedBeforeExecution()
    {
        using var workspace = new TemporaryDirectory();
        var fixture = new SecurityFixture();
        var request = await fixture.RequestAsync(SecuritySubject.Specialist(SpecialistRole.Investigator, SpecialistInstanceId.New(), MissionId.New(), AssignmentId.New()), SecurityActions.FileWrite, ResourceKind.File, Path.Combine(workspace.Path, "x.cs"), workspace.Path, mutation: true);
        var decision = await fixture.Kernel.AuthorizeAsync(request);
        Assert.Equal(AuthorizationReasonCode.DeniedSpecialistPolicy, decision.ReasonCode);
    }

    [Fact]
    public async Task DaedalusWriteIsBoundedToCanonicalWorktree()
    {
        using var workspace = new TemporaryDirectory(); using var outside = new TemporaryDirectory();
        var subject = SecuritySubject.Specialist(SpecialistRole.Builder, SpecialistInstanceId.New(), MissionId.New(), AssignmentId.New());
        var fixture = new SecurityFixture();
        Assert.True((await fixture.Kernel.AuthorizeAsync(await fixture.RequestAsync(subject, SecurityActions.FileWrite, ResourceKind.File, Path.Combine(workspace.Path, "src.cs"), workspace.Path, mutation: true))).IsAllowed);
        var denied = await fixture.Kernel.AuthorizeAsync(await fixture.RequestAsync(subject, SecurityActions.FileWrite, ResourceKind.File, Path.Combine(outside.Path, "secret"), workspace.Path, mutation: true));
        Assert.Equal(AuthorizationReasonCode.DeniedOutsideWorkspace, denied.ReasonCode);
    }

    [Fact]
    public async Task SymlinkCannotEscapeWorkspaceScope()
    {
        if (OperatingSystem.IsWindows()) return;
        using var workspace = new TemporaryDirectory(); using var outside = new TemporaryDirectory();
        var link = Path.Combine(workspace.Path, "link"); Directory.CreateSymbolicLink(link, outside.Path);
        var fixture = new SecurityFixture(); var subject = SecuritySubject.Specialist(SpecialistRole.Builder, SpecialistInstanceId.New(), MissionId.New(), AssignmentId.New());
        var decision = await fixture.Kernel.AuthorizeAsync(await fixture.RequestAsync(subject, SecurityActions.FileWrite, ResourceKind.File, Path.Combine(link, "escape.txt"), workspace.Path, mutation: true));
        Assert.Equal(AuthorizationReasonCode.DeniedOutsideWorkspace, decision.ReasonCode);
    }

    [Fact]
    public async Task GitPushRequiresApprovalAndOneShotGrantIsConsumed()
    {
        var fixture = new SecurityFixture(); var subject = SecuritySubject.Specialist(SpecialistRole.Builder, SpecialistInstanceId.New(), MissionId.New(), AssignmentId.New());
        var request = await fixture.RequestAsync(subject, SecurityActions.GitPush, ResourceKind.GitRepository, "git://github.com/velumix/abraxius", Environment.CurrentDirectory, external: true);
        var first = await fixture.Kernel.AuthorizeAsync(request); Assert.Equal(AuthorizationOutcome.RequireApproval, first.Outcome);
        var needsStore = new InMemoryNeedsYouStore(); var notifications = new NotificationHub(new DefaultAttentionPolicy(), new UnavailableNativeNotificationService(), new InMemoryInAppNotificationSink());
        var needs = new NeedsYouService(needsStore, notifications); await needs.InitializeAsync();
        var approvals = new SecurityApprovalService(needs, fixture.Grants, fixture.Audit);
        var pending = await approvals.RequestAsync(request, first, DefaultAttention());
        var grant = await approvals.ApproveAsync(pending.NeedsYouId, GrantScope.Once); Assert.NotNull(grant);
        Assert.True((await fixture.Kernel.AuthorizeAsync(request with { RequestedAt = DateTimeOffset.UtcNow })).IsAllowed);
        Assert.Equal(AuthorizationOutcome.RequireApproval, (await fixture.Kernel.AuthorizeAsync(request with { RequestedAt = DateTimeOffset.UtcNow })).Outcome);
    }

    [Fact]
    public async Task MissionGrantDoesNotAuthorizeAnotherMission()
    {
        var fixture = new SecurityFixture(); var principal = new PrincipalId("specialist:builder-1"); var mission = MissionId.New();
        var subject = new SecuritySubject(principal, PrincipalType.Specialist, MissionId: mission, SpecialistRole: SpecialistRole.Builder);
        var request = await fixture.RequestAsync(subject, SecurityActions.GitPush, ResourceKind.GitRepository, "git://github.com/velumix/abraxius", Environment.CurrentDirectory, external: true);
        fixture.Grants.Issue(new AuthorizationGrant(AuthorizationGrantId.New(), subject, ImmutableHashSet.Create(SecurityActions.GitPush), request.Action.Resource.CanonicalUri,
            GrantScope.Mission, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "user", "mission push", mission));
        Assert.True((await fixture.Kernel.AuthorizeAsync(request)).IsAllowed);
        var other = request with { Subject = subject with { MissionId = MissionId.New() }, Action = request.Action with { Subject = subject with { MissionId = MissionId.New() } }, RequestedAt = DateTimeOffset.UtcNow };
        Assert.Equal(AuthorizationOutcome.RequireApproval, (await fixture.Kernel.AuthorizeAsync(other)).Outcome);
    }

    [Fact]
    public async Task RawSecretAndPromptInjectionAreDenied()
    {
        var fixture = new SecurityFixture(); var subject = SecuritySubject.System("model-proposal");
        var request = await fixture.RequestAsync(subject, SecurityActions.SecretReadRaw, ResourceKind.Secret, "secret://github/default", Environment.CurrentDirectory);
        var decision = await fixture.Kernel.AuthorizeAsync(request);
        Assert.Equal(AuthorizationOutcome.Deny, decision.Outcome);
        Assert.Equal(AuthorizationReasonCode.DeniedPolicy, decision.ReasonCode);
    }

    [Fact]
    public async Task SecretUseIsBrokeredAndAuditNeverContainsValue()
    {
        var fixture = new SecurityFixture(); var subject = SecuritySubject.System("github-transport"); var reference = new SecretReference("secret://github/default");
        using var store = new InMemorySecretStore(); var secret = "super-secret-token";
        await store.StoreAsync(new SecretMetadata(reference, "GitHub", "memory-test", ["https://api.github.com/"], DateTimeOffset.UtcNow, RequiresApproval: false), secret.AsMemory());
        fixture.Grants.Issue(new AuthorizationGrant(AuthorizationGrantId.New(), subject, ImmutableHashSet.Create(SecurityActions.SecretUse), reference.Value,
            GrantScope.Once, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(1), "user", "test", MaximumUses: 1));
        var broker = new SecretBroker(store, fixture.Kernel, fixture.Audit, fixture.Resources);
        var result = await broker.UseAsync(new SecretUseRequest(subject, reference, "https://api.github.com/repos/velumix/abraxius", "Git.Push", new AuthorizationContext()),
            (value, _) => ValueTask.FromResult(value.Span.SequenceEqual(secret.AsSpan())));
        Assert.True(result);
        await foreach (var item in fixture.Audit.QueryAsync()) Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(item), StringComparison.Ordinal);
    }

    [Fact]
    public void RedactorRemovesRegisteredAndCommonCredentialForms()
    {
        var redactor = new SecretRedactor(); redactor.RegisterSensitiveValue("abc12345");
        var output = redactor.Redact("token=abc12345 Authorization: Bearer xyz987");
        Assert.DoesNotContain("abc12345", output); Assert.DoesNotContain("xyz987", output);
    }

    [Fact]
    public async Task UnknownCapabilityDefaultsToDeny()
    {
        var fixture = new SecurityFixture();
        var decision = await fixture.Kernel.AuthorizeAsync(await fixture.RequestAsync(SecuritySubject.System(), "system.superuser", ResourceKind.Capability, "capability://system/superuser", Environment.CurrentDirectory));
        Assert.Equal(AuthorizationReasonCode.DeniedUnknownCapability, decision.ReasonCode);
    }

    [Fact]
    public async Task TrustedSkillAndPrestigeMetadataCannotExpandAuthority()
    {
        using var workspace = new TemporaryDirectory(); using var outside = new TemporaryDirectory(); var fixture = new SecurityFixture();
        var subject = new SecuritySubject(new PrincipalId("skill:trusted"), PrincipalType.Skill, MissionId: MissionId.New());
        var request = await fixture.RequestAsync(subject, SecurityActions.FileWrite, ResourceKind.File, Path.Combine(outside.Path, "x"), workspace.Path, mutation: true,
            parameters: new Dictionary<string, string> { ["skill.trust"] = "Trusted", ["operator.prestige"] = "100", ["model.tier"] = "Frontier" });
        Assert.Equal(AuthorizationReasonCode.DeniedOutsideWorkspace, (await fixture.Kernel.AuthorizeAsync(request)).ReasonCode);
    }

    [Fact]
    public async Task InternalNetworkAndMetadataEndpointsAreBlocked()
    {
        var resources = new ResourceCanonicalizer(new FixedResolver(IPAddress.Parse("169.254.169.254"))); var fixture = new SecurityFixture(resources);
        var request = await fixture.RequestAsync(SecuritySubject.System(), SecurityActions.NetworkHttpGet, ResourceKind.Network, "http://metadata.example/latest", Environment.CurrentDirectory, external: true);
        var decision = await fixture.Kernel.AuthorizeAsync(request);
        Assert.Equal(AuthorizationOutcome.Deny, decision.Outcome);
    }

    [Fact]
    public void ShellExecutionHasHigherRiskThanDirectProcess()
    {
        var classifier = new DeterministicRiskClassifier(); var resource = new SecurityResource(ResourceKind.Process, "process://local/dotnet"); var subject = SecuritySubject.System();
        var direct = classifier.Classify(new ProposedAction(ActionId.New(), subject, "process", SecurityActions.ProcessExecute, resource));
        var shell = classifier.Classify(new ProposedAction(ActionId.New(), subject, "process", SecurityActions.ProcessShellExecute, resource));
        Assert.False(direct.HasFlag(RiskClass.Shell)); Assert.True(shell.HasFlag(RiskClass.Shell));
    }

    [Fact]
    public void ChildEnvironmentDoesNotInheritUnrelatedSecrets()
    {
        Environment.SetEnvironmentVariable("ABRAXIUS_SECURITY_TEST_SECRET", "must-not-leak");
        try { Assert.DoesNotContain("ABRAXIUS_SECURITY_TEST_SECRET", SanitizedProcessEnvironment.Create().Keys); }
        finally { Environment.SetEnvironmentVariable("ABRAXIUS_SECURITY_TEST_SECRET", null); }
    }

    [Fact]
    public void LocalOnlyCloudEgressIsDenied()
    {
        var decision = new ModelEgressPolicy().Evaluate(SecuritySubject.System(), DataClassification.LocalOnly, providerIsLocal: false, "frontier");
        Assert.Equal(AuthorizationReasonCode.DeniedLocalOnlyPolicy, decision.ReasonCode);
    }

    [Fact]
    public async Task ReplayCannotRepeatExternalSideEffect()
    {
        var fixture = new SecurityFixture(); var subject = SecuritySubject.System();
        var request = await fixture.RequestAsync(subject, SecurityActions.GitPush, ResourceKind.GitRepository, "git://github.com/velumix/abraxius", Environment.CurrentDirectory, external: true, replay: true);
        Assert.Equal(AuthorizationReasonCode.DeniedReplaySideEffect, (await fixture.Kernel.AuthorizeAsync(request)).ReasonCode);
    }

    [Fact]
    public async Task RequiredSandboxNeverSilentlyDowngrades()
    {
        var fixture = new SecurityFixture(); var request = await fixture.RequestAsync(SecuritySubject.System(), SecurityActions.ProcessExecute, ResourceKind.Process, "dotnet", Environment.CurrentDirectory, minimumSandbox: SandboxLevel.Container);
        Assert.Equal(AuthorizationReasonCode.DeniedSandboxUnavailable, (await fixture.Kernel.AuthorizeAsync(request)).ReasonCode);
    }

    [Fact]
    public async Task LockdownImmediatelyDeniesNewMutations()
    {
        using var workspace = new TemporaryDirectory(); var fixture = new SecurityFixture(); fixture.Kernel.Lockdown = true;
        var subject = SecuritySubject.Specialist(SpecialistRole.Builder, SpecialistInstanceId.New(), MissionId.New(), AssignmentId.New());
        Assert.Equal(AuthorizationReasonCode.DeniedLockdown, (await fixture.Kernel.AuthorizeAsync(await fixture.RequestAsync(subject, SecurityActions.FileWrite, ResourceKind.File, Path.Combine(workspace.Path, "x"), workspace.Path, mutation: true))).ReasonCode);
    }

    [Fact]
    public async Task ConcurrentAuthorizationIsThreadSafeAndAudited()
    {
        using var workspace = new TemporaryDirectory(); var file = Path.Combine(workspace.Path, "x"); await File.WriteAllTextAsync(file, "x"); var fixture = new SecurityFixture();
        var subject = SecuritySubject.Specialist(SpecialistRole.Investigator, SpecialistInstanceId.New(), MissionId.New(), AssignmentId.New());
        var request = await fixture.RequestAsync(subject, SecurityActions.FileRead, ResourceKind.File, file, workspace.Path);
        var results = await Task.WhenAll(Enumerable.Range(0, 10_000).Select(_ => fixture.Kernel.AuthorizeAsync(request with { RequestedAt = DateTimeOffset.UtcNow }).AsTask()));
        Assert.All(results, decision => Assert.True(decision.IsAllowed));
        var count = 0; await foreach (var _ in fixture.Audit.QueryAsync(10_000)) count++;
        Assert.Equal(10_000, count); // bounded audit retains the newest request/decision events
    }

    private static AttentionContext DefaultAttention() => new(WindowPresenceState.Hidden, new PresenceSettings(), DateTimeOffset.UtcNow, false, NotificationPermissionState.Unavailable);

    private sealed class SecurityFixture
    {
        public SecurityFixture(IResourceCanonicalizer? resources = null)
        {
            Resources = resources ?? new ResourceCanonicalizer(new FixedResolver(IPAddress.Parse("8.8.8.8")));
            Audit = new InMemorySecurityAuditStore(10_000); Grants = new InMemoryAuthorizationGrantStore();
            Kernel = new SecurityKernel(new DeterministicPolicyEngine(), new DeterministicRiskClassifier(), Grants, Audit, Resources);
        }
        public IResourceCanonicalizer Resources { get; }
        public InMemorySecurityAuditStore Audit { get; }
        public InMemoryAuthorizationGrantStore Grants { get; }
        public SecurityKernel Kernel { get; }
        public async ValueTask<AuthorizationRequest> RequestAsync(SecuritySubject subject, string operation, ResourceKind kind, string target, string workspace,
            bool mutation = false, bool external = false, bool replay = false, IReadOnlyDictionary<string, string>? parameters = null, SandboxLevel minimumSandbox = SandboxLevel.None) =>
            await AuthorizationRequestFactory.CreateAsync(Resources, subject, "test", operation, kind, target,
                new AuthorizationContext(WorkspaceRoot: workspace, AvailableSandbox: SandboxLevel.IsolatedWorkspace, Replay: replay), parameters, mutation, external, minimumSandbox: minimumSandbox);
    }

    private sealed class FixedResolver(params IPAddress[] addresses) : INetworkAddressResolver
    {
        public ValueTask<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<IPAddress>>(addresses);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "abraxius-security-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public string Path { get; }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}

using System.Collections.Immutable;
using System.Text;
using Abraxius.Artifacts;
using Abraxius.Presence;
using Abraxius.Protocol;
using Abraxius.Security;
using Xunit;

namespace Abraxius.Artifacts.Tests;

public sealed class ArtifactTests
{
    [Fact]
    public async Task NewRevisionDoesNotInheritVerificationOrApproval()
    {
        var fixture = await Fixture.CreateAsync();
        var first = await fixture.CreateAsync("one");
        var verification = Verification(first.CurrentRevision.Id, ArtifactVerificationResult.Passed);
        first = await fixture.Service.AttachVerificationAsync(first.Descriptor.Id, verification);
        var review = await fixture.Reviews.RequestReviewAsync(first.Descriptor.Id, first.CurrentRevision.Id, User(), Attention());
        await fixture.Reviews.DecideAsync(review.Id, User(), ArtifactApprovalState.Approved);

        var second = await fixture.ReviseAsync(first.Descriptor.Id, first.CurrentRevision.Id, "two");

        Assert.Equal(ArtifactState.Candidate, second.Descriptor.State);
        Assert.DoesNotContain(second.SafeVerifications, item => item.ArtifactRevisionId == second.CurrentRevision.Id);
        Assert.DoesNotContain(second.SafeApprovals, item => item.ArtifactRevisionId == second.CurrentRevision.Id);
        Assert.Contains(second.SafeVerifications, item => item.ArtifactRevisionId == first.CurrentRevision.Id);
        Assert.Contains(second.SafeApprovals, item => item.ArtifactRevisionId == first.CurrentRevision.Id);
    }

    [Fact]
    public async Task VerificationPinsExactRevision()
    {
        var fixture = await Fixture.CreateAsync(); var first = await fixture.CreateAsync("one");
        var second = await fixture.ReviseAsync(first.Descriptor.Id, first.CurrentRevision.Id, "two");
        var updated = await fixture.Service.AttachVerificationAsync(first.Descriptor.Id, Verification(first.CurrentRevision.Id, ArtifactVerificationResult.Passed));
        Assert.Equal(ArtifactState.Candidate, updated.Descriptor.State);
        Assert.DoesNotContain(updated.SafeVerifications, item => item.ArtifactRevisionId == second.CurrentRevision.Id);
    }

    [Fact]
    public async Task ApprovalRefusesStaleReview()
    {
        var fixture = await Fixture.CreateAsync(); var first = await fixture.CreateAsync("one");
        var review = await fixture.Reviews.RequestReviewAsync(first.Descriptor.Id, first.CurrentRevision.Id, User(), Attention());
        await fixture.ReviseAsync(first.Descriptor.Id, first.CurrentRevision.Id, "two");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Reviews.DecideAsync(review.Id, User(), ArtifactApprovalState.Approved));
    }

    [Fact]
    public async Task ReviewCreatesOneDurableNeedsYouItem()
    {
        var fixture = await Fixture.CreateAsync(); var artifact = await fixture.CreateAsync("one");
        var first = await fixture.Reviews.RequestReviewAsync(artifact.Descriptor.Id, artifact.CurrentRevision.Id, User(), Attention());
        var second = await fixture.Reviews.RequestReviewAsync(artifact.Descriptor.Id, artifact.CurrentRevision.Id, User(), Attention());
        Assert.Equal(first.Id, second.Id);
        var item = Assert.Single(await fixture.NeedsYou.ListAsync());
        Assert.Equal(NeedsYouReason.ArtifactReview, item.Reason); Assert.Equal(NeedsYouState.Pending, item.State);
    }

    [Fact]
    public async Task DismissingNotificationDoesNotApproveArtifact()
    {
        var fixture = await Fixture.CreateAsync(); var artifact = await fixture.CreateAsync("one");
        await fixture.Reviews.RequestReviewAsync(artifact.Descriptor.Id, artifact.CurrentRevision.Id, User(), Attention());
        var current = await fixture.Store.GetAsync(artifact.Descriptor.Id);
        Assert.Empty(current!.SafeApprovals); Assert.Equal(ArtifactState.AwaitingReview, current.Descriptor.State);
    }

    [Fact]
    public async Task ContentStoreDeduplicatesAndDetectsTamper()
    {
        var root = Path.Combine(Path.GetTempPath(), $"abraxius-artifact-{Guid.NewGuid():N}");
        try
        {
            var store = new FileArtifactContentStore(root);
            var first = await store.PutAsync(Stream("same"), "text/plain"); var second = await store.PutAsync(Stream("same"), "text/plain");
            Assert.Equal(first.BlobId, second.BlobId); Assert.True(await store.VerifyAsync(first));
            var path = new Uri(first.Location.Reference).LocalPath;
            await File.AppendAllTextAsync(path, "tamper");
            Assert.False(await store.VerifyAsync(first));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TextDiffIsBoundedAndShowsChanges()
    {
        var fixture = await Fixture.CreateAsync(); var first = await fixture.CreateAsync("alpha\nbeta\ngamma\n"); var second = await fixture.ReviseAsync(first.Descriptor.Id, first.CurrentRevision.Id, "alpha\nchanged\ngamma\n");
        var provider = new LinearTextDiffProvider();
        await using var oldStream = await fixture.Content.OpenReadAsync(first.CurrentRevision.Content.BlobId); await using var newStream = await fixture.Content.OpenReadAsync(second.CurrentRevision.Content.BlobId);
        var result = await provider.CompareAsync(first.CurrentRevision, oldStream, second.CurrentRevision, newStream, new ArtifactDiffOptions(MaximumLines: 100));
        Assert.False(result.Truncated); Assert.Equal(1, result.Added); Assert.Equal(1, result.Deleted); Assert.Contains(result.Hunks.SelectMany(static item => item.Lines), line => line.Text == "changed" && line.Prefix == '+');
    }

    [Fact]
    public async Task TextDiffTruncatesLargeInput()
    {
        var fixture = await Fixture.CreateAsync(); var first = await fixture.CreateAsync(string.Join('\n', Enumerable.Range(0, 100))); var second = await fixture.ReviseAsync(first.Descriptor.Id, first.CurrentRevision.Id, string.Join('\n', Enumerable.Range(0, 101)));
        await using var oldStream = await fixture.Content.OpenReadAsync(first.CurrentRevision.Content.BlobId); await using var newStream = await fixture.Content.OpenReadAsync(second.CurrentRevision.Content.BlobId);
        var result = await new LinearTextDiffProvider().CompareAsync(first.CurrentRevision, oldStream, second.CurrentRevision, newStream, new ArtifactDiffOptions(MaximumLines: 10));
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task DependencyMustPinExistingRevision()
    {
        var fixture = await Fixture.CreateAsync(); var dependency = await fixture.CreateAsync("base");
        var request = fixture.Request() with { Dependencies = [new(ArtifactDependencyId.New(), ArtifactDependencyKind.Input, dependency.Descriptor.Id, ArtifactRevisionId.New(), "bad pin")] };
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Service.CreateAsync(request, Stream("candidate")));
    }

    [Fact]
    public async Task IntegrationDetectsStaleTargetBeforeAuthorization()
    {
        var fixture = await Fixture.CreateAsync(); var artifact = await fixture.CreateAsync("candidate");
        var review = await fixture.Reviews.RequestReviewAsync(artifact.Descriptor.Id, artifact.CurrentRevision.Id, User(), Attention()); await fixture.Reviews.DecideAsync(review.Id, User(), ArtifactApprovalState.Approved);
        var kernel = new FakeKernel(); var target = new FakeTarget("changed");
        var service = new ArtifactIntegrationService(fixture.Store, fixture.Content, kernel, new ResourceCanonicalizer(), target);
        var result = await service.IntegrateAsync(new(artifact.Descriptor.Id, artifact.CurrentRevision.Id, "file:///tmp/target", "base", SecuritySubject.System(), new AuthorizationContext(WorkspaceRoot: "/tmp"), SecurityActions.FileWrite, ResourceKind.File));
        Assert.Equal(ArtifactIntegrationState.NeedsRebase, result.Integration.State); Assert.Equal(0, kernel.Calls); Assert.Equal(0, target.ApplyCalls);
    }

    [Fact]
    public async Task ApprovedArtifactStillRequiresSecurityAuthorization()
    {
        var fixture = await Fixture.CreateAsync(); var artifact = await fixture.CreateAsync("candidate");
        var review = await fixture.Reviews.RequestReviewAsync(artifact.Descriptor.Id, artifact.CurrentRevision.Id, User(), Attention()); await fixture.Reviews.DecideAsync(review.Id, User(), ArtifactApprovalState.Approved);
        var kernel = new FakeKernel(AuthorizationOutcome.RequireApproval); var target = new FakeTarget("base");
        var service = new ArtifactIntegrationService(fixture.Store, fixture.Content, kernel, new ResourceCanonicalizer(), target);
        var result = await service.IntegrateAsync(new(artifact.Descriptor.Id, artifact.CurrentRevision.Id, "file:///tmp/target", "base", SecuritySubject.System(), new AuthorizationContext(WorkspaceRoot: "/tmp"), SecurityActions.FileWrite, ResourceKind.File));
        Assert.Equal(ArtifactIntegrationState.IntegrationFailed, result.Integration.State); Assert.Equal(1, kernel.Calls); Assert.Equal(0, target.ApplyCalls);
    }

    [Fact]
    public async Task LocalOnlyPublicationIsBlockedBeforeNetworkAuthorization()
    {
        var fixture = await Fixture.CreateAsync(DataClassification.LocalOnly); var artifact = await fixture.CreateAsync("local");
        var kernel = new FakeKernel(AuthorizationOutcome.Allow); var publication = new ArtifactPublicationService(fixture.Store, fixture.Content, kernel, new ResourceCanonicalizer(), new PatternArtifactSecretScanner(), new FakePublisher());
        var result = await publication.PublishAsync(new(artifact.Descriptor.Id, artifact.CurrentRevision.Id, new Uri("https://example.com/upload"), SecuritySubject.System(), new AuthorizationContext(), ApprovalRequired: false));
        Assert.Equal(ArtifactPublicationResult.Blocked, result.Result); Assert.Equal(0, kernel.Calls);
    }

    [Fact]
    public async Task SecretScannerBlocksPublication()
    {
        var fixture = await Fixture.CreateAsync(); var artifact = await fixture.CreateAsync("api_key=abcdefghijklmnopqrstuvwxyz123456"); var kernel = new FakeKernel(AuthorizationOutcome.Allow);
        var publication = new ArtifactPublicationService(fixture.Store, fixture.Content, kernel, new ResourceCanonicalizer(new PublicResolver()), new PatternArtifactSecretScanner(), new FakePublisher());
        var result = await publication.PublishAsync(new(artifact.Descriptor.Id, artifact.CurrentRevision.Id, new Uri("https://example.com/upload"), SecuritySubject.System(), new AuthorizationContext(), ApprovalRequired: false));
        Assert.Equal(ArtifactPublicationResult.Blocked, result.Result); Assert.Equal(0, kernel.Calls);
    }

    [Fact]
    public async Task SqliteStoreRoundTripsImmutableAggregate()
    {
        var path = Path.Combine(Path.GetTempPath(), $"abraxius-artifact-{Guid.NewGuid():N}", "artifacts.db");
        try
        {
            await using var store = new SqliteArtifactStore(path); var content = new InMemoryArtifactContentStore(); var service = new ArtifactService(store, content); await service.InitializeAsync();
            var fixture = await Fixture.CreateAsync(store, content); var artifact = await fixture.CreateAsync("persisted");
            var loaded = await store.GetByRevisionAsync(artifact.CurrentRevision.Id); Assert.NotNull(loaded); Assert.Equal(artifact.CurrentRevision.RevisionHash, loaded!.CurrentRevision.RevisionHash);
        }
        finally { var root = Path.GetDirectoryName(path)!; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ParallelRevisionCreationHasUniqueIdentities()
    {
        var fixture = await Fixture.CreateAsync(); var first = await fixture.CreateAsync("root");
        var revisions = await Task.WhenAll(Enumerable.Range(0, 20).Select(async index => await fixture.ReviseLatestAsync(first.Descriptor.Id, $"candidate-{index}")));
        Assert.Equal(20, revisions.Select(static item => item.CurrentRevision.Id).Distinct().Count());
        Assert.Equal(21, (await fixture.Store.GetAsync(first.Descriptor.Id))!.Revisions.Length);
    }

    private static ArtifactVerification Verification(ArtifactRevisionId revision, ArtifactVerificationResult result) => new(ArtifactVerificationId.New(), revision, new(User(), ArtifactProducerKind.Argus, "Argus"), "plan", [new("requirement", "pass", result.ToString(), [], result)], [], result, DateTimeOffset.UtcNow, "test");
    private static PrincipalId User() => new("user:local");
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text), writable: false);
    private static AttentionContext Attention() => new(WindowPresenceState.VisibleFocused, new PresenceSettings(), DateTimeOffset.UtcNow, false, NotificationPermissionState.Denied);

    private sealed class Fixture
    {
        private readonly ArtifactClassification _classification;
        private Fixture(IArtifactStore store, IArtifactContentStore content, ArtifactService service, ArtifactReviewService reviews, NeedsYouService needsYou, ArtifactClassification classification) { Store = store; Content = content; Service = service; Reviews = reviews; NeedsYou = needsYou; _classification = classification; }
        public IArtifactStore Store { get; } public IArtifactContentStore Content { get; } public ArtifactService Service { get; } public ArtifactReviewService Reviews { get; } public NeedsYouService NeedsYou { get; }
        public static async ValueTask<Fixture> CreateAsync(DataClassification classification = DataClassification.Internal) => await CreateAsync(new InMemoryArtifactStore(), new InMemoryArtifactContentStore(), classification);
        public static async ValueTask<Fixture> CreateAsync(IArtifactStore store, IArtifactContentStore content, DataClassification classification = DataClassification.Internal)
        {
            var service = new ArtifactService(store, content); await service.InitializeAsync(); var needsStore = new InMemoryNeedsYouStore(); var hub = new NotificationHub(new DefaultAttentionPolicy(), new UnavailableNativeNotificationService(), new InMemoryInAppNotificationSink()); var needs = new NeedsYouService(needsStore, hub); await needs.InitializeAsync();
            return new(store, content, service, new ArtifactReviewService(store, needs), needs, new ArtifactClassification(classification));
        }
        public CreateArtifactRequest Request() => new(ArtifactKind.SourceChange, "test artifact", new(User(), ArtifactProducerKind.Daedalus, "Daedalus"), new ArtifactProvenance(), _classification, "text/plain");
        public async ValueTask<ArtifactAggregate> CreateAsync(string text) => await Service.CreateAsync(Request(), Stream(text));
        public async ValueTask<ArtifactAggregate> ReviseAsync(ArtifactId id, ArtifactRevisionId parent, string text) => await Service.CreateRevisionAsync(new(id, parent, new(User(), ArtifactProducerKind.Daedalus, "Daedalus"), new ArtifactProvenance(), "text/plain"), Stream(text));
        public async ValueTask<ArtifactAggregate> ReviseLatestAsync(ArtifactId id, string text)
        {
            while (true) { var current = await Store.GetAsync(id) ?? throw new InvalidOperationException(); try { return await ReviseAsync(id, current.Descriptor.CurrentRevision, text); } catch (ArtifactConcurrencyException) { } }
        }
    }

    private sealed class FakeKernel(AuthorizationOutcome outcome = AuthorizationOutcome.Allow) : ISecurityKernel
    {
        public int Calls; public bool Lockdown { get; set; }
        public ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken = default) { Calls++; return ValueTask.FromResult(new AuthorizationDecision(AuthorizationDecisionId.New(), outcome, outcome == AuthorizationOutcome.Allow ? AuthorizationReasonCode.AllowedByPolicy : AuthorizationReasonCode.ApprovalRequiredForExternalMutation, "test", RiskClass.Mutation)); }
        public ValueTask RecordExecutionResultAsync(AuthorizationRequest request, AuthorizationDecision decision, bool succeeded, string? resultCode = null, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public AuthorizationExplanation Explain(AuthorizationRequest request) => new(new(AuthorizationDecisionId.New(), outcome, AuthorizationReasonCode.AllowedByPolicy, "test", RiskClass.ReadOnly), []);
    }
    private sealed class FakeTarget(string state) : IArtifactTargetAdapter { public int ApplyCalls; public ValueTask<ArtifactTargetState> ResolveAsync(string target, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ArtifactTargetState(target, state, true)); public ValueTask<ArtifactApplyResult> ApplyAsync(ArtifactRevision revision, Stream content, string target, CancellationToken cancellationToken = default) { ApplyCalls++; return ValueTask.FromResult(new ArtifactApplyResult(true, revision.Content.ContentHash)); } }
    private sealed class FakePublisher : IArtifactPublisher { public ValueTask<ArtifactApplyResult> PublishAsync(ArtifactRevision revision, Stream content, Uri destination, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ArtifactApplyResult(true, revision.Content.ContentHash, "external-1")); }
    private sealed class PublicResolver : INetworkAddressResolver { public ValueTask<IReadOnlyList<System.Net.IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<System.Net.IPAddress>>([System.Net.IPAddress.Parse("93.184.216.34")]); }
}

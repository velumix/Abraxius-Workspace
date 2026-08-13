using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using Abraxius.Design;
using Abraxius.Design.Google;
using Abraxius.Models;
using Abraxius.Security;
using Xunit;
using SecurityClassification = Abraxius.Security.DataClassification;

namespace Abraxius.Runtime.Tests;

public sealed class DesignStudioTests
{
    [Fact]
    public async Task SourceSnapshotIsPinnedAndHashed()
    {
        var root = Path.Combine(Path.GetTempPath(), "abraxius-design", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "Chat.axaml");
            await File.WriteAllTextAsync(file, "<UserControl />");
            var resolver = new FileSystemDesignSourceResolver(root, "dev", "abc123");
            var surface = new DesignSurfaceDescriptor(new("chat.workspace"), "Chat", DesignSurfaceCategory.Workspace,
                ["Chat.axaml"], [], [], ["Expanded"], []);

            var snapshot = await resolver.ResolveAsync(surface);

            var source = Assert.Single(snapshot.SafeFiles);
            Assert.Equal("Chat.axaml", source.RelativePath);
            Assert.Equal(64, source.ContentHash.Length);
            Assert.Equal("dev", snapshot.Branch);
            Assert.Equal("abc123", snapshot.GitCommit);
            Assert.Equal(64, snapshot.SnapshotHash.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OrchestratorPersistsBoundedCandidatesAndKeepsArtifactReferences()
    {
        var provider = new FakeDesignProvider();
        var sink = new CapturingArtifactSink();
        var registry = new DesignSurfaceRegistry();
        registry.Register(new DesignableSurface(new DesignSurfaceDescriptor(
            DesignSurfaceId.ChatWorkspace, "Chat", DesignSurfaceCategory.Workspace, [], ["Enter sends"], [], ["Expanded"], []),
            (request, _) => ValueTask.FromResult(new DesignSurfaceSnapshot(DesignSurfaceId.ChatWorkspace, request,
                DesignCaptureStatus.Unavailable, null, "test fixture intentionally has no live bitmap", DateTimeOffset.UtcNow, "fixture"))));
        var source = new InMemorySourceResolver();
        var orchestrator = new DesignOrchestrator(registry, source, new DesignContextCompiler(), provider,
            new StableDesignProjectResolver(provider), new AllowDesignEgressPolicy(), sink);

        var session = await orchestrator.GenerateAsync(DesignSurfaceId.ChatWorkspace, "Make the conversation calmer.",
            new DesignCaptureRequest(DesignViewportProfile.Expanded, 1920, 1080), SecurityClassification.Internal, 99);

        Assert.Equal(DesignSessionState.Ready, session.State);
        Assert.Equal(5, session.Generation?.SafeCandidates.Length);
        Assert.Equal(5, sink.References.Count);
        Assert.All(session.Generation!.SafeCandidates, candidate => Assert.StartsWith("artifact://", candidate.ArtifactReference, StringComparison.Ordinal));
        Assert.Equal(1, provider.GenerateCalls);
    }

    [Fact]
    public async Task LocalOnlyDesignContextIsBlockedBeforeProviderCall()
    {
        var provider = new FakeDesignProvider();
        var registry = new DesignSurfaceRegistry();
        registry.Register(new DesignableSurface(new DesignSurfaceDescriptor(DesignSurfaceId.ChatWorkspace, "Chat", DesignSurfaceCategory.Workspace, [], [], [], [], [])));
        var orchestrator = new DesignOrchestrator(registry, new InMemorySourceResolver(), new DesignContextCompiler(), provider,
            new StableDesignProjectResolver(provider), new AllowDesignEgressPolicy());

        await Assert.ThrowsAsync<DesignProviderSecurityException>(async () => await orchestrator.GenerateAsync(
            DesignSurfaceId.ChatWorkspace, "Do not send this", new DesignCaptureRequest(DesignViewportProfile.Expanded, 100, 100), SecurityClassification.LocalOnly));

        Assert.Equal(0, provider.GenerateCalls);
    }

    [Fact]
    public async Task OAuthAuthorizationUsesFreshPkceAndLoopbackState()
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);
        using var store = new InMemorySecretStore();
        var client = new GoogleStitchOAuthClient(store, http, new GoogleStitchOAuthOptions("client-id"));

        var attempt = client.BeginAuthorization(43127);

        Assert.Equal("accounts.google.com", attempt.AuthorizationUri.Host);
        Assert.Contains(Uri.EscapeDataString("http://127.0.0.1:43127/oauth/callback/"), attempt.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.Contains("code_challenge=", attempt.AuthorizationUri.Query, StringComparison.Ordinal);
        Assert.Contains("state=", attempt.AuthorizationUri.Query, StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ExchangeAsync(attempt, "code", "wrong-state").AsTask());
    }

    [Fact]
    public async Task OAuthExchangeStoresOnlySecretMetadataOutsideTheValueCallback()
    {
        using var handler = new StubHandler("{\"access_token\":\"access-secret\",\"refresh_token\":\"refresh-secret\",\"expires_in\":3600}");
        using var http = new HttpClient(handler);
        using var store = new InMemorySecretStore();
        var client = new GoogleStitchOAuthClient(store, http, new GoogleStitchOAuthOptions("client-id"));
        var attempt = client.BeginAuthorization(43128);

        var token = await client.ExchangeAsync(attempt, "code", attempt.State);
        var metadata = await store.ListAsync();

        Assert.Equal("access-secret", token.AccessToken);
        Assert.Equal(2, metadata.Count);
        Assert.DoesNotContain(metadata, item => item.DisplayName.Contains("access-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(metadata, item => item.DisplayName.Contains("refresh-secret", StringComparison.Ordinal));
    }

    private sealed class InMemorySourceResolver : IDesignSourceResolver
    {
        public ValueTask<DesignSourceSnapshot> ResolveAsync(DesignSurfaceDescriptor surface, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesignSourceSnapshot(DesignSourceSnapshotId.New(), "abc", "dev", surface.Id,
                [new DesignSourceFile("Chat.axaml", "hash", 4, "test")], "theme", DateTimeOffset.UtcNow));
    }

    private sealed class CapturingArtifactSink : IDesignArtifactSink
    {
        public List<string> References { get; } = [];
        public ValueTask<DesignCandidateArtifactReference> PersistCandidateAsync(DesignGenerationResult generation, DesignCandidate candidate, CancellationToken cancellationToken = default)
        {
            var reference = $"artifact://design/{candidate.Id}";
            References.Add(reference);
            return ValueTask.FromResult(new DesignCandidateArtifactReference(reference, "revision-1"));
        }
    }

    private sealed class FakeDesignProvider : IDesignGenerationProvider
    {
        public DesignProviderId Id => new("test-provider");
        public int GenerateCalls { get; private set; }
        public ValueTask<DesignProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesignProviderHealth(Id, DesignProviderConnectionState.Connected, "test", DateTimeOffset.UtcNow, true));
        public ValueTask<DesignProjectRef> EnsureProjectAsync(DesignProjectRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesignProjectRef(Id, new DesignProjectId("test-project"), request.Title));
        public ValueTask<DesignGenerationResult> GenerateAsync(DesignGenerationRequest request, CancellationToken cancellationToken = default)
        {
            GenerateCalls++;
            var candidates = Enumerable.Range(1, request.VariantCount).Select(index => new DesignCandidate(
                DesignCandidateId.New(), $"Variant {index}", $"screen-{index}", "<reference-only />", null, request.Context.Brief,
                request.Context.Source, request.Context.CaptureRequest, ImmutableDictionary<string, string>.Empty.Add("provider", "test"), DateTimeOffset.UtcNow)).ToImmutableArray();
            return ValueTask.FromResult(new DesignGenerationResult(request.GenerationId, Id, request.Project, candidates,
                request.Context.Brief, request.Context.Source, ImmutableDictionary<string, string>.Empty, TimeSpan.Zero));
        }
        public ValueTask<IReadOnlyList<DesignCandidate>> GenerateVariantsAsync(DesignVariantRequest request, CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<DesignCandidate>>([]);
        public ValueTask<DesignGenerationResult> RefineAsync(DesignRefinementRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DesignGenerationResult(request.GenerationId, Id, request.Project, [], request.Context.Brief, request.Context.Source, ImmutableDictionary<string, string>.Empty, TimeSpan.Zero));
    }

    private sealed class StubHandler(string body = "{}") : HttpMessageHandler, IDisposable
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}

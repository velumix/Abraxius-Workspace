using System.Collections.Immutable;
using System.Text.Json;
using Abraxius.Artifacts;
using Abraxius.Design;
using Abraxius.Design.Google;
using Abraxius.Protocol;
using Abraxius.Security;

namespace Abraxius.Runtime;

public sealed class DesignRuntime : IAsyncDisposable
{
    public DesignRuntime(IDesignSurfaceRegistry surfaces, IDesignGenerationProvider provider, IDesignContextCompiler contextCompiler,
        IDesignSourceResolver sourceResolver, IDesignProjectResolver projects, IDesignArtifactSink artifacts, IDesignEgressPolicy egress,
        GoogleStitchOAuthClient? oauth = null)
    {
        Surfaces = surfaces;
        Provider = provider;
        Orchestrator = new DesignOrchestrator(surfaces, sourceResolver, contextCompiler, provider, projects, egress, artifacts);
        GoogleOAuth = oauth;
    }

    public IDesignSurfaceRegistry Surfaces { get; }
    public IDesignGenerationProvider Provider { get; }
    public DesignOrchestrator Orchestrator { get; }
    public GoogleStitchOAuthClient? GoogleOAuth { get; }
    public ValueTask<DesignProviderHealth> GetHealthAsync(CancellationToken cancellationToken = default) => Provider.GetHealthAsync(cancellationToken);
    public async ValueTask<OAuthTokenResult> ConnectGoogleAsync(CancellationToken cancellationToken = default)
    {
        if (GoogleOAuth is null) throw new GoogleStitchNotConfiguredException("Google Stitch OAuth is not configured for this installation.");
        return await GoogleOAuth.ConnectAsync(cancellationToken).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        await Orchestrator.DisposeAsync().ConfigureAwait(false);
    }
}

internal sealed class RuntimeDesignEgressPolicy(IModelEgressPolicy egress) : IDesignEgressPolicy
{
    public AuthorizationDecision Evaluate(DataClassification classification, DesignProviderId provider, bool providerIsLocal = false) =>
        egress.Evaluate(SecuritySubject.System("design-studio"), classification, providerIsLocal, provider.Value);
}

internal sealed class RuntimeDesignArtifactSink(IArtifactService artifacts) : IDesignArtifactSink
{
    public async ValueTask<DesignCandidateArtifactReference> PersistCandidateAsync(DesignGenerationResult generation, DesignCandidate candidate, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            schema = "abraxius.design-candidate/1",
            candidateId = candidate.Id.ToString(),
            generationId = generation.GenerationId.ToString(),
            provider = generation.Provider.Value,
            project = generation.Project.ProjectId.Value,
            candidate.Title,
            candidate.ProviderScreenRef,
            candidate.GeneratedMarkup,
            screenshotSha256 = candidate.ScreenshotPng is { Length: > 0 } image ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(image)).ToLowerInvariant() : null,
            screenshotPngBase64 = candidate.ScreenshotPng is { Length: > 0 } screenshot && screenshot.Length <= 16 * 1024 * 1024 ? Convert.ToBase64String(screenshot) : null,
            screenshotTruncated = candidate.ScreenshotPng is { Length: > 16 * 1024 * 1024 },
            candidate.Prompt,
            sourceSnapshot = candidate.SourceSnapshot.SnapshotHash,
            sourceFiles = candidate.SourceSnapshot.SafeFiles.Select(static file => new { file.RelativePath, file.ContentHash }).ToArray(),
            viewport = candidate.Viewport,
            metadata = candidate.SafeMetadata,
            candidate.DerivedFrom,
            references = candidate.SafeReferences
        };
        await using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(payload));
        var aggregate = await artifacts.CreateAsync(new CreateArtifactRequest(
            ArtifactKind.Design,
            $"Design candidate · {candidate.Title}",
            new ArtifactProducer(new PrincipalId("system:design-studio"), ArtifactProducerKind.System, "Abraxius Design Studio"),
            new ArtifactProvenance(WorkspaceId: "abraxius", GitCommit: generation.SourceSnapshot.GitCommit),
            new ArtifactClassification(DataClassification.Internal),
            "application/json",
            $"design-{candidate.Id}.json",
            ArtifactState.Candidate,
            ArtifactRetention.Persistent,
            TypedMetadata: ImmutableDictionary<string, string>.Empty
                .Add("design.surface", generation.SourceSnapshot.Surface.Value)
                .Add("design.provider", generation.Provider.Value)
                .Add("design.source-snapshot", generation.SourceSnapshot.SnapshotHash)), stream, cancellationToken).ConfigureAwait(false);
        return new DesignCandidateArtifactReference($"artifact://{aggregate.Descriptor.Id}", aggregate.CurrentRevision.Id.ToString());
    }
}

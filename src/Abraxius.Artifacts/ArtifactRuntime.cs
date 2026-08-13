namespace Abraxius.Artifacts;

public sealed class ArtifactRuntime : IAsyncDisposable
{
    public ArtifactRuntime(IArtifactStore store, IArtifactContentStore content, IArtifactService service, IArtifactReviewService reviews,
        ArtifactIntegrationService integration, ArtifactPublicationService publication, ArtifactDiffProviderRegistry diffs, IReadOnlyList<IArtifactPreviewProvider> previews)
    {
        Store = store; Content = content; Service = service; Reviews = reviews; Integration = integration; Publication = publication; Diffs = diffs; Previews = previews;
    }

    public IArtifactStore Store { get; }
    public IArtifactContentStore Content { get; }
    public IArtifactService Service { get; }
    public IArtifactReviewService Reviews { get; }
    public ArtifactIntegrationService Integration { get; }
    public ArtifactPublicationService Publication { get; }
    public ArtifactDiffProviderRegistry Diffs { get; }
    public IReadOnlyList<IArtifactPreviewProvider> Previews { get; }
    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => Service.InitializeAsync(cancellationToken);
    public ValueTask DisposeAsync() => Store.DisposeAsync();
}

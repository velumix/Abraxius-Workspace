using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Abraxius.Protocol;

namespace Abraxius.Artifacts;

public sealed record CreateArtifactRequest(
    ArtifactKind Kind,
    string Title,
    ArtifactProducer Producer,
    ArtifactProvenance Provenance,
    ArtifactClassification Classification,
    string MediaType,
    string? FileName = null,
    ArtifactState InitialState = ArtifactState.Candidate,
    ArtifactRetention Retention = ArtifactRetention.Persistent,
    string? ProjectId = null,
    ChangeSet? ChangeSet = null,
    ImmutableArray<ArtifactInputRef> InputRefs = default,
    ImmutableArray<ArtifactDependency> Dependencies = default,
    ImmutableArray<EvidenceId> EvidenceRefs = default,
    ImmutableDictionary<string, string>? TypedMetadata = null);

public sealed record CreateRevisionRequest(
    ArtifactId ArtifactId,
    ArtifactRevisionId ParentRevisionId,
    ArtifactProducer Producer,
    ArtifactProvenance Provenance,
    string MediaType,
    string? FileName = null,
    ArtifactState State = ArtifactState.Candidate,
    ChangeSet? ChangeSet = null,
    ImmutableArray<ArtifactInputRef> InputRefs = default,
    ImmutableArray<ArtifactDependency> Dependencies = default,
    ImmutableArray<EvidenceId> EvidenceRefs = default,
    ImmutableDictionary<string, string>? TypedMetadata = null);

public interface IArtifactService
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask<ArtifactAggregate> CreateAsync(CreateArtifactRequest request, Stream source, CancellationToken cancellationToken = default);
    ValueTask<ArtifactAggregate> CreateRevisionAsync(CreateRevisionRequest request, Stream source, CancellationToken cancellationToken = default);
    ValueTask<ArtifactAggregate> AttachVerificationAsync(ArtifactId artifactId, ArtifactVerification verification, CancellationToken cancellationToken = default);
    ValueTask<ArtifactAggregate> SetStateAsync(ArtifactId artifactId, ArtifactRevisionId revisionId, ArtifactState state, CancellationToken cancellationToken = default);
    ValueTask<ArtifactAggregate?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken = default);
    ValueTask<ArtifactRevision?> GetRevisionAsync(ArtifactRevisionId revisionId, CancellationToken cancellationToken = default);
    ValueTask<ArtifactStoreIntegrity> VerifyStoreAsync(CancellationToken cancellationToken = default);
}

public sealed class ArtifactService(IArtifactStore store, IArtifactContentStore content) : IArtifactService
{
    private readonly ConcurrentDictionary<ArtifactId, SemaphoreSlim> _gates = new();

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default) => store.InitializeAsync(cancellationToken);

    public async ValueTask<ArtifactAggregate> CreateAsync(CreateArtifactRequest request, Stream source, CancellationToken cancellationToken = default)
    {
        ValidateRequest(request.Title, request.Kind, request.Dependencies);
        var descriptor = await content.PutAsync(source, request.MediaType, request.FileName, cancellationToken).ConfigureAwait(false);
        var artifactId = ArtifactId.New();
        await ValidateDependenciesAsync(artifactId, request.Dependencies, cancellationToken).ConfigureAwait(false);
        var revision = NewRevision(artifactId, 1, null, descriptor, request.Producer, request.Provenance, request.Classification,
            request.ChangeSet, request.InputRefs, request.Dependencies, request.EvidenceRefs, request.TypedMetadata);
        var now = revision.CreatedAt;
        var artifact = new ArtifactAggregate(new ArtifactDescriptor(artifactId, request.Kind, request.Title, revision.Id, request.Producer,
            request.Provenance, request.Classification, request.InitialState, request.Retention, now, now, request.ProjectId), [revision]);
        await store.InsertAsync(artifact, cancellationToken).ConfigureAwait(false);
        return artifact;
    }

    public async ValueTask<ArtifactAggregate> CreateRevisionAsync(CreateRevisionRequest request, Stream source, CancellationToken cancellationToken = default)
    {
        var gate = _gates.GetOrAdd(request.ArtifactId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await RequireAsync(request.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (!current.Revisions.Any(item => item.Id == request.ParentRevisionId)) throw new InvalidOperationException("The parent revision does not belong to this artifact.");
            await ValidateDependenciesAsync(request.ArtifactId, request.Dependencies, cancellationToken).ConfigureAwait(false);
            var descriptor = await content.PutAsync(source, request.MediaType, request.FileName, cancellationToken).ConfigureAwait(false);
            var revision = NewRevision(request.ArtifactId, current.Revisions.Max(static item => item.RevisionNumber) + 1,
                request.ParentRevisionId, descriptor, request.Producer, request.Provenance, current.Descriptor.Classification,
                request.ChangeSet, request.InputRefs, request.Dependencies, request.EvidenceRefs, request.TypedMetadata);
            var updated = current with
            {
                Descriptor = current.Descriptor with { CurrentRevision = revision.Id, State = request.State, Producer = request.Producer, Provenance = request.Provenance, UpdatedAt = revision.CreatedAt },
                Revisions = current.Revisions.Add(revision)
            };
            await store.UpdateAsync(updated, current.Descriptor.CurrentRevision, cancellationToken).ConfigureAwait(false);
            return updated;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<ArtifactAggregate> AttachVerificationAsync(ArtifactId artifactId, ArtifactVerification verification, CancellationToken cancellationToken = default)
    {
        var current = await RequireAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (!current.Revisions.Any(item => item.Id == verification.ArtifactRevisionId)) throw new InvalidOperationException("Verification must pin a revision belonging to this artifact.");
        if (current.SafeVerifications.Any(item => item.Id == verification.Id)) return current;
        var isCurrent = current.Descriptor.CurrentRevision == verification.ArtifactRevisionId;
        var state = !isCurrent ? current.Descriptor.State : verification.Result switch
        {
            ArtifactVerificationResult.Passed => ArtifactState.Verified,
            ArtifactVerificationResult.Failed => ArtifactState.VerificationFailed,
            _ => ArtifactState.AwaitingVerification
        };
        var updated = current with { Descriptor = current.Descriptor with { State = state, UpdatedAt = DateTimeOffset.UtcNow }, Verifications = current.SafeVerifications.Add(verification) };
        await store.UpdateAsync(updated, current.Descriptor.CurrentRevision, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async ValueTask<ArtifactAggregate> SetStateAsync(ArtifactId artifactId, ArtifactRevisionId revisionId, ArtifactState state, CancellationToken cancellationToken = default)
    {
        var current = await RequireAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (current.Descriptor.CurrentRevision != revisionId) throw new InvalidOperationException("Lifecycle state may only advance for the explicitly selected current revision.");
        var updated = current with { Descriptor = current.Descriptor with { State = state, UpdatedAt = DateTimeOffset.UtcNow } };
        await store.UpdateAsync(updated, current.Descriptor.CurrentRevision, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public ValueTask<ArtifactAggregate?> GetAsync(ArtifactId artifactId, CancellationToken cancellationToken = default) => store.GetAsync(artifactId, cancellationToken);
    public async ValueTask<ArtifactRevision?> GetRevisionAsync(ArtifactRevisionId revisionId, CancellationToken cancellationToken = default) => (await store.GetByRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false))?.Revisions.SingleOrDefault(item => item.Id == revisionId);

    public async ValueTask<ArtifactStoreIntegrity> VerifyStoreAsync(CancellationToken cancellationToken = default)
    {
        var artifacts = 0; var revisions = 0; var missing = 0; var corrupt = 0; var retained = new HashSet<ArtifactBlobId>();
        await foreach (var artifact in store.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            artifacts++;
            foreach (var revision in artifact.Revisions)
            {
                revisions++; retained.Add(revision.Content.BlobId);
                try { if (!await content.VerifyAsync(revision.Content, cancellationToken).ConfigureAwait(false)) corrupt++; }
                catch (FileNotFoundException) { missing++; }
            }
        }
        return new(artifacts, revisions, missing, corrupt, 0);
    }

    private async ValueTask<ArtifactAggregate> RequireAsync(ArtifactId id, CancellationToken token) => await store.GetAsync(id, token).ConfigureAwait(false) ?? throw new KeyNotFoundException($"Artifact {id} was not found.");

    private async ValueTask ValidateDependenciesAsync(ArtifactId owner, ImmutableArray<ArtifactDependency> dependencies, CancellationToken cancellationToken)
    {
        if (dependencies.IsDefaultOrEmpty) return;
        foreach (var dependency in dependencies)
        {
            if (dependency.ArtifactId == owner) throw new InvalidOperationException("Artifact dependency cycles are not allowed.");
            var target = await store.GetAsync(dependency.ArtifactId, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException($"Dependency artifact {dependency.ArtifactId} does not exist.");
            if (!target.Revisions.Any(item => item.Id == dependency.RevisionId)) throw new InvalidOperationException("Artifact dependencies must pin an exact existing revision.");
            if (await ReachesAsync(dependency.ArtifactId, owner, new HashSet<ArtifactId>(), cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("Artifact dependency cycles are not allowed.");
        }
    }

    private async ValueTask<bool> ReachesAsync(ArtifactId current, ArtifactId target, HashSet<ArtifactId> visited, CancellationToken token)
    {
        if (current == target) return true;
        if (!visited.Add(current)) return false;
        var artifact = await store.GetAsync(current, token).ConfigureAwait(false);
        if (artifact is null) return false;
        foreach (var dependency in artifact.Revisions.SelectMany(static item => item.SafeDependencies))
            if (await ReachesAsync(dependency.ArtifactId, target, visited, token).ConfigureAwait(false)) return true;
        return false;
    }

    private static void ValidateRequest(string title, ArtifactKind kind, ImmutableArray<ArtifactDependency> dependencies)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Artifact title is required.", nameof(title));
        if (string.IsNullOrWhiteSpace(kind.Value)) throw new ArgumentException("Artifact kind is required.", nameof(kind));
        if (!dependencies.IsDefault && dependencies.Select(static item => item.Id).Distinct().Count() != dependencies.Length) throw new ArgumentException("Dependency identities must be unique.", nameof(dependencies));
    }

    private static ArtifactRevision NewRevision(ArtifactId artifactId, long number, ArtifactRevisionId? parent, ArtifactContentDescriptor content,
        ArtifactProducer producer, ArtifactProvenance provenance, ArtifactClassification classification, ChangeSet? changeSet,
        ImmutableArray<ArtifactInputRef> inputs, ImmutableArray<ArtifactDependency> dependencies, ImmutableArray<EvidenceId> evidence,
        ImmutableDictionary<string, string>? metadata)
    {
        var id = ArtifactRevisionId.New();
        var hash = ComputeRevisionHash(artifactId, id, number, parent, content, changeSet, dependencies, metadata);
        return new(id, artifactId, number, parent, content, changeSet, DateTimeOffset.UtcNow, producer, provenance,
            inputs.IsDefault ? [] : inputs, dependencies.IsDefault ? [] : dependencies, evidence.IsDefault ? [] : evidence, classification, hash, metadata);
    }

    private static string ComputeRevisionHash(ArtifactId artifactId, ArtifactRevisionId revisionId, long number, ArtifactRevisionId? parent,
        ArtifactContentDescriptor content, ChangeSet? changeSet, ImmutableArray<ArtifactDependency> dependencies, ImmutableDictionary<string, string>? metadata)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) { var bytes = Encoding.UTF8.GetBytes(value); hash.AppendData(bytes); hash.AppendData([0]); }
        Add("abraxius.artifact-revision/1"); Add(artifactId.ToString()); Add(revisionId.ToString()); Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture)); Add(parent?.ToString() ?? ""); Add(content.ContentHash); Add(content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)); Add(content.MediaType); Add(changeSet?.Hash ?? "");
        foreach (var dependency in (dependencies.IsDefault ? [] : dependencies).OrderBy(static item => item.Id.Value)) { Add(dependency.ArtifactId.ToString()); Add(dependency.RevisionId.ToString()); Add(dependency.Kind.ToString()); }
        foreach (var pair in (metadata ?? ImmutableDictionary<string, string>.Empty).OrderBy(static item => item.Key, StringComparer.Ordinal)) { Add(pair.Key); Add(pair.Value); }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

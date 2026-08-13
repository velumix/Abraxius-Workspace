using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Abraxius.Presence;
using Abraxius.Security;

namespace Abraxius.Artifacts;

public interface IArtifactReviewService
{
    ValueTask<ArtifactReview> RequestReviewAsync(ArtifactId artifactId, ArtifactRevisionId revisionId, PrincipalId requestedBy, AttentionContext attention, DateTimeOffset? deadline = null, CancellationToken cancellationToken = default);
    ValueTask<ArtifactReview> AddCommentAsync(ReviewId reviewId, ReviewComment comment, CancellationToken cancellationToken = default);
    ValueTask<ArtifactApproval> DecideAsync(ReviewId reviewId, PrincipalId reviewer, ArtifactApprovalState decision, string? reason = null, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<ArtifactReview>> GetQueueAsync(CancellationToken cancellationToken = default);
}

public sealed class ArtifactReviewService(IArtifactStore store, INeedsYouService needsYou) : IArtifactReviewService
{
    public async ValueTask<ArtifactReview> RequestReviewAsync(ArtifactId artifactId, ArtifactRevisionId revisionId, PrincipalId requestedBy, AttentionContext attention, DateTimeOffset? deadline = null, CancellationToken cancellationToken = default)
    {
        var aggregate = await RequireAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (!aggregate.Revisions.Any(item => item.Id == revisionId)) throw new InvalidOperationException("Review must pin an exact artifact revision.");
        var existing = aggregate.SafeReviews.FirstOrDefault(item => item.ArtifactRevisionId == revisionId && item.State is ArtifactReviewState.Pending or ArtifactReviewState.Viewed);
        if (existing is not null) return existing;
        var id = ReviewId.New();
        var item = new NeedsYouItem(NeedsYouId.New(), aggregate.Descriptor.Provenance.MissionId, aggregate.Descriptor.Provenance.AssignmentId,
            aggregate.Descriptor.Producer.DisplayName, NeedsYouReason.ArtifactReview, NotificationSeverity.AttentionRequired,
            new NotificationActionId("artifact.review"), $"A verified artifact is ready for review: {aggregate.Descriptor.Title}", [], DateTimeOffset.UtcNow, deadline,
            SourceEventId: $"artifact-review:{id}");
        await needsYou.CreateAsync(item, attention, cancellationToken).ConfigureAwait(false);
        var review = new ArtifactReview(id, artifactId, revisionId, requestedBy, DateTimeOffset.UtcNow, Deadline: deadline, NeedsYouId: item.Id.ToString());
        var updated = aggregate with { Descriptor = aggregate.Descriptor with { State = ArtifactState.AwaitingReview, UpdatedAt = DateTimeOffset.UtcNow }, Reviews = aggregate.SafeReviews.Add(review) };
        await store.UpdateAsync(updated, aggregate.Descriptor.CurrentRevision, cancellationToken).ConfigureAwait(false);
        return review;
    }

    public async ValueTask<ArtifactReview> AddCommentAsync(ReviewId reviewId, ReviewComment comment, CancellationToken cancellationToken = default)
    {
        var (aggregate, review) = await FindReviewAsync(reviewId, cancellationToken).ConfigureAwait(false);
        if (comment.ReviewId != reviewId || comment.ArtifactRevisionId != review.ArtifactRevisionId) throw new InvalidOperationException("Review comments must pin the same review and revision.");
        var changed = review with { Comments = review.SafeComments.Add(comment), State = comment.Severity is ReviewCommentSeverity.Required or ReviewCommentSeverity.Blocking ? ArtifactReviewState.ChangesRequested : review.State };
        var updated = ReplaceReview(aggregate, changed);
        await store.UpdateAsync(updated, aggregate.Descriptor.CurrentRevision, cancellationToken).ConfigureAwait(false);
        return changed;
    }

    public async ValueTask<ArtifactApproval> DecideAsync(ReviewId reviewId, PrincipalId reviewer, ArtifactApprovalState decision, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (decision is ArtifactApprovalState.Pending) throw new ArgumentException("A final review decision is required.", nameof(decision));
        var (aggregate, review) = await FindReviewAsync(reviewId, cancellationToken).ConfigureAwait(false);
        if (aggregate.Descriptor.CurrentRevision != review.ArtifactRevisionId) throw new InvalidOperationException("The review is stale because a newer artifact revision exists.");
        var reviewState = decision switch { ArtifactApprovalState.Approved => ArtifactReviewState.Approved, ArtifactApprovalState.Rejected => ArtifactReviewState.Rejected, ArtifactApprovalState.ChangesRequested => ArtifactReviewState.ChangesRequested, _ => ArtifactReviewState.Cancelled };
        var approval = new ArtifactApproval(ApprovalId.New(), reviewId, review.ArtifactRevisionId, reviewer, decision, DateTimeOffset.UtcNow, reason);
        var changed = review with { State = reviewState };
        var artifactState = decision switch { ArtifactApprovalState.Approved => ArtifactState.Approved, ArtifactApprovalState.Rejected => ArtifactState.Rejected, ArtifactApprovalState.ChangesRequested => ArtifactState.Candidate, _ => aggregate.Descriptor.State };
        var updated = ReplaceReview(aggregate, changed) with { Descriptor = aggregate.Descriptor with { State = artifactState, UpdatedAt = DateTimeOffset.UtcNow }, Approvals = aggregate.SafeApprovals.Add(approval) };
        await store.UpdateAsync(updated, aggregate.Descriptor.CurrentRevision, cancellationToken).ConfigureAwait(false);
        if (review.NeedsYouId is { } needsId && Guid.TryParseExact(needsId, "N", out var parsed))
            await needsYou.ResolveAsync(new NeedsYouId(parsed), decision == ArtifactApprovalState.Approved ? NeedsYouResolution.Approved : NeedsYouResolution.Rejected, reason, cancellationToken).ConfigureAwait(false);
        return approval;
    }

    public async ValueTask<IReadOnlyList<ArtifactReview>> GetQueueAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<ArtifactReview>(); await foreach (var artifact in store.ReadAllAsync(cancellationToken).ConfigureAwait(false)) result.AddRange(artifact.SafeReviews.Where(static item => item.State is ArtifactReviewState.Pending or ArtifactReviewState.Viewed or ArtifactReviewState.ChangesRequested));
        return result.OrderBy(static item => item.Deadline ?? DateTimeOffset.MaxValue).ThenBy(static item => item.CreatedAt).ToArray();
    }

    private async ValueTask<ArtifactAggregate> RequireAsync(ArtifactId id, CancellationToken token) => await store.GetAsync(id, token).ConfigureAwait(false) ?? throw new KeyNotFoundException("Artifact not found.");
    private async ValueTask<(ArtifactAggregate Aggregate, ArtifactReview Review)> FindReviewAsync(ReviewId id, CancellationToken token)
    {
        await foreach (var artifact in store.ReadAllAsync(token).ConfigureAwait(false)) if (artifact.SafeReviews.FirstOrDefault(item => item.Id == id) is { } review) return (artifact, review);
        throw new KeyNotFoundException("Review not found.");
    }
    private static ArtifactAggregate ReplaceReview(ArtifactAggregate aggregate, ArtifactReview review) => aggregate with { Reviews = aggregate.SafeReviews.Select(item => item.Id == review.Id ? review : item).ToImmutableArray() };
}

public sealed record ArtifactTargetState(string Target, string StateHash, bool Exists);
public sealed record ArtifactApplyResult(bool Succeeded, string? ResultState, string? ExternalIdentifier = null, string? RollbackReference = null, string? Error = null);
public interface IArtifactTargetAdapter
{
    ValueTask<ArtifactTargetState> ResolveAsync(string target, CancellationToken cancellationToken = default);
    ValueTask<ArtifactApplyResult> ApplyAsync(ArtifactRevision revision, Stream content, string target, CancellationToken cancellationToken = default);
}

public sealed class AtomicFileArtifactTargetAdapter : IArtifactTargetAdapter
{
    public async ValueTask<ArtifactTargetState> ResolveAsync(string target, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(target);
        if (!File.Exists(path)) return new(target, "missing", false);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(target, Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant(), true);
    }

    public async ValueTask<ArtifactApplyResult> ApplyAsync(ArtifactRevision revision, Stream content, string target, CancellationToken cancellationToken = default)
    {
        var path = ResolvePath(target); var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Target directory is unavailable."); Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.artifact.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await content.CopyToAsync(output, cancellationToken).ConfigureAwait(false); await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
            return new(true, revision.Content.ContentHash, RollbackReference: revision.ParentRevisionId?.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            return new(false, null, Error: exception.Message);
        }
    }
    private static string ResolvePath(string target) => Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.IsFile ? Path.GetFullPath(uri.LocalPath) : Path.GetFullPath(target);
}

public sealed record ArtifactIntegrationRequest(ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Target, string ExpectedBaseState, SecuritySubject Subject, AuthorizationContext AuthorizationContext, string SecurityOperation, ResourceKind ResourceKind, bool ApprovalRequired = true, ArtifactReversibility Reversibility = ArtifactReversibility.Unknown);
public sealed record ArtifactIntegrationResult(ArtifactIntegration Integration, AuthorizationDecision Authorization);

public sealed class ArtifactIntegrationService(IArtifactStore store, IArtifactContentStore content, ISecurityKernel security, IResourceCanonicalizer resources, IArtifactTargetAdapter target)
{
    public async ValueTask<ArtifactIntegrationResult> IntegrateAsync(ArtifactIntegrationRequest request, CancellationToken cancellationToken = default)
    {
        var aggregate = await store.GetAsync(request.ArtifactId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Artifact not found.");
        var revision = aggregate.Revisions.SingleOrDefault(item => item.Id == request.RevisionId) ?? throw new InvalidOperationException("Integration revision does not belong to the artifact.");
        if (request.ApprovalRequired && !aggregate.SafeApprovals.Any(item => item.ArtifactRevisionId == request.RevisionId && item.State == ArtifactApprovalState.Approved)) throw new InvalidOperationException("The exact artifact revision is not approved.");
        if (!await content.VerifyAsync(revision.Content, cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("Artifact content failed integrity verification.");
        var currentTarget = await target.ResolveAsync(request.Target, cancellationToken).ConfigureAwait(false);
        if (!currentTarget.StateHash.Equals(request.ExpectedBaseState, StringComparison.Ordinal))
        {
            var stale = new ArtifactIntegration(IntegrationId.New(), request.RevisionId, ArtifactIntegrationState.NeedsRebase, request.Target, request.ExpectedBaseState,
                currentTarget.StateHash, revision.RevisionHash, DateTimeOffset.UtcNow, request.Subject.PrincipalId, Reversibility: request.Reversibility, Error: "Target changed after artifact creation.");
            await AppendIntegrationAsync(aggregate, stale, cancellationToken).ConfigureAwait(false);
            return new(stale, new AuthorizationDecision(AuthorizationDecisionId.New(), AuthorizationOutcome.Deny, AuthorizationReasonCode.DeniedPolicy, "Target state is stale; create and verify a new revision.", RiskClass.Mutation));
        }
        var authorizationRequest = await AuthorizationRequestFactory.CreateAsync(resources, request.Subject, request.SecurityOperation, request.SecurityOperation,
            request.ResourceKind, request.Target, request.AuthorizationContext, mutation: true, external: request.SecurityOperation is SecurityActions.GitPush or SecurityActions.DeploymentPublish, cancellationToken: cancellationToken).ConfigureAwait(false);
        var decision = await security.AuthorizeAsync(authorizationRequest, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            var blocked = new ArtifactIntegration(IntegrationId.New(), request.RevisionId, ArtifactIntegrationState.IntegrationFailed, request.Target, request.ExpectedBaseState,
                currentTarget.StateHash, revision.RevisionHash, DateTimeOffset.UtcNow, request.Subject.PrincipalId, decision.DecisionId.ToString(), request.Reversibility, Error: decision.HumanExplanation);
            await AppendIntegrationAsync(aggregate, blocked, cancellationToken).ConfigureAwait(false);
            return new(blocked, decision);
        }
        await using var stream = await content.OpenReadAsync(revision.Content.BlobId, cancellationToken).ConfigureAwait(false);
        var applied = await target.ApplyAsync(revision, stream, request.Target, cancellationToken).ConfigureAwait(false);
        await security.RecordExecutionResultAsync(authorizationRequest, decision, applied.Succeeded, applied.Error, cancellationToken).ConfigureAwait(false);
        var integration = new ArtifactIntegration(IntegrationId.New(), request.RevisionId, applied.Succeeded ? ArtifactIntegrationState.Integrated : ArtifactIntegrationState.IntegrationFailed,
            request.Target, request.ExpectedBaseState, applied.ResultState, revision.RevisionHash, DateTimeOffset.UtcNow, request.Subject.PrincipalId,
            decision.DecisionId.ToString(), request.Reversibility, applied.RollbackReference, applied.Error);
        await AppendIntegrationAsync(aggregate, integration, cancellationToken).ConfigureAwait(false);
        return new(integration, decision);
    }

    private async ValueTask AppendIntegrationAsync(ArtifactAggregate original, ArtifactIntegration integration, CancellationToken token)
    {
        var latest = await store.GetAsync(original.Descriptor.Id, token).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var state = integration.State == ArtifactIntegrationState.Integrated && latest.Descriptor.CurrentRevision == integration.ArtifactRevisionId ? ArtifactState.Integrated : latest.Descriptor.State;
        var updated = latest with { Descriptor = latest.Descriptor with { State = state, UpdatedAt = DateTimeOffset.UtcNow }, Integrations = latest.SafeIntegrations.Add(integration) };
        await store.UpdateAsync(updated, latest.Descriptor.CurrentRevision, token).ConfigureAwait(false);
    }
}

public interface IArtifactSecretScanner { ValueTask<ImmutableArray<string>> ScanAsync(Stream content, CancellationToken cancellationToken = default); }
public sealed partial class PatternArtifactSecretScanner : IArtifactSecretScanner
{
    [GeneratedRegex("""(?i)(api[_-]?key|token|secret|password)\s*[:=]\s*['"]?[A-Za-z0-9_\-/+=]{16,}""")]
    private static partial Regex CredentialPattern();
    public async ValueTask<ImmutableArray<string>> ScanAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, leaveOpen: true); var buffer = new char[64 * 1024]; var found = ImmutableArray.CreateBuilder<string>(); long inspected = 0;
        while (inspected < 8 * 1024 * 1024 && await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) is var read && read > 0)
        {
            inspected += read; var text = new string(buffer, 0, read); if (CredentialPattern().IsMatch(text)) found.Add("potential-credential");
        }
        return found.Distinct().ToImmutableArray();
    }
}

public sealed record ArtifactPublicationRequest(ArtifactId ArtifactId, ArtifactRevisionId RevisionId, Uri Destination, SecuritySubject Subject, AuthorizationContext AuthorizationContext, bool ApprovalRequired = true);
public interface IArtifactPublisher { ValueTask<ArtifactApplyResult> PublishAsync(ArtifactRevision revision, Stream content, Uri destination, CancellationToken cancellationToken = default); }
public sealed class UnavailableArtifactPublisher : IArtifactPublisher
{
    public ValueTask<ArtifactApplyResult> PublishAsync(ArtifactRevision revision, Stream content, Uri destination, CancellationToken cancellationToken = default) => ValueTask.FromResult(new ArtifactApplyResult(false, null, Error: "No publication adapter is configured for this destination."));
}

public sealed class ArtifactPublicationService(IArtifactStore store, IArtifactContentStore content, ISecurityKernel security, IResourceCanonicalizer resources, IArtifactSecretScanner scanner, IArtifactPublisher publisher)
{
    public async ValueTask<ArtifactPublication> PublishAsync(ArtifactPublicationRequest request, CancellationToken cancellationToken = default)
    {
        var aggregate = await store.GetAsync(request.ArtifactId, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Artifact not found.");
        var revision = aggregate.Revisions.SingleOrDefault(item => item.Id == request.RevisionId) ?? throw new InvalidOperationException("Publication must pin a revision belonging to the artifact.");
        if (request.ApprovalRequired && !aggregate.SafeApprovals.Any(item => item.ArtifactRevisionId == request.RevisionId && item.State == ArtifactApprovalState.Approved)) throw new InvalidOperationException("The exact revision is not approved.");
        if (revision.Classification.Level == DataClassification.LocalOnly)
            return await AppendAsync(aggregate, new(PublicationId.New(), request.RevisionId, request.Destination.AbsoluteUri, DateTimeOffset.UtcNow, request.Subject.PrincipalId, "classification:local-only", null, ArtifactPublicationResult.Blocked, "LocalOnly artifacts cannot be published externally."), cancellationToken).ConfigureAwait(false);
        if (!await content.VerifyAsync(revision.Content, cancellationToken).ConfigureAwait(false)) throw new InvalidDataException("Artifact content failed integrity verification.");
        if (IsText(revision.Content.MediaType))
        {
            await using var scanStream = await content.OpenReadAsync(revision.Content.BlobId, cancellationToken).ConfigureAwait(false);
            if (!(await scanner.ScanAsync(scanStream, cancellationToken).ConfigureAwait(false)).IsEmpty)
                return await AppendAsync(aggregate, new(PublicationId.New(), request.RevisionId, request.Destination.AbsoluteUri, DateTimeOffset.UtcNow, request.Subject.PrincipalId, "security:secret-scan", null, ArtifactPublicationResult.Blocked, "Potential credential material was detected."), cancellationToken).ConfigureAwait(false);
        }
        var authRequest = await AuthorizationRequestFactory.CreateAsync(resources, request.Subject, SecurityActions.ArtifactPublish, SecurityActions.ArtifactPublish,
            ResourceKind.Network, request.Destination.AbsoluteUri, request.AuthorizationContext with { Classification = revision.Classification.Level }, mutation: true, external: true, cancellationToken: cancellationToken).ConfigureAwait(false);
        var decision = await security.AuthorizeAsync(authRequest, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed) return await AppendAsync(aggregate, new(PublicationId.New(), request.RevisionId, request.Destination.AbsoluteUri, DateTimeOffset.UtcNow, request.Subject.PrincipalId, decision.DecisionId.ToString(), null, ArtifactPublicationResult.Blocked, decision.HumanExplanation), cancellationToken).ConfigureAwait(false);
        await using var publishStream = await content.OpenReadAsync(revision.Content.BlobId, cancellationToken).ConfigureAwait(false);
        var result = await publisher.PublishAsync(revision, publishStream, request.Destination, cancellationToken).ConfigureAwait(false);
        await security.RecordExecutionResultAsync(authRequest, decision, result.Succeeded, result.Error, cancellationToken).ConfigureAwait(false);
        return await AppendAsync(aggregate, new(PublicationId.New(), request.RevisionId, request.Destination.AbsoluteUri, DateTimeOffset.UtcNow, request.Subject.PrincipalId,
            decision.DecisionId.ToString(), result.ExternalIdentifier, result.Succeeded ? ArtifactPublicationResult.Published : ArtifactPublicationResult.Failed, result.Error), cancellationToken).ConfigureAwait(false);
    }
    private async ValueTask<ArtifactPublication> AppendAsync(ArtifactAggregate original, ArtifactPublication publication, CancellationToken token)
    {
        var latest = await store.GetAsync(original.Descriptor.Id, token).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var state = publication.Result == ArtifactPublicationResult.Published && latest.Descriptor.CurrentRevision == publication.ArtifactRevisionId ? ArtifactState.Published : latest.Descriptor.State;
        await store.UpdateAsync(latest with { Descriptor = latest.Descriptor with { State = state, UpdatedAt = DateTimeOffset.UtcNow }, Publications = latest.SafePublications.Add(publication) }, latest.Descriptor.CurrentRevision, token).ConfigureAwait(false);
        return publication;
    }
    private static bool IsText(string mediaType) => mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) || mediaType.Contains("yaml", StringComparison.OrdinalIgnoreCase);
}

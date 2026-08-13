using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Protocol;
using Abraxius.Security;

namespace Abraxius.Artifacts;

public readonly record struct ArtifactRevisionId(Guid Value) { public static ArtifactRevisionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct ArtifactCollectionId(Guid Value) { public static ArtifactCollectionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct ChangeSetId(Guid Value) { public static ChangeSetId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct ReviewId(Guid Value) { public static ReviewId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct ApprovalId(Guid Value) { public static ApprovalId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct IntegrationId(Guid Value) { public static IntegrationId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct PublicationId(Guid Value) { public static PublicationId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct ArtifactVerificationId(Guid Value) { public static ArtifactVerificationId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct ReviewCommentId(Guid Value) { public static ReviewCommentId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct ArtifactDependencyId(Guid Value) { public static ArtifactDependencyId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct ArtifactBlobId(string Value) { public override string ToString() => Value; }

public readonly record struct ArtifactKind(string Value)
{
    public static readonly ArtifactKind SourceChange = new("source-change");
    public static readonly ArtifactKind File = new("file");
    public static readonly ArtifactKind Document = new("document");
    public static readonly ArtifactKind Report = new("report");
    public static readonly ArtifactKind Configuration = new("configuration");
    public static readonly ArtifactKind Build = new("build");
    public static readonly ArtifactKind Package = new("package");
    public static readonly ArtifactKind Benchmark = new("benchmark");
    public static readonly ArtifactKind TestResult = new("test-result");
    public static readonly ArtifactKind Image = new("image");
    public static readonly ArtifactKind Asset = new("asset");
    public static readonly ArtifactKind Migration = new("migration");
    public static readonly ArtifactKind ReleaseCandidate = new("release-candidate");
    public static readonly ArtifactKind DeploymentPlan = new("deployment-plan");
    public static readonly ArtifactKind GeneratedData = new("generated-data");
    public override string ToString() => Value;
}

public enum ArtifactState { Draft, Candidate, AwaitingVerification, Verified, VerificationFailed, AwaitingReview, Approved, Rejected, Integrated, Published, Superseded, Archived }
public enum ArtifactProducerKind { Athena, Orion, Daedalus, Argus, Skill, Plugin, System, User, ExternalImport }
public enum ArtifactRetention { Temporary, Persistent, Pinned, Archived }
public enum ArtifactLocationKind { Local, Remote, External, ContentStore }
public enum ArtifactInputKind { ArtifactRevision, SourceFile, Evidence, Memory, GitState, UserData, ExternalSource }
public enum ArtifactDependencyKind { Input, BuildInput, VerificationInput, DerivedFrom, Supersedes }
public enum ChangeEntryKind { FileAdded, FileModified, FileDeleted, FileRenamed, BinaryChanged, ConfigurationChanged, DatabaseMigration, GitMetadataChange }
public enum ArtifactVerificationResult { Passed, Failed, Inconclusive, Skipped }
public enum ArtifactApprovalState { Pending, Approved, Rejected, ChangesRequested, Expired, Cancelled }
public enum ArtifactReviewState { Pending, Viewed, Approved, Rejected, ChangesRequested, Stale, Cancelled }
public enum ReviewCommentSeverity { Suggestion, Required, Blocking }
public enum ReviewCommentTargetKind { Artifact, Revision, File, DiffHunk, Line, PreviewRegion, General }
public enum ArtifactIntegrationState { NotIntegrated, Ready, Integrating, Integrated, IntegrationFailed, NeedsRebase, Conflict, RolledBack }
public enum ArtifactPublicationResult { Pending, Published, Failed, Blocked }
public enum ArtifactReversibility { Unknown, Reversible, PartiallyReversible, Irreversible }

public sealed record ArtifactClassification(DataClassification Level)
{
    public static ArtifactClassification Internal { get; } = new(DataClassification.Internal);
}

public sealed record ArtifactProducer(PrincipalId PrincipalId, ArtifactProducerKind Kind, string DisplayName);

public sealed record ArtifactProvenance(
    MissionId? MissionId = null,
    string? TrajectoryId = null,
    AssignmentId? AssignmentId = null,
    SpecialistInstanceId? AgentInstanceId = null,
    string? SkillExecutionId = null,
    ImmutableArray<EvidenceId> SourceEvidenceIds = default,
    ImmutableArray<ArtifactRevisionId> InputArtifactRevisions = default,
    string? WorkspaceId = null,
    string? GitCommit = null,
    ImmutableArray<string> ModelRouteRefs = default)
{
    public ImmutableArray<EvidenceId> SafeSourceEvidenceIds => SourceEvidenceIds.IsDefault ? [] : SourceEvidenceIds;
    public ImmutableArray<ArtifactRevisionId> SafeInputArtifactRevisions => InputArtifactRevisions.IsDefault ? [] : InputArtifactRevisions;
    public ImmutableArray<string> SafeModelRouteRefs => ModelRouteRefs.IsDefault ? [] : ModelRouteRefs;
}

public sealed record ArtifactLocation(ArtifactLocationKind Kind, string Reference, string? NodeId = null);

public sealed record ArtifactContentDescriptor(
    ArtifactBlobId BlobId,
    string ContentHash,
    long Length,
    string MediaType,
    string? FileName,
    ArtifactLocation Location,
    ImmutableDictionary<string, string>? Metadata = null)
{
    public ImmutableDictionary<string, string> SafeMetadata => Metadata ?? ImmutableDictionary<string, string>.Empty;
}

public sealed record ArtifactInputRef(ArtifactInputKind Kind, string Reference, ArtifactRevisionId? RevisionId = null, string? Hash = null);
public sealed record ArtifactDependency(ArtifactDependencyId Id, ArtifactDependencyKind Kind, ArtifactId ArtifactId, ArtifactRevisionId RevisionId, string Reason);

public sealed record DiffLine(int? OldLine, int? NewLine, string Text, char Prefix);
public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, ImmutableArray<DiffLine> Lines);

public sealed record ChangeEntry(
    ChangeEntryKind Kind,
    string? OldPath,
    string? NewPath,
    string? OldHash,
    string? NewHash,
    long OldSize = 0,
    long NewSize = 0,
    ImmutableArray<DiffHunk> Hunks = default,
    string? MediaType = null)
{
    public ImmutableArray<DiffHunk> SafeHunks => Hunks.IsDefault ? [] : Hunks;
}

public sealed record ChangeSetStatistics(int FilesAdded, int FilesModified, int FilesDeleted, int FilesRenamed, long LinesAdded, long LinesDeleted, long BytesChanged);
public sealed record ChangeSet(ChangeSetId Id, string BaseState, string TargetState, ImmutableArray<ChangeEntry> Entries, string Summary, ChangeSetStatistics Statistics, ImmutableArray<string> AffectedResources, string Hash);

public sealed record ArtifactDescriptor(
    ArtifactId Id,
    ArtifactKind Kind,
    string Title,
    ArtifactRevisionId CurrentRevision,
    ArtifactProducer Producer,
    ArtifactProvenance Provenance,
    ArtifactClassification Classification,
    ArtifactState State,
    ArtifactRetention Retention,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ProjectId = null,
    ImmutableArray<string> Tags = default)
{
    public ImmutableArray<string> SafeTags => Tags.IsDefault ? [] : Tags;
}

public sealed record ArtifactRevision(
    ArtifactRevisionId Id,
    ArtifactId ArtifactId,
    long RevisionNumber,
    ArtifactRevisionId? ParentRevisionId,
    ArtifactContentDescriptor Content,
    ChangeSet? ChangeSet,
    DateTimeOffset CreatedAt,
    ArtifactProducer Producer,
    ArtifactProvenance Provenance,
    ImmutableArray<ArtifactInputRef> InputRefs,
    ImmutableArray<ArtifactDependency> Dependencies,
    ImmutableArray<EvidenceId> EvidenceRefs,
    ArtifactClassification Classification,
    string RevisionHash,
    ImmutableDictionary<string, string>? TypedMetadata = null)
{
    public ImmutableArray<ArtifactInputRef> SafeInputRefs => InputRefs.IsDefault ? [] : InputRefs;
    public ImmutableArray<ArtifactDependency> SafeDependencies => Dependencies.IsDefault ? [] : Dependencies;
    public ImmutableArray<EvidenceId> SafeEvidenceRefs => EvidenceRefs.IsDefault ? [] : EvidenceRefs;
    public ImmutableDictionary<string, string> SafeTypedMetadata => TypedMetadata ?? ImmutableDictionary<string, string>.Empty;
}

public sealed record ArtifactVerificationCheck(string Requirement, string Expected, string Observed, ImmutableArray<EvidenceId> EvidenceRefs, ArtifactVerificationResult Result);
public sealed record ArtifactVerification(ArtifactVerificationId Id, ArtifactRevisionId ArtifactRevisionId, ArtifactProducer Verifier, string VerificationPlan, ImmutableArray<ArtifactVerificationCheck> Checks, ImmutableArray<EvidenceId> EvidenceRefs, ArtifactVerificationResult Result, DateTimeOffset Timestamp, string Environment);

public sealed record ReviewComment(
    ReviewCommentId Id,
    ReviewId ReviewId,
    ArtifactRevisionId ArtifactRevisionId,
    PrincipalId Author,
    ReviewCommentTargetKind TargetKind,
    string? Path,
    int? Line,
    string Body,
    ReviewCommentSeverity Severity,
    DateTimeOffset CreatedAt,
    bool Resolved = false,
    string? Resolution = null,
    ReviewCommentId? ParentId = null);

public sealed record ArtifactReview(ReviewId Id, ArtifactId ArtifactId, ArtifactRevisionId ArtifactRevisionId, PrincipalId RequestedBy, DateTimeOffset CreatedAt, ArtifactReviewState State = ArtifactReviewState.Pending, ImmutableArray<ReviewComment> Comments = default, DateTimeOffset? Deadline = null, string? NeedsYouId = null)
{
    public ImmutableArray<ReviewComment> SafeComments => Comments.IsDefault ? [] : Comments;
}

public sealed record ArtifactApproval(ApprovalId Id, ReviewId ReviewId, ArtifactRevisionId ArtifactRevisionId, PrincipalId Principal, ArtifactApprovalState State, DateTimeOffset Timestamp, string? Reason = null);
public sealed record ArtifactIntegration(IntegrationId Id, ArtifactRevisionId ArtifactRevisionId, ArtifactIntegrationState State, string Target, string ExpectedBaseState, string? ObservedTargetState, string ArtifactHash, DateTimeOffset Timestamp, PrincipalId Principal, string? AuthorizationDecisionId = null, ArtifactReversibility Reversibility = ArtifactReversibility.Unknown, string? RollbackReference = null, string? Error = null);
public sealed record ArtifactPublication(PublicationId Id, ArtifactRevisionId ArtifactRevisionId, string Destination, DateTimeOffset Timestamp, PrincipalId Principal, string AuthorizationDecisionId, string? ExternalIdentifier, ArtifactPublicationResult Result, string? Error = null);

[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "ArtifactCollection is the intentional domain term for a version-pinned group of outputs.")]
public sealed record ArtifactCollection(ArtifactCollectionId Id, string Title, ImmutableArray<ArtifactRevisionId> Revisions, ArtifactClassification Classification, DateTimeOffset CreatedAt);

public sealed record ArtifactAggregate(
    ArtifactDescriptor Descriptor,
    ImmutableArray<ArtifactRevision> Revisions,
    ImmutableArray<ArtifactVerification> Verifications = default,
    ImmutableArray<ArtifactReview> Reviews = default,
    ImmutableArray<ArtifactApproval> Approvals = default,
    ImmutableArray<ArtifactIntegration> Integrations = default,
    ImmutableArray<ArtifactPublication> Publications = default)
{
    public ImmutableArray<ArtifactVerification> SafeVerifications => Verifications.IsDefault ? [] : Verifications;
    public ImmutableArray<ArtifactReview> SafeReviews => Reviews.IsDefault ? [] : Reviews;
    public ImmutableArray<ArtifactApproval> SafeApprovals => Approvals.IsDefault ? [] : Approvals;
    public ImmutableArray<ArtifactIntegration> SafeIntegrations => Integrations.IsDefault ? [] : Integrations;
    public ImmutableArray<ArtifactPublication> SafePublications => Publications.IsDefault ? [] : Publications;
    public ArtifactRevision CurrentRevision => Revisions.Single(item => item.Id == Descriptor.CurrentRevision);
}

public sealed record ArtifactQuery(string? Text = null, ArtifactKind? Kind = null, ArtifactState? State = null, string? ProjectId = null, PrincipalId? Producer = null, int Offset = 0, int Limit = 100);
public sealed record ArtifactStoreIntegrity(int ArtifactCount, int RevisionCount, int MissingBlobs, int CorruptBlobs, int OrphanBlobs);

public sealed record BuildArtifactMetadata(string Configuration, string Target, string Platform, string Architecture, string RuntimeVersion, string SourceRevision, int WarningCount, bool Succeeded);
public sealed record TestResultArtifactMetadata(string Suite, int Passed, int Failed, int Skipped, TimeSpan Duration, string Environment, ArtifactRevisionId SourceRevision);
public sealed record BenchmarkArtifactMetadata(string Benchmark, string Environment, ArtifactRevisionId InputRevision, ArtifactRevisionId? BaselineRevision, ImmutableDictionary<string, double> Results);
public sealed record ImageArtifactMetadata(int Width, int Height, string Format, string? ColorSpace = null);
public sealed record ReportClaim(string Claim, ImmutableArray<EvidenceId> EvidenceRefs, double Confidence);
public sealed record ReportArtifactMetadata(ImmutableArray<ReportClaim> Claims);
public sealed record MigrationArtifactMetadata(string Version, string SchemaBefore, string SchemaAfter, ImmutableArray<string> Operations, string? RollbackReference);

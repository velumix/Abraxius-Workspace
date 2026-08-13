using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Memory;
using Abraxius.Protocol;
using Abraxius.Voice;

namespace Abraxius.Debrief;

public readonly record struct DebriefId(Guid Value)
{
    public static DebriefId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct EpisodePlanId(Guid Value)
{
    public static EpisodePlanId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ChapterId(Guid Value)
{
    public static ChapterId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct DialogueTurnId(Guid Value)
{
    public static DialogueTurnId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct DebriefGenerationId(long Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public readonly record struct AudioSegmentId(Guid Value)
{
    public static AudioSegmentId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum DebriefMode
{
    Briefing,
    DeepDive,
    Postmortem,
    ArchitectureReview,
    Debate,
    TeachMe,
    ReleaseOverview,
    MissionReplay
}

public enum DebriefAudience { Beginner, Developer, Expert, Executive }
public enum DebriefState { Planning, Preparing, Playing, Paused, Interrupted, Regenerating, Completed, Cancelled, Failed }
public enum DebriefClaimStatus { Supported, Inferred, Contested, Unsupported, Stale }
public enum DebriefSpeechStyle { Composed, Investigative, Technical, Analytical }
public enum DebriefEventKind { Created, PlanningCompleted, ChapterReady, TurnReady, ClaimRejected, PlaybackStarted, PlaybackStopped, Paused, Resumed, Interrupted, LiveQuestion, Completed, Failed, Cancelled }

public sealed record DebriefSourceSet(
    string? ProjectKey = null,
    IReadOnlyList<ExecutionId>? ExecutionIds = null,
    IReadOnlyList<EvidenceId>? EvidenceIds = null,
    IReadOnlyList<MemoryId>? MemoryIds = null,
    IReadOnlyList<string>? GitRefs = null,
    IReadOnlyList<string>? UserNotes = null,
    string? Query = null)
{
    public IReadOnlyList<ExecutionId> SafeExecutionIds => ExecutionIds ?? Array.Empty<ExecutionId>();
    public IReadOnlyList<EvidenceId> SafeEvidenceIds => EvidenceIds ?? Array.Empty<EvidenceId>();
    public IReadOnlyList<MemoryId> SafeMemoryIds => MemoryIds ?? Array.Empty<MemoryId>();
    public IReadOnlyList<string> SafeGitRefs => GitRefs ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeUserNotes => UserNotes ?? Array.Empty<string>();
}

public sealed record SourceSnapshot(
    string SnapshotId,
    DebriefSourceSet Sources,
    DateTimeOffset CapturedAt,
    string ContentHash,
    IReadOnlyDictionary<string, string> SourceHashes,
    IReadOnlyList<MemoryId> ResolvedMemoryIds,
    IReadOnlyList<EvidenceId> ResolvedEvidenceIds)
{
    public bool IsEmpty => ResolvedMemoryIds.Count == 0 && ResolvedEvidenceIds.Count == 0;
}

public sealed record DebriefEvidence(
    string Id,
    string Title,
    string Content,
    MemoryId? MemoryId = null,
    IReadOnlyList<EvidenceId>? EvidenceIds = null,
    MemorySourceKind Source = MemorySourceKind.System,
    double Authority = 0.5,
    bool IsStale = false,
    string? SourcePath = null)
{
    public IReadOnlyList<EvidenceId> SafeEvidenceIds => EvidenceIds ?? Array.Empty<EvidenceId>();
}

public sealed record DebriefClaim(
    string ClaimId,
    string Statement,
    IReadOnlyList<string> EvidenceIds,
    double Confidence,
    double SourceAuthority,
    IReadOnlyList<SpecialistRole> SpeakerCandidates,
    DebriefClaimStatus Status,
    string? Explanation = null)
{
    public bool IsSpeakable => Status is DebriefClaimStatus.Supported or DebriefClaimStatus.Inferred;
}

public sealed record DebriefChapter(
    ChapterId Id,
    int Ordinal,
    string Title,
    string Objective,
    IReadOnlyList<SpecialistRole> Speakers,
    IReadOnlyList<string> ClaimIds,
    IReadOnlyList<ChapterId>? Dependencies = null,
    TimeSpan? DurationBudget = null)
{
    public IReadOnlyList<ChapterId> SafeDependencies => Dependencies ?? Array.Empty<ChapterId>();
    public TimeSpan EffectiveDurationBudget => DurationBudget ?? TimeSpan.FromMinutes(2);
}

public sealed record EpisodePlan(
    EpisodePlanId Id,
    DebriefMode Mode,
    string Title,
    string Objective,
    DebriefAudience Audience,
    TimeSpan TargetDuration,
    IReadOnlyList<SpecialistRole> Participants,
    IReadOnlyList<DebriefChapter> Chapters,
    IReadOnlyList<DebriefClaim> Claims,
    SourceSnapshot SourceSnapshot,
    DateTimeOffset CreatedAt,
    string TechnicalDepth = "standard")
{
    public IReadOnlyDictionary<string, DebriefClaim> ClaimMap => Claims.ToDictionary(static claim => claim.ClaimId, StringComparer.Ordinal);
}

public sealed record DialogueTurn(
    DialogueTurnId Id,
    ChapterId ChapterId,
    SpecialistRole Speaker,
    string SpeakerName,
    string Text,
    IReadOnlyList<string> ClaimIds,
    IReadOnlyList<EvidenceId> EvidenceRefs,
    DebriefSpeechStyle SpeechStyle,
    TimeSpan DurationEstimate,
    bool Interruptible = true,
    string? DisplayText = null,
    IReadOnlyList<string>? SourceRefs = null,
    DateTimeOffset? CreatedAt = null)
{
    public DateTimeOffset EffectiveCreatedAt => CreatedAt ?? DateTimeOffset.UtcNow;
    public IReadOnlyList<string> SafeSourceRefs => SourceRefs ?? Array.Empty<string>();
}

public sealed record CachedAudioSegment(
    AudioSegmentId Id,
    DialogueTurnId TurnId,
    string CacheKey,
    AudioFormat Format,
    IReadOnlyList<AudioFrame> Frames,
    TimeSpan Duration,
    DateTimeOffset CreatedAt);

public sealed record DebriefRequest(
    DebriefSourceSet Sources,
    DebriefMode Mode = DebriefMode.Briefing,
    string? Title = null,
    string? Objective = null,
    DebriefAudience Audience = DebriefAudience.Developer,
    TimeSpan? TargetDuration = null,
    IReadOnlyList<SpecialistRole>? Participants = null,
    string? Language = null,
    bool PrivateMode = false,
    bool GenerateAudio = true,
    string? VoiceLanguage = null,
    IReadOnlyDictionary<SpecialistRole, string>? VoiceProfiles = null,
    int ContextWindow = 16_000,
    int ReservedOutputTokens = 2_000)
{
    public TimeSpan EffectiveTargetDuration => TargetDuration ?? TimeSpan.FromMinutes(10);
    public IReadOnlyList<SpecialistRole> SafeParticipants => Participants ?? Array.Empty<SpecialistRole>();
    public string VoiceFor(SpecialistRole role) => VoiceProfiles is not null && VoiceProfiles.TryGetValue(role, out var voice) && !string.IsNullOrWhiteSpace(voice)
        ? voice
        : role switch
        {
            SpecialistRole.Coordinator => "athena",
            SpecialistRole.Investigator => "orion",
            SpecialistRole.Builder => "daedalus",
            SpecialistRole.Verifier => "argus",
            _ => role.ToString().ToLowerInvariant()
        };
}

public sealed record DebriefOptions(
    int MaxClaims = 32,
    int MaxTurns = 256,
    int MaxQueuedTurns = 4,
    int MaxContextTokens = 16_000,
    bool UseModelNarration = false,
    bool RequireEvidence = true,
    TimeSpan? PlaybackLead = null)
{
    public TimeSpan EffectivePlaybackLead => PlaybackLead ?? TimeSpan.FromMinutes(3);
}

public sealed record DebriefEvent(
    DebriefEventKind Kind,
    DebriefId DebriefId,
    DateTimeOffset Timestamp,
    DebriefState State,
    string? Detail = null,
    DialogueTurn? Turn = null,
    DebriefClaim? Claim = null,
    EpisodePlan? Plan = null,
    object? Payload = null);

public sealed class DebriefSession
{
    private readonly List<DialogueTurn> _turns = [];
    private long _generation;

    internal DebriefSession(DebriefId id, DebriefRequest request, EpisodePlan plan)
    {
        Id = id;
        Request = request;
        Plan = plan;
        State = DebriefState.Planning;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public DebriefId Id { get; }
    public DebriefRequest Request { get; }
    public EpisodePlan Plan { get; private set; }
    public DebriefState State { get; internal set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; internal set; }
    public ChapterId? CurrentChapter { get; internal set; }
    public int CurrentTurnIndex { get; internal set; }
    public IReadOnlyList<DialogueTurn> Turns => _turns;
    public DebriefGenerationId Generation => new(Volatile.Read(ref _generation));
    public bool IsSourceStale { get; internal set; }

    internal void AddTurn(DialogueTurn turn)
    {
        _turns.Add(turn);
        CurrentTurnIndex = _turns.Count - 1;
    }

    internal void ReplacePlan(EpisodePlan plan) => Plan = plan;
    internal DebriefGenerationId AdvanceGeneration() => new(Interlocked.Increment(ref _generation));
    internal void ResetGeneration() => Interlocked.Exchange(ref _generation, 0);

    public static DebriefSession Restore(DebriefSessionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var session = new DebriefSession(snapshot.Id, snapshot.Request, snapshot.Plan)
        {
            CreatedAt = snapshot.CreatedAt,
            State = snapshot.State,
            CompletedAt = snapshot.CompletedAt,
            CurrentChapter = snapshot.CurrentChapter,
            CurrentTurnIndex = snapshot.CurrentTurnIndex,
            IsSourceStale = snapshot.IsSourceStale
        };
        session._turns.AddRange(snapshot.Turns);
        session.CurrentTurnIndex = Math.Clamp(snapshot.CurrentTurnIndex, -1, session._turns.Count - 1);
        return session;
    }
}

public sealed record DebriefSessionSnapshot(
    DebriefId Id,
    DebriefRequest Request,
    EpisodePlan Plan,
    DebriefState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    ChapterId? CurrentChapter,
    int CurrentTurnIndex,
    IReadOnlyList<DialogueTurn> Turns,
    bool IsSourceStale);

public sealed record DebriefResult(
    DebriefSession Session,
    bool Succeeded,
    string Summary,
    TimeSpan Duration,
    int SupportedClaims,
    int RejectedClaims,
    int AudioSegments,
    int PlaybackUnderruns = 0);

public sealed record DebriefLiveQuestion(
    string Text,
    SpecialistRole? ExplicitSpeaker = null,
    bool ResumeAfterAnswer = true);

public sealed record DebriefLiveAnswer(
    DialogueTurn Turn,
    IReadOnlyList<EvidenceId> Evidence,
    bool Resumed,
    string Summary);

public interface IDebriefSourceResolver
{
    ValueTask<DebriefSourceResolution> ResolveAsync(DebriefSourceSet sources, string query, int limit, CancellationToken cancellationToken = default);
}

public sealed record DebriefSourceResolution(
    SourceSnapshot Snapshot,
    IReadOnlyList<DebriefEvidence> Evidence,
    IReadOnlyList<MemoryConflict> Conflicts,
    TimeSpan RetrievalLatency);

public interface IDebriefPlanner
{
    ValueTask<EpisodePlan> PlanAsync(DebriefRequest request, CancellationToken cancellationToken = default);
}

public interface IDebriefDialogueComposer
{
    ValueTask<IReadOnlyList<DialogueTurn>> ComposeAsync(EpisodePlan plan, DebriefChapter chapter, IReadOnlyList<DialogueTurn> priorTurns, CancellationToken cancellationToken = default);
}

public interface IDebriefGroundingPolicy
{
    IReadOnlyList<DialogueTurn> Verify(EpisodePlan plan, DebriefChapter chapter, IReadOnlyList<DialogueTurn> turns, out IReadOnlyList<DebriefClaim> rejectedClaims);
}

public interface IDebriefAudioCache
{
    ValueTask<CachedAudioSegment?> GetAsync(string cacheKey, CancellationToken cancellationToken = default);
    ValueTask PutAsync(CachedAudioSegment segment, CancellationToken cancellationToken = default);
    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

public interface IDebriefSessionStore
{
    ValueTask SaveAsync(DebriefSessionSnapshot snapshot, CancellationToken cancellationToken = default);
    ValueTask<DebriefSessionSnapshot?> GetAsync(DebriefId id, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DebriefSessionSnapshot>> ListAsync(CancellationToken cancellationToken = default);
}

public interface IDebriefEngine : IAsyncDisposable
{
    DebriefEventHub Events { get; }
    ValueTask<DebriefSession> CreateAsync(DebriefRequest request, CancellationToken cancellationToken = default);
    Task<DebriefResult> PlayAsync(DebriefSession session, CancellationToken cancellationToken = default);
    ValueTask PauseAsync(DebriefSession session, CancellationToken cancellationToken = default);
    ValueTask ResumeAsync(DebriefSession session, CancellationToken cancellationToken = default);
    ValueTask SkipToChapterAsync(DebriefSession session, ChapterId chapterId, CancellationToken cancellationToken = default);
    ValueTask<DebriefLiveAnswer> AskAsync(DebriefSession session, DebriefLiveQuestion question, CancellationToken cancellationToken = default);
    ValueTask CancelAsync(DebriefSession session, CancellationToken cancellationToken = default);
    ValueTask SaveAsync(DebriefSession session, CancellationToken cancellationToken = default);
    ValueTask<DebriefSession?> RestoreAsync(DebriefId id, CancellationToken cancellationToken = default);
    ValueTask<IReadOnlyList<DebriefSessionSnapshot>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask ExportTranscriptAsync(DebriefSession session, Stream destination, CancellationToken cancellationToken = default);
    ValueTask ExportAudioAsync(DebriefSession session, Stream destination, CancellationToken cancellationToken = default);
}

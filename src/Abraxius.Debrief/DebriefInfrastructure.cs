using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Abraxius.Agents;
using Abraxius.Memory;

namespace Abraxius.Debrief;

public sealed class DebriefEventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<DebriefEvent>> _subscriptions = new();
    private readonly int _capacity;

    public DebriefEventHub(int capacity = 512) => _capacity = Math.Max(32, capacity);

    public DebriefEventSubscription Subscribe()
    {
        var key = Guid.NewGuid();
        var channel = Channel.CreateBounded<DebriefEvent>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false
        });
        _subscriptions[key] = channel;
        return new DebriefEventSubscription(channel.Reader, () =>
        {
            if (_subscriptions.TryRemove(key, out var removed)) removed.Writer.TryComplete();
            return ValueTask.CompletedTask;
        });
    }

    public void Publish(DebriefEvent value)
    {
        foreach (var channel in _subscriptions.Values) channel.Writer.TryWrite(value);
    }
}

public sealed class DebriefEventSubscription : IAsyncDisposable
{
    private readonly ChannelReader<DebriefEvent> _reader;
    private readonly Func<ValueTask> _dispose;

    internal DebriefEventSubscription(ChannelReader<DebriefEvent> reader, Func<ValueTask> dispose)
    {
        _reader = reader;
        _dispose = dispose;
    }

    public IAsyncEnumerable<DebriefEvent> ReadAllAsync(CancellationToken cancellationToken = default) => _reader.ReadAllAsync(cancellationToken);
    public ValueTask DisposeAsync() => _dispose();
}

public sealed class MemoryDebriefSourceResolver(IHybridMemoryRetriever retriever) : IDebriefSourceResolver
{
    private readonly IHybridMemoryRetriever _retriever = retriever ?? throw new ArgumentNullException(nameof(retriever));

    public async ValueTask<DebriefSourceResolution> ResolveAsync(
        DebriefSourceSet sources,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var retrieval = await _retriever.RetrieveAsync(new MemorySearchQuery(
            string.IsNullOrWhiteSpace(query) ? sources.Query ?? "project mission" : query,
            Math.Clamp(limit, 1, 64),
            sources.ProjectKey,
            Mode: MemoryRetrievalMode.Hybrid,
            MaximumPrivacy: MemoryPrivacyClass.Sensitive), cancellationToken).ConfigureAwait(false);

        var selectedMemoryIds = sources.SafeMemoryIds.ToHashSet();
        var hits = retrieval.Hits
            .Where(hit => selectedMemoryIds.Count == 0 || selectedMemoryIds.Contains(hit.Entry.Id))
            .ToArray();
        var evidence = hits.Select(hit => new DebriefEvidence(
            $"m:{hit.Entry.Id}",
            hit.Entry.Title,
            Normalize(hit.Entry.Content),
            hit.Entry.Id,
            hit.Entry.Evidence.Where(static link => link.EvidenceId.HasValue).Select(static link => link.EvidenceId!.Value).ToArray(),
            hit.Entry.Provenance.Source,
            hit.Entry.Provenance.ClampedConfidence,
            hit.IsStale,
            hit.Entry.Provenance.SourcePath)).ToList();

        foreach (var note in sources.SafeUserNotes.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            evidence.Add(new DebriefEvidence($"note:{evidence.Count}", "User note", Normalize(note), Source: MemorySourceKind.User, Authority: 0.9));
        }

        var sourceHashes = evidence
            .Where(static item => item.SourcePath is not null)
            .ToDictionary(
                static item => item.SourcePath!,
                static item => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Content))).ToLowerInvariant(),
                StringComparer.Ordinal);
        var resolvedEvidenceIds = evidence.SelectMany(static item => item.SafeEvidenceIds).Concat(sources.SafeEvidenceIds).Distinct().ToArray();
        var resolvedMemoryIds = evidence.Where(static item => item.MemoryId.HasValue).Select(static item => item.MemoryId!.Value).Distinct().ToArray();
        var snapshotInput = string.Join('|',
            sources.ProjectKey,
            string.Join(',', sources.SafeExecutionIds),
            string.Join(',', resolvedMemoryIds),
            string.Join(',', resolvedEvidenceIds),
            string.Join('|', sources.SafeGitRefs),
            string.Join('|', evidence.Select(static item => $"{item.Id}:{item.Content}")));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(snapshotInput))).ToLowerInvariant();
        var snapshot = new SourceSnapshot(hash[..16], sources, DateTimeOffset.UtcNow, hash, sourceHashes, resolvedMemoryIds, resolvedEvidenceIds);
        return new DebriefSourceResolution(snapshot, evidence, retrieval.Conflicts, retrieval.Latency);
    }

    private static string Normalize(string content) => string.Join(' ', content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
}

public sealed class DeterministicDebriefPlanner(IDebriefSourceResolver resolver, DebriefOptions? options = null) : IDebriefPlanner
{
    private readonly IDebriefSourceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly DebriefOptions _options = options ?? new DebriefOptions();

    public async ValueTask<EpisodePlan> PlanAsync(DebriefRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var objective = string.IsNullOrWhiteSpace(request.Objective)
            ? request.Sources.Query ?? "Explain the selected Abraxius material"
            : request.Objective.Trim();
        var resolution = await _resolver.ResolveAsync(request.Sources, objective, _options.MaxClaims, cancellationToken).ConfigureAwait(false);
        var claims = BuildClaims(resolution.Evidence, resolution.Conflicts, request.Mode, request.SafeParticipants, _options.MaxClaims);
        var participants = SelectParticipants(request, claims);
        var chapters = BuildChapters(request.Mode, participants, claims, request.EffectiveTargetDuration);
        var title = string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle(request.Mode, objective) : request.Title.Trim();
        return new EpisodePlan(
            EpisodePlanId.New(), request.Mode, title, objective, request.Audience, request.EffectiveTargetDuration,
            participants, chapters, claims, resolution.Snapshot, DateTimeOffset.UtcNow,
            request.Audience == DebriefAudience.Expert ? "expert" : request.Audience.ToString().ToLowerInvariant());
    }

    private static DebriefClaim[] BuildClaims(
        IReadOnlyList<DebriefEvidence> evidence,
        IReadOnlyList<MemoryConflict> conflicts,
        DebriefMode mode,
        IReadOnlyList<SpecialistRole> requested,
        int maxClaims)
    {
        var conflictIds = conflicts.SelectMany(static conflict => conflict.Entries).Select(static id => $"m:{id}").ToHashSet(StringComparer.Ordinal);
        return evidence.Take(maxClaims).Select((item, index) =>
        {
            var ids = item.SafeEvidenceIds.Select(static id => $"e:{id}").ToList();
            ids.Add(item.Id);
            var status = item.IsStale
                ? DebriefClaimStatus.Stale
                : conflictIds.Contains(item.Id)
                    ? DebriefClaimStatus.Contested
                    : item.Source is MemorySourceKind.SourceCode or MemorySourceKind.ToolResult or MemorySourceKind.VerifiedExecution or MemorySourceKind.User
                        ? DebriefClaimStatus.Supported
                        : DebriefClaimStatus.Inferred;
            return new DebriefClaim(
                $"cl-{index + 1}", item.Content, ids, Math.Clamp(item.Authority, 0, 1), Math.Clamp(item.Authority, 0, 1),
                CandidateSpeakers(item, mode, requested), status,
                item.IsStale ? "The source hash is stale." : item.SourcePath is null ? "Supported by a scoped memory entry." : $"Source: {item.SourcePath}");
        }).ToArray();
    }

    private static IReadOnlyList<SpecialistRole> CandidateSpeakers(DebriefEvidence evidence, DebriefMode mode, IReadOnlyList<SpecialistRole> requested)
    {
        if (requested.Count > 0) return requested;
        if (mode == DebriefMode.Postmortem && evidence.Content.Contains("fail", StringComparison.OrdinalIgnoreCase)) return [SpecialistRole.Investigator, SpecialistRole.Verifier];
        if (evidence.Source is MemorySourceKind.VerifiedExecution or MemorySourceKind.ToolResult) return [SpecialistRole.Verifier, SpecialistRole.Investigator];
        if (evidence.Source is MemorySourceKind.SourceCode or MemorySourceKind.Documentation) return [SpecialistRole.Builder, SpecialistRole.Investigator];
        return [SpecialistRole.Investigator, SpecialistRole.Coordinator];
    }

    private static SpecialistRole[] SelectParticipants(DebriefRequest request, DebriefClaim[] claims)
    {
        if (request.SafeParticipants.Count > 0) return request.SafeParticipants.Distinct().ToArray();
        var roles = new List<SpecialistRole> { SpecialistRole.Coordinator };
        switch (request.Mode)
        {
            case DebriefMode.Briefing: roles.Add(SpecialistRole.Investigator); break;
            case DebriefMode.ArchitectureReview: roles.AddRange([SpecialistRole.Builder, SpecialistRole.Verifier]); break;
            case DebriefMode.Postmortem: roles.AddRange([SpecialistRole.Investigator, SpecialistRole.Verifier]); break;
            case DebriefMode.Debate: roles.AddRange([SpecialistRole.Investigator, SpecialistRole.Builder, SpecialistRole.Verifier]); break;
            default: roles.AddRange([SpecialistRole.Investigator, SpecialistRole.Builder, SpecialistRole.Verifier]); break;
        }
        return roles.Distinct().Where(role => claims.Length > 0 || role == SpecialistRole.Coordinator).ToArray();
    }

    private static List<DebriefChapter> BuildChapters(DebriefMode mode, IReadOnlyList<SpecialistRole> participants, DebriefClaim[] claims, TimeSpan duration)
    {
        var labels = mode switch
        {
            DebriefMode.Briefing => new[] { ("Overview", "Frame the material and outcome."), ("Evidence", "Explain the strongest source-grounded findings."), ("Conclusion", "Summarize implications and uncertainty.") },
            DebriefMode.Postmortem => new[] { ("What happened", "Establish the observed failure."), ("Investigation", "Trace the evidence and failed approaches."), ("Verification", "State what is and is not proven."), ("Lessons", "Capture durable lessons without inventing causes.") },
            DebriefMode.ArchitectureReview => new[] { ("Design", "Describe the architecture represented by the sources."), ("Tradeoffs", "Discuss supported design consequences."), ("Risks", "Identify verified risks and open questions.") },
            DebriefMode.MissionReplay => new[] { ("Mission start", "Replay the mission objective and initial state."), ("Specialist work", "Describe evidence, implementation, and verification."), ("Outcome", "Report the actual mission result.") },
            _ => new[] { ("Orientation", "Introduce the objective and source snapshot."), ("Investigation", "Walk through the strongest evidence."), ("Engineering view", "Explain implementation or architectural meaning."), ("Verification", "Separate proof from inference."), ("Takeaways", "Close with concise, source-grounded conclusions.") }
        };
        var perChapter = Math.Max(1, claims.Length / Math.Max(1, labels.Length));
        var result = new List<DebriefChapter>(labels.Length);
        for (var index = 0; index < labels.Length; index++)
        {
            var claimIds = claims.Skip(index * perChapter).Take(index == labels.Length - 1 ? int.MaxValue : perChapter).Select(static claim => claim.ClaimId).ToArray();
            var speakers = participants.Where(role => index == 0 ? role is SpecialistRole.Coordinator or SpecialistRole.Investigator : true).ToArray();
            if (speakers.Length == 0) speakers = participants.ToArray();
            result.Add(new DebriefChapter(ChapterId.New(), index + 1, labels[index].Item1, labels[index].Item2, speakers, claimIds, index == 0 ? [] : [result[^1].Id], TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(1).Ticks, duration.Ticks / labels.Length))));
        }
        return result;
    }

    private static string DefaultTitle(DebriefMode mode, string objective) => $"{mode}: {objective[..Math.Min(80, objective.Length)]}";
}

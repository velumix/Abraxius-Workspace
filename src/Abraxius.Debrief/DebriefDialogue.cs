using System.Collections.Concurrent;
using System.Collections.Immutable;
using Abraxius.Axl;
using Abraxius.Agents;
using Abraxius.Protocol;
using Abraxius.Voice;

namespace Abraxius.Debrief;

public sealed class GroundedDebriefDialogueComposer : IDebriefDialogueComposer
{
    private static readonly Dictionary<SpecialistRole, (string Name, DebriefSpeechStyle Style)> Speakers = new()
    {
        [SpecialistRole.Coordinator] = ("Athena", DebriefSpeechStyle.Composed),
        [SpecialistRole.Investigator] = ("Orion", DebriefSpeechStyle.Investigative),
        [SpecialistRole.Builder] = ("Daedalus", DebriefSpeechStyle.Technical),
        [SpecialistRole.Verifier] = ("Argus", DebriefSpeechStyle.Analytical),
        [SpecialistRole.DomainExpert] = ("Expert", DebriefSpeechStyle.Technical)
    };

    public ValueTask<IReadOnlyList<DialogueTurn>> ComposeAsync(
        EpisodePlan plan,
        DebriefChapter chapter,
        IReadOnlyList<DialogueTurn> priorTurns,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var claims = chapter.ClaimIds
            .Select(id => plan.ClaimMap.TryGetValue(id, out var claim) ? claim : null)
            .Where(static claim => claim is not null)
            .Cast<DebriefClaim>()
            .Where(static claim => claim.IsSpeakable)
            .ToArray();
        var turns = new List<DialogueTurn>();
        var openingRole = chapter.Speakers.FirstOrDefault(SpecialistRole.Coordinator);
        AddTurn(turns, chapter, openingRole, $"This section covers {chapter.Objective.ToLowerInvariant()}", [], []);

        if (claims.Length == 0)
        {
            var verifier = chapter.Speakers.FirstOrDefault(SpecialistRole.Verifier);
            AddTurn(turns, chapter, verifier, "The available sources do not establish a supported factual claim for this section, so we are leaving that point open.", [], []);
        }
        else
        {
            for (var index = 0; index < claims.Length; index++)
            {
                var claim = claims[index];
                var role = claim.SpeakerCandidates.FirstOrDefault(chapter.Speakers.Contains);
                if (!chapter.Speakers.Contains(role)) role = chapter.Speakers[Math.Min(index, chapter.Speakers.Count - 1)];
                var lead = index == 0 ? "The strongest supported point is" : "A related source-grounded point is";
                var qualifier = claim.Status == DebriefClaimStatus.Inferred ? "The available evidence suggests" : lead;
                AddTurn(turns, chapter, role, $"{qualifier}: {claim.Statement}", [claim.ClaimId], claim.EvidenceIds);
            }
        }

        var conclusionRole = chapter.Speakers.Contains(SpecialistRole.Coordinator) ? SpecialistRole.Coordinator : chapter.Speakers[^1];
        AddTurn(turns, chapter, conclusionRole,
            claims.Length == 0 ? "We should not treat this section as resolved." : "That is the evidence-backed conclusion for this section; the references remain available in the evidence panel.",
            [], []);
        return ValueTask.FromResult<IReadOnlyList<DialogueTurn>>(turns);
    }

    private static void AddTurn(List<DialogueTurn> turns, DebriefChapter chapter, SpecialistRole role, string text, IReadOnlyList<string> claims, IReadOnlyList<string> refs)
    {
        var evidence = refs
            .Where(static value => value.StartsWith("e:", StringComparison.Ordinal))
            .Select(static value => value[2..])
            .Where(static value => EvidenceId.TryParse(value, out _))
            .Select(static value => new EvidenceId(Guid.Parse(value)))
            .ToArray();
        var speaker = Speakers.TryGetValue(role, out var profile) ? profile : (role.ToString(), DebriefSpeechStyle.Composed);
        turns.Add(new DialogueTurn(
            DialogueTurnId.New(), chapter.Id, role, speaker.Item1, text, claims, evidence, speaker.Item2,
            TimeSpan.FromSeconds(Math.Max(2, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length / 2.5)),
            SourceRefs: refs, DisplayText: text));
    }
}

public sealed class DeterministicDebriefGroundingPolicy(bool requireEvidence = true) : IDebriefGroundingPolicy
{
    private readonly bool _requireEvidence = requireEvidence;

    public IReadOnlyList<DialogueTurn> Verify(
        EpisodePlan plan,
        DebriefChapter chapter,
        IReadOnlyList<DialogueTurn> turns,
        out IReadOnlyList<DebriefClaim> rejectedClaims)
    {
        var claims = plan.ClaimMap;
        var rejected = new List<DebriefClaim>();
        var accepted = new List<DialogueTurn>();
        foreach (var turn in turns)
        {
            var validClaimIds = turn.ClaimIds
                .Where(id => claims.TryGetValue(id, out var claim) && claim.IsSpeakable && (!_requireEvidence || claim.EvidenceIds.Count > 0))
                .ToArray();
            foreach (var id in turn.ClaimIds.Except(validClaimIds))
            {
                if (claims.TryGetValue(id, out var rejectedClaim)) rejected.Add(rejectedClaim with { Status = DebriefClaimStatus.Unsupported });
            }
            if (turn.ClaimIds.Count > 0 && validClaimIds.Length == 0) continue;
            accepted.Add(turn with { ClaimIds = validClaimIds });
        }
        rejectedClaims = rejected;
        return accepted;
    }
}

public sealed class InMemoryDebriefAudioCache(int maximumSegments = 128) : IDebriefAudioCache
{
    private readonly ConcurrentDictionary<string, CachedAudioSegment> _segments = new(StringComparer.Ordinal);
    private readonly int _maximumSegments = Math.Max(4, maximumSegments);

    public ValueTask<CachedAudioSegment?> GetAsync(string cacheKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_segments.TryGetValue(cacheKey, out var segment) ? segment : null);
    }

    public ValueTask PutAsync(CachedAudioSegment segment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _segments[segment.CacheKey] = segment;
        while (_segments.Count > _maximumSegments)
        {
            var oldest = _segments.Values.OrderBy(static item => item.CreatedAt).FirstOrDefault();
            if (oldest is null || !_segments.TryRemove(oldest.CacheKey, out _)) break;
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _segments.Clear();
        return ValueTask.CompletedTask;
    }
}

public sealed class InMemoryDebriefSessionStore : IDebriefSessionStore
{
    private readonly ConcurrentDictionary<DebriefId, DebriefSessionSnapshot> _sessions = new();

    public ValueTask SaveAsync(DebriefSessionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sessions[snapshot.Id] = snapshot;
        return ValueTask.CompletedTask;
    }

    public ValueTask<DebriefSessionSnapshot?> GetAsync(DebriefId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_sessions.TryGetValue(id, out var value) ? value : null);
    }

    public ValueTask<IReadOnlyList<DebriefSessionSnapshot>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<DebriefSessionSnapshot>>(_sessions.Values.OrderByDescending(static item => item.CreatedAt).ToArray());
    }
}

public static class DebriefAxlProjection
{
    public static string Format(EpisodePlan plan)
    {
        var commands = new List<AxlCommand>
        {
            new AxlIntent(plan.Objective, WorkPriority.Interactive, CommandId: new AxlCommandId("debrief"))
        };
        var previous = new List<AxlReference>();
        foreach (var chapter in plan.Chapters)
        {
            var refs = previous.Count == 0
                ? ImmutableArray<AxlReference>.Empty
                : ImmutableArray.Create(previous[^1]);
            var claimSummary = chapter.ClaimIds.Count == 0 ? "no claim refs" : $"claims={string.Join(',', chapter.ClaimIds)}";
            commands.Add(new AxlSynthesis($"debrief.chapter {chapter.Ordinal}: {chapter.Title} {claimSummary}", refs, new AxlCommandId($"ch{chapter.Ordinal}")));
            previous.Add(new AxlReference(AxlReferenceKind.Command, $"ch{chapter.Ordinal}"));
        }
        commands.Add(new AxlVerification("verify grounded debrief claims", previous.ToImmutableArray(), "debrief", new AxlCommandId("verify")));
        return AxlFormatter.Compact(new AxlDocument(AxlVersion.Current, commands.ToImmutableArray()));
    }
}

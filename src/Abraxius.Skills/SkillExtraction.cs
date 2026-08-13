using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Protocol;

namespace Abraxius.Skills;

public sealed record SkillExtractionRequest(
    MissionId MissionId,
    string Objective,
    IReadOnlyList<string> SuccessfulSteps,
    IReadOnlyList<EvidenceId> Evidence,
    IReadOnlyList<string> VerificationCriteria,
    string? ProjectKey = null,
    SpecialistRole? PreferredRole = null,
    bool UserRequested = false);

public sealed record SkillCandidate(
    SkillCandidateId Id,
    SkillDefinition Definition,
    IReadOnlyList<string> Reasons,
    DateTimeOffset CreatedAt,
    bool RequiresReview = true);

public interface ISkillCandidateExtractor
{
    ValueTask<SkillCandidate?> ExtractAsync(SkillExtractionRequest request, CancellationToken cancellationToken = default);
}

public sealed class DeterministicSkillCandidateExtractor : ISkillCandidateExtractor
{
    public ValueTask<SkillCandidate?> ExtractAsync(SkillExtractionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.UserRequested && request.SuccessfulSteps.Count < 2) return ValueTask.FromResult<SkillCandidate?>(null);
        if (request.Evidence.Count == 0 || request.VerificationCriteria.Count == 0) return ValueTask.FromResult<SkillCandidate?>(null);
        var slug = Slug(request.Objective);
        if (slug.Length == 0) return ValueTask.FromResult<SkillCandidate?>(null);
        var id = new SkillId($"learned.{slug}");
        var steps = request.SuccessfulSteps.Select((label, index) => new SkillSpecialistAssignmentStep(
            new SkillStepId($"step-{index + 1}"),
            label,
            request.PreferredRole ?? SpecialistRole.Coordinator,
            label,
            ["Complete the extracted procedure step."],
            index == 0 ? [] : [new SkillStepId($"step-{index}")])).Cast<SkillStep>().ToArray();
        var definition = new SkillDefinition
        {
            Id = id,
            Version = SkillVersion.Initial,
            Name = id.Value,
            Description = $"Candidate procedure extracted from verified mission {request.MissionId}.",
            Category = SkillCategory.General,
            State = SkillLifecycleState.Candidate,
            Origin = SkillOrigin.ExtractedFromMission,
            Triggers = new SkillTriggerSet(Concepts: request.Objective.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(8).ToArray()),
            Procedure = new SkillProcedure(steps),
            Verification = new SkillVerificationPlan(request.VerificationCriteria),
            SpecialistPolicy = new SkillSpecialistPolicy(request.PreferredRole),
            Provenance = new SkillProvenance(SkillOrigin.ExtractedFromMission, SourceMissions: [request.MissionId], SourceEvidence: request.Evidence, CandidateId: SkillCandidateId.New()),
            Preconditions = new SkillPreconditions(Scope: string.IsNullOrWhiteSpace(request.ProjectKey) ? null : new SkillScope(SkillScopeKind.Project, request.ProjectKey))
        };
        return ValueTask.FromResult<SkillCandidate?>(new SkillCandidate(SkillCandidateId.New(), definition, ["mission completed with verified evidence", "procedure contained multiple ordered steps"], DateTimeOffset.UtcNow));
    }

    private static string Slug(string value)
    {
        var chars = value.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : '-').ToArray();
        return new string(chars).Trim('-').Replace("--", "-", StringComparison.Ordinal);
    }
}

public sealed class SkillCandidateStore
{
    private readonly List<SkillCandidate> _candidates = [];
    private readonly object _gate = new();

    public IReadOnlyList<SkillCandidate> List()
    {
        lock (_gate) return _candidates.ToArray();
    }

    public void Add(SkillCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (_gate)
        {
            if (_candidates.Any(item => item.Definition.Id == candidate.Definition.Id && item.Definition.Version == candidate.Definition.Version)) return;
            _candidates.Add(candidate);
        }
    }
}

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Abraxius.Agents;
using Abraxius.Models;
using Abraxius.Progression;
using Abraxius.Skills;

namespace Abraxius.Runtime;

internal static class RuntimeProgressionProjection
{
    public static ProgressionTrajectory FromMission(
        MissionResult result,
        IReadOnlyDictionary<AssignmentId, AgentAssignment> assignments,
        IntelligenceTier? routeTier,
        SkillExecutionResult? skillResult = null,
        SkillDefinition? skill = null)
    {
        var missionAssignments = assignments.Values.Where(item => item.MissionId == result.Mission.Id).ToArray();
        var contributions = missionAssignments.GroupBy(static item => item.Role).Select(group =>
        {
            var roleResults = group.Select(item => result.AssignmentResults.GetValueOrDefault(item.Id)).Where(static item => item is not null).Cast<AgentAssignmentResult>().ToArray();
            var defects = group.Key == SpecialistRole.Verifier ? roleResults.Count(static item => !item.Succeeded || item.Verification == VerificationStatus.Failed) : 0;
            var categories = group.Key switch
            {
                SpecialistRole.Coordinator => ImmutableArray.Create(MasteryCategory.MissionPlanning, MasteryCategory.Delegation),
                SpecialistRole.Investigator => ImmutableArray.Create(MasteryCategory.SourceRecon, MasteryCategory.EvidenceFusion, MasteryCategory.RootCause),
                SpecialistRole.Builder => ImmutableArray.Create(MasteryCategory.Implementation, group.Any(static item => item.Attempt > 0) ? MasteryCategory.Repair : MasteryCategory.Architecture),
                SpecialistRole.Verifier => ImmutableArray.Create(MasteryCategory.Tests, MasteryCategory.Requirements, MasteryCategory.Regression),
                _ => ImmutableArray<MasteryCategory>.Empty
            };
            return new SpecialistContributionFacts(group.Key, roleResults.Count(static item => item.Succeeded), roleResults.Count(static item => !item.Succeeded),
                roleResults.SelectMany(static item => item.SafeEvidence).Distinct().Count(),
                group.Key == SpecialistRole.Investigator && roleResults.Any(static item => item.Succeeded && item.SafeEvidence.Count > 0),
                group.Key == SpecialistRole.Builder && roleResults.Any(static item => item.Succeeded),
                group.Key == SpecialistRole.Verifier && roleResults.Any(static item => item.Verification is VerificationStatus.Passed),
                defects, group.Key == SpecialistRole.Builder ? group.Count(static item => item.Attempt > 0) : 0, categories);
        }).ToImmutableArray();
        if (skillResult is not null && skill is not null && contributions.Length == 0)
        {
            contributions = [new SpecialistContributionFacts(skill.SpecialistPolicy.PreferredRole ?? SpecialistRole.Coordinator, 1,
                IndependentVerification: skillResult.Verification == SkillVerificationOutcome.Passed,
                Categories: CategoriesFor(skill.SpecialistPolicy.PreferredRole ?? SpecialistRole.Coordinator))];
        }
        var evidence = result.Mission.SafeEvidence.Concat(result.AssignmentResults.Values.SelectMany(static item => item.SafeEvidence)).Distinct().ToImmutableArray();
        var verificationResults = result.AssignmentResults.Values.Where(static item => item.Verification is not null).ToArray();
        var verification = !result.Succeeded ? VerificationStrength.Unverified
            : skillResult?.Verification == SkillVerificationOutcome.Passed ? VerificationStrength.Verified
            : verificationResults.Any(static item => item.Verification == VerificationStatus.Passed) ? VerificationStrength.IndependentlyVerified
            : result.Mission.Intent.Constraints.RequireVerification ? VerificationStrength.Verified : VerificationStrength.Inconclusive;
        var skillUses = skillResult is null || skill is null ? ImmutableArray<SkillUseFacts>.Empty
            : [new SkillUseFacts(skillResult.SkillId, skillResult.Version, skillResult.Verification == SkillVerificationOutcome.Passed, true, skill.Statistics.Reliability, Environment.OSVersion.Platform.ToString())];
        var objectiveKey = Normalize(result.Mission.Intent.Objective);
        var scope = result.Mission.Intent.Scope ?? "local";
        var evidenceKey = string.Join('|', evidence.OrderBy(static item => item.Value).Select(static item => item.ToString()));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{objectiveKey}|{scope}|{evidenceKey}|{skillResult?.SkillId}"))).ToLowerInvariant()[..24];
        var meaningfulNodes = Math.Max(1, missionAssignments.Length + (skillResult is null ? 0 : 3));
        var parallelBranches = missionAssignments.Count(static item => item.Role == SpecialistRole.Investigator);
        var failedApproaches = result.AssignmentResults.Values.Count(static item => !item.Succeeded && item.MadeProgress);
        return new ProgressionTrajectory(new TrajectoryId(result.Mission.Id.Value), result.Mission.Id, result.Mission.Intent.Objective, fingerprint,
            result.Mission.CompletedAt ?? DateTimeOffset.UtcNow, result.Mission.State, RewardEligibility.Eligible, verification,
            meaningfulNodes, Math.Max(1, missionAssignments.Length - parallelBranches + 1), parallelBranches,
            result.Mission.Intent.SafeAttributes.TryGetValue("domains", out var domains) ? domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length : 1,
            missionAssignments.Count(static item => item.Role == SpecialistRole.Builder), missionAssignments.Count(static item => item.Role == SpecialistRole.Verifier),
            missionAssignments.Count(static item => item.Attempt > 0), failedApproaches, skill?.State == SkillLifecycleState.Trusted, false,
            routeTier is >= IntelligenceTier.Frontier, routeTier is IntelligenceTier.Deterministic or IntelligenceTier.Free or IntelligenceTier.Included,
            false, false, contributions, skillUses, evidence,
            result.Mission.Intent.SafeAttributes.TryGetValue("domains", out domains) ? domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray() : []);
    }

    private static string Normalize(string value) => string.Join(' ', value.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static ImmutableArray<MasteryCategory> CategoriesFor(SpecialistRole role) => role switch
    {
        SpecialistRole.Coordinator => [MasteryCategory.MissionPlanning], SpecialistRole.Investigator => [MasteryCategory.SourceRecon],
        SpecialistRole.Builder => [MasteryCategory.Implementation], SpecialistRole.Verifier => [MasteryCategory.Tests], _ => []
    };
}

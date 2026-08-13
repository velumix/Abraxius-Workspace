using System.Collections.Immutable;
using Abraxius.Axl;

namespace Abraxius.Skills;

public static class SkillAxlProjection
{
    public static AxlSkill ToAxl(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        return new AxlSkill(
            skill.Id.Value,
            skill.Version.ToString(),
            skill.Triggers.SafeConcepts.Concat(skill.Triggers.SafeTaskClasses).Concat(skill.Triggers.SafeErrorCodes).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            skill.Preconditions.SafeCapabilities.Concat(skill.CapabilityPolicy.SafeCapabilities).Select(static capability => capability.Value).Distinct(StringComparer.Ordinal).ToImmutableArray(),
            skill.Procedure.SafeSteps.Select(static step => $"{step.Id}:{step.Kind}").ToImmutableArray(),
            skill.Verification.SafeCriteria.ToImmutableArray(),
            skill.CapabilityPolicy.Safety.ToString().ToLowerInvariant());
    }

    public static string Format(SkillDefinition skill, AxlFormatMode mode = AxlFormatMode.Compact) =>
        AxlFormatter.Format(new AxlDocument(AxlVersion.Current, [ToAxl(skill)]), mode);

    public static SkillValidationReport ValidateAxlProjection(SkillDefinition skill)
    {
        var text = Format(skill);
        var parsed = AxlPipeline.ParseAndValidate(text, options: new AxlValidationOptions(AllowMutations: true));
        if (parsed.IsSuccess) return new SkillValidationReport(true, Array.Empty<SkillDiagnostic>(), SkillValidationId.New(), DateTimeOffset.UtcNow);
        var diagnostics = parsed.Diagnostics.Select(item => new SkillDiagnostic(SkillDiagnosticSeverity.Error, $"AXL{(int)item.Code:000}", item.Message, Stage: SkillValidationStage.Structural)).ToArray();
        return new SkillValidationReport(false, diagnostics, SkillValidationId.New(), DateTimeOffset.UtcNow);
    }
}

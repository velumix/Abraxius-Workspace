using System.Collections.Immutable;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Abraxius.Agents;
using Abraxius.Axl;
using Abraxius.Memory;
using Abraxius.Protocol;

namespace Abraxius.Skills;

public readonly record struct SkillId
{
    private static readonly Regex Pattern = new("^[a-z0-9]+(?:[._-][a-z0-9]+)+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public SkillId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Pattern.IsMatch(value.Trim()))
        {
            throw new ArgumentException("Skill IDs must be lowercase namespaced identifiers such as git.regression-investigation.", nameof(value));
        }

        Value = value.Trim();
    }

    public string Value { get; }
    public override string ToString() => Value;
    public static implicit operator SkillId(string value) => new(value);
    public static bool TryParse(string? value, out SkillId id)
    {
        try
        {
            id = new SkillId(value ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            id = default;
            return false;
        }
    }
}

public readonly record struct SkillVersion(int Major, int Minor, int Patch, string? PreRelease = null) : IComparable<SkillVersion>
{
    public static SkillVersion Initial => new(1, 0, 0);

    public int CompareTo(SkillVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;
        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;
        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;
        if (string.IsNullOrEmpty(PreRelease) && string.IsNullOrEmpty(other.PreRelease)) return 0;
        if (string.IsNullOrEmpty(PreRelease)) return 1;
        if (string.IsNullOrEmpty(other.PreRelease)) return -1;
        return string.Compare(PreRelease, other.PreRelease, StringComparison.Ordinal);
    }

    public static bool operator <(SkillVersion left, SkillVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(SkillVersion left, SkillVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(SkillVersion left, SkillVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(SkillVersion left, SkillVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() => string.IsNullOrWhiteSpace(PreRelease) ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";

    public static bool TryParse(string? value, out SkillVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var parts = value.Trim().Split('-', 2, StringSplitOptions.None);
        var numeric = parts[0].Split('.', StringSplitOptions.None);
        if (numeric.Length != 3 || !int.TryParse(numeric[0], out var major) || !int.TryParse(numeric[1], out var minor) || !int.TryParse(numeric[2], out var patch) || major < 0 || minor < 0 || patch < 0) return false;
        version = new SkillVersion(major, minor, patch, parts.Length == 2 ? parts[1] : null);
        return true;
    }
}

public readonly record struct SkillRevisionId(Guid Value)
{
    public static SkillRevisionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct SkillExecutionId(Guid Value)
{
    public static SkillExecutionId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct SkillCandidateId(Guid Value)
{
    public static SkillCandidateId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct SkillValidationId(Guid Value)
{
    public static SkillValidationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public enum SkillLifecycleState { Candidate, Experimental, Validated, Trusted, Deprecated, Rejected, Disabled, NeedsRevalidation }
public enum SkillOrigin { BuiltIn, UserAuthored, ExtractedFromMission, Imported, PluginProvided, GeneratedCandidate }
public enum SkillCategory { General, Repository, Regression, Build, Verification, Release, Performance, Domain }
public enum SkillSafetyClass { ReadOnly, Mutation, Privileged, ExternalSideEffect }
public enum SkillScopeKind { Global, Language, Framework, Project, Repository }
public enum SkillStepKind { ContextQuery, CapabilityCall, SpecialistAssignment, Verification, Model, Conditional, Composition }
public enum SkillValueType { Text, WholeNumber, Boolean, Duration, ProjectReference, MemoryReference, EvidenceReference, ArtifactReference }
public enum SkillParameterKind { Text, WholeNumber, Boolean, Duration, Reference }
public enum SkillExecutionStatus { Succeeded, Failed, Blocked, Cancelled, DryRun }
public enum SkillValidationStage { Structural, Capability, Policy, Safety, Fixture, Replay, Outcome }
public enum SkillDiagnosticSeverity { Error, Warning, Info }
public enum SkillConditionKind { StepSucceeded, OutputExists, StatusEquals }

public sealed record SkillDiagnostic(
    SkillDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? StepId = null,
    SkillValidationStage Stage = SkillValidationStage.Structural);

public sealed record SkillScope(SkillScopeKind Kind, string Key)
{
    public bool Matches(string? projectKey, string? language, string? framework, string? repositoryKey)
    {
        if (string.IsNullOrWhiteSpace(Key)) return true;
        var candidate = Kind switch
        {
            SkillScopeKind.Project => projectKey,
            SkillScopeKind.Repository => repositoryKey,
            SkillScopeKind.Language => language,
            SkillScopeKind.Framework => framework,
            _ => null
        };
        return Kind == SkillScopeKind.Global || string.Equals(candidate, Key, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record SkillParameterValue(SkillParameterKind Kind, string? TextValue = null, long WholeNumber = 0, bool BooleanValue = false, TimeSpan? Duration = null)
{
    public static SkillParameterValue From(string value) => new(SkillParameterKind.Text, value);
    public static SkillParameterValue From(long value) => new(SkillParameterKind.WholeNumber, WholeNumber: value);
    public static SkillParameterValue From(bool value) => new(SkillParameterKind.Boolean, BooleanValue: value);
    public static SkillParameterValue From(TimeSpan value) => new(SkillParameterKind.Duration, Duration: value);
    public string AsText() => Kind switch
    {
        SkillParameterKind.Text or SkillParameterKind.Reference => TextValue ?? string.Empty,
        SkillParameterKind.WholeNumber => WholeNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
        SkillParameterKind.Boolean => BooleanValue ? "true" : "false",
        SkillParameterKind.Duration => Duration?.ToString() ?? string.Empty,
        _ => string.Empty
    };
}

public sealed record SkillParameterDefinition(
    string Name,
    SkillValueType Type,
    bool Required = true,
    SkillParameterValue? DefaultValue = null);

public sealed record SkillInputContract(IReadOnlyList<SkillParameterDefinition> Parameters)
{
    public IReadOnlyList<SkillParameterDefinition> SafeParameters => Parameters ?? Array.Empty<SkillParameterDefinition>();
}

public sealed record SkillOutputContract(IReadOnlyList<SkillParameterDefinition> Values)
{
    public IReadOnlyList<SkillParameterDefinition> SafeValues => Values ?? Array.Empty<SkillParameterDefinition>();
}

public sealed record SkillTriggerSet(
    IReadOnlyList<string>? TaskClasses = null,
    IReadOnlyList<string>? Concepts = null,
    IReadOnlyList<string>? ErrorCodes = null,
    IReadOnlyList<string>? Symbols = null,
    IReadOnlyList<string>? ProjectTypes = null,
    IReadOnlyList<string>? Frameworks = null,
    IReadOnlyList<CapabilityId>? RequiredCapabilities = null)
{
    public IReadOnlyList<string> SafeTaskClasses => TaskClasses ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeConcepts => Concepts ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeErrorCodes => ErrorCodes ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeSymbols => Symbols ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeProjectTypes => ProjectTypes ?? Array.Empty<string>();
    public IReadOnlyList<string> SafeFrameworks => Frameworks ?? Array.Empty<string>();
    public IReadOnlyList<CapabilityId> SafeRequiredCapabilities => RequiredCapabilities ?? Array.Empty<CapabilityId>();
}

public sealed record SkillPreconditions(
    IReadOnlyList<CapabilityId>? RequiredCapabilities = null,
    IReadOnlyList<SpecialistRole>? RequiredRoles = null,
    WorkspacePolicy? Workspace = null,
    bool RequiresGit = false,
    bool RequiresWritableWorkspace = false,
    string? Language = null,
    string? Framework = null,
    SkillScope? Scope = null)
{
    public IReadOnlyList<CapabilityId> SafeCapabilities => RequiredCapabilities ?? Array.Empty<CapabilityId>();
    public IReadOnlyList<SpecialistRole> SafeRoles => RequiredRoles ?? Array.Empty<SpecialistRole>();
}

public sealed record SkillCapabilityPolicy(
    IReadOnlyList<CapabilityId>? RequiredCapabilities = null,
    SkillSafetyClass Safety = SkillSafetyClass.ReadOnly,
    bool RequiresHumanApproval = false,
    bool RequiresIsolation = false)
{
    public IReadOnlyList<CapabilityId> SafeCapabilities => RequiredCapabilities ?? Array.Empty<CapabilityId>();
}

public sealed record SkillSpecialistPolicy(
    SpecialistRole? PreferredRole = null,
    IReadOnlyList<SpecialistRole>? AllowedRoles = null,
    int MaxParallelSpecialists = 4)
{
    public IReadOnlyList<SpecialistRole> SafeAllowedRoles => AllowedRoles ?? Array.Empty<SpecialistRole>();
    public bool Allows(SpecialistRole? role) => role is null || (SafeAllowedRoles.Count == 0 ? PreferredRole is null || PreferredRole == role : SafeAllowedRoles.Contains(role.Value));
}

public sealed record SkillResourceProfile(
    int MaxSteps = 64,
    int MaxConcurrentSteps = 4,
    TimeSpan? MaximumDuration = null,
    decimal MaximumCost = 0);

public sealed record SkillProvenance(
    SkillOrigin Origin,
    string? Creator = null,
    DateTimeOffset? CreatedAt = null,
    string? AbraxiusVersion = null,
    AxlVersion? AxlVersion = null,
    IReadOnlyList<MissionId>? SourceMissions = null,
    IReadOnlyList<EvidenceId>? SourceEvidence = null,
    SkillCandidateId? CandidateId = null,
    string? ContentHash = null)
{
    public IReadOnlyList<MissionId> SafeSourceMissions => SourceMissions ?? Array.Empty<MissionId>();
    public IReadOnlyList<EvidenceId> SafeSourceEvidence => SourceEvidence ?? Array.Empty<EvidenceId>();
}

public readonly record struct SkillStepId(string Value)
{
    public override string ToString() => Value;
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(SkillContextQueryStep), "context")]
[JsonDerivedType(typeof(SkillCapabilityCallStep), "capability")]
[JsonDerivedType(typeof(SkillSpecialistAssignmentStep), "specialist")]
[JsonDerivedType(typeof(SkillVerificationStep), "verification")]
[JsonDerivedType(typeof(SkillModelStep), "model")]
[JsonDerivedType(typeof(SkillConditionalStep), "conditional")]
[JsonDerivedType(typeof(SkillCompositionStep), "composition")]
public abstract record SkillStep(SkillStepId Id, string Label, ImmutableArray<SkillStepId> Dependencies)
{
    public abstract SkillStepKind Kind { get; }
    public ImmutableArray<SkillStepId> SafeDependencies => Dependencies.IsDefault ? ImmutableArray<SkillStepId>.Empty : Dependencies;
}

public sealed record SkillContextQueryStep(
    SkillStepId Id,
    string Label,
    string Query,
    MemoryRetrievalMode Mode = MemoryRetrievalMode.Hybrid,
    ImmutableArray<SkillStepId> Dependencies = default) : SkillStep(Id, Label, Dependencies)
{
    public override SkillStepKind Kind => SkillStepKind.ContextQuery;
}

public sealed record SkillCapabilityCallStep(
    SkillStepId Id,
    string Label,
    CapabilityId Capability,
    string Operation,
    string Target = "current_project",
    bool Mutation = false,
    ImmutableDictionary<string, SkillParameterValue>? Parameters = null,
    ImmutableArray<SkillStepId> Dependencies = default) : SkillStep(Id, Label, Dependencies)
{
    public override SkillStepKind Kind => SkillStepKind.CapabilityCall;
    public IReadOnlyDictionary<string, SkillParameterValue> SafeParameters => Parameters ?? ImmutableDictionary<string, SkillParameterValue>.Empty;
}

public sealed record SkillSpecialistAssignmentStep(
    SkillStepId Id,
    string Label,
    SpecialistRole Role,
    string Objective,
    IReadOnlyList<string>? SuccessCriteria = null,
    ImmutableArray<SkillStepId> Dependencies = default) : SkillStep(Id, Label, Dependencies)
{
    public override SkillStepKind Kind => SkillStepKind.SpecialistAssignment;
    public IReadOnlyList<string> SafeSuccessCriteria => SuccessCriteria ?? Array.Empty<string>();
}

public sealed record SkillVerificationStep(
    SkillStepId Id,
    string Label,
    string Objective,
    string? Profile = null,
    ImmutableArray<SkillStepId> Dependencies = default) : SkillStep(Id, Label, Dependencies)
{
    public override SkillStepKind Kind => SkillStepKind.Verification;
}

public sealed record SkillModelStep(
    SkillStepId Id,
    string Label,
    string Operation,
    string PromptTemplate,
    ImmutableArray<SkillStepId> Dependencies = default) : SkillStep(Id, Label, Dependencies)
{
    public override SkillStepKind Kind => SkillStepKind.Model;
}

public sealed record SkillCondition(SkillConditionKind Kind, SkillStepId Step, string? Value = null);

public sealed record SkillConditionalStep(
    SkillStepId Id,
    string Label,
    SkillCondition Condition,
    SkillStepId ThenStep,
    SkillStepId? ElseStep = null,
    ImmutableArray<SkillStepId> Dependencies = default) : SkillStep(Id, Label, Dependencies)
{
    public override SkillStepKind Kind => SkillStepKind.Conditional;
}

/// <summary>
/// Declarative composition of another registered Skill. Composition is data, not
/// embedded code; the executor resolves the exact requested version and applies
/// the current mission policy again before running it.
/// </summary>
public sealed record SkillCompositionStep(
    SkillStepId Id,
    string Label,
    SkillId ChildSkill,
    SkillVersion? ChildVersion = null,
    IReadOnlyDictionary<string, string>? InputBindings = null,
    ImmutableArray<SkillStepId> Dependencies = default) : SkillStep(Id, Label, Dependencies)
{
    public override SkillStepKind Kind => SkillStepKind.Composition;
    public IReadOnlyDictionary<string, string> SafeInputBindings => InputBindings ?? ImmutableDictionary<string, string>.Empty;
}

public sealed record SkillProcedure(IReadOnlyList<SkillStep> Steps)
{
    public IReadOnlyList<SkillStep> SafeSteps => Steps ?? Array.Empty<SkillStep>();
}

public sealed record SkillVerificationPlan(
    IReadOnlyList<string> Criteria,
    IReadOnlyList<SkillStepId>? RequiredSteps = null,
    bool RequiresArgus = true)
{
    public IReadOnlyList<string> SafeCriteria => Criteria ?? Array.Empty<string>();
    public IReadOnlyList<SkillStepId> SafeRequiredSteps => RequiredSteps ?? Array.Empty<SkillStepId>();
}

public sealed record SkillStatistics(
    int Executions = 0,
    int VerifiedSuccesses = 0,
    int Failures = 0,
    int Inconclusive = 0,
    double AverageDurationMilliseconds = 0,
    double AverageModelCalls = 0,
    decimal AverageCost = 0,
    double AverageReplans = 0,
    DateTimeOffset? LastExecutedAt = null,
    DateTimeOffset? LastVerifiedSuccessAt = null,
    int RecentFailures = 0)
{
    public double Reliability => (VerifiedSuccesses + 1d) / (Executions + 2d);
    public SkillStatistics Record(SkillExecutionResult result)
    {
        var executions = Executions + 1;
        var success = VerifiedSuccesses + (result.Verification is SkillVerificationOutcome.Passed ? 1 : 0);
        var failures = Failures + (result.Status is SkillExecutionStatus.Failed or SkillExecutionStatus.Blocked ? 1 : 0);
        var inconclusive = Inconclusive + (result.Verification is SkillVerificationOutcome.Inconclusive ? 1 : 0);
        var duration = AverageDurationMilliseconds + (result.Duration.TotalMilliseconds - AverageDurationMilliseconds) / executions;
        var recentFailures = result.Status is SkillExecutionStatus.Failed or SkillExecutionStatus.Blocked ? RecentFailures + 1 : Math.Max(0, RecentFailures - 1);
        return this with
        {
            Executions = executions,
            VerifiedSuccesses = success,
            Failures = failures,
            Inconclusive = inconclusive,
            AverageDurationMilliseconds = duration,
            LastExecutedAt = DateTimeOffset.UtcNow,
            LastVerifiedSuccessAt = result.Verification is SkillVerificationOutcome.Passed ? DateTimeOffset.UtcNow : LastVerifiedSuccessAt,
            RecentFailures = recentFailures
        };
    }
}

public enum SkillVerificationOutcome { NotRun, Passed, Failed, Inconclusive }

public sealed record SkillDefinition
{
    public required SkillId Id { get; init; }
    public required SkillVersion Version { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required SkillCategory Category { get; init; }
    public SkillLifecycleState State { get; init; } = SkillLifecycleState.Candidate;
    public SkillOrigin Origin { get; init; } = SkillOrigin.UserAuthored;
    public SkillTriggerSet Triggers { get; init; } = new();
    public SkillPreconditions Preconditions { get; init; } = new();
    public required SkillProcedure Procedure { get; init; }
    public required SkillVerificationPlan Verification { get; init; }
    public SkillCapabilityPolicy CapabilityPolicy { get; init; } = new();
    public SkillSpecialistPolicy SpecialistPolicy { get; init; } = new();
    public SkillResourceProfile ResourceProfile { get; init; } = new();
    public SkillInputContract Inputs { get; init; } = new(Array.Empty<SkillParameterDefinition>());
    public SkillOutputContract Outputs { get; init; } = new(Array.Empty<SkillParameterDefinition>());
    public SkillProvenance Provenance { get; init; } = new(SkillOrigin.UserAuthored);
    public SkillStatistics Statistics { get; init; } = new();
    public bool Enabled { get; init; } = true;
    public SkillRevisionId Revision { get; init; } = SkillRevisionId.New();
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record SkillMatchRequest(
    string Objective,
    SpecialistRole? SpecialistRole = null,
    string? ProjectKey = null,
    string? RepositoryKey = null,
    string? Language = null,
    string? Framework = null,
    IReadOnlySet<CapabilityId>? AvailableCapabilities = null,
    bool AllowMutation = false,
    bool AllowSkillReuse = true,
    bool ExplicitSkillRequest = false);

public sealed record SkillMatchReason(string Code, string Message, double Weight);

public sealed record SkillMatch(
    SkillDefinition Skill,
    double Score,
    IReadOnlyList<SkillMatchReason> Reasons)
{
    public string Explanation => string.Join("; ", Reasons.Select(static reason => reason.Message));
}

public sealed record SkillExecutionRequest(
    SkillDefinition Skill,
    IReadOnlyDictionary<string, SkillParameterValue>? Inputs = null,
    MissionId? MissionId = null,
    SpecialistRole? SpecialistRole = null,
    string? ProjectKey = null,
    string? WorkspacePath = null,
    bool DryRun = false,
    bool RequireVerification = true,
    bool ExplicitVersion = false,
    bool AllowMutation = false,
    bool AllowExternalSideEffects = false,
    IReadOnlySet<string>? CompositionStack = null);

public sealed record SkillStepResult(
    bool Succeeded,
    string Summary,
    IReadOnlyDictionary<string, SkillParameterValue>? Outputs = null,
    IReadOnlyList<EvidenceId>? Evidence = null,
    SkillVerificationOutcome Verification = SkillVerificationOutcome.NotRun,
    string? FailureCode = null)
{
    public IReadOnlyDictionary<string, SkillParameterValue> SafeOutputs => Outputs ?? ImmutableDictionary<string, SkillParameterValue>.Empty;
    public IReadOnlyList<EvidenceId> SafeEvidence => Evidence ?? Array.Empty<EvidenceId>();
}

public sealed record SkillExecutionContext(
    SkillDefinition Skill,
    SkillExecutionId ExecutionId,
    MissionId? MissionId,
    SpecialistRole? SpecialistRole,
    IReadOnlyDictionary<string, SkillParameterValue> Inputs,
    string? ProjectKey,
    string? WorkspacePath,
    IReadOnlyDictionary<SkillStepId, SkillStepResult> DependencyResults,
    CancellationToken CancellationToken);

public sealed record SkillExecutionResult(
    SkillId SkillId,
    SkillVersion Version,
    SkillExecutionId ExecutionId,
    SkillExecutionStatus Status,
    string Summary,
    IReadOnlyDictionary<string, SkillParameterValue>? Outputs,
    IReadOnlyList<EvidenceId>? Evidence,
    SkillVerificationOutcome Verification,
    TimeSpan Duration,
    int ModelCalls = 0,
    decimal Cost = 0,
    string? FailureCode = null,
    IReadOnlyList<SkillDiagnostic>? Diagnostics = null)
{
    public bool Succeeded => Status == SkillExecutionStatus.Succeeded && (Verification is SkillVerificationOutcome.Passed or SkillVerificationOutcome.NotRun);
    public IReadOnlyDictionary<string, SkillParameterValue> SafeOutputs => Outputs ?? ImmutableDictionary<string, SkillParameterValue>.Empty;
    public IReadOnlyList<EvidenceId> SafeEvidence => Evidence ?? Array.Empty<EvidenceId>();
    public IReadOnlyList<SkillDiagnostic> SafeDiagnostics => Diagnostics ?? Array.Empty<SkillDiagnostic>();
}

public interface ISkillStepRunner
{
    ValueTask<SkillStepResult> RunAsync(SkillStep skillStep, SkillExecutionContext context);
}

/// <summary>Provider-neutral seam for explicit model cognition steps inside a Skill.</summary>
public interface ISkillModelOperator
{
    ValueTask<SkillStepResult> RunAsync(SkillModelStep modelStep, SkillExecutionContext context, CancellationToken cancellationToken = default);
}

public interface ISkillRegistry
{
    IReadOnlyList<SkillDefinition> List(bool includeDisabled = false);
    bool TryGet(SkillId id, SkillVersion? version, out SkillDefinition skill);
    IReadOnlyList<SkillDefinition> GetVersions(SkillId id);
    void Register(SkillDefinition skill, bool replace = false);
    bool TryUpdate(SkillId id, SkillVersion version, Func<SkillDefinition, SkillDefinition> update);
    ValueTask LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(CancellationToken cancellationToken = default);
}

public interface ISkillMatcher
{
    IReadOnlyList<SkillMatch> Match(SkillMatchRequest request, int limit = 8);
}

public interface ISkillValidator
{
    SkillValidationReport Validate(SkillDefinition skill, SkillValidationOptions? options = null);
}

public interface ISkillExecutor
{
    ValueTask<SkillExecutionResult> ExecuteAsync(SkillExecutionRequest request, CancellationToken cancellationToken = default);
}

public interface ISkillPromotionPolicy
{
    SkillDefinition Apply(SkillDefinition skill, SkillValidationReport validation, SkillExecutionResult? result = null, bool userApproved = false);
}

public sealed record SkillValidationOptions(
    IReadOnlySet<CapabilityId>? AvailableCapabilities = null,
    bool AllowMutation = false,
    bool AllowExternalSideEffects = false,
    bool RequireIsolationForExperimentalMutation = true,
    int MaxSteps = 256);

public sealed record SkillValidationReport(
    bool IsValid,
    IReadOnlyList<SkillDiagnostic> Diagnostics,
    SkillValidationId ValidationId,
    DateTimeOffset ValidatedAt)
{
    public bool HasErrors => Diagnostics.Any(static diagnostic => diagnostic.Severity == SkillDiagnosticSeverity.Error);
}

public sealed record SkillExecutionPlan(IReadOnlyList<IReadOnlyList<SkillStep>> Levels, IReadOnlyList<SkillDiagnostic> Diagnostics)
{
    public bool IsValid => Diagnostics.All(static diagnostic => diagnostic.Severity != SkillDiagnosticSeverity.Error);
}

public sealed record SkillEngineOptions(
    double MinimumAutomaticMatchScore = 0.58,
    int TrustedMinimumExecutions = 3,
    double TrustedMinimumReliability = 0.80,
    int TrustedFailureWindow = 3,
    bool RequireUserApprovalForTrusted = false,
    int MaximumPersistedSkills = 100_000,
    int MaximumCompositionDepth = 4);

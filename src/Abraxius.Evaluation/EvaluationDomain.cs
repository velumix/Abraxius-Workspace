using Abraxius.Artifacts;
using Abraxius.Security;

namespace Abraxius.Evaluation;

public readonly record struct EvalSuiteId(string Value) { public override string ToString() => Value; }
public readonly record struct EvalCaseId(string Value) { public override string ToString() => Value; }
public readonly record struct EvalRunId(Guid Value) { public static EvalRunId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct EvalExecutionId(Guid Value) { public static EvalExecutionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct EvalDatasetId(string Value) { public override string ToString() => Value; }
public readonly record struct EvalBaselineId(Guid Value) { public static EvalBaselineId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct EvalCandidateId(Guid Value) { public static EvalCandidateId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct EvalMetricId(string Value) { public override string ToString() => Value; }
public readonly record struct EvalComparisonId(Guid Value) { public static EvalComparisonId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct EvalRegressionId(Guid Value) { public static EvalRegressionId New() => new(Guid.NewGuid()); public override string ToString() => Value.ToString("N"); }
public readonly record struct EvalGateId(string Value) { public override string ToString() => Value; }

public enum EvalDomain { CoreMission, Model, Routing, Specialist, Skill, Memory, Retrieval, Axl, Scheduler, Tooling, Voice, Security, Artifact, Performance, CrossPlatform, Reliability }
public enum EvalSuiteState { Draft, Experimental, Validated, ReleaseGate, Deprecated, Archived }
public enum EvalCaseStatus { Passed, Failed, Inconclusive, Skipped, InfrastructureFailure, Cancelled }
public enum EvalRunStatus { Pending, Running, Passed, Failed, Inconclusive, Partial, Cancelled, InfrastructureFailure }
public enum EvalDeterminism { Deterministic, Seeded, Nondeterministic, HumanEvaluation }
public enum EvalDatasetOrigin { Synthetic, RealMissionDerived, UserAuthored, Imported, Generated }
public enum EvalSamplingPreset { Smoke, Standard, Full }
public enum EvalMetricDirection { HigherIsBetter, LowerIsBetter, Target }
public enum EvalMetricAggregation { Count, Rate, Sum, Mean, Median, P90, P95, P99, Minimum, Maximum }
public enum EvalMetricAvailability { Known, Unknown, Unavailable, NotApplicable }
public enum EvalChangeClassification { Improvement, Regression, Neutral, Inconclusive }
public enum EvalRegressionSeverity { Informational, Minor, Major, Critical }
public enum EvalRegressionState { Open, Investigating, FixCandidate, Resolved, Accepted, WontFix }
public enum EvalGateSeverity { Advisory, Required, ReleaseBlocking, SecurityCritical }
public enum EvalGateStatus { Passed, Failed, Inconclusive, OverrideRequired }
public enum EvalGateMode { AbsoluteMinimum, AbsoluteMaximum, RelativeMaximumRegression, RelativeMinimumImprovement, ZeroTolerance }
public enum EvalIsolationKind { InMemory, TemporaryWorkspace, IsolatedDatabase, Sandbox, Remote }
public enum EvalExecutionTargetKind { Local, Remote }
public enum EvalCostPrecision { Unknown, Estimated, Actual }
public enum EvalVerificationKind { ExactResult, Invariant, RequiredEvidence, RequiredArtifact, TestsPass, SecurityDenied, AxlParses, SchemaValid, LatencyThreshold, ArtifactHash, Custom }

public static class EvalMetricIds
{
    public static readonly EvalMetricId SuccessRate = new("success.rate");
    public static readonly EvalMetricId VerifiedSuccessRate = new("success.verified-rate");
    public static readonly EvalMetricId LatencyMilliseconds = new("latency.ms");
    public static readonly EvalMetricId CostUsd = new("cost.usd");
    public static readonly EvalMetricId TokenUsage = new("model.tokens");
    public static readonly EvalMetricId ModelCalls = new("model.calls");
    public static readonly EvalMetricId ToolCalls = new("tool.calls");
    public static readonly EvalMetricId FrontierEscalationRate = new("routing.frontier-rate");
    public static readonly EvalMetricId RecallAtK = new("retrieval.recall-at-k");
    public static readonly EvalMetricId PrecisionAtK = new("retrieval.precision-at-k");
    public static readonly EvalMetricId MeanReciprocalRank = new("retrieval.mrr");
    public static readonly EvalMetricId AxlParseSuccess = new("axl.parse-success");
    public static readonly EvalMetricId SecurityCriticalEscapes = new("security.critical-escapes");
}

public sealed record EvalCaseInput(
    string Operation,
    string? MissionIntent = null,
    string? RepositoryFixture = null,
    string? MemoryFixture = null,
    ImmutableArray<ArtifactRevisionId> ArtifactRevisionIds = default,
    ImmutableArray<EvalDatasetId> DatasetRefs = default,
    ImmutableDictionary<string, string>? Properties = null,
    string? SecurityPolicyVersion = null,
    string? ModelPolicy = null,
    string? SkillPolicy = null)
{
    public ImmutableArray<ArtifactRevisionId> SafeArtifactRevisionIds => ArtifactRevisionIds.IsDefault ? [] : ArtifactRevisionIds;
    public ImmutableDictionary<string, string> SafeProperties => Properties ?? ImmutableDictionary<string, string>.Empty;
}

public sealed record EvalExpectedOutcome(
    string? ExactResult = null,
    ImmutableArray<string> Invariants = default,
    ImmutableArray<string> RequiredEvidence = default,
    ImmutableArray<ArtifactKind> RequiredArtifacts = default,
    AuthorizationOutcome? SecurityOutcome = null);

public sealed record EvalVerificationCheckDefinition(EvalVerificationKind Kind, string Expected, string? Property = null, double? Threshold = null);
public sealed record EvalVerificationPlan(ImmutableArray<EvalVerificationCheckDefinition> Checks, bool RequireAll = true);
public sealed record EvalEnvironmentRequirements(ImmutableArray<string> Platforms = default, string? Architecture = null, bool RequiresGpu = false, EvalIsolationKind Isolation = EvalIsolationKind.InMemory);

public sealed record EvalCase(
    EvalCaseId Id,
    string Name,
    EvalCaseInput Input,
    EvalEnvironmentRequirements Environment,
    EvalExpectedOutcome ExpectedOutcome,
    EvalVerificationPlan VerificationPlan,
    ImmutableArray<string> Tags,
    int Difficulty,
    EvalDeterminism Determinism,
    int RepeatCount = 1,
    ImmutableArray<int> Seeds = default,
    TimeSpan? Timeout = null,
    DataClassification Classification = DataClassification.Internal)
{
    public int EffectiveRepeatCount => Math.Max(1, RepeatCount);
    public ImmutableArray<int> EffectiveSeeds => Seeds.IsDefaultOrEmpty ? [0] : Seeds;
}

public sealed record EvalMetricDefinition(EvalMetricId Id, string Name, string Unit, EvalMetricDirection Direction, EvalMetricAggregation Aggregation, bool Primary = false);
public sealed record EvalGateDefinition(EvalGateId Id, EvalMetricId MetricId, EvalGateMode Mode, double Threshold, EvalGateSeverity Severity, int RequiredSampleSize = 1, string Explanation = "");
public sealed record EvalExecutionPolicy(int MaxConcurrency = 4, EvalSamplingPreset Preset = EvalSamplingPreset.Standard, int? MaxCases = null, TimeSpan? MaxDuration = null, decimal? MaxCost = null, int? MaxFrontierCalls = null, bool ContinueAfterCriticalFailure = true);

public sealed record EvalSuite(
    EvalSuiteId Id,
    string Version,
    string Name,
    string Description,
    EvalDomain Domain,
    ImmutableArray<EvalCase> Cases,
    ImmutableArray<EvalMetricDefinition> Metrics,
    ImmutableArray<EvalGateDefinition> Gates,
    EvalExecutionPolicy ExecutionPolicy,
    EvalSuiteState State = EvalSuiteState.Validated,
    EvalDatasetId? DatasetId = null);

public sealed record EvalDataset(EvalDatasetId Id, string Version, string Name, ImmutableArray<EvalCaseId> Cases, EvalDatasetOrigin Origin, string Provenance, DataClassification Classification, string? License = null, DateTimeOffset? CreatedAt = null);
public sealed record EvalCandidate(EvalCandidateId Id, string Name, string Kind, string Reference, ImmutableDictionary<string, string>? Configuration = null);
public sealed record EvalBaseline(EvalBaselineId Id, string Name, EvalRunId RunId, EvalCandidate Candidate, string SuiteVersion, string? EnvironmentFingerprint = null);

public sealed record EvalEnvironmentSnapshot(
    string AbraxiusVersion,
    string GitCommit,
    string OperatingSystem,
    string Architecture,
    string DotnetRuntime,
    string AvaloniaVersion,
    string Cpu,
    string Gpu,
    long RamBytes,
    ImmutableDictionary<string, string> ModelIdentities,
    ImmutableDictionary<string, string> SkillVersions,
    string AxlVersion,
    string MemoryConfiguration,
    string RoutingConfiguration,
    string SecurityPolicyVersion,
    int? Seed,
    string ExecutionMode,
    DateTimeOffset CapturedAt)
{
    public string Fingerprint => string.Join('|', OperatingSystem, Architecture, DotnetRuntime, Cpu, Gpu, RamBytes, ExecutionMode);
}

public sealed record EvalMetricSample(EvalMetricId MetricId, double Value, string Unit, int Repeat, int? Seed = null, DateTimeOffset? Timestamp = null);
public sealed record EvalMetricValue(EvalMetricId MetricId, double? Value, string Unit, EvalMetricAggregation Aggregation, int SampleCount, EvalMetricAvailability Availability = EvalMetricAvailability.Known)
{
    public static EvalMetricValue Unknown(EvalMetricId id, string unit, EvalMetricAggregation aggregation, EvalMetricAvailability availability = EvalMetricAvailability.Unknown) => new(id, null, unit, aggregation, 0, availability);
}

public sealed record EvalVerificationResult(bool Verified, ImmutableArray<string> Passed, ImmutableArray<string> Failed, ImmutableArray<EvidenceId> EvidenceRefs);
public sealed record EvalCaseResult(
    EvalExecutionId ExecutionId,
    EvalRunId RunId,
    EvalCaseId CaseId,
    int Repeat,
    int? Seed,
    EvalCaseStatus Status,
    EvalVerificationResult Verification,
    ImmutableArray<EvalMetricSample> Metrics,
    ImmutableArray<ArtifactRevisionId> Artifacts,
    string? TrajectoryId,
    ImmutableArray<string> Errors,
    TimeSpan Duration,
    decimal? Cost,
    EvalCostPrecision CostPrecision,
    ImmutableArray<string> ProviderCalls,
    ImmutableArray<string> SpecialistContributions,
    DateTimeOffset CompletedAt);

public sealed record EvalRun(
    EvalRunId Id,
    EvalSuiteId SuiteId,
    string SuiteVersion,
    EvalCandidate Candidate,
    EvalBaselineId? BaselineId,
    EvalEnvironmentSnapshot Environment,
    EvalRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    ImmutableArray<EvalCaseResult> CaseResults,
    ImmutableArray<EvalMetricValue> Metrics,
    ArtifactRevisionId? ReportArtifactRevision = null,
    bool RewardEligible = false);

public sealed record EvalMetricDelta(EvalMetricId MetricId, EvalMetricValue Baseline, EvalMetricValue Candidate, double? AbsoluteDelta, double? RelativeDelta, EvalChangeClassification Classification, string Explanation);
public sealed record EvalRegression(EvalRegressionId Id, EvalComparisonId ComparisonId, EvalSuiteId SuiteId, EvalCaseId? CaseId, EvalMetricId MetricId, double Baseline, double Candidate, double Delta, EvalRegressionSeverity Severity, string Evidence, ImmutableArray<ArtifactRevisionId> ArtifactRefs, string? TrajectoryId = null, EvalRegressionState State = EvalRegressionState.Open, string? FixMissionId = null);
public sealed record EvalImprovement(EvalMetricId MetricId, double Baseline, double Candidate, double Delta, string Evidence);
public sealed record EvalGateResult(EvalGateId GateId, EvalGateStatus Status, EvalGateSeverity Severity, EvalMetricId MetricId, double? Observed, double? Baseline, double Threshold, int SampleCount, string Explanation, string? OverrideUser = null, string? OverrideReason = null);
public sealed record EvalComparison(EvalComparisonId Id, EvalRunId BaselineRunId, EvalRunId CandidateRunId, DateTimeOffset CreatedAt, bool SameWorkload, bool EnvironmentCompatible, ImmutableArray<string> Warnings, ImmutableArray<EvalMetricDelta> Deltas, ImmutableArray<EvalRegression> Regressions, ImmutableArray<EvalImprovement> Improvements, ImmutableArray<EvalGateResult> Gates)
{
    public bool ReleaseBlocked => Gates.Any(static gate => gate.Status == EvalGateStatus.Failed && gate.Severity is EvalGateSeverity.ReleaseBlocking or EvalGateSeverity.SecurityCritical);
}

public sealed record EvalRunRequest(EvalSuite Suite, EvalCandidate Candidate, EvalEnvironmentSnapshot Environment, EvalBaselineId? BaselineId = null, EvalSamplingPreset? Preset = null, EvalRunId? ResumeRunId = null);
public sealed record EvalCaseExecution(EvalCaseStatus Status, string? Observed, ImmutableDictionary<string, string> Observations, ImmutableArray<EvalMetricSample> Metrics, ImmutableArray<ArtifactRevisionId> Artifacts = default, ImmutableArray<EvidenceId> Evidence = default, ImmutableArray<string> Errors = default, string? TrajectoryId = null, decimal? Cost = null, EvalCostPrecision CostPrecision = EvalCostPrecision.Unknown, ImmutableArray<string> ProviderCalls = default, ImmutableArray<string> SpecialistContributions = default);

public sealed record EvalRunSummary(EvalRunId Id, EvalSuiteId SuiteId, string SuiteVersion, string Candidate, EvalRunStatus Status, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt, int Passed, int Failed, int Inconclusive, int InfrastructureFailures, bool ReleaseBlocked = false);

public sealed record EvalRegressionMission(EvalRegressionId RegressionId, string Objective, ImmutableDictionary<string, string> References, DataClassification Classification);

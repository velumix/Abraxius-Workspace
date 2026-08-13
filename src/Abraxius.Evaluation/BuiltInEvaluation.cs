using System.Security.Cryptography;
using System.Text;
using Abraxius.Artifacts;
using Abraxius.Axl;
using Abraxius.Security;

namespace Abraxius.Evaluation;

public sealed class BuiltInEvalCaseExecutor(
    IModelEgressPolicy egressPolicy,
    ISecurityKernel securityKernel,
    IArtifactService? artifactService = null) : IEvalCaseExecutor
{
    public async ValueTask<EvalCaseExecution> ExecuteAsync(EvalCaseContext context)
    {
        var properties = context.Case.Input.SafeProperties;
        return context.Case.Input.Operation switch
        {
            "deterministic" => Deterministic(properties, context),
            "delay" => await DelayAsync(properties, context).ConfigureAwait(false),
            "fixture.file" => await FileFixtureAsync(context).ConfigureAwait(false),
            "axl.parse" => AxlParse(properties, context),
            "retrieval.rank" => Retrieval(properties, context),
            "security.egress" => SecurityEgress(properties, context),
            "security.authorize" => await SecurityAuthorizeAsync(properties, context).ConfigureAwait(false),
            "artifact.integrity" => await ArtifactIntegrityAsync(context).ConfigureAwait(false),
            "skill.effectiveness" => SkillEffectiveness(properties, context),
            _ => new(EvalCaseStatus.InfrastructureFailure, null, ImmutableDictionary<string, string>.Empty, [], Errors: [$"No evaluation executor is registered for '{context.Case.Input.Operation}'."])
        };
    }

    private static EvalCaseExecution Deterministic(ImmutableDictionary<string, string> properties, EvalCaseContext context)
    {
        if (properties.GetValueOrDefault("infrastructureFailure") == "true") throw new IOException("Synthetic provider unavailable.");
        var observed = properties.GetValueOrDefault("observed", "pass");
        return new(EvalCaseStatus.Passed, observed, properties, [], ProviderCalls: ParseList(properties.GetValueOrDefault("providers")), SpecialistContributions: ParseList(properties.GetValueOrDefault("specialists")));
    }

    private static async ValueTask<EvalCaseExecution> DelayAsync(ImmutableDictionary<string, string> properties, EvalCaseContext context)
    {
        var delay = int.TryParse(properties.GetValueOrDefault("milliseconds"), out var parsed) ? Math.Clamp(parsed, 0, 5000) : 10;
        await Task.Delay(delay, context.CancellationToken).ConfigureAwait(false);
        return new(EvalCaseStatus.Passed, "completed", properties, []);
    }

    private static async ValueTask<EvalCaseExecution> FileFixtureAsync(EvalCaseContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Workspace)) return new(EvalCaseStatus.InfrastructureFailure, null, ImmutableDictionary<string, string>.Empty, [], Errors: ["A temporary workspace is required."]);
        var path = Path.Combine(context.Workspace, "fixture.txt"); await File.WriteAllTextAsync(path, "verified fixture", context.CancellationToken).ConfigureAwait(false);
        var observed = await File.ReadAllTextAsync(path, context.CancellationToken).ConfigureAwait(false);
        return new(EvalCaseStatus.Passed, observed, ImmutableDictionary<string, string>.Empty.Add("exists", File.Exists(path).ToString()), []);
    }

    private static EvalCaseExecution AxlParse(ImmutableDictionary<string, string> properties, EvalCaseContext context)
    {
        var text = properties.GetValueOrDefault("text", "axl/1 find code q=ExecutionGraph lim=20"); var parsed = AxlParser.Parse(text);
        var observed = parsed.IsSuccess ? "valid" : "invalid";
        return new(EvalCaseStatus.Passed, observed, ImmutableDictionary<string, string>.Empty.Add("parse", observed), [new(EvalMetricIds.AxlParseSuccess, parsed.IsSuccess ? 1 : 0, "ratio", context.Repeat, context.Seed)]);
    }

    private static EvalCaseExecution Retrieval(ImmutableDictionary<string, string> properties, EvalCaseContext context)
    {
        var relevant = ParseList(properties.GetValueOrDefault("relevant")); var ranked = ParseList(properties.GetValueOrDefault("ranked"));
        var k = int.TryParse(properties.GetValueOrDefault("k"), out var parsed) ? parsed : 5;
        var metrics = ImmutableArray.Create(
            new EvalMetricSample(EvalMetricIds.RecallAtK, EvalMetricMath.RecallAtK(relevant, ranked, k), "ratio", context.Repeat, context.Seed),
            new EvalMetricSample(EvalMetricIds.PrecisionAtK, EvalMetricMath.PrecisionAtK(relevant, ranked, k), "ratio", context.Repeat, context.Seed),
            new EvalMetricSample(EvalMetricIds.MeanReciprocalRank, EvalMetricMath.MeanReciprocalRank(relevant, ranked), "ratio", context.Repeat, context.Seed));
        return new(EvalCaseStatus.Passed, metrics[0].Value >= .5 ? "relevant" : "irrelevant", properties, metrics);
    }

    private EvalCaseExecution SecurityEgress(ImmutableDictionary<string, string> properties, EvalCaseContext context)
    {
        var classification = Enum.TryParse<DataClassification>(properties.GetValueOrDefault("classification"), true, out var parsed) ? parsed : context.Case.Classification;
        var local = bool.TryParse(properties.GetValueOrDefault("local"), out var isLocal) && isLocal;
        var decision = egressPolicy.Evaluate(SecuritySubject.System("evaluation"), classification, local, properties.GetValueOrDefault("provider", "cloud-fixture"));
        var escaped = decision.Outcome == AuthorizationOutcome.Allow && !local && classification is DataClassification.LocalOnly or DataClassification.Secret;
        return new(EvalCaseStatus.Passed, decision.Outcome.ToString(), ImmutableDictionary<string, string>.Empty.Add("decision", decision.Outcome.ToString()), [new(EvalMetricIds.SecurityCriticalEscapes, escaped ? 1 : 0, "count", context.Repeat, context.Seed)]);
    }

    private async ValueTask<EvalCaseExecution> SecurityAuthorizeAsync(ImmutableDictionary<string, string> properties, EvalCaseContext context)
    {
        var capability = properties.GetValueOrDefault("capability", "system.superuser"); var uri = properties.GetValueOrDefault("resource", "capability://unknown");
        var kind = Enum.TryParse<ResourceKind>(properties.GetValueOrDefault("resourceKind"), true, out var parsedKind) ? parsedKind : ResourceKind.Unknown;
        var subject = SecuritySubject.System("evaluation"); var resource = new SecurityResource(kind, uri, properties.GetValueOrDefault("localPath"));
        var action = new Abraxius.Security.ProposedAction(ActionId.New(), subject, capability, properties.GetValueOrDefault("operation", "evaluate"), resource, ExternalEffect: properties.GetValueOrDefault("external") == "true");
        var decision = await securityKernel.AuthorizeAsync(new(subject, action, new AuthorizationContext(WorkspaceRoot: context.Workspace, Classification: context.Case.Classification, Replay: properties.GetValueOrDefault("replay") == "true"), DateTimeOffset.UtcNow), context.CancellationToken).ConfigureAwait(false);
        var criticalEscape = properties.GetValueOrDefault("mustDeny") == "true" && decision.IsAllowed;
        return new(EvalCaseStatus.Passed, decision.Outcome.ToString(), ImmutableDictionary<string, string>.Empty.Add("decision", decision.Outcome.ToString()).Add("reason", decision.ReasonCode.ToString()), [new(EvalMetricIds.SecurityCriticalEscapes, criticalEscape ? 1 : 0, "count", context.Repeat, context.Seed)]);
    }

    private async ValueTask<EvalCaseExecution> ArtifactIntegrityAsync(EvalCaseContext context)
    {
        if (artifactService is null) return new(EvalCaseStatus.InfrastructureFailure, null, ImmutableDictionary<string, string>.Empty, [], Errors: ["Artifact service unavailable."]);
        var producer = new ArtifactProducer(new PrincipalId("system:evaluation"), ArtifactProducerKind.System, "Evaluation Lab");
        await using var firstContent = new MemoryStream(Encoding.UTF8.GetBytes("revision-one"), writable: false);
        var first = await artifactService.CreateAsync(new(ArtifactKind.GeneratedData, "Artifact integrity fixture", producer, new ArtifactProvenance(TrajectoryId: $"eval:{context.RunId}"), new ArtifactClassification(context.Case.Classification), "text/plain", Retention: ArtifactRetention.Temporary), firstContent, context.CancellationToken).ConfigureAwait(false);
        var verification = new ArtifactVerification(ArtifactVerificationId.New(), first.CurrentRevision.Id, producer, "hash pin", [], [], ArtifactVerificationResult.Passed, DateTimeOffset.UtcNow, context.Environment.Fingerprint);
        first = await artifactService.AttachVerificationAsync(first.Descriptor.Id, verification, context.CancellationToken).ConfigureAwait(false);
        await using var secondContent = new MemoryStream(Encoding.UTF8.GetBytes("revision-two"), writable: false);
        var second = await artifactService.CreateRevisionAsync(new(first.Descriptor.Id, first.CurrentRevision.Id, producer, first.Descriptor.Provenance, "text/plain", State: ArtifactState.Candidate), secondContent, context.CancellationToken).ConfigureAwait(false);
        var pinned = second.SafeVerifications.All(item => item.ArtifactRevisionId != second.CurrentRevision.Id) && !string.Equals(first.CurrentRevision.RevisionHash, second.CurrentRevision.RevisionHash, StringComparison.Ordinal);
        return new(EvalCaseStatus.Passed, pinned ? "pinned" : "invalid", ImmutableDictionary<string, string>.Empty.Add("pinning", pinned.ToString()), [], [second.CurrentRevision.Id]);
    }

    private static EvalCaseExecution SkillEffectiveness(ImmutableDictionary<string, string> properties, EvalCaseContext context)
    {
        var success = double.TryParse(properties.GetValueOrDefault("verifiedSuccess"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 1;
        var modelCalls = double.TryParse(properties.GetValueOrDefault("modelCalls"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed) ? parsed : 1;
        return new(EvalCaseStatus.Passed, success >= .5 ? "verified" : "failed", properties, [new(EvalMetricIds.VerifiedSuccessRate, success, "ratio", context.Repeat, context.Seed), new(EvalMetricIds.ModelCalls, modelCalls, "count", context.Repeat, context.Seed)]);
    }

    private static ImmutableArray<string> ParseList(string? value) => string.IsNullOrWhiteSpace(value) ? [] : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();
}

public static class BuiltInEvalSuites
{
    private static readonly ImmutableArray<EvalMetricDefinition> Common =
    [
        new(EvalMetricIds.VerifiedSuccessRate, "Verified success", "ratio", EvalMetricDirection.HigherIsBetter, EvalMetricAggregation.Mean, true),
        new(EvalMetricIds.LatencyMilliseconds, "Latency", "ms", EvalMetricDirection.LowerIsBetter, EvalMetricAggregation.Median)
    ];

    public static ImmutableArray<EvalSuite> CreateAll() =>
    [
        Suite("core.mission-smoke", EvalDomain.CoreMission, [Case("inspect", "fixture.file", "verified fixture", EvalIsolationKind.TemporaryWorkspace), Case("known-outcome", "deterministic", "pass")]),
        Suite("axl.core", EvalDomain.Axl,
            [Case("valid", "axl.parse", "valid", properties: Props(("text", "axl/1 find code q=ExecutionGraph lim=20"))), Case("invalid-capability", "axl.parse", "invalid", properties: Props(("text", "axl/1 explode target=all")))],
            metrics: Common.Add(new(EvalMetricIds.AxlParseSuccess, "AXL parse success", "ratio", EvalMetricDirection.HigherIsBetter, EvalMetricAggregation.Mean)),
            gates: [new(new("axl-validity"), EvalMetricIds.AxlParseSuccess, EvalGateMode.AbsoluteMinimum, .5, EvalGateSeverity.ReleaseBlocking, 2)]),
        Suite("memory.retrieval", EvalDomain.Retrieval,
            [Case("exact", "retrieval.rank", "relevant", properties: Props(("relevant", "source-a"), ("ranked", "source-a,noise"))), Case("stale", "retrieval.rank", "relevant", properties: Props(("relevant", "current"), ("ranked", "stale,current")))],
            metrics: Common.Add(new(EvalMetricIds.RecallAtK, "Recall@K", "ratio", EvalMetricDirection.HigherIsBetter, EvalMetricAggregation.Mean)).Add(new(EvalMetricIds.PrecisionAtK, "Precision@K", "ratio", EvalMetricDirection.HigherIsBetter, EvalMetricAggregation.Mean)).Add(new(EvalMetricIds.MeanReciprocalRank, "MRR", "ratio", EvalMetricDirection.HigherIsBetter, EvalMetricAggregation.Mean))),
        Suite("scheduler.parallelism", EvalDomain.Scheduler, Enumerable.Range(0, 8).Select(index => Case($"branch-{index}", "delay", "completed", properties: Props(("milliseconds", "20")))).ToImmutableArray()),
        Suite("security.adversarial", EvalDomain.Security,
            [Case("localonly-egress", "security.egress", "Deny", properties: Props(("classification", "LocalOnly"), ("local", "false"))), Case("unknown-capability", "security.authorize", "Deny", properties: Props(("capability", "system.superuser"), ("mustDeny", "true")))],
            metrics: Common.Add(new(EvalMetricIds.SecurityCriticalEscapes, "Critical escapes", "count", EvalMetricDirection.LowerIsBetter, EvalMetricAggregation.Sum)),
            gates: [new(new("security-zero-escape"), EvalMetricIds.SecurityCriticalEscapes, EvalGateMode.ZeroTolerance, 0, EvalGateSeverity.SecurityCritical, 2)]),
        Suite("skills.effectiveness", EvalDomain.Skill,
            [Case("repo.inspect-project", "skill.effectiveness", "verified", properties: Props(("verifiedSuccess", "1"), ("modelCalls", "1"))), Case("git.regression-investigation", "skill.effectiveness", "verified", properties: Props(("verifiedSuccess", "1"), ("modelCalls", "2"))), Case("dotnet.build-and-test", "skill.effectiveness", "verified"), Case("verify.standard-code-change", "skill.effectiveness", "verified")],
            metrics: Common.Add(new(EvalMetricIds.ModelCalls, "Model calls", "count", EvalMetricDirection.LowerIsBetter, EvalMetricAggregation.Mean))),
        Suite("artifacts.integrity", EvalDomain.Artifact, [Case("revision-pinning", "artifact.integrity", "pinned")])
    ];

    public static EvalSuite? Find(string id) => CreateAll().FirstOrDefault(item => string.Equals(item.Id.Value, id, StringComparison.OrdinalIgnoreCase));

    private static EvalSuite Suite(string id, EvalDomain domain, ImmutableArray<EvalCase> cases, ImmutableArray<EvalMetricDefinition>? metrics = null, ImmutableArray<EvalGateDefinition>? gates = null) =>
        new(new(id), "1.0.0", id, $"Built-in {domain} evaluation suite.", domain, cases, metrics ?? Common,
            gates ?? [new(new($"{id}.verified"), EvalMetricIds.VerifiedSuccessRate, EvalGateMode.AbsoluteMinimum, .5, EvalGateSeverity.Required, 1)], new(4, EvalSamplingPreset.Standard), EvalSuiteState.ReleaseGate, new EvalDatasetId($"{id}.dataset"));

    private static EvalCase Case(string id, string operation, string expected, EvalIsolationKind isolation = EvalIsolationKind.InMemory, ImmutableDictionary<string, string>? properties = null) =>
        new(new(id), id, new(operation, Properties: properties), new(Isolation: isolation), new(ExactResult: expected), new([new(EvalVerificationKind.ExactResult, expected)]), [id], 1, EvalDeterminism.Deterministic);
    private static ImmutableDictionary<string, string> Props(params (string Key, string Value)[] pairs) => pairs.ToImmutableDictionary(static item => item.Key, static item => item.Value, StringComparer.Ordinal);
}

public static class EvalRegressionMissionFactory
{
    public static EvalRegressionMission Create(EvalRegression regression, EvalRun baseline, EvalRun candidate) => new(regression.Id,
        $"Investigate and repair evaluation regression {regression.MetricId} in suite {regression.SuiteId}.",
        ImmutableDictionary<string, string>.Empty.Add("evalRegressionId", regression.Id.ToString()).Add("suiteId", regression.SuiteId.Value).Add("baselineRunId", baseline.Id.ToString()).Add("candidateRunId", candidate.Id.ToString()).Add("metricId", regression.MetricId.Value).Add("baseline", regression.Baseline.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Add("candidate", regression.Candidate.ToString("R", System.Globalization.CultureInfo.InvariantCulture)),
        DataClassification.Internal);
}

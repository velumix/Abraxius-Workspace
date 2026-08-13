using System.Collections.Concurrent;
using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Axl;
using Abraxius.Models;
using Abraxius.Protocol;
using Abraxius.Runtime;
using Abraxius.Skills;
using Xunit;

namespace Abraxius.Skills.Tests;

public sealed class SkillTests
{
    [Fact]
    public void SkillAxlProjectionRoundTripsThroughStrictParser()
    {
        var skill = BuiltInSkills.RegressionInvestigation();
        var text = SkillAxlProjection.Format(skill);
        var parsed = AxlPipeline.ParseAndValidate(text, options: new AxlValidationOptions(AllowMutations: true));

        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));
        Assert.Contains(parsed.Document!.Commands, command => command is AxlSkill);
        Assert.Contains("skill id=\"git.regression-investigation\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ExactTriggerOutranksWeakSemanticCandidate()
    {
        var registry = new SkillRegistry();
        var exact = BuiltInSkills.RegressionInvestigation();
        var weak = exact with { Id = new SkillId("misc.investigation"), Name = "misc.investigation", Triggers = new SkillTriggerSet(Concepts: ["unrelated"]) };
        registry.Register(exact);
        registry.Register(weak);

        var matches = new DeterministicSkillMatcher(registry).Match(new SkillMatchRequest("find which commit caused this cancellation race", SpecialistRole.Investigator));

        Assert.NotEmpty(matches);
        Assert.Equal(exact.Id, matches[0].Skill.Id);
        Assert.Contains(matches[0].Reasons, reason => reason.Code == "trigger.exact");
    }

    [Fact]
    public void ValidatorRejectsCyclesAndUntrustedImport()
    {
        var stepA = new SkillContextQueryStep(new SkillStepId("a"), "A", "one", Dependencies: [new SkillStepId("b")]);
        var stepB = new SkillContextQueryStep(new SkillStepId("b"), "B", "two", Dependencies: [new SkillStepId("a")]);
        var skill = BuiltInSkills.ProjectInspection() with
        {
            Id = new SkillId("imported.cycle"),
            Name = "imported.cycle",
            Origin = SkillOrigin.Imported,
            State = SkillLifecycleState.Trusted,
            Procedure = new SkillProcedure([stepA, stepB])
        };

        var report = new SkillValidator().Validate(skill);

        Assert.False(report.IsValid);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "SKILL007");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "SKILL015");
    }

    [Fact]
    public void ImportedSkillCannotBeTrustedAndPromotionNeedsRepeatedVerifiedRuns()
    {
        var skill = BuiltInSkills.ProjectInspection() with
        {
            Id = new SkillId("imported.project-inspection"),
            Name = "imported.project-inspection",
            Origin = SkillOrigin.Imported,
            State = SkillLifecycleState.Candidate
        };
        var validator = new SkillValidator();
        var promotion = new SkillPromotionPolicy(new SkillEngineOptions(TrustedMinimumExecutions: 3, TrustedMinimumReliability: .75));
        var report = validator.Validate(skill);
        var first = promotion.Apply(skill, report);

        Assert.Equal(SkillLifecycleState.Experimental, first.State);
        Assert.NotEqual(SkillLifecycleState.Trusted, first.State);

        var current = first;
        for (var i = 0; i < 3; i++)
        {
            var result = SuccessfulResult(current);
            current = promotion.Apply(current, validator.Validate(current), result);
        }

        Assert.Equal(SkillLifecycleState.Trusted, current.State);
        Assert.Equal(3, current.Statistics.VerifiedSuccesses);
        Assert.InRange(current.Statistics.Reliability, .75, 1);
    }

    [Fact]
    public void TrustedSkillIsFlaggedAfterRecentFailures()
    {
        var skill = BuiltInSkills.ProjectInspection() with
        {
            Id = new SkillId("trusted.project-inspection"),
            Name = "trusted.project-inspection",
            State = SkillLifecycleState.Trusted,
            Statistics = new SkillStatistics(Executions: 4, VerifiedSuccesses: 4)
        };
        var promotion = new SkillPromotionPolicy(new SkillEngineOptions(TrustedFailureWindow: 2));
        var current = skill;
        for (var i = 0; i < 2; i++) current = promotion.Apply(current, new SkillValidator().Validate(current), FailedResult(current));

        Assert.Equal(SkillLifecycleState.NeedsRevalidation, current.State);
    }

    [Fact]
    public async Task ExecutorRunsIndependentLevelsConcurrentlyAndHonorsDependencies()
    {
        var starts = new ConcurrentDictionary<string, DateTimeOffset>();
        var runner = new RecordingRunner(starts);
        var skill = BuiltInSkills.RegressionInvestigation();
        var executor = new SkillExecutor(new SkillValidator(), runner);

        var result = await executor.ExecuteAsync(new SkillExecutionRequest(skill));

        Assert.True(result.Succeeded);
        Assert.True(starts["source"] < starts["synthesis"]);
        Assert.True(starts["git"] < starts["synthesis"]);
        Assert.True(starts["memory"] < starts["synthesis"]);
        Assert.True((starts["git"] - starts["source"]).Duration() < TimeSpan.FromMilliseconds(80));
    }

    [Fact]
    public async Task CandidateExtractionIsEvidenceBackedAndStartsUntrusted()
    {
        var extractor = new DeterministicSkillCandidateExtractor();
        var candidate = await extractor.ExtractAsync(new SkillExtractionRequest(
            MissionId.New(),
            "repair scheduler cancellation race",
            ["inspect completion", "reproduce race", "verify stress test"],
            [EvidenceId.New()],
            ["stress test passes"]), CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Equal(SkillLifecycleState.Candidate, candidate!.Definition.State);
        Assert.Equal(SkillOrigin.ExtractedFromMission, candidate.Definition.Origin);
        Assert.NotEmpty(candidate.Definition.Provenance.SafeSourceEvidence);
    }

    [Fact]
    public async Task JsonRegistryPersistsAndRestoresSkillDefinitions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "abraxius-skills-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "registry.json");
        try
        {
            var first = new SkillRegistry(new JsonSkillRegistryStore(path));
            first.Register(BuiltInSkills.ProjectInspection());
            await first.SaveAsync();
            var second = new SkillRegistry(new JsonSkillRegistryStore(path));
            await second.LoadAsync();

            Assert.True(second.TryGet(new SkillId("repo.inspect-project"), null, out var restored));
            Assert.Equal(SkillVersion.Initial, restored.Version);
            Assert.Equal(SkillStepKind.SpecialistAssignment, restored.Procedure.SafeSteps[1].Kind);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProjectScopedSkillDoesNotMatchAnotherProject()
    {
        var skill = BuiltInSkills.ProjectInspection() with
        {
            Id = new SkillId("project.inspect-local"),
            Name = "project.inspect-local",
            Preconditions = new SkillPreconditions(Scope: new SkillScope(SkillScopeKind.Project, "alpha"))
        };
        var registry = new SkillRegistry();
        registry.Register(skill);
        var matcher = new DeterministicSkillMatcher(registry);

        Assert.Empty(matcher.Match(new SkillMatchRequest("inspect project", ProjectKey: "beta")));
        Assert.NotEmpty(matcher.Match(new SkillMatchRequest("inspect project", ProjectKey: "alpha")));
    }

    [Fact]
    public async Task RequiredTypedInputIsValidatedBeforeRunnerRuns()
    {
        var invoked = 0;
        var skill = BuiltInSkills.ProjectInspection() with
        {
            Id = new SkillId("test.required-input"),
            Name = "test.required-input",
            Inputs = new SkillInputContract([new SkillParameterDefinition("project", SkillValueType.Text)])
        };
        var executor = new SkillExecutor(new SkillValidator(), new CountingRunner(() => invoked++));

        var result = await executor.ExecuteAsync(new SkillExecutionRequest(skill));

        Assert.Equal(SkillExecutionStatus.Blocked, result.Status);
        Assert.Contains(result.SafeDiagnostics, item => item.Code == "SKILL017");
        Assert.Equal(0, invoked);
    }

    [Fact]
    public void ImportedDangerousSkillCannotBeTrustedOrValidatedWithoutMutationPolicy()
    {
        var skill = BuiltInSkills.ProjectInspection() with
        {
            Id = new SkillId("imported.dangerous"),
            Name = "imported.dangerous",
            Origin = SkillOrigin.Imported,
            State = SkillLifecycleState.Trusted,
            CapabilityPolicy = new SkillCapabilityPolicy(Safety: SkillSafetyClass.Privileged)
        };

        var report = new SkillValidator().Validate(skill);

        Assert.False(report.IsValid);
        Assert.Contains(report.Diagnostics, item => item.Code == "SKILL007");
        Assert.Contains(report.Diagnostics, item => item.Code == "SKILL005");
    }

    [Fact]
    public async Task CancellationStopsSkillBeforeLaterDependencyLevel()
    {
        using var cancellation = new CancellationTokenSource();
        var ran = new ConcurrentBag<string>();
        var skill = BuiltInSkills.RegressionInvestigation();
        var executor = new SkillExecutor(new SkillValidator(), new CancellingRunner(ran, cancellation));

        var result = await executor.ExecuteAsync(new SkillExecutionRequest(skill), cancellation.Token);

        Assert.Equal(SkillExecutionStatus.Cancelled, result.Status);
        Assert.DoesNotContain("synthesis", ran);
    }

    [Fact]
    public async Task CompositionExecutesPinnedChildAndRejectsCycles()
    {
        var child = BuiltInSkills.ProjectInspection();
        var parent = child with
        {
            Id = new SkillId("mission.project-review"),
            Name = "mission.project-review",
            Procedure = new SkillProcedure(
            [
                new SkillCompositionStep(new SkillStepId("inspect"), "Run project inspection", child.Id, child.Version),
                new SkillVerificationStep(new SkillStepId("verify"), "Verify composed inspection", "Verify the composed project inspection.", Dependencies: [new SkillStepId("inspect")])
            ])
        };
        var registry = new SkillRegistry();
        registry.Register(child);
        registry.Register(parent);
        var executor = new SkillExecutor(new SkillValidator(), new NoOpSkillStepRunner(), registry: registry);

        var result = await executor.ExecuteAsync(new SkillExecutionRequest(parent));

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.SafeDiagnostics));

        var a = parent with
        {
            Id = new SkillId("composition.a"),
            Name = "composition.a",
            Procedure = new SkillProcedure([new SkillCompositionStep(new SkillStepId("b"), "B", new SkillId("composition.b"))])
        };
        var b = parent with
        {
            Id = new SkillId("composition.b"),
            Name = "composition.b",
            Procedure = new SkillProcedure([new SkillCompositionStep(new SkillStepId("a"), "A", a.Id)])
        };
        var cycleRegistry = new SkillRegistry();
        cycleRegistry.Register(a);
        cycleRegistry.Register(b);
        var cycleExecutor = new SkillExecutor(new SkillValidator(), new NoOpSkillStepRunner(), new SkillEngineOptions(MaximumCompositionDepth: 4), cycleRegistry);

        var cycleResult = await cycleExecutor.ExecuteAsync(new SkillExecutionRequest(a));

        Assert.Equal(SkillExecutionStatus.Failed, cycleResult.Status);
        Assert.Equal("CompositionCycle", cycleResult.FailureCode);
    }

    [Fact]
    public async Task RuntimeModelOperatorUsesThePhaseSixProviderAsAnExplicitStep()
    {
        var definition = BuiltInSkills.ProjectInspection();
        var context = new SkillExecutionContext(
            definition,
            SkillExecutionId.New(),
            null,
            SpecialistRole.Investigator,
            new Dictionary<string, SkillParameterValue>(),
            null,
            null,
            new Dictionary<SkillStepId, SkillStepResult>(),
            CancellationToken.None);
        var modelStep = new SkillModelStep(new SkillStepId("summarize"), "Summarize", "summarize", "Return a short structured summary.");
        var operatorInstance = new RuntimeSkillModelOperator(new MockModelProvider(TimeSpan.Zero));

        var result = await operatorInstance.RunAsync(modelStep, context);

        Assert.True(result.Succeeded);
        Assert.Contains("Deterministic synthesis", result.Summary, StringComparison.Ordinal);
        Assert.Contains("text", result.SafeOutputs.Keys);
    }

    private static SkillExecutionResult SuccessfulResult(SkillDefinition skill) => new(skill.Id, skill.Version, SkillExecutionId.New(), SkillExecutionStatus.Succeeded, "pass", null, [], SkillVerificationOutcome.Passed, TimeSpan.FromMilliseconds(1));
    private static SkillExecutionResult FailedResult(SkillDefinition skill) => new(skill.Id, skill.Version, SkillExecutionId.New(), SkillExecutionStatus.Failed, "fail", null, [], SkillVerificationOutcome.Failed, TimeSpan.FromMilliseconds(1));

    private sealed class RecordingRunner(ConcurrentDictionary<string, DateTimeOffset> starts) : ISkillStepRunner
    {
        public async ValueTask<SkillStepResult> RunAsync(SkillStep skillStep, SkillExecutionContext context)
        {
            starts[skillStep.Id.Value] = DateTimeOffset.UtcNow;
            await Task.Delay(skillStep.Id.Value is "source" or "git" or "memory" ? 30 : 1, context.CancellationToken);
            return new SkillStepResult(true, skillStep.Label, Verification: skillStep is SkillVerificationStep ? SkillVerificationOutcome.Passed : SkillVerificationOutcome.NotRun);
        }
    }

    private sealed class CountingRunner(Action callback) : ISkillStepRunner
    {
        public ValueTask<SkillStepResult> RunAsync(SkillStep skillStep, SkillExecutionContext context)
        {
            callback();
            return ValueTask.FromResult(new SkillStepResult(true, skillStep.Label));
        }
    }

    private sealed class CancellingRunner(ConcurrentBag<string> ran, CancellationTokenSource cancellation) : ISkillStepRunner
    {
        public ValueTask<SkillStepResult> RunAsync(SkillStep skillStep, SkillExecutionContext context)
        {
            ran.Add(skillStep.Id.Value);
            cancellation.Cancel();
            return ValueTask.FromResult(new SkillStepResult(true, skillStep.Label));
        }
    }
}

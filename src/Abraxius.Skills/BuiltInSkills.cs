using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Memory;
using Abraxius.Protocol;

namespace Abraxius.Skills;

public static class BuiltInSkills
{
    public static IReadOnlyList<SkillDefinition> CreateAll() =>
    [
        ProjectInspection(),
        RegressionInvestigation(),
        DotnetBuildAndTest(),
        StandardCodeChangeVerification()
    ];

    public static SkillDefinition ProjectInspection() => Create(
        "repo.inspect-project",
        "Inspect a repository's structure, manifests, source entry points, Git state, and relevant project memory.",
        SkillCategory.Repository,
        SkillSafetyClass.ReadOnly,
        SpecialistRole.Investigator,
        new SkillTriggerSet(TaskClasses: ["inspect", "repository"], Concepts: ["project inspection", "repository structure", "project map"]),
        new SkillProcedure(
        [
            new SkillContextQueryStep(new SkillStepId("memory"), "Retrieve project memory", "project structure", MemoryRetrievalMode.Hybrid),
            new SkillSpecialistAssignmentStep(new SkillStepId("inspect"), "Inspect source and manifests", SpecialistRole.Investigator, "Inspect the repository structure, manifests, entry points, and current Git state.", ["Return evidence-backed project findings."], [new SkillStepId("memory")]),
            new SkillVerificationStep(new SkillStepId("verify"), "Verify project inspection", "Verify that project structure findings have source or tool evidence.", "evidence", [new SkillStepId("inspect")])
        ]),
        ["Project structure and entry points are identified."],
        ["Project structure is supported by source or tool evidence."]);

    public static SkillDefinition RegressionInvestigation() => Create(
        "git.regression-investigation",
        "Investigate a regression using current source, Git history, and project memory in parallel before synthesizing evidence.",
        SkillCategory.Regression,
        SkillSafetyClass.ReadOnly,
        SpecialistRole.Investigator,
        new SkillTriggerSet(TaskClasses: ["regression", "investigation"], Concepts: ["regression", "which commit", "root cause", "slowdown", "cancellation race"]),
        new SkillProcedure(
        [
            new SkillSpecialistAssignmentStep(new SkillStepId("source"), "Source investigation", SpecialistRole.Investigator, "Inspect current source, symbols, diagnostics, and likely execution path.", ["Produce source evidence."]),
            new SkillSpecialistAssignmentStep(new SkillStepId("git"), "Git investigation", SpecialistRole.Investigator, "Inspect Git history and changed files related to the reported regression.", ["Produce commit/change evidence."]),
            new SkillContextQueryStep(new SkillStepId("memory"), "Memory investigation", "previous attempts, failures, and verified fixes", MemoryRetrievalMode.Hybrid),
            new SkillSpecialistAssignmentStep(new SkillStepId("synthesis"), "Synthesize findings", SpecialistRole.Coordinator, "Synthesize the parallel investigation results into ranked cause candidates.", ["Return hypotheses with supporting evidence."], [new SkillStepId("source"), new SkillStepId("git"), new SkillStepId("memory")]),
            new SkillVerificationStep(new SkillStepId("verify"), "Verify regression findings", "Verify that ranked cause candidates are supported by the collected evidence.", "evidence", [new SkillStepId("synthesis")])
        ]),
        ["A ranked cause candidate is supported by evidence."],
        ["Source, Git, and memory findings are compared before conclusion."]);

    public static SkillDefinition DotnetBuildAndTest() => Create(
        "dotnet.build-and-test",
        "Build the current .NET workspace and run its applicable tests through the normal scheduler boundary.",
        SkillCategory.Build,
        SkillSafetyClass.ReadOnly,
        SpecialistRole.Builder,
        new SkillTriggerSet(TaskClasses: ["build", "test"], Concepts: ["dotnet build", "run tests", "compile and test"]),
        new SkillProcedure(
        [
            new SkillSpecialistAssignmentStep(new SkillStepId("build"), "Build workspace", SpecialistRole.Builder, "Build the current .NET workspace and report warnings/errors.", ["Build completes or returns structured diagnostics."]),
            new SkillSpecialistAssignmentStep(new SkillStepId("test"), "Run tests", SpecialistRole.Verifier, "Run the applicable test suite for the current .NET workspace.", ["Tests complete with structured results."], [new SkillStepId("build")]),
            new SkillVerificationStep(new SkillStepId("verify"), "Verify build and test result", "Verify that the build and applicable tests satisfy the requested criteria.", "standard", [new SkillStepId("test")])
        ]),
        ["Build and tests complete without an unverified failure."],
        ["Build output and test output are independently checked."]);

    public static SkillDefinition StandardCodeChangeVerification() => Create(
        "verify.standard-code-change",
        "Independently inspect a code change, build it, run relevant tests, and check the stated requirements.",
        SkillCategory.Verification,
        SkillSafetyClass.ReadOnly,
        SpecialistRole.Verifier,
        new SkillTriggerSet(TaskClasses: ["verify", "review"], Concepts: ["verify code change", "review diff", "regression check"]),
        new SkillProcedure(
        [
            new SkillSpecialistAssignmentStep(new SkillStepId("diff"), "Inspect diff", SpecialistRole.Verifier, "Inspect the actual candidate diff and compare it with the success contract.", ["List changed files and requirement coverage."]),
            new SkillSpecialistAssignmentStep(new SkillStepId("checks"), "Run independent checks", SpecialistRole.Verifier, "Build and run relevant tests without modifying the candidate.", ["Return actual check results."], [new SkillStepId("diff")]),
            new SkillVerificationStep(new SkillStepId("decision"), "Verify change", "Determine whether the code change satisfies all required criteria.", "independent", [new SkillStepId("checks")])
        ]),
        ["All required criteria pass independent verification."],
        ["The actual diff and checks, not the implementer's claim, determine the result."]);

    private static SkillDefinition Create(
        string id,
        string description,
        SkillCategory category,
        SkillSafetyClass safety,
        SpecialistRole role,
        SkillTriggerSet triggers,
        SkillProcedure procedure,
        IReadOnlyList<string> outputs,
        IReadOnlyList<string> verification)
    {
        var skillId = new SkillId(id);
        return new SkillDefinition
        {
            Id = skillId,
            Version = SkillVersion.Initial,
            Name = id,
            Description = description,
            Category = category,
            State = SkillLifecycleState.Validated,
            Origin = SkillOrigin.BuiltIn,
            Triggers = triggers,
            Preconditions = new SkillPreconditions(RequiredRoles: [role], RequiresGit: id.Contains("git.", StringComparison.Ordinal)),
            Procedure = procedure,
            Verification = new SkillVerificationPlan(verification, RequiresArgus: true),
            CapabilityPolicy = new SkillCapabilityPolicy(Safety: safety),
            SpecialistPolicy = new SkillSpecialistPolicy(role, [role]),
            ResourceProfile = new SkillResourceProfile(MaxSteps: 32, MaxConcurrentSteps: 3, MaximumDuration: TimeSpan.FromMinutes(10)),
            Outputs = new SkillOutputContract(outputs.Select(name => new SkillParameterDefinition(name, SkillValueType.Text, Required: false)).ToArray()),
            Provenance = new SkillProvenance(SkillOrigin.BuiltIn, Creator: "Abraxius", AxlVersion: Abraxius.Axl.AxlVersion.Current)
        };
    }
}

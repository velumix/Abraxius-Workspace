using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Memory;
using Abraxius.Protocol;
using Abraxius.Skills;
using Abraxius.Scheduler;
using Abraxius.Platform;
using Abraxius.Fabric;
using Abraxius.Security;

namespace Abraxius.Runtime;

/// <summary>
/// Runtime adapter for Skill steps. Specialist steps enter AgentKernel, whose assignments
/// are already compiled into Phase 4 graphs. Capability steps are separately submitted to
/// the same scheduler and remain subject to the Skill and current runtime policy.
/// </summary>
public sealed class RuntimeSkillStepRunner(
    AgentKernel agents,
    IHybridMemoryRetriever memory,
    DagScheduler scheduler,
    IWorkExecutorRegistry executors,
    IEvidenceStore evidence,
    IPlatformEnvironment environment,
    FabricRuntime fabric,
    ISkillModelOperator? modelOperator = null) : ISkillStepRunner
{
    public async ValueTask<SkillStepResult> RunAsync(SkillStep skillStep, SkillExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(skillStep);
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        return skillStep switch
        {
            SkillContextQueryStep query => await RunContextQueryAsync(query, context).ConfigureAwait(false),
            SkillSpecialistAssignmentStep assignment => await RunAssignmentAsync(assignment, context).ConfigureAwait(false),
            SkillCapabilityCallStep capability => await RunCapabilityAsync(capability, context).ConfigureAwait(false),
            SkillVerificationStep verification => await RunVerificationAsync(verification, context).ConfigureAwait(false),
            SkillModelStep modelStep when modelOperator is not null => await modelOperator.RunAsync(modelStep, context, context.CancellationToken).ConfigureAwait(false),
            SkillModelStep => new SkillStepResult(false, "Model steps require an explicit runtime model operator.", FailureCode: "ModelOperatorUnavailable"),
            SkillConditionalStep conditional => RunConditional(conditional, context),
            _ => new SkillStepResult(false, $"Unsupported Skill step type {skillStep.GetType().Name}.", FailureCode: "StepUnsupported")
        };
    }

    private async ValueTask<SkillStepResult> RunContextQueryAsync(SkillContextQueryStep step, SkillExecutionContext context)
    {
        var result = await memory.RetrieveAsync(new MemorySearchQuery(step.Query, Limit: 16, ProjectKey: context.ProjectKey, Mode: step.Mode), context.CancellationToken).ConfigureAwait(false);
        var evidence = result.Hits.SelectMany(static hit => hit.Entry.Evidence.Where(static link => link.EvidenceId.HasValue).Select(static link => link.EvidenceId!.Value)).Distinct().ToArray();
        var summary = result.Hits.Count == 0 ? "No matching project memory was found." : $"Retrieved {result.Hits.Count} memory result(s) using {result.Planner ?? "hybrid"} retrieval.";
        return new SkillStepResult(true, summary, Evidence: evidence);
    }

    private async ValueTask<SkillStepResult> RunAssignmentAsync(SkillSpecialistAssignmentStep step, SkillExecutionContext context)
    {
        if (!context.Skill.SpecialistPolicy.Allows(step.Role)) return new SkillStepResult(false, $"Skill does not permit specialist role {step.Role}.", FailureCode: "SpecialistDenied");
        var intent = new Intent(step.Objective, CorrelationId.New());
        var contract = new MissionSuccessContract(step.Objective, step.SafeSuccessCriteria, context.Skill.Verification.SafeCriteria);
        var result = await agents.RunMissionAsync(intent, contract, step.Role, context.CancellationToken).ConfigureAwait(false);
        var verification = result.Succeeded ? SkillVerificationOutcome.Passed : result.Mission.State == MissionState.Blocked ? SkillVerificationOutcome.Inconclusive : SkillVerificationOutcome.Failed;
        var evidence = agents.Results.Where(pair => result.AssignmentResults.ContainsKey(pair.Key)).SelectMany(static pair => pair.Value.SafeEvidence).Distinct().ToArray();
        return new SkillStepResult(result.Succeeded, result.Summary, Evidence: evidence, Verification: verification, FailureCode: result.Succeeded ? null : "SpecialistMissionFailed");
    }

    private async ValueTask<SkillStepResult> RunVerificationAsync(SkillVerificationStep step, SkillExecutionContext context)
    {
        var intent = new Intent(step.Objective, CorrelationId.New());
        var contract = new MissionSuccessContract(step.Objective, [step.Objective], context.Skill.Verification.SafeCriteria);
        var result = await agents.RunMissionAsync(intent, contract, SpecialistRole.Verifier, context.CancellationToken).ConfigureAwait(false);
        var verification = result.Succeeded ? SkillVerificationOutcome.Passed : result.Mission.State == MissionState.Blocked ? SkillVerificationOutcome.Inconclusive : SkillVerificationOutcome.Failed;
        return new SkillStepResult(result.Succeeded, result.Summary, Verification: verification, FailureCode: result.Succeeded ? null : "VerificationFailed");
    }

    private async ValueTask<SkillStepResult> RunCapabilityAsync(SkillCapabilityCallStep step, SkillExecutionContext context)
    {
        if (!context.Skill.CapabilityPolicy.SafeCapabilities.Contains(step.Capability)) return new SkillStepResult(false, $"Capability '{step.Capability}' is not declared by the Skill policy.", FailureCode: "CapabilityNotDeclared");
        if (step.Mutation && context.Skill.CapabilityPolicy.Safety == SkillSafetyClass.ReadOnly) return new SkillStepResult(false, "Read-only Skill attempted a mutation.", FailureCode: "MutationDenied");
        var executionId = ExecutionId.New();
        var node = new ExecutionNodeDefinition(NodeId.New(), TaskId.New(), executionId, new ToolWorkDescriptor(step.Capability, step.Operation, new ActionTarget(step.Target), step.SafeParameters.ToDictionary(static item => item.Key, static item => item.Value.AsText(), StringComparer.Ordinal), step.Mutation), priority: WorkPriority.Interactive, timeout: TimeSpan.FromMinutes(2));
        var correlationId = CorrelationId.New();
        var graph = new ExecutionGraph(executionId, correlationId, [node], [node.Id], metadata: new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["skill.id"] = context.Skill.Id.Value,
            ["skill.version"] = context.Skill.Version.ToString(),
            ["skill.execution"] = context.ExecutionId.ToString()
        }).Compile();
        var schedulerContext = new SchedulerExecutionContext
        {
            ExecutionId = executionId,
            CorrelationId = correlationId,
            Environment = environment,
            Executors = executors,
            Constraints = new ExecutionConstraints { AllowMutation = step.Mutation, RequireVerification = false },
            RemoteExecutor = fabric.RemoteExecutor,
            RemoteHosts = fabric.RemoteHosts,
            SecurityContext = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["principal.type"] = PrincipalType.Skill.ToString(),
                ["principal.id"] = $"skill:{context.Skill.Id.Value}",
                ["skill.execution"] = context.ExecutionId.ToString()
            }
        };
        var result = await scheduler.ExecuteAsync(graph, schedulerContext, evidence, context.CancellationToken).ConfigureAwait(false);
        var work = result.Results.Values.FirstOrDefault();
        return new SkillStepResult(result.Succeeded, work?.Summary ?? "Capability step completed without a summary.", Evidence: work?.Evidence ?? Array.Empty<EvidenceId>(), FailureCode: result.Succeeded ? null : "CapabilityExecutionFailed");
    }

    private static SkillStepResult RunConditional(SkillConditionalStep step, SkillExecutionContext context)
    {
        var satisfied = step.Condition.Kind switch
        {
            SkillConditionKind.StepSucceeded => context.DependencyResults.TryGetValue(step.Condition.Step, out var result) && result.Succeeded,
            SkillConditionKind.OutputExists => context.DependencyResults.TryGetValue(step.Condition.Step, out var output) && output.SafeOutputs.ContainsKey(step.Condition.Value ?? string.Empty),
            SkillConditionKind.StatusEquals => context.DependencyResults.TryGetValue(step.Condition.Step, out var status) && string.Equals(status.Summary, step.Condition.Value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        return new SkillStepResult(true, satisfied ? $"Condition selected {step.ThenStep}." : $"Condition selected {step.ElseStep?.ToString() ?? "no branch"}.");
    }
}

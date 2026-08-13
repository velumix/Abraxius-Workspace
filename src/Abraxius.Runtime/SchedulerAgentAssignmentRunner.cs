using System.Collections.Immutable;
using System.Text;
using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Ledger;
using Abraxius.Protocol;
using Abraxius.Scheduler;
using Abraxius.Fabric;

namespace Abraxius.Runtime;

/// <summary>
/// Runtime adapter that turns one specialist assignment into an existing Phase 4 graph.
/// It deliberately returns proposals/results; it never grants a specialist capabilities or writes files.
/// </summary>
public sealed class SchedulerAgentAssignmentRunner(
    DagScheduler scheduler,
    IWorkExecutorRegistry executors,
    IEvidenceStore evidence,
    Abraxius.Platform.IPlatformEnvironment environment,
    FabricRuntime fabric) : IAgentAssignmentRunner
{
    public async ValueTask<AgentAssignmentResult> RunAsync(
        SpecialistDefinition definition,
        SpecialistInstance instance,
        AgentAssignment assignment,
        MissionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(assignment);
        var executionId = ExecutionId.New();
        var root = NewNode(executionId, CreateWork(definition, assignment, context), assignment.Priority, 0);
        var nodes = ImmutableArray.CreateBuilder<ExecutionNodeDefinition>();
        nodes.Add(root);
        if (definition.Role == SpecialistRole.Verifier)
        {
            var verificationNode = NewNode(
                executionId,
                new VerificationWorkDescriptor(assignment.Objective, [root.Id], "independent"),
                WorkPriority.Critical,
                1,
                [root.Id]);
            nodes.Add(verificationNode);
        }

        var graph = new ExecutionGraph(
            executionId,
            context.Mission.Intent.CorrelationId,
            nodes.ToImmutable(),
            [root.Id],
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["agent.mission"] = assignment.MissionId.ToString(),
                ["agent.assignment"] = assignment.Id.ToString(),
                ["agent.role"] = definition.Role.ToString(),
                ["agent.instance"] = instance.Id.ToString()
            }).Compile();
        var schedulerContext = new SchedulerExecutionContext
        {
            ExecutionId = executionId,
            CorrelationId = context.Mission.Intent.CorrelationId,
            Environment = environment,
            Executors = executors,
            Constraints = context.Mission.Intent.Constraints with { RequireVerification = false },
            SecurityContext = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["principal.type"] = PrincipalTypeName,
                ["principal.id"] = $"specialist:{instance.Id}",
                ["specialist.role"] = definition.Role.ToString(),
                ["agent.instance"] = instance.Id.ToString(),
                ["mission.id"] = assignment.MissionId.ToString(),
                ["assignment.id"] = assignment.Id.ToString()
            },
            RemoteExecutor = fabric.RemoteExecutor,
            RemoteHosts = fabric.RemoteHosts
        };
        var execution = await scheduler.ExecuteAsync(graph, schedulerContext, evidence, cancellationToken).ConfigureAwait(false);
        var work = execution.Results.Values.LastOrDefault();
        var summary = work?.Summary ?? (execution.Succeeded ? "Assignment completed without a summary." : "Assignment failed in the scheduler.");
        var verification = definition.Role == SpecialistRole.Verifier
            ? (Abraxius.Agents.VerificationStatus?)(execution.Succeeded ? Abraxius.Agents.VerificationStatus.Passed : Abraxius.Agents.VerificationStatus.Failed)
            : null;
        return new AgentAssignmentResult(
            execution.Succeeded,
            summary,
            work?.Evidence ?? Array.Empty<EvidenceId>(),
            verification,
            work?.Value?.ToString(),
            execution.Succeeded ? null : "SchedulerExecutionFailed");
    }

    private const string PrincipalTypeName = "Specialist";

    private static WorkDescriptor CreateWork(SpecialistDefinition definition, AgentAssignment assignment, MissionContext context) => definition.Role switch
    {
        SpecialistRole.Investigator => new MemoryWorkDescriptor(assignment.Objective, 16),
        SpecialistRole.Verifier => new ModelWorkDescriptor(BuildInstruction(definition, assignment, context), ExpectedOutput: new OutputContract("verification-summary"), MaxOutputTokens: definition.ModelPolicy.MaxOutputTokens),
        SpecialistRole.Builder => new ModelWorkDescriptor(BuildInstruction(definition, assignment, context), ExpectedOutput: new OutputContract("implementation-proposal"), MaxOutputTokens: definition.ModelPolicy.MaxOutputTokens),
        _ => new ModelWorkDescriptor(BuildInstruction(definition, assignment, context), MaxOutputTokens: definition.ModelPolicy.MaxOutputTokens)
    };

    private static string BuildInstruction(SpecialistDefinition definition, AgentAssignment assignment, MissionContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are the " + definition.DisplayName + " " + definition.Role + " specialist.");
        builder.AppendLine("Assignment: " + assignment.Objective);
        builder.AppendLine("Return a concise operational result. Do not claim authorization or execution you did not receive.");
        builder.AppendLine("Success criteria: " + string.Join("; ", assignment.SuccessCriteria));
        if (assignment.SafeEvidence.Count > 0) builder.AppendLine("Evidence references: " + string.Join(' ', assignment.SafeEvidence.Select(static item => "e#" + item)));
        if (context.MemoryContext is { Text.Length: > 0 } memory) builder.AppendLine("Curated memory context:\n" + memory.Text);
        return builder.ToString();
    }

    private static ExecutionNodeDefinition NewNode(ExecutionId executionId, WorkDescriptor work, WorkPriority priority, int order, ImmutableArray<NodeId> dependencies = default) => new(
        NodeId.New(), TaskId.New(), executionId, work, dependencies, priority: priority, timeout: TimeSpan.FromMinutes(2), creationOrder: order);
}

using System.Collections.Immutable;
using Abraxius.Core;
using Abraxius.Protocol;

namespace Abraxius.Runtime;

internal static class DemoExecutionGraphFactory
{
    public static CompiledExecutionGraph Create(Intent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var executionId = ExecutionId.New();
        var root = NewNode(
            executionId,
            new BackgroundWorkDescriptor("intent.accepted"),
            WorkPriority.Interactive,
            creationOrder: 0);
        var git = NewNode(
            executionId,
            new ToolWorkDescriptor(new CapabilityId("demo"), "status", new ActionTarget("repository")),
            WorkPriority.Interactive,
            [root.Id],
            TimeSpan.FromSeconds(5),
            1);
        var search = NewNode(
            executionId,
            new ToolWorkDescriptor(new CapabilityId("demo"), "search_files", new ActionTarget("src")),
            WorkPriority.Interactive,
            [root.Id],
            TimeSpan.FromSeconds(5),
            2);
        var memory = NewNode(
            executionId,
            new MemoryWorkDescriptor(intent.Objective),
            WorkPriority.Normal,
            [root.Id],
            TimeSpan.FromSeconds(5),
            3);
        var synthesis = NewNode(
            executionId,
            new SynthesisWorkDescriptor(intent.Objective, [git.Id, search.Id, memory.Id]),
            WorkPriority.Interactive,
            [git.Id, search.Id, memory.Id],
            TimeSpan.FromSeconds(10),
            4);
        var verification = NewNode(
            executionId,
            new VerificationWorkDescriptor("the synthesized result is complete", [synthesis.Id]),
            WorkPriority.Critical,
            [synthesis.Id],
            TimeSpan.FromSeconds(5),
            5);

        return new ExecutionGraph(
            executionId,
            intent.CorrelationId,
            [root, git, search, memory, synthesis, verification],
            [root.Id],
            metadata: new Dictionary<string, string>
            {
                ["demo"] = "true",
                ["objective"] = intent.Objective
            }).Compile();
    }

    private static ExecutionNodeDefinition NewNode(
        ExecutionId executionId,
        WorkDescriptor work,
        WorkPriority priority,
        ImmutableArray<NodeId> dependencies = default,
        TimeSpan? timeout = null,
        int creationOrder = 0)
    {
        return new ExecutionNodeDefinition(
            NodeId.New(),
            TaskId.New(),
            executionId,
            work,
            dependencies,
            priority: priority,
            timeout: timeout,
            creationOrder: creationOrder);
    }
}

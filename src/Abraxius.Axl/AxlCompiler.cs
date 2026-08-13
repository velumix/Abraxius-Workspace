using System.Collections.Immutable;
using System.Globalization;
using Abraxius.Core;
using Abraxius.Protocol;

namespace Abraxius.Axl;

/// <summary>Pure mapper from validated AXL semantics to the existing Phase 2 runtime graph.</summary>
public sealed class AxlExecutionCompiler : IAxlExecutionCompiler
{
    public AxlCompilationResult Compile(AxlDocument document, AxlCompilationContext context, AxlValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var diagnostics = AxlValidator.Validate(document, options);
        if (diagnostics.Any(static diagnostic => diagnostic.Severity == AxlDiagnosticSeverity.Error))
        {
            return new(null, null, ImmutableDictionary<AxlCommandId, NodeId>.Empty, diagnostics);
        }

        var nodeMap = ImmutableDictionary.CreateBuilder<AxlCommandId, NodeId>();
        for (var index = 0; index < document.Commands.Length; index++)
        {
            var command = document.Commands[index];
            if (command.Id is { } id && command is not AxlIntent and not AxlResult and not AxlState and not AxlSkill)
            {
                nodeMap[id] = NodeId.New();
            }
        }

        var nodes = ImmutableArray.CreateBuilder<ExecutionNodeDefinition>();
        Intent? intent = null;
        for (var index = 0; index < document.Commands.Length; index++)
        {
            var command = document.Commands[index];
            if (command is AxlIntent parsedIntent)
            {
                if (intent is not null)
                {
                    diagnostics = diagnostics.Add(new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.SemanticConflict, "Only one intent command may define a compilation objective."));
                }
                else
                {
                    intent = new Intent(parsedIntent.Objective, context.CorrelationId, parsedIntent.Attributes)
                    {
                        Priority = parsedIntent.Priority
                    };
                }

                continue;
            }

            if (!TryCreateWork(command, nodeMap, out var work, out var dependencies, out var failure))
            {
                if (failure is not null)
                {
                    diagnostics = diagnostics.Add(failure);
                }

                continue;
            }

            var nodeId = command.Id is { } commandId && nodeMap.TryGetValue(commandId, out var mappedNodeId)
                ? mappedNodeId
                : NodeId.New();
            var node = new ExecutionNodeDefinition(
                nodeId,
                TaskId.New(),
                context.ExecutionId,
                work,
                dependencies,
                priority: command is AxlVerification ? WorkPriority.High : WorkPriority.Interactive,
                creationOrder: index);
            nodes.Add(node);
        }

        if (diagnostics.Any(static diagnostic => diagnostic.Severity == AxlDiagnosticSeverity.Error))
        {
            return new(intent, null, nodeMap.ToImmutable(), diagnostics);
        }

        ExecutionGraph? graph = null;
        if (nodes.Count > 0)
        {
            graph = new ExecutionGraph(
                context.ExecutionId,
                context.CorrelationId,
                nodes.ToImmutable(),
                metadata: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["axl.version"] = document.Version.ToString(),
                    ["axl.hash"] = document.SemanticHash()
                });
        }

        return new(intent, graph, nodeMap.ToImmutable(), diagnostics);
    }

    private static bool TryCreateWork(
        AxlCommand command,
        ImmutableDictionary<AxlCommandId, NodeId>.Builder nodeMap,
        out WorkDescriptor work,
        out ImmutableArray<NodeId> dependencies,
        out AxlDiagnostic? failure)
    {
        work = null!;
        dependencies = ImmutableArray<NodeId>.Empty;
        failure = null;

        switch (command)
        {
            case AxlFindCode find:
                work = new ToolWorkDescriptor(
                    new CapabilityId("code.search"),
                    "search",
                    new ActionTarget(find.Scope?.ToString() ?? "current_project"),
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["q"] = find.Query,
                        ["lim"] = find.Limit.ToString(CultureInfo.InvariantCulture)
                    });
                return true;
            case AxlCapabilityCall call:
                work = new ToolWorkDescriptor(
                    new CapabilityId(call.Capability.Value),
                    call.Operation,
                    new ActionTarget(call.Target),
                    (call.Parameters ?? ImmutableDictionary<string, AxlValue>.Empty).ToStringDictionary(),
                    call.Mutation);
                return true;
            case AxlMemoryQuery memory:
                work = new MemoryWorkDescriptor(memory.Query, memory.Limit, memory.Scopes.IsDefault ? null : memory.Scopes);
                return true;
            case AxlSynthesis synthesis:
                if (!TryResolveDependencies(synthesis.Inputs, nodeMap, out dependencies, out failure))
                {
                    return false;
                }

                work = new SynthesisWorkDescriptor(synthesis.Objective, dependencies.Select(static id => new NodeId(id.Value)).ToImmutableArray());
                return true;
            case AxlVerification verification:
                if (!TryResolveDependencies(verification.Inputs, nodeMap, out dependencies, out failure))
                {
                    return false;
                }

                work = new VerificationWorkDescriptor(verification.Objective, dependencies.Select(static id => new NodeId(id.Value)).ToImmutableArray(), verification.Profile);
                return true;
            case AxlDelegation delegation:
                work = new ModelWorkDescriptor($"delegate agent={delegation.Agent} mode={delegation.Mode} objective={delegation.Objective}");
                return true;
            case AxlResult or AxlState or AxlSkill:
                // Result/state records are protocol observations, not executable graph nodes.
                return false;
            default:
                failure = new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.UnsupportedOperation, $"Command '{command.Name}' cannot be compiled into an execution node.");
                return false;
        }
    }

    private static bool TryResolveDependencies(
        ImmutableArray<AxlReference> references,
        ImmutableDictionary<AxlCommandId, NodeId>.Builder nodeMap,
        out ImmutableArray<NodeId> dependencies,
        out AxlDiagnostic? failure)
    {
        var values = ImmutableArray.CreateBuilder<NodeId>();
        failure = null;
        foreach (var reference in references)
        {
            if (reference.Kind != AxlReferenceKind.Command || !AxlCommandId.TryParse($"c#{reference.Value}", out var commandId) || !nodeMap.TryGetValue(commandId, out var nodeId))
            {
                failure = new(AxlDiagnosticSeverity.Error, AxlDiagnosticCode.UnknownCommandReference, $"Cannot resolve dependency {reference} to a compiled execution node.");
                dependencies = ImmutableArray<NodeId>.Empty;
                return false;
            }

            values.Add(nodeId);
        }

        dependencies = values.ToImmutable();
        return true;
    }
}

public static class AxlCapabilityRequestCompiler
{
    public static CapabilityRequest Compile(
        AxlCapabilityCall call,
        ExecutionId executionId,
        TaskId taskId,
        CorrelationId correlationId)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new CapabilityRequest(
            new CapabilityId(call.Capability.Value),
            call.Operation,
            call.Target,
            (call.Parameters ?? ImmutableDictionary<string, AxlValue>.Empty).ToStringDictionary(),
            correlationId,
            executionId,
            taskId);
    }

    public static ProposedAction ToProposedAction(AxlCapabilityCall call, IReadOnlyList<EvidenceId>? evidence = null) =>
        new(
            new CapabilityId(call.Capability.Value),
            $"{call.Operation} {call.Target}",
            new ActionTarget(call.Target),
            call.Operation,
            (call.Parameters ?? ImmutableDictionary<string, AxlValue>.Empty).ToStringDictionary(),
            evidence);
}

internal static class AxlValueConversions
{
    public static IReadOnlyDictionary<string, string> ToStringDictionary(this IReadOnlyDictionary<string, AxlValue> values)
    {
        var result = new Dictionary<string, string>(values.Count, StringComparer.Ordinal);
        foreach (var pair in values)
        {
            result[pair.Key] = ToStringValue(pair.Value);
        }

        return result;
    }

    private static string ToStringValue(AxlValue value) => value switch
    {
        AxlValue.Text text => text.Value,
        AxlValue.Identifier identifier => identifier.Value,
        AxlValue.SignedInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
        AxlValue.UnsignedInteger unsigned => unsigned.Value.ToString(CultureInfo.InvariantCulture),
        AxlValue.DecimalValue decimalValue => decimalValue.Value.ToString(CultureInfo.InvariantCulture),
        AxlValue.BooleanValue boolean => boolean.Value ? "true" : "false",
        AxlValue.ReferenceValue reference => reference.Value.ToString(),
        AxlValue.Null => string.Empty,
        _ => AxlFormatter.ValueToCanonical(value)
    };
}

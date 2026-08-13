using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using Abraxius.Core;
using Abraxius.Lattice;
using Abraxius.Memory;
using Abraxius.Models;
using Abraxius.Platform;
using Abraxius.Protocol;
using Abraxius.Scheduler;

namespace Abraxius.Runtime;

/// <summary>Provider adapters used by the headless runtime and application hosts.</summary>
internal static class RuntimeWorkExecutorFactory
{
    public static IWorkExecutorRegistry Create(
        IModelProvider model,
        IMemoryProvider memory,
        IHybridMemoryRetriever memoryRetriever,
        LatticeExecutor lattice,
        IEvidenceStore evidence,
        IRuntimeEventSink events)
    {
        return new WorkExecutorRegistry(
        [
            new ModelWorkExecutor(model, events, new MemoryContextCompiler(memoryRetriever)),
            new ToolWorkExecutor(lattice, events),
            new MemoryWorkExecutor(memory, events),
            new CpuWorkExecutor(),
            new IoWorkExecutor(),
            new VerificationWorkExecutor(evidence, events),
            new BackgroundWorkExecutor()
        ]);
    }
}

internal sealed class ModelWorkExecutor(IModelProvider model, IRuntimeEventSink events, MemoryContextCompiler? contextCompiler = null) : IWorkExecutor
{
    private readonly MemoryContextCompiler? _contextCompiler = contextCompiler;
    public ExecutorKind Kind => ExecutorKind.Model;

    public async ValueTask<WorkResult> ExecuteAsync(ExecutionNodeDefinition node, SchedulerWorkContext context)
    {
        var (instruction, modelName, expectedOutput, maxOutputTokens, stream) = node.Work switch
        {
            ModelWorkDescriptor work => (work.Instruction, work.Model, work.ExpectedOutput, work.MaxOutputTokens, work.Stream),
            SynthesisWorkDescriptor work => (BuildSynthesisPrompt(work, context), null, work.ExpectedOutput, null, false),
            _ => throw new WorkExecutionException(new RuntimeError(
                ErrorCategory.Model,
                "invalid_model_descriptor",
                $"Work descriptor '{node.Work.GetType().Name}' cannot run in the model executor."))
        };

        var preferRemote = context.Execution.EffectiveBudget.PreferRemote ||
            context.Execution.Environment.ExecutionMode == RuntimeExecutionMode.Remote;
        var modelResolution = context.Execution.CapabilityResolver.Resolve(
            PlatformCapabilities.LocalModelInference,
            preferRemote: preferRemote);
        if (modelResolution.Route.Placement == ExecutionPlacement.Remote)
        {
            if (modelResolution.Route.HostId is not { } hostId || context.Execution.RemoteExecutor is null)
            {
                throw new WorkExecutionException(new RuntimeError(
                    ErrorCategory.Transport,
                    "remote_executor_unavailable",
                    "Model inference was resolved remotely, but no remote executor is registered.",
                    IsTransient: true));
            }

            return await context.Execution.RemoteExecutor.ExecuteAsync(
                new RemoteWorkRequest(
                    hostId,
                    node.ExecutionId,
                    node.TaskId,
                    context.Execution.CorrelationId,
                    node,
                    context.DependencyResults),
                context).ConfigureAwait(false);
        }

        if (!modelResolution.IsExecutable)
        {
            throw new WorkExecutionException(new RuntimeError(
                ErrorCategory.Model,
                "capability_unavailable",
                modelResolution.Error?.Message ?? "Local model inference is unavailable."));
        }

        await events.PublishAsync(new ModelRequestedEvent(
            DateTimeOffset.UtcNow,
            node.ExecutionId,
            node.TaskId,
            context.Execution.CorrelationId,
            "runtime.model",
            modelName ?? "mock-reasoner",
            node.Priority)).ConfigureAwait(false);

        var started = Stopwatch.GetTimestamp();
        ModelResult result;
        try
        {
            var modelRequest = new ModelRequest(
                instruction,
                modelName,
                ExpectedJsonSchema: expectedOutput?.JsonSchema,
                Priority: node.Priority,
                Timeout: node.Timeout)
            {
                ExecutionId = node.ExecutionId,
                TaskId = node.TaskId,
                TaskClass = node.Work is SynthesisWorkDescriptor ? IntelligenceTaskClass.Planning : IntelligenceTaskClass.General,
                RequiredCapabilities = expectedOutput is not null ? [ModelCapability.StructuredOutput] : [],
                MaxOutputTokens = maxOutputTokens,
                Stream = stream,
                ExecutionMaximumCost = context.Execution.Constraints.MaxCost,
                ExecutionMaximumCalls = context.Execution.Constraints.MaxModelCalls
            };
            if (node.Work is SynthesisWorkDescriptor synthesis && _contextCompiler is not null)
            {
                var currentState = context.DependencyResults.Values
                    .Select(static value => value.Summary)
                    .OfType<string>()
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .ToArray();
                var package = await _contextCompiler.CompileAsync(new ContextCompilationRequest(
                    synthesis.Objective,
                    new MemorySearchQuery(synthesis.Objective, Limit: 16),
                    ReservedOutputTokens: maxOutputTokens ?? 4_000,
                    CurrentState: currentState), context.CancellationToken).ConfigureAwait(false);
                modelRequest = modelRequest.WithMemoryContext(package);
            }

            if (stream)
            {
                result = await ConsumeModelStreamAsync(model, modelRequest, node, context, events).ConfigureAwait(false);
            }
            else
            {
                result = await model.InferAsync(modelRequest, context.CancellationToken).ConfigureAwait(false);
            }

        }
        catch (ModelProviderException exception)
        {
            throw new WorkExecutionException(exception.Error);
        }
        catch (IntelligenceRoutingException exception)
        {
            throw new WorkExecutionException(exception.Error);
        }
        var latency = Stopwatch.GetElapsedTime(started);

        if (result.Route is { } route)
        {
            await events.PublishAsync(new IntelligenceRouteSelectedEvent(
                DateTimeOffset.UtcNow,
                node.ExecutionId,
                node.TaskId,
                context.Execution.CorrelationId,
                "runtime.intelligence",
                route.Tier.ToString(),
                route.Gateway.ToString(),
                route.Route,
                route.Reason,
                route.EstimatedCost)).ConfigureAwait(false);
        }

        await events.PublishAsync(new ModelCompletedEvent(
            DateTimeOffset.UtcNow,
            node.ExecutionId,
            node.TaskId,
            context.Execution.CorrelationId,
            "runtime.model",
            result.Model,
            latency,
            result.Usage?.InputTokens,
            result.Usage?.OutputTokens)).ConfigureAwait(false);

        return new WorkResult(
            ResultId.New(),
            result.Text,
            [],
            JsonSerializer.SerializeToElement(result));
    }

    private static string BuildSynthesisPrompt(SynthesisWorkDescriptor descriptor, SchedulerWorkContext context)
    {
        var evidence = context.DependencyResults.Values
            .Select(static result => result.Summary)
            .Where(static summary => !string.IsNullOrWhiteSpace(summary));
        return $"{descriptor.Objective}{Environment.NewLine}Evidence summaries:{Environment.NewLine}{string.Join(Environment.NewLine, evidence)}";
    }

    private static async ValueTask<ModelResult> ConsumeModelStreamAsync(
        IModelProvider model,
        ModelRequest request,
        ExecutionNodeDefinition node,
        SchedulerWorkContext context,
        IRuntimeEventSink events)
    {
        ModelResult? completed = null;
        await foreach (var streamEvent in model.StreamAsync(request, context.CancellationToken).ConfigureAwait(false))
        {
            switch (streamEvent)
            {
                case ModelStreamEvent.Token token:
                    await events.PublishAsync(new ModelStreamingEvent(
                        DateTimeOffset.UtcNow,
                        node.ExecutionId,
                        node.TaskId,
                        context.Execution.CorrelationId,
                        "runtime.model",
                        request.Model ?? "selected",
                        token.Text)).ConfigureAwait(false);
                    break;
                case ModelStreamEvent.Completed completion:
                    completed = completion.Result;
                    break;
            }
        }

        return completed ?? throw new WorkExecutionException(new RuntimeError(
            ErrorCategory.Model,
            "stream_completed_without_result",
            "The model stream ended without a completion result."));
    }
}

internal sealed class ToolWorkExecutor(LatticeExecutor lattice, IRuntimeEventSink events) : IWorkExecutor
{
    public ExecutorKind Kind => ExecutorKind.Tool;

    public async ValueTask<WorkResult> ExecuteAsync(ExecutionNodeDefinition node, SchedulerWorkContext context)
    {
        if (node.Work is not ToolWorkDescriptor descriptor)
        {
            throw new WorkExecutionException(new RuntimeError(
                ErrorCategory.Tool,
                "invalid_tool_descriptor",
                $"Work descriptor '{node.Work.GetType().Name}' cannot run in the tool executor."));
        }

        var preferRemote = context.Execution.EffectiveBudget.PreferRemote ||
            context.Execution.Environment.ExecutionMode == RuntimeExecutionMode.Remote;
        var resolution = context.Execution.CapabilityResolver.Resolve(descriptor.Capability, preferRemote: preferRemote);
        var discoveredLocally = lattice.Discover().Any(capability => capability.Id == descriptor.Capability);
        if (!resolution.IsExecutable && !discoveredLocally)
        {
            throw new WorkExecutionException(new RuntimeError(
                ErrorCategory.Tool,
                "capability_unavailable",
                resolution.Error?.Message ?? $"Capability '{descriptor.Capability}' is unavailable."));
        }

        if (resolution.Route.Placement == ExecutionPlacement.Remote)
        {
            if (resolution.Route.HostId is not { } hostId || context.Execution.RemoteExecutor is null)
            {
                throw new WorkExecutionException(new RuntimeError(
                    ErrorCategory.Transport,
                    "remote_executor_unavailable",
                    $"Capability '{descriptor.Capability}' was resolved remotely, but no remote executor is registered.",
                    IsTransient: true));
            }

            var remoteResult = await context.Execution.RemoteExecutor.ExecuteAsync(
                new RemoteWorkRequest(
                    hostId,
                    node.ExecutionId,
                    node.TaskId,
                    context.Execution.CorrelationId,
                    node,
                    context.DependencyResults),
                context).ConfigureAwait(false);
            return remoteResult;
        }

        var request = new CapabilityRequest(
            descriptor.Capability,
            descriptor.Operation,
            descriptor.Target.Value,
            descriptor.Parameters,
            context.Execution.CorrelationId,
            node.ExecutionId,
            node.TaskId,
            context.DependencyResults.Values.SelectMany(static result => result.Evidence).ToArray(),
            context.Execution.SecurityContext);
        var started = Stopwatch.GetTimestamp();
        await events.PublishAsync(new ToolRequestedEvent(
            DateTimeOffset.UtcNow,
            node.ExecutionId,
            node.TaskId,
            context.Execution.CorrelationId,
            "runtime.tool",
            descriptor.Capability.Value,
            descriptor.Operation,
            descriptor.Target.Value)).ConfigureAwait(false);

        var result = await lattice.ExecuteAsync(request, context.CancellationToken).ConfigureAwait(false);
        var duration = Stopwatch.GetElapsedTime(started);
        await events.PublishAsync(new ToolCompletedEvent(
            DateTimeOffset.UtcNow,
            node.ExecutionId,
            node.TaskId,
            context.Execution.CorrelationId,
            "runtime.tool",
            descriptor.Capability,
            result.Succeeded,
            duration,
            result.Evidence)).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new WorkExecutionException(result.Error ?? new RuntimeError(
                ErrorCategory.Tool,
                "tool_failed",
                $"Capability '{descriptor.Capability}' failed operation '{descriptor.Operation}'."));
        }

        return new WorkResult(
            result.ResultId ?? ResultId.New(),
            result.Summary,
            result.Evidence,
            JsonSerializer.SerializeToElement(result.Values ?? ImmutableDictionary<string, string>.Empty));
    }
}

internal sealed class MemoryWorkExecutor(IMemoryProvider memory, IRuntimeEventSink events) : IWorkExecutor
{
    public ExecutorKind Kind => ExecutorKind.Memory;

    public async ValueTask<WorkResult> ExecuteAsync(ExecutionNodeDefinition node, SchedulerWorkContext context)
    {
        if (node.Work is not MemoryWorkDescriptor descriptor)
        {
            throw new WorkExecutionException(new RuntimeError(
                ErrorCategory.Memory,
                "invalid_memory_descriptor",
                $"Work descriptor '{node.Work.GetType().Name}' cannot run in the memory executor."));
        }

        await events.PublishAsync(new MemoryRequestedEvent(
            DateTimeOffset.UtcNow,
            node.ExecutionId,
            node.TaskId,
            context.Execution.CorrelationId,
            "runtime.memory",
            descriptor.Query)).ConfigureAwait(false);
        var result = await memory.QueryAsync(
            new MemoryQuery(descriptor.Query, descriptor.Limit, descriptor.Scopes, node.ExecutionId),
            context.CancellationToken).ConfigureAwait(false);
        await events.PublishAsync(new MemoryCompletedEvent(
            DateTimeOffset.UtcNow,
            node.ExecutionId,
            node.TaskId,
            context.Execution.CorrelationId,
            "runtime.memory",
            result.Latency,
            result.Hits.Count)).ConfigureAwait(false);
        return new WorkResult(
            ResultId.New(),
            $"Memory returned {result.Hits.Count} references.",
            result.Hits.SelectMany(static hit => hit.Evidence).ToArray(),
            JsonSerializer.SerializeToElement(result));
    }
}

internal sealed class CpuWorkExecutor : IWorkExecutor
{
    public ExecutorKind Kind => ExecutorKind.Cpu;

    public ValueTask<WorkResult> ExecuteAsync(ExecutionNodeDefinition node, SchedulerWorkContext context) =>
        ValueTask.FromResult(WorkResult.Empty($"CPU operation '{node.Work}' completed."));
}

internal sealed class IoWorkExecutor : IWorkExecutor
{
    public ExecutorKind Kind => ExecutorKind.Io;

    public ValueTask<WorkResult> ExecuteAsync(ExecutionNodeDefinition node, SchedulerWorkContext context) =>
        ValueTask.FromResult(WorkResult.Empty($"I/O operation '{node.Work}' completed."));
}

internal sealed class BackgroundWorkExecutor : IWorkExecutor
{
    public ExecutorKind Kind => ExecutorKind.Background;

    public ValueTask<WorkResult> ExecuteAsync(ExecutionNodeDefinition node, SchedulerWorkContext context) =>
        ValueTask.FromResult(WorkResult.Empty($"Background operation '{node.Work}' completed."));
}

internal sealed class VerificationWorkExecutor(IEvidenceStore evidence, IRuntimeEventSink events) : IWorkExecutor
{
    public ExecutorKind Kind => ExecutorKind.Verification;

    public async ValueTask<WorkResult> ExecuteAsync(ExecutionNodeDefinition node, SchedulerWorkContext context)
    {
        if (node.Work is not VerificationWorkDescriptor descriptor)
        {
            throw new WorkExecutionException(new RuntimeError(
                ErrorCategory.Verification,
                "invalid_verification_descriptor",
                $"Work descriptor '{node.Work.GetType().Name}' cannot run in the verification executor."));
        }

        await events.PublishAsync(new VerificationStartedEvent(
            DateTimeOffset.UtcNow,
            node.ExecutionId,
            node.TaskId,
            context.Execution.CorrelationId,
            "runtime.verification")).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var passed = context.DependencyResults.Values.All(static result => !string.IsNullOrWhiteSpace(result.Summary));
        var summary = passed ? $"Verification passed: {descriptor.Objective}." : "Verification found an incomplete result.";
        var reference = await evidence.StoreAsync(
            new EvidenceInput("verification", "verification.txt", System.Text.Encoding.UTF8.GetBytes(summary), "text/plain"),
            context.CancellationToken).ConfigureAwait(false);
        var duration = Stopwatch.GetElapsedTime(started);
        await events.PublishAsync(new VerificationCompletedEvent(
            DateTimeOffset.UtcNow,
            node.ExecutionId,
            node.TaskId,
            context.Execution.CorrelationId,
            "runtime.verification",
            passed,
            duration,
            summary)).ConfigureAwait(false);

        if (!passed)
        {
            throw new WorkExecutionException(new RuntimeError(
                ErrorCategory.Verification,
                "verification_failed",
                summary));
        }

        return new WorkResult(ResultId.New(), summary, [reference.Id]);
    }
}

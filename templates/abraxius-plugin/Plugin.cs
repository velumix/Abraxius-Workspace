using Abraxius.Plugin.Contracts;
using Abraxius.Plugin.Managed.Abstractions;

namespace ExamplePlugin;

public sealed class Plugin : IAbraxiusPlugin
{
    public PluginRegistration Registration { get; } = PluginRegistration.Empty with
    {
        Commands = [new("hello", "Hello from plugin", "Returns a structured greeting.", "true", "hello")]
    };

    public ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask<PluginInvocationResult> InvokeAsync(PluginInvocation invocation, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new PluginInvocationResult(invocation.InvocationId, PluginInvocationStatus.Succeeded, "{\"message\":\"Hello from the isolated PluginHost\"}"));
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

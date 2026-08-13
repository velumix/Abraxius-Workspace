using System.Collections.Immutable;
using System.Text.Json;
using Abraxius.Plugin.Contracts;
using Abraxius.Plugin.Managed;

namespace Abraxius.Extensions.Repository;

public sealed class RepositoryIntelligencePlugin : IAbraxiusPlugin
{
    public ValueTask<PluginRegistration> InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capability = new PluginCapabilityDescriptor("repo.inspect", "Inspect repository metadata", "repo.inspect.input/1", "repo.inspect.output/1", PluginCapabilitySideEffect.ReadOnly, TimeSpan.FromSeconds(5), true, ["project.read"]);
        var command = new PluginCommandDescriptor("repo.open", "Open Repository Intelligence", "Inspect brokered repository facts without direct filesystem authority.", "project.open", "repo.inspect");
        var page = new PluginViewDescriptor("repo.page", "Repository Intelligence", new("root", PluginViewComponentKind.Stack, Children:
        [
            new("heading", PluginViewComponentKind.Heading, "Repository Intelligence"),
            new("summary", PluginViewComponentKind.KeyValue, ValuePath: "summary"),
            new("languages", PluginViewComponentKind.Table, ValuePath: "languages"),
            new("refresh", PluginViewComponentKind.Button, "Refresh", CommandId: "repo.open")
        ]), MaximumRows: 1_000, PageSize: 100);
        var artifact = new PluginArtifactKindDescriptor("repo.report", "Repository intelligence report", ["application/vnd.abraxius.repository-report+json"]);
        var other = ImmutableArray.Create(
            new PluginContributionDeclaration("repo.recognizer", PluginContributionKind.ProjectRecognizer, "Repository recognizer", "1", ["project.read"]),
            new PluginContributionDeclaration("repo.eval", PluginContributionKind.EvalSuite, "Repository extension contract eval", "1", []));
        return ValueTask.FromResult(new PluginRegistration([capability], [command], [], [artifact], [page], [], [], other));
    }

    public ValueTask<PluginInvocationResult> InvokeAsync(PluginInvocation invocation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (invocation.ContributionId is not ("repo.inspect" or "repo.open")) return ValueTask.FromResult(new PluginInvocationResult(invocation.InvocationId, PluginInvocationStatus.Failed, ErrorCode: "unknown-contribution", ErrorMessage: "Contribution is not registered."));
        using var input = JsonDocument.Parse(invocation.PayloadJson, new JsonDocumentOptions { MaxDepth = 16, CommentHandling = JsonCommentHandling.Disallow });
        var root = input.RootElement;
        var fileCount = root.TryGetProperty("fileCount", out var files) && files.TryGetInt32(out var count) ? Math.Max(0, count) : 0;
        var branch = root.TryGetProperty("branch", out var branchValue) ? branchValue.GetString() ?? "unknown" : "unknown";
        var clean = root.TryGetProperty("clean", out var cleanValue) && cleanValue.ValueKind is JsonValueKind.True or JsonValueKind.False && cleanValue.GetBoolean();
        var output = JsonSerializer.Serialize(new { kind = "repository-inspection", fileCount, branch, clean, summary = $"{fileCount} files on {branch}; working tree {(clean ? "clean" : "changed")}.", evidence = "brokered-structured-input" });
        return ValueTask.FromResult(new PluginInvocationResult(invocation.InvocationId, PluginInvocationStatus.Succeeded, output));
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

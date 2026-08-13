using Abraxius.Memory;
using Xunit;

namespace Abraxius.Memory.Tests;

public sealed class MemoryTests
{
    [Fact]
    public async Task SqliteStorePersistsAndRetrievesScopedFacts()
    {
        var root = CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "knowledge.db");
            await using (var store = new SqliteMemoryStore(path))
            {
                var entry = MemoryEntry.Create(MemoryKind.Semantic, MemoryScopeKind.Project, "project-a", "Scheduler boundary", "ExecutionGraph stores scheduling structure separately from runtime state.", new MemoryProvenance(MemorySourceKind.VerifiedExecution, 1, FactKey: "scheduler.boundary", FactValue: "separate"));
                await store.UpsertAsync(entry);
                var graphSource = new KnowledgeNode(KnowledgeNodeId.New(), entry.Id, "concept", "scheduler", "Scheduler", "project-a");
                var graphTarget = new KnowledgeNode(KnowledgeNodeId.New(), entry.Id, "concept", "runtime-state", "runtime state", "project-a");
                await store.AddNodeAsync(graphSource);
                await store.AddNodeAsync(graphTarget);
                await store.AddEdgeAsync(new KnowledgeEdge(KnowledgeEdgeId.New(), graphSource.Id, KnowledgeRelationType.RelatedTo, graphTarget.Id));
                await store.InitializeAsync();
            }

            await using var reopened = new SqliteMemoryStore(path);
            var retriever = new HybridMemoryRetriever(reopened, new HashEmbeddingProvider());
            var result = await retriever.RetrieveAsync(new MemorySearchQuery("runtime state graph definitions", ProjectKey: "project-a"));
            Assert.Contains(result.Hits, hit => hit.Entry.Content.Contains("runtime state", StringComparison.Ordinal));
            Assert.NotEmpty(await reopened.SearchGraphAsync(new MemorySearchQuery("runtime", ProjectKey: "project-a")));
            Assert.DoesNotContain(result.Hits, hit => hit.Entry.ScopeKey == "project-b");
        }
        finally { DeleteTempDirectory(root); }
    }

    [Fact]
    public async Task ScopeAndForgetRemoveMemoryFromEveryRetrievalPath()
    {
        await using var store = new InMemoryKnowledgeStore();
        var a = MemoryEntry.Create(MemoryKind.Project, MemoryScopeKind.Project, "a", "CancellationToken", "Project A cancellation contract.", new MemoryProvenance(MemorySourceKind.SourceCode, 0.98));
        var b = MemoryEntry.Create(MemoryKind.Project, MemoryScopeKind.Project, "b", "CancellationToken", "Project B cancellation contract.", new MemoryProvenance(MemorySourceKind.SourceCode, 0.98));
        await store.UpsertAsync(a);
        await store.UpsertAsync(b);
        var retriever = new HybridMemoryRetriever(store);
        var scoped = await retriever.RetrieveAsync(new MemorySearchQuery("CancellationToken", ProjectKey: "a"));
        Assert.All(scoped.Hits, hit => Assert.Equal("a", hit.Entry.ScopeKey));
        await store.ForgetAsync(a.Id);
        var forgotten = await retriever.RetrieveAsync(new MemorySearchQuery("CancellationToken", ProjectKey: "a"));
        Assert.DoesNotContain(forgotten.Hits, hit => hit.Entry.Id == a.Id);
    }

    [Fact]
    public async Task CurrentSourceHashMarksOlderSourceMemoryStale()
    {
        await using var store = new InMemoryKnowledgeStore();
        var entry = MemoryEntry.Create(
            MemoryKind.Source,
            MemoryScopeKind.Project,
            "p",
            "Scheduler.cs",
            "public sealed class Scheduler { }",
            new MemoryProvenance(MemorySourceKind.SourceCode, 0.98, SourcePath: "Scheduler.cs", SourceHash: "old-hash"));
        await store.UpsertAsync(entry);

        var result = await new HybridMemoryRetriever(store).RetrieveAsync(new MemorySearchQuery(
            "Scheduler",
            ProjectKey: "p",
            CurrentSourceHashes: new Dictionary<string, string> { ["Scheduler.cs"] = "new-hash" }));

        var hit = Assert.Single(result.Hits);
        Assert.True(hit.IsStale);
    }

    [Fact]
    public async Task ForgetRemovesKnowledgeNodesAndEdges()
    {
        await using var store = new InMemoryKnowledgeStore();
        var memory = MemoryEntry.Create(MemoryKind.Source, MemoryScopeKind.Project, "p", "Scheduler", "class Scheduler { }", new MemoryProvenance(MemorySourceKind.SourceCode, 0.98));
        await store.UpsertAsync(memory);
        var source = new KnowledgeNode(KnowledgeNodeId.New(), memory.Id, "type", "Scheduler", "Scheduler", "p");
        var target = new KnowledgeNode(KnowledgeNodeId.New(), memory.Id, "file", "Scheduler.cs", "Scheduler.cs", "p");
        await store.AddNodeAsync(source);
        await store.AddNodeAsync(target);
        await store.AddEdgeAsync(new KnowledgeEdge(KnowledgeEdgeId.New(), source.Id, KnowledgeRelationType.Defines, target.Id));

        var before = await store.GetStatisticsAsync();
        Assert.Equal(2, before.KnowledgeNodes);
        Assert.Equal(1, before.KnowledgeEdges);

        await store.ForgetAsync(memory.Id);

        var after = await store.GetStatisticsAsync();
        Assert.Equal(0, after.KnowledgeNodes);
        Assert.Equal(0, after.KnowledgeEdges);
    }

    [Fact]
    public async Task ConflictingFactsAreSurfacedInsteadOfSilentlyMerged()
    {
        await using var store = new InMemoryKnowledgeStore();
        await store.UpsertAsync(MemoryEntry.Create(MemoryKind.Semantic, MemoryScopeKind.Project, "p", "Version", "Avalonia 12.0", new MemoryProvenance(MemorySourceKind.ModelInference, 0.4, FactKey: "avalonia.version", FactValue: "12.0")));
        await store.UpsertAsync(MemoryEntry.Create(MemoryKind.Semantic, MemoryScopeKind.Project, "p", "Version", "Avalonia 12.1", new MemoryProvenance(MemorySourceKind.VerifiedExecution, 1, FactKey: "avalonia.version", FactValue: "12.1")));
        var result = await new HybridMemoryRetriever(store).RetrieveAsync(new MemorySearchQuery("Avalonia version", ProjectKey: "p"));
        Assert.NotEmpty(result.Conflicts);
        Assert.Contains(result.Hits, hit => hit.IsConflict);
    }

    [Fact]
    public async Task ContextCompilerDeduplicatesAndReservesOutputBudget()
    {
        await using var store = new InMemoryKnowledgeStore();
        var entry = MemoryEntry.Create(MemoryKind.Source, MemoryScopeKind.Project, "p", "ExecutionGraph", "ExecutionGraph definitions are immutable and runtime state is separate.", new MemoryProvenance(MemorySourceKind.SourceCode, 0.98));
        await store.UpsertAsync(entry);
        var package = await new MemoryContextCompiler(new HybridMemoryRetriever(store)).CompileAsync(new ContextCompilationRequest("Explain the scheduler boundary", new MemorySearchQuery("ExecutionGraph runtime state", ProjectKey: "p"), ContextWindow: 512, ReservedOutputTokens: 128));
        Assert.Contains("OBJECTIVE", package.Text, StringComparison.Ordinal);
        Assert.Contains(entry.Id, package.IncludedMemories);
        Assert.True(package.EstimatedTokens <= 384);
        Assert.StartsWith("axl/1", package.AxlProjection, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepositoryIngestionIsIncrementalAndStructureAware()
    {
        var root = CreateTempDirectory();
        try
        {
            var source = Path.Combine(root, "Scheduler.cs");
            await File.WriteAllTextAsync(source, "public sealed class Scheduler { public void Run() { } }\n");
            await using var store = new InMemoryKnowledgeStore();
            var service = new RepositoryIngestionService(store, new HashEmbeddingProvider(), git: new FixedGitMetadataProvider());
            var first = await service.IngestAsync(new RepositoryIngestionOptions(root, "p"));
            var second = await service.IngestAsync(new RepositoryIngestionOptions(root, "p"));
            Assert.Equal(1, first.Indexed);
            Assert.Equal(1, second.Skipped);
            var result = await new HybridMemoryRetriever(store).RetrieveAsync(new MemorySearchQuery("Scheduler Run", ProjectKey: "p", Mode: MemoryRetrievalMode.Symbol));
            Assert.Contains(result.Hits, hit => hit.Entry.Content.Contains("Scheduler", StringComparison.Ordinal));
            var graphResult = await new HybridMemoryRetriever(store).RetrieveAsync(new MemorySearchQuery("Scheduler.cs", ProjectKey: "p"));
            Assert.Contains(graphResult.Hits, hit => hit.Entry.Content.Contains("Scheduler", StringComparison.Ordinal));
            var branchResult = await new HybridMemoryRetriever(store).RetrieveAsync(new MemorySearchQuery("Scheduler", ProjectKey: "p", Branch: "feature/memory"));
            Assert.Contains(branchResult.Hits, hit => hit.Entry.Provenance.SourceCommit == "commit-123");
            var indexedFile = await store.GetIndexedFileAsync("p", "Scheduler.cs");
            Assert.Equal("feature/memory", indexedFile?.Branch);
            Assert.Equal("commit-123", indexedFile?.Commit);
            var stats = await store.GetStatisticsAsync();
            Assert.True(stats.Embeddings > 0);
            Assert.True(stats.KnowledgeNodes > 0);
            Assert.True(stats.KnowledgeEdges > 0);
        }
        finally { DeleteTempDirectory(root); }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "abraxius-memory-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class FixedGitMetadataProvider : IGitMetadataProvider
    {
        public GitMetadata Read(string repositoryRoot) => new("feature/memory", "commit-123");
    }
}

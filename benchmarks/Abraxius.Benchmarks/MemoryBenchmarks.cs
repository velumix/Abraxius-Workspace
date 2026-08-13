using Abraxius.Memory;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class MemoryBenchmarks : IDisposable
{
    private InMemoryKnowledgeStore _store = null!;
    private HybridMemoryRetriever _retriever = null!;
    private MemoryContextCompiler _compiler = null!;

    [Params(100, 1_000)]
    public int Entries { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _store = new InMemoryKnowledgeStore();
        for (var index = 0; index < Entries; index++)
        {
            var entry = MemoryEntry.Create(
                MemoryKind.Source,
                MemoryScopeKind.Project,
                "benchmark",
                $"ExecutionGraph.Symbol{index}",
                $"ExecutionGraph symbol {index} stores dependency edges and scheduler state for the Abraxius runtime.",
                new MemoryProvenance(MemorySourceKind.SourceCode, 0.98));
            _store.UpsertAsync(entry).AsTask().GetAwaiter().GetResult();
            _store.AddEmbeddingAsync(EmbeddingId.New(), entry.Id, new HashEmbeddingProvider().EmbedAsync(entry.Content).AsTask().GetAwaiter().GetResult()!, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        }

        _retriever = new HybridMemoryRetriever(_store, new HashEmbeddingProvider());
        _compiler = new MemoryContextCompiler(_retriever);
    }

    [Benchmark]
    public MemoryRetrievalResult HybridRetrieve() => _retriever.RetrieveAsync(new MemorySearchQuery("ExecutionGraph scheduler dependency", ProjectKey: "benchmark")).AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public MemoryContextPackage CompileContext() => _compiler.CompileAsync(new ContextCompilationRequest("Diagnose scheduler dependency behavior", new MemorySearchQuery("ExecutionGraph scheduler dependency", ProjectKey: "benchmark"), ContextWindow: 4_096, ReservedOutputTokens: 1_024)).AsTask().GetAwaiter().GetResult();

    public void Dispose()
    {
        _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

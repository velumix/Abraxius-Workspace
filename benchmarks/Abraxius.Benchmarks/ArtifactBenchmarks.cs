using System.Text;
using Abraxius.Artifacts;
using Abraxius.Protocol;
using Abraxius.Security;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet invokes the asynchronous GlobalCleanup method after the benchmark lifecycle.")]
public class ArtifactBenchmarks
{
    private InMemoryArtifactStore _store = null!;
    private ArtifactService _service = null!;
    private ArtifactAggregate _baseline = null!;
    private ArtifactAggregate _candidate = null!;
    private byte[] _oneMegabyte = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _store = new InMemoryArtifactStore(); var content = new InMemoryArtifactContentStore(); _service = new ArtifactService(_store, content); await _service.InitializeAsync();
        _baseline = await _service.CreateAsync(Request("baseline"), Text(string.Join('\n', Enumerable.Range(0, 10_000).Select(static value => $"line-{value}"))));
        _candidate = await _service.CreateRevisionAsync(new(_baseline.Descriptor.Id, _baseline.CurrentRevision.Id, Producer(), new ArtifactProvenance(), "text/plain"), Text(string.Join('\n', Enumerable.Range(0, 10_000).Select(static value => value == 5000 ? "changed" : $"line-{value}"))));
        for (var index = 0; index < 10_000; index++) await _service.CreateAsync(Request($"artifact-{index}"), Text(index.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        _oneMegabyte = new byte[1024 * 1024]; Random.Shared.NextBytes(_oneMegabyte);
    }

    [Benchmark]
    public ValueTask<IReadOnlyList<ArtifactDescriptor>> QueryTenThousand() => _store.QueryAsync(new ArtifactQuery(Text: "artifact-99", Limit: 100));

    [Benchmark]
    public async Task<ArtifactDiffDocument> DiffTenThousandLines()
    {
        var provider = new LinearTextDiffProvider();
        await using var oldContent = Text(string.Join('\n', Enumerable.Range(0, 10_000).Select(static value => $"line-{value}")));
        await using var newContent = Text(string.Join('\n', Enumerable.Range(0, 10_000).Select(static value => value == 5000 ? "changed" : $"line-{value}")));
        return await provider.CompareAsync(_baseline.CurrentRevision, oldContent, _candidate.CurrentRevision, newContent, new ArtifactDiffOptions());
    }

    [Benchmark]
    public async Task<ArtifactContentDescriptor> HashOneMegabyte()
    {
        var store = new InMemoryArtifactContentStore(); await using var stream = new MemoryStream(_oneMegabyte, writable: false); return await store.PutAsync(stream, "application/octet-stream");
    }

    [GlobalCleanup]
    public async Task Cleanup() => await _store.DisposeAsync();

    private static CreateArtifactRequest Request(string title) => new(ArtifactKind.Report, title, Producer(), new ArtifactProvenance(), ArtifactClassification.Internal, "text/plain");
    private static ArtifactProducer Producer() => new(new PrincipalId("system:benchmark"), ArtifactProducerKind.System, "Benchmark");
    private static MemoryStream Text(string value) => new(Encoding.UTF8.GetBytes(value), writable: false);
}

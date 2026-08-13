using Abraxius.Axl;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class AxlBenchmarks
{
    private const string Sample = "axl/1 batch { c#1 find code q=\"ExecutionGraph\" lim=20 c#2 call @cap:git.status c#3 synth obj=\"combine evidence\" dep=[c#1 c#2] c#4 verify obj=\"check result\" dep=[c#3] }";
    private readonly AxlBinaryCodec _codec = new();
    private AxlDocument _document = null!;
    private byte[] _binary = [];

    [GlobalSetup]
    public void Setup()
    {
        var result = AxlPipeline.ParseAndValidate(Sample);
        _document = result.Document ?? throw new InvalidOperationException("Benchmark sample must be valid AXL.");
        _binary = _codec.Encode(_document);
    }

    [Benchmark]
    public AxlParseStatus Parse()
    {
        _ = _document;
        return AxlParser.Parse(Sample).Status;
    }

    [Benchmark]
    public string Format() => AxlFormatter.Compact(_document);

    [Benchmark]
    public byte[] EncodeBinary() => _codec.Encode(_document);

    [Benchmark]
    public bool DecodeBinary() => _codec.TryDecode(_binary, out _, out _);
}

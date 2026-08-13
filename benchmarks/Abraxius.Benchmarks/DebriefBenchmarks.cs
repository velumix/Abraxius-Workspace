using Abraxius.Debrief;
using Abraxius.Memory;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class DebriefBenchmarks
{
    private DeterministicDebriefPlanner _planner = null!;
    private EpisodePlan _plan = null!;
    private DebriefChapter _chapter = null!;
    private GroundedDebriefDialogueComposer _composer = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var evidence = Enumerable.Range(1, 64)
            .Select(index => new DebriefEvidence($"m:{index}", $"Source {index}", $"Verified source-grounded project finding number {index}.", Source: MemorySourceKind.VerifiedExecution, Authority: 1))
            .ToArray();
        _planner = new DeterministicDebriefPlanner(new FixtureResolver(evidence));
        _composer = new GroundedDebriefDialogueComposer();
        _plan = await _planner.PlanAsync(new DebriefRequest(new DebriefSourceSet(Query: "project findings"), TargetDuration: TimeSpan.FromMinutes(20), GenerateAudio: false));
        _chapter = _plan.Chapters[1];
    }

    [Benchmark]
    public EpisodePlan PlanEpisode() => _planner.PlanAsync(new DebriefRequest(new DebriefSourceSet(Query: "project findings"), GenerateAudio: false)).AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public IReadOnlyList<DialogueTurn> ComposeGroundedChapter() => _composer.ComposeAsync(_plan, _chapter, []).AsTask().GetAwaiter().GetResult();

    private sealed class FixtureResolver(IReadOnlyList<DebriefEvidence> evidence) : IDebriefSourceResolver
    {
        public ValueTask<DebriefSourceResolution> ResolveAsync(DebriefSourceSet sources, string query, int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new DebriefSourceResolution(
                new SourceSnapshot("bench", sources, DateTimeOffset.UtcNow, "bench", new Dictionary<string, string>(), [], []),
                evidence.Take(limit).ToArray(), [], TimeSpan.Zero));
    }
}

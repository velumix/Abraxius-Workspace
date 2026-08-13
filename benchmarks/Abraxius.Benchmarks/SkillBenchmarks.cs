using Abraxius.Agents;
using Abraxius.Skills;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class SkillBenchmarks
{
    [Params(10, 1_000, 10_000)]
    public int RegistrySize { get; set; }

    private DeterministicSkillMatcher _matcher = null!;
    private SkillDefinition _skill = null!;
    private SkillMatchRequest _request = null!;
    private SkillMatchRequest _selectiveRequest = null!;

    [GlobalSetup]
    public void Setup()
    {
        var registry = new SkillRegistry();
        _skill = BuiltInSkills.ProjectInspection();
        registry.Register(_skill);
        for (var index = 0; index < RegistrySize - 1; index++)
        {
            registry.Register(_skill with
            {
                Id = new SkillId($"repo.inspect-project-{index}"),
                Name = $"repo.inspect-project-{index}",
                Triggers = new SkillTriggerSet(TaskClasses: [$"project-inspection-{index}"])
            });
        }

        _matcher = new DeterministicSkillMatcher(registry);
        _request = new SkillMatchRequest("inspect this project repository", SpecialistRole.Investigator, ProjectKey: "bench");
        _selectiveRequest = new SkillMatchRequest($"project-inspection-{Math.Max(0, RegistrySize - 2)}", SpecialistRole.Investigator, ProjectKey: "bench");
    }

    [Benchmark]
    public IReadOnlyList<SkillMatch> MatchRegistry() => _matcher.Match(_request, 4);

    [Benchmark]
    public IReadOnlyList<SkillMatch> MatchSelectiveRegistry() => _matcher.Match(_selectiveRequest, 4);

    [Benchmark]
    public SkillExecutionPlan CompilePlan() => SkillPlanCompiler.Compile(_skill);
}

using System.Collections.Immutable;
using Abraxius.Plugin.Contracts;
using Abraxius.Plugins;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class PluginBenchmarks
{
    private readonly PluginManifestValidator _validator = new();
    private readonly PluginContributionRegistry _registry = new();
    private readonly PluginManifest _manifest;
    private readonly PluginId _pluginId = new("com.abraxius.benchmark");
    private readonly PluginVersion _version = new(1, 0, 0);

    public PluginBenchmarks()
    {
        var contributions = Enumerable.Range(0, 100)
            .Select(index => new PluginContributionDeclaration($"command-{index}", PluginContributionKind.Command, $"Command {index}", "1", []))
            .ToImmutableArray();
        _manifest = new(1, _pluginId.Value, _version.ToString(), "Benchmark", "Abraxius", "Benchmark fixture",
            new(">=1.0 <2.0", PluginApiVersion.Current, ["Linux"], ["x64"]),
            [new(PluginExecutionTier.ManagedOutOfProcess, "lib/plugin.dll", "Benchmark.Plugin")], [], contributions, Dependencies: []);
        var commands = contributions.Select(item => new PluginCommandDescriptor(item.Id, item.DisplayName, string.Empty, "true", item.Id)).ToImmutableArray();
        _registry.Register(_pluginId, _version, PluginRegistration.Empty with { Commands = commands });
    }

    [Benchmark]
    public ImmutableArray<string> ValidateManifest() => _validator.Validate(_manifest);

    [Benchmark]
    public int QueryContributionRegistry() => _registry.Contributions.Count(item => item.PluginId == _pluginId && item.PluginVersion == _version);
}

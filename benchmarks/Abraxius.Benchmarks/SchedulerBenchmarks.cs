using Abraxius.Core;
using Abraxius.Memory;
using Abraxius.Platform;
using Abraxius.Protocol;
using Abraxius.Scheduler;
using BenchmarkDotNet.Attributes;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
[RankColumn]
public class SchedulerBenchmarks
{
    [Params(4, 16, 64)]
    public int Branches { get; set; }

    [Params(1, 5)]
    public int WorkMilliseconds { get; set; }

    [Benchmark(Baseline = true)]
    public async Task SerialBaseline()
    {
        for (var index = 0; index < Branches; index++)
        {
            await Task.Delay(WorkMilliseconds).ConfigureAwait(false);
        }
    }

    [Benchmark]
    public async Task ParallelDag()
    {
        var execution = ExecutionId.New();
        var graph = new ExecutionGraph(
            execution,
            CorrelationId.New(),
            Enumerable.Range(0, Branches).Select(index => new ExecutionNodeDefinition(
                NodeId.New(),
                TaskId.New(),
                execution,
                new IoWorkDescriptor("delay", new Dictionary<string, string> { ["milliseconds"] = WorkMilliseconds.ToString(CultureInfo.InvariantCulture) }),
                creationOrder: index))).Compile();
        var registry = new WorkExecutorRegistry
        ([
            new DelegateWorkExecutor(ExecutorKind.Io, async (_, context) =>
            {
                await Task.Delay(WorkMilliseconds, context.CancellationToken).ConfigureAwait(false);
                return WorkResult.Empty();
            })
        ]);
        await using var scheduler = new DagScheduler(new SchedulerOptions
        {
            DefaultQueueCapacity = Math.Max(Branches, 8),
            DefaultTimeout = TimeSpan.FromSeconds(10),
            ConcurrencyLimits = Enum.GetValues<ExecutorKind>().ToDictionary(kind => kind, _ => Math.Min(Branches, 32))
        });
        await scheduler.ExecuteAsync(
            graph,
            new SchedulerExecutionContext
            {
                ExecutionId = execution,
                CorrelationId = graph.Source.CorrelationId,
                Environment = BenchmarkEnvironment(),
                Executors = registry
            },
            new InMemoryEvidenceStore()).ConfigureAwait(false);
    }

    private static PlatformEnvironment BenchmarkEnvironment()
    {
        var platform = new PlatformDescriptor(PlatformFamily.Linux, "benchmark", "benchmark", Architecture.X64, ".NET", true);
        var device = new DeviceProfile(DeviceClass.Workstation, 16, 64UL * 1024 * 1024 * 1024, false, false, HardwareAccelerationClass.DedicatedGpu);
        var capabilities = new PlatformCapabilitySet([new PlatformCapability(PlatformCapabilities.LocalLattice, CapabilityAvailability.Available)]);
        return PlatformEnvironmentFactory.Create(platform, device, capabilities, RuntimeExecutionMode.LocalFull);
    }
}

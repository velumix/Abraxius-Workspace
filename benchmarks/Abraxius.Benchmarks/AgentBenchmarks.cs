using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Protocol;
using BenchmarkDotNet.Attributes;

namespace Abraxius.Benchmarks;

[MemoryDiagnoser]
public class AgentBenchmarks : IDisposable
{
    private AgentKernel _kernel = null!;

    [GlobalSetup]
    public void Setup() => _kernel = new AgentKernel(new SpecialistRegistry(), new NoLatencyRunner());

    [Benchmark]
    public MissionResult BuildMissionWithFourAssignments() =>
        _kernel.RunMissionAsync(new Intent("Find and fix a scheduler race", CorrelationId.New())).AsTask().GetAwaiter().GetResult();

    [Benchmark]
    public MissionResult DirectInvestigation() =>
        _kernel.RunMissionAsync(new Intent("@Orion find ExecutionGraph callers", CorrelationId.New())).AsTask().GetAwaiter().GetResult();

    private sealed class NoLatencyRunner : IAgentAssignmentRunner
    {
        public ValueTask<AgentAssignmentResult> RunAsync(SpecialistDefinition definition, SpecialistInstance instance, AgentAssignment assignment, MissionContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AgentAssignmentResult(true, definition.DisplayName + " completed", Verification: definition.Role == SpecialistRole.Verifier ? Abraxius.Agents.VerificationStatus.Passed : null));
    }

    public void Dispose()
    {
        _kernel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }
}

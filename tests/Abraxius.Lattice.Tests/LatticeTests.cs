using Abraxius.Lattice;
using Abraxius.Protocol;
using Xunit;

namespace Abraxius.Lattice.Tests;

public sealed class LatticeTests
{
    [Fact]
    public async Task PolicyRejectsUnknownCapabilityOperation()
    {
        var executor = new LatticeExecutor([new MockLatticeCapability()], new AllowListPolicy(["demo"], ["status"]));
        var request = new CapabilityRequest("demo", "run", "repository", null, CorrelationId.New(), ExecutionId.New(), TaskId.New());

        var result = await executor.ExecuteAsync(request);

        Assert.False(result.Succeeded);
        Assert.Equal(ErrorCategory.Policy, result.Error?.Category);
    }

    [Fact]
    public async Task CapabilityDiscoveryAndExecutionRemainStructured()
    {
        var executor = new LatticeExecutor([new MockLatticeCapability(TimeSpan.FromMilliseconds(1))]);
        var request = new CapabilityRequest("demo", "status", "repository", null, CorrelationId.New(), ExecutionId.New(), TaskId.New());

        var descriptors = executor.Discover();
        var result = await executor.ExecuteAsync(request);

        Assert.Contains(descriptors, descriptor => descriptor.Name == "demo");
        Assert.True(result.Succeeded);
        Assert.Equal("repository", result.Values!["target"]);
    }
}

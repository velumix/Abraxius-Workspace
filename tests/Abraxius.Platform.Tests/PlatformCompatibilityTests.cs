using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using Abraxius.Core;
using Abraxius.Platform;
using Abraxius.Protocol;
using Xunit;

namespace Abraxius.Platform.Tests;

public sealed class PlatformCompatibilityTests
{
    [Fact]
    public void HighEndDesktopUsesLocalFullModeAndAUsefulBudget()
    {
        var environment = PlatformEnvironmentFactory.Create(
            new PlatformDescriptor(
                PlatformFamily.Linux,
                "Linux",
                "6.8",
                Architecture.X64,
                ".NET 10",
                true),
            new DeviceProfile(
                DeviceClass.Workstation,
                16,
                64UL * 1024 * 1024 * 1024,
                batteryPowered: false,
                touchPrimary: false,
                HardwareAccelerationClass.DedicatedGpu,
                PowerSource.Ac),
            Capabilities(
                (PlatformCapabilities.FileSystem, CapabilityAvailability.Available),
                (PlatformCapabilities.ProcessExecution, CapabilityAvailability.Available),
                (PlatformCapabilities.LocalModelInference, CapabilityAvailability.Available),
                (PlatformCapabilities.LocalLattice, CapabilityAvailability.Available)),
            RuntimeExecutionMode.LocalFull);

        Assert.Equal(RuntimeExecutionMode.LocalFull, environment.ExecutionMode);
        Assert.True(environment.Capabilities.ProcessExecution);
        Assert.True(environment.Budget.ModelConcurrency > 0);
        Assert.False(environment.Budget.PreferRemote);
        Assert.Equal(PowerPreference.Performance, environment.Budget.PowerPreference);
    }

    [Fact]
    public void PhoneUsesConservativeHybridBudgetWithoutPretendingToHaveAShell()
    {
        var environment = PlatformEnvironmentFactory.Create(
            new PlatformDescriptor(
                PlatformFamily.Android,
                "Android",
                "15",
                Architecture.Arm64,
                ".NET 10",
                true),
            new DeviceProfile(
                DeviceClass.Phone,
                8,
                4UL * 1024 * 1024 * 1024,
                batteryPowered: true,
                touchPrimary: true,
                HardwareAccelerationClass.HardwareAccelerated,
                PowerSource.Battery),
            Capabilities(
                (PlatformCapabilities.FileSystem, CapabilityAvailability.Restricted),
                (PlatformCapabilities.ProcessExecution, CapabilityAvailability.Unavailable),
                (PlatformCapabilities.Network, CapabilityAvailability.Available),
                (PlatformCapabilities.LocalModelInference, CapabilityAvailability.Restricted),
                (PlatformCapabilities.LocalLattice, CapabilityAvailability.RemoteOnly)),
            RuntimeExecutionMode.Hybrid);

        Assert.Equal(RuntimeExecutionMode.Hybrid, environment.ExecutionMode);
        Assert.False(environment.Capabilities.ProcessExecution);
        Assert.True(environment.Budget.PreferRemote);
        Assert.Equal(0, environment.Budget.ModelConcurrency);
        Assert.Equal(PowerPreference.Efficiency, environment.Budget.PowerPreference);
        Assert.True(environment.Budget.MaximumConcurrency <= 4);
    }

    [Fact]
    public void BrowserRoutesUnavailableCapabilitiesToAnAdvertisedHost()
    {
        var browser = PlatformEnvironmentFactory.Create(
            new PlatformDescriptor(
                PlatformFamily.Browser,
                "Browser",
                "WASM",
                Architecture.X64,
                ".NET 10 browser",
                true),
            new DeviceProfile(
                DeviceClass.Browser,
                4,
                2UL * 1024 * 1024 * 1024,
                batteryPowered: false,
                touchPrimary: false,
                HardwareAccelerationClass.HardwareAccelerated),
            Capabilities(
                (PlatformCapabilities.Network, CapabilityAvailability.Available),
                (PlatformCapabilities.ProcessExecution, CapabilityAvailability.Unavailable),
                (PlatformCapabilities.Git, CapabilityAvailability.Unavailable)),
            RuntimeExecutionMode.Remote);
        var host = new RemoteCapabilityAdvertisement(
            RemoteHostId.New(),
            "Linux execution host",
            AbraxiusProtocol.CurrentVersion,
            new PlatformDescriptor(PlatformFamily.Linux, "Linux", "6.8", Architecture.X64, ".NET 10", true),
            RuntimeExecutionMode.LocalFull,
            ImmutableArray.Create(new CapabilityAdvertisement(PlatformCapabilities.Git, CapabilityAvailability.Available)));

        var resolver = new CapabilityResolver(browser, [host]);
        var routed = resolver.Resolve(PlatformCapabilities.Git);

        Assert.True(routed.IsExecutable);
        Assert.Equal(ExecutionPlacement.Remote, routed.Route.Placement);
        Assert.Equal(host.HostId, routed.Route.HostId);
    }

    [Fact]
    public void CapabilityNegotiationUsesCompatibleVersionAndRejectsUnknownMajorVersions()
    {
        var host = new RemoteCapabilityAdvertisement(
            RemoteHostId.New(),
            "Host",
            new ProtocolVersion(1, 3),
            new PlatformDescriptor(PlatformFamily.Linux, "Linux", "6.8", Architecture.X64, ".NET 10", true),
            RuntimeExecutionMode.LocalFull,
            ImmutableArray.Create(new CapabilityAdvertisement(PlatformCapabilities.Git, CapabilityAvailability.Available)));

        var compatible = CapabilityNegotiator.Negotiate(new ProtocolVersion(1, 1), host);
        var incompatible = CapabilityNegotiator.Negotiate(new ProtocolVersion(2, 0), host);

        Assert.True(compatible.Compatible);
        Assert.Equal(new ProtocolVersion(1, 1), compatible.NegotiatedVersion);
        Assert.Single(compatible.Capabilities);
        Assert.False(incompatible.Compatible);
        Assert.Equal(PlatformErrorCode.ProtocolMismatch, incompatible.Error!.Code);
    }

    [Fact]
    public void MissingAndPermissionRequiredCapabilitiesProduceStructuredResults()
    {
        var environment = PlatformEnvironmentFactory.Create(
            new PlatformDescriptor(PlatformFamily.Ios, "iOS", "18", Architecture.Arm64, ".NET 10", true),
            new DeviceProfile(DeviceClass.Phone, 6, null, true, true, HardwareAccelerationClass.HardwareAccelerated),
            Capabilities((PlatformCapabilities.FileSystem, CapabilityAvailability.PermissionRequired)),
            RuntimeExecutionMode.LocalConstrained);
        var resolver = new CapabilityResolver(environment);

        var permission = resolver.Resolve(PlatformCapabilities.FileSystem, allowRemote: false);
        var missing = resolver.Resolve(PlatformCapabilities.Git, allowRemote: false);

        Assert.Equal(ExecutionPlacement.PermissionRequired, permission.Route.Placement);
        Assert.Equal(PlatformErrorCode.PermissionRequired, permission.Error!.Code);
        Assert.Equal(ExecutionPlacement.Unavailable, missing.Route.Placement);
        Assert.Equal(PlatformErrorCode.CapabilityUnavailable, missing.Error!.Code);
    }

    [Fact]
    public void ResponsivePolicyUsesViewportInsteadOfOperatingSystemNames()
    {
        var compact = ViewportProfile.From(390, 844, 3, touchPrimary: true);
        var expanded = ViewportProfile.From(2560, 1440, 1, touchPrimary: false);

        Assert.Equal(ViewportClass.Compact, compact.Class);
        Assert.True(ResponsiveLayoutPolicy.For(compact, PerformanceProfile.Automatic).UseBottomNavigation);
        Assert.Equal(ViewportClass.UltraWide, expanded.Class);
        Assert.True(ResponsiveLayoutPolicy.For(expanded, PerformanceProfile.Balanced).ShowDesktopSidebars);
        Assert.False(ResponsiveLayoutPolicy.For(expanded, PerformanceProfile.Balanced).UseBottomNavigation);
    }

    [Fact]
    public void ExecutionGraphSerializationRemainsPlatformNeutral()
    {
        var executionId = ExecutionId.New();
        var node = new ExecutionNodeDefinition(
            NodeId.New(),
            TaskId.New(),
            executionId,
            new ToolWorkDescriptor(
                PlatformCapabilities.Git,
                "status",
                new ActionTarget("repository", "/workspace")),
            priority: WorkPriority.Interactive);
        var graph = new ExecutionGraph(executionId, CorrelationId.New(), [node], [node.Id]);

        var copy = ExecutionGraphJson.Deserialize(ExecutionGraphJson.Serialize(graph));

        Assert.Equal(graph.ExecutionId, copy.ExecutionId);
        Assert.Equal(graph.CorrelationId, copy.CorrelationId);
        Assert.IsType<ToolWorkDescriptor>(copy.Nodes[0].Work);
        Assert.Equal(node.Work, copy.Nodes[0].Work);
        Assert.True(copy.Compile().TryGetIndex(node.Id, out _));
    }

    [Fact]
    public async Task BoundedTransportCarriesCorrelatedMessagesBetweenPeers()
    {
        var pair = InMemoryAbraxiusTransport.CreatePair(1);
        await using var left = pair.Left;
        await using var right = pair.Right;
        var endpoint = new TransportEndpoint(new Uri("loopback://abraxius"));
        await left.ConnectAsync(endpoint);
        await right.ConnectAsync(endpoint);
        var executionId = ExecutionId.New();
        var envelope = ProtocolEnvelope.Create("state.query", new ExecutionStateQuery(executionId, CorrelationId.New()), executionId: executionId);

        await left.SendAsync(envelope);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        TransportMessage? received = null;
        await foreach (var message in right.ReceiveAsync(timeout.Token))
        {
            received = message;
            break;
        }

        Assert.NotNull(received);
        Assert.Equal(envelope.Version, received!.Version);
        Assert.Equal(envelope.CorrelationId, received.CorrelationId);
        Assert.Equal(executionId, received.ExecutionId);
        Assert.Equal("state.query", received.MessageType);
    }

    [Fact]
    public void CoreAndProtocolAssembliesHaveNoUiOrNativeHostReferences()
    {
        var assemblies = new[]
        {
            typeof(ExecutionGraph).Assembly,
            typeof(ProtocolEnvelope<>).Assembly,
            typeof(PlatformEnvironment).Assembly
        };
        var forbidden = new[] { "Avalonia", "Android", "UIKit", "WindowsBase", "System.Windows" };

        foreach (var assembly in assemblies)
        {
            var names = assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty);
            Assert.DoesNotContain(names, name => forbidden.Any(item => name.Contains(item, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private static PlatformCapabilitySet Capabilities(params (CapabilityId Id, CapabilityAvailability Availability)[] capabilities) =>
        new(capabilities.Select(item => new PlatformCapability(item.Id, item.Availability)));
}

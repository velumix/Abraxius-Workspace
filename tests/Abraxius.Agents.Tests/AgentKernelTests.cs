using System.Collections.Concurrent;
using Abraxius.Axl;
using Abraxius.Agents;
using Abraxius.Core;
using Abraxius.Protocol;
using Xunit;

namespace Abraxius.Agents.Tests;

public sealed class AgentKernelTests
{
    [Fact]
    public void BuiltInsUseSemanticRolesAndCompatibilityAliases()
    {
        var registry = new SpecialistRegistry();
        Assert.Equal(4, registry.Definitions.Count);
        Assert.True(registry.TryResolve("Athena", out var athena));
        Assert.True(registry.TryResolve("Scout", out var orion));
        Assert.Equal(SpecialistRole.Coordinator, athena.Role);
        Assert.Equal(SpecialistRole.Investigator, orion.Role);
        Assert.Equal("Athena", athena.DisplayName);
    }

    [Fact]
    public void ReadOnlySpecialistsCannotMutate()
    {
        var registry = new SpecialistRegistry();
        var definition = registry.Definitions.Single(item => item.Role == SpecialistRole.Investigator);
        var assignment = NewAssignment(definition.Role);
        var decision = new DefaultAgentPolicyEnforcer().Authorize(definition, assignment, new CapabilityId("code.search"), mutation: true);
        Assert.False(decision.Allowed);
        Assert.Contains("read-only", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DynamicSpecialistCannotExpandTemplateCapabilities()
    {
        var template = new SpecialistRegistry().Definitions.Single(item => item.Role == SpecialistRole.Investigator);
        var specialist = new SpecialistFactory().CreateDomainSpecialist(template, "performance", "PerformanceEngineer", SpecialistRole.DomainExpert, [new CapabilityId("evidence.search"), new CapabilityId("shell.execute")]);
        Assert.Contains(new CapabilityId("evidence.search"), specialist.CapabilityPolicy.AllowedCapabilities);
        Assert.DoesNotContain(new CapabilityId("shell.execute"), specialist.CapabilityPolicy.AllowedCapabilities);
    }

    [Fact]
    public async Task BuildMissionFansOutOrionAndRunsIndependentVerification()
    {
        var runner = new RecordingRunner();
        var kernel = new AgentKernel(new SpecialistRegistry(), runner, options: new AgentKernelOptions(MaxConcurrentSpecialists: 4));
        var result = await kernel.RunMissionAsync(new Intent("Find the scheduler race and fix it", CorrelationId.New()));

        Assert.True(result.Succeeded);
        Assert.Equal(MissionState.Succeeded, result.Mission.State);
        Assert.Equal(4, result.AssignmentResults.Count);
        Assert.Equal(2, runner.Roles.Count(role => role == SpecialistRole.Investigator));
        Assert.Contains(SpecialistRole.Builder, runner.Roles);
        Assert.Contains(SpecialistRole.Verifier, runner.Roles);
        Assert.True(runner.MaximumConcurrent >= 2);
    }

    [Fact]
    public async Task ExplicitArgusAssignmentDoesNotSpawnBuilder()
    {
        var runner = new RecordingRunner();
        var kernel = new AgentKernel(new SpecialistRegistry(), runner);
        var result = await kernel.RunMissionAsync(new Intent("@Argus verify the current branch", CorrelationId.New()));

        Assert.True(result.Succeeded);
        Assert.Single(result.AssignmentResults);
        Assert.Equal(SpecialistRole.Verifier, runner.Roles.Single());
    }

    [Fact]
    public async Task FailedIndependentVerificationCreatesBoundedRepair()
    {
        var runner = new FailFirstVerificationRunner();
        var kernel = new AgentKernel(new SpecialistRegistry(), runner);
        var result = await kernel.RunMissionAsync(new Intent("Fix the scheduler race", CorrelationId.New()));

        Assert.True(result.Succeeded);
        Assert.Equal(2, runner.VerificationCalls);
        Assert.Equal(6, result.AssignmentResults.Count);
    }

    [Fact]
    public async Task CancelledMissionDoesNotLoop()
    {
        var runner = new BlockingRunner();
        var kernel = new AgentKernel(new SpecialistRegistry(), runner, options: new AgentKernelOptions(DefaultMissionTimeout: TimeSpan.FromMilliseconds(30)));
        var result = await kernel.RunMissionAsync(new Intent("@Orion investigate cancellation", CorrelationId.New()));

        Assert.Equal(MissionState.Blocked, result.Mission.State);
        Assert.True(runner.Cancelled);
    }

    [Fact]
    public async Task MessageBusRoutesToRecipientAndObservers()
    {
        var bus = new AgentMessageBus(8);
        var target = SpecialistInstanceId.New();
        await using var recipient = bus.Subscribe(target);
        await using var observer = bus.SubscribeAll();
        var mission = MissionId.New();
        var envelope = new AgentMessageEnvelope(AgentMessageId.New(), mission, null, SpecialistInstanceId.New(), target, SpecialistRole.Investigator, CorrelationId.New(), DateTimeOffset.UtcNow);
        await bus.PublishAsync(new ProgressSummaryMessage(envelope, "progress", .5));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var received = await recipient.ReadAllAsync(timeout.Token).FirstAsync(timeout.Token);
        var observed = await observer.ReadAllAsync(timeout.Token).FirstAsync(timeout.Token);
        Assert.Equal("progress", received.Kind);
        Assert.Equal(received.Envelope.MessageId, observed.Envelope.MessageId);
    }

    [Fact]
    public void HandoffUsesExistingAxlDelegationAndRoundTrips()
    {
        var registry = new SpecialistRegistry();
        var orion = registry.CreateInstance(new SpecialistDefinitionId("orion"));
        var daedalus = registry.CreateInstance(new SpecialistDefinitionId("daedalus"));
        var assignment = NewAssignment(SpecialistRole.Builder);
        var result = new AgentAssignmentResult(true, "Root cause and implementation context", [EvidenceId.New()]);
        var document = AgentKernel.ToAxlHandoff(assignment, result, orion, daedalus);
        var text = AxlFormatter.Compact(document);
        var parsed = AxlParser.Parse(text);

        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Diagnostics));
        Assert.Equal(2, parsed.Document!.Commands.Length);
        Assert.IsType<AxlDelegation>(parsed.Document.Commands[0]);
        Assert.IsType<AxlResult>(parsed.Document.Commands[1]);
    }

    [Fact]
    public async Task IsolatedWorkspaceIdentityDoesNotShareBuilderPath()
    {
        var service = new ManagedWorkspaceIsolationService("/tmp/abraxius-agent-tests");
        var mission = MissionId.New();
        var first = await service.CreateAsync(new WorkspaceRequest("/repo", WorkspacePolicy.IsolatedWorktree, mission, AssignmentId.New()));
        var second = await service.CreateAsync(new WorkspaceRequest("/repo", WorkspacePolicy.IsolatedWorktree, mission, AssignmentId.New()));

        Assert.True(first.IsIsolated);
        Assert.NotEqual(first.Path, second.Path);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await service.IntegrateAsync(first));
    }

    private static AgentAssignment NewAssignment(SpecialistRole role) => new(AssignmentId.New(), MissionId.New(), role, SpecialistInstanceId.New(), "inspect", ["evidence"]);

    private sealed class RecordingRunner : IAgentAssignmentRunner
    {
        private int _active;
        public ConcurrentBag<SpecialistRole> Roles { get; } = [];
        public int MaximumConcurrent { get; private set; }
        public async ValueTask<AgentAssignmentResult> RunAsync(SpecialistDefinition definition, SpecialistInstance instance, AgentAssignment assignment, MissionContext context, CancellationToken cancellationToken)
        {
            Roles.Add(definition.Role);
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrent = Math.Max(MaximumConcurrent, active);
            await Task.Delay(10, cancellationToken);
            Interlocked.Decrement(ref _active);
            return new AgentAssignmentResult(true, definition.DisplayName + " completed", Verification: definition.Role == SpecialistRole.Verifier ? VerificationStatus.Passed : null);
        }
    }

    private sealed class BlockingRunner : IAgentAssignmentRunner
    {
        public bool Cancelled { get; private set; }
        public async ValueTask<AgentAssignmentResult> RunAsync(SpecialistDefinition definition, SpecialistInstance instance, AgentAssignment assignment, MissionContext context, CancellationToken cancellationToken)
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new AgentAssignmentResult(true, "unexpected");
        }
    }

    private sealed class FailFirstVerificationRunner : IAgentAssignmentRunner
    {
        public int VerificationCalls { get; private set; }
        public ValueTask<AgentAssignmentResult> RunAsync(SpecialistDefinition definition, SpecialistInstance instance, AgentAssignment assignment, MissionContext context, CancellationToken cancellationToken)
        {
            if (definition.Role == SpecialistRole.Verifier)
            {
                VerificationCalls++;
                if (VerificationCalls == 1) return ValueTask.FromResult(new AgentAssignmentResult(false, "race remains", Verification: VerificationStatus.Failed, MadeProgress: true));
                return ValueTask.FromResult(new AgentAssignmentResult(true, "verified", Verification: VerificationStatus.Passed));
            }
            return ValueTask.FromResult(new AgentAssignmentResult(true, definition.DisplayName + " completed"));
        }
    }
}

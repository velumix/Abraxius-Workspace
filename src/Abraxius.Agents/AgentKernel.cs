using System.Collections.Concurrent;
using System.Collections.Immutable;
using Abraxius.Axl;
using Abraxius.Core;
using Abraxius.Memory;
using Abraxius.Protocol;

namespace Abraxius.Agents;

public interface IAgentAssignmentRunner
{
    ValueTask<AgentAssignmentResult> RunAsync(SpecialistDefinition definition, SpecialistInstance instance, AgentAssignment assignment, MissionContext context, CancellationToken cancellationToken);
}

public interface IAgentPolicyEnforcer
{
    AgentPolicyDecision Authorize(SpecialistDefinition definition, AgentAssignment assignment, CapabilityId capability, bool mutation);
}

public sealed class DefaultAgentPolicyEnforcer : IAgentPolicyEnforcer
{
    public AgentPolicyDecision Authorize(SpecialistDefinition definition, AgentAssignment assignment, CapabilityId capability, bool mutation)
    {
        if (mutation && definition.CapabilityPolicy.Mutation == MutationPolicy.Deny) return AgentPolicyDecision.Denied($"{definition.DisplayName} is read-only.");
        if (!definition.CapabilityPolicy.Allows(capability)) return AgentPolicyDecision.Denied($"Capability '{capability}' is not granted to {definition.DisplayName}.");
        return AgentPolicyDecision.Granted();
    }
}

public sealed class AgentKernel : IAsyncDisposable
{
    private readonly ISpecialistRegistry _registry;
    private readonly IAgentAssignmentRunner _runner;
    private readonly IAgentPolicyEnforcer _policy;
    private readonly IAgentMessageBus _messages;
    private readonly AgentEventHub _events;
    private readonly MemoryContextCompiler? _memory;
    private readonly IAgentMissionStore? _missionStore;
    private readonly IAgentIntentInterpreter _intentInterpreter;
    private readonly AgentKernelOptions _options;
    private readonly SemaphoreSlim _runningSlots;
    private readonly ConcurrentDictionary<MissionId, Mission> _missions = new();
    private readonly ConcurrentDictionary<AssignmentId, AgentAssignment> _assignments = new();
    private readonly ConcurrentDictionary<AssignmentId, AgentAssignmentResult> _results = new();
    private readonly ConcurrentDictionary<MissionId, CancellationTokenSource> _missionCancellation = new();

    public AgentKernel(ISpecialistRegistry registry, IAgentAssignmentRunner runner, IAgentPolicyEnforcer? policy = null, IAgentMessageBus? messages = null, AgentKernelOptions? options = null, MemoryContextCompiler? memory = null, IAgentMissionStore? missionStore = null, IAgentIntentInterpreter? intentInterpreter = null)
    {
        _registry = registry;
        _runner = runner;
        _policy = policy ?? new DefaultAgentPolicyEnforcer();
        _options = options ?? new AgentKernelOptions();
        _runningSlots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentSpecialists));
        _messages = messages ?? new AgentMessageBus(_options.MessageBufferCapacity);
        _events = new AgentEventHub(_options.EventBufferCapacity);
        _memory = memory;
        _missionStore = missionStore;
        _intentInterpreter = intentInterpreter ?? new AgentIntentInterpreter(registry);
    }

    public ISpecialistRegistry Registry => _registry;
    public IAgentMessageBus Messages => _messages;
    public AgentEventHub Events => _events;
    public IReadOnlyList<Mission> Missions => _missions.Values.OrderByDescending(static mission => mission.CreatedAt).Take(_options.MaxMissionHistory).ToArray();
    public IReadOnlyDictionary<AssignmentId, AgentAssignmentResult> Results => _results;
    public IReadOnlyDictionary<AssignmentId, AgentAssignment> Assignments => _assignments;
    public IReadOnlyList<AgentMissionRecord> MissionRecords => _missions.Values.Select(mission => new AgentMissionRecord(mission, Describe(mission), mission.CompletedAt ?? mission.CreatedAt ?? DateTimeOffset.UtcNow, _results.Count(pair => _assignments.TryGetValue(pair.Key, out var assignment) && assignment.MissionId == mission.Id))).OrderByDescending(static record => record.RecordedAt).ToArray();

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_missionStore is null) return;
        foreach (var record in await _missionStore.LoadAsync(cancellationToken).ConfigureAwait(false)) _missions.TryAdd(record.Mission.Id, record.Mission);
    }

    public async ValueTask<MissionResult> RunMissionAsync(Intent intent, MissionSuccessContract? successContract = null, SpecialistRole? explicitRole = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(intent);
        var started = DateTimeOffset.UtcNow;
        var contract = successContract ?? InferContract(intent.Objective);
        var leadDefinition = ResolveLead(intent.Objective, explicitRole);
        var mission = new Mission(MissionId.New(), intent, contract, intent.Priority, leadDefinition.CognitiveBudget, leadDefinition.AutonomyBudget, leadDefinition.WorkspacePolicy.Mode, CreatedAt: started);
        _missions[mission.Id] = mission;
        Publish(new AgentEvent(AgentEventKind.MissionCreated, started, mission.Id, Detail: contract.Objective));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var maximumDuration = _options.DefaultMissionTimeout ?? leadDefinition.CognitiveBudget.MaximumDuration;
        if (maximumDuration is { } duration)
        {
            timeout.CancelAfter(_options.DefaultMissionTimeout ?? duration);
        }
        _missionCancellation[mission.Id] = timeout;
        try
        {
            var interpretation = _intentInterpreter.Interpret(intent.Objective);
            var targetedRole = explicitRole ?? interpretation.ExplicitRole;
            if (targetedRole is not null)
            {
                mission = await RunSingleRoleAsync(mission, ResolveLead(intent.Objective, explicitRole ?? targetedRole), timeout.Token).ConfigureAwait(false);
            }
            else if (interpretation.Mode == AgentMissionMode.Build)
            {
                mission = await RunTeamMissionAsync(mission, timeout.Token).ConfigureAwait(false);
            }
            else if (interpretation.Mode == AgentMissionMode.Investigation)
            {
                mission = await RunSingleRoleAsync(mission, ResolveLead(intent.Objective, SpecialistRole.Investigator), timeout.Token).ConfigureAwait(false);
            }
            else if (interpretation.Mode == AgentMissionMode.Verification)
            {
                mission = await RunSingleRoleAsync(mission, ResolveLead(intent.Objective, SpecialistRole.Verifier), timeout.Token).ConfigureAwait(false);
            }
            else
            {
                mission = await RunSingleRoleAsync(mission, leadDefinition, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            mission = ChangeMission(mission, cancellationToken.IsCancellationRequested ? MissionState.Cancelled : MissionState.Blocked, "Mission cancelled or timed out.");
        }
        catch (Exception exception)
        {
            mission = ChangeMission(mission, MissionState.Failed, exception.Message);
        }
        finally
        {
            _missionCancellation.TryRemove(mission.Id, out _);
        }

        var result = new MissionResult(mission, Describe(mission), _results.Where(pair => _assignments.TryGetValue(pair.Key, out var assignment) && assignment.MissionId == mission.Id).ToDictionary(static pair => pair.Key, static pair => pair.Value), DateTimeOffset.UtcNow - started);
        if (_missionStore is not null)
        {
            await _missionStore.SaveAsync(new AgentMissionRecord(result.Mission, result.Summary, DateTimeOffset.UtcNow, result.AssignmentResults.Count), CancellationToken.None).ConfigureAwait(false);
        }
        Publish(new AgentEvent(AgentEventKind.MissionCompleted, DateTimeOffset.UtcNow, mission.Id, Detail: result.Summary, Payload: result));
        return result;
    }

    public void CancelMission(MissionId id, string reason = "Cancelled by user.")
    {
        if (_missionCancellation.TryGetValue(id, out var cancellation)) cancellation.Cancel();
        if (_missions.TryGetValue(id, out var mission)) ChangeMission(mission, MissionState.Cancelled, reason);
    }

    public static AxlDocument ToAxlHandoff(AgentAssignment assignment, AgentAssignmentResult result, SpecialistInstance from, SpecialistInstance to)
    {
        var envelope = new AxlDelegation(new AxlReference(AxlReferenceKind.Agent, to.DisplayName.ToLowerInvariant()), result.Summary, result.SafeEvidence.Select(static evidence => new AxlReference(AxlReferenceKind.Evidence, evidence.ToString())).ToImmutableArray(), "readonly");
        var response = new AxlResult(new AxlReference(AxlReferenceKind.Task, assignment.Id.ToString()), result.Succeeded, result.SafeEvidence.Select(static evidence => new AxlReference(AxlReferenceKind.Evidence, evidence.ToString())).ToImmutableArray(), result.FailureCode);
        return new AxlDocument(AxlVersion.Current, [envelope, response]);
    }

    private async ValueTask<Mission> RunTeamMissionAsync(Mission mission, CancellationToken cancellationToken)
    {
        mission = ChangeMission(mission, MissionState.Planning, "Athena is coordinating investigation, implementation, and verification.");
        var orion = ResolveLead("", SpecialistRole.Investigator);
        var investigations = new[] { "Investigate current source and symbols.", "Inspect project history, diagnostics, and relevant memory." };
        var tasks = investigations.Select(objective => RunAssignmentAsync(mission, orion, objective, ["Produce evidence-backed findings."], [], cancellationToken).AsTask()).ToArray();
        var findings = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (findings.Any(static item => !item.Succeeded)) return ChangeMission(mission, MissionState.Blocked, "Investigation did not produce enough evidence for implementation.");
        var evidence = findings.SelectMany(static item => item.SafeEvidence).Distinct().ToImmutableArray();
        mission = mission with { Evidence = evidence };
        _missions[mission.Id] = mission;
        var daedalus = ResolveLead("", SpecialistRole.Builder);
        var implementation = await RunAssignmentAsync(mission, daedalus, mission.SuccessContract.Objective, mission.SuccessContract.RequiredOutcomes, evidence, cancellationToken).ConfigureAwait(false);
        if (!implementation.Succeeded) return ChangeMission(mission, MissionState.Blocked, implementation.Summary);
        var argus = ResolveLead("", SpecialistRole.Verifier);
        mission = ChangeMission(mission, MissionState.Verifying, "Argus is independently verifying the candidate.");
        var verification = await RunAssignmentAsync(mission, argus, mission.SuccessContract.Objective, mission.SuccessContract.VerificationRequirements, implementation.SafeEvidence, cancellationToken).ConfigureAwait(false);
        if (verification.Succeeded && verification.Verification is not VerificationStatus.Failed) return ChangeMission(mission, MissionState.Succeeded, "Argus verified the mission success contract.");
        if (daedalus.VerificationPolicy.MaxRepairAttempts > 0)
        {
            var repair = await RunAssignmentAsync(mission, daedalus, $"Repair the implementation after independent verification failure: {verification.Summary}", mission.SuccessContract.RequiredOutcomes, verification.SafeEvidence, cancellationToken, attempt: 1).ConfigureAwait(false);
            if (repair.Succeeded)
            {
                var final = await RunAssignmentAsync(mission, argus, mission.SuccessContract.Objective, mission.SuccessContract.VerificationRequirements, repair.SafeEvidence, cancellationToken, attempt: 1).ConfigureAwait(false);
                return ChangeMission(mission, final.Succeeded ? MissionState.Succeeded : MissionState.Failed, final.Summary);
            }
        }
        return ChangeMission(mission, MissionState.Failed, $"Argus rejected the implementation: {verification.Summary}");
    }

    private async ValueTask<Mission> RunSingleRoleAsync(Mission mission, SpecialistDefinition definition, CancellationToken cancellationToken)
    {
        mission = ChangeMission(mission, MissionState.Executing, $"{definition.DisplayName} is handling the assignment.");
        var criteria = definition.Role == SpecialistRole.Verifier ? mission.SuccessContract.VerificationRequirements : mission.SuccessContract.RequiredOutcomes;
        var result = await RunAssignmentAsync(mission, definition, mission.Intent.Objective, criteria, mission.SafeEvidence, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && definition.Role == SpecialistRole.Builder && definition.VerificationPolicy.Required)
        {
            var verifier = ResolveLead(string.Empty, SpecialistRole.Verifier);
            mission = ChangeMission(mission, MissionState.Verifying, "Argus is independently checking the approved builder result.");
            var verification = await RunAssignmentAsync(mission, verifier, mission.SuccessContract.Objective, mission.SuccessContract.VerificationRequirements, result.SafeEvidence, cancellationToken).ConfigureAwait(false);
            return ChangeMission(mission, verification.Succeeded ? MissionState.Succeeded : MissionState.Failed, verification.Summary);
        }
        var state = result.Succeeded ? MissionState.Succeeded : MissionState.Failed;
        if (definition.Role == SpecialistRole.Verifier && result.Verification == VerificationStatus.Inconclusive) state = MissionState.Blocked;
        return ChangeMission(mission, state, result.Summary);
    }

    private async ValueTask<AgentAssignmentResult> RunAssignmentAsync(Mission mission, SpecialistDefinition definition, string objective, IReadOnlyList<string> criteria, IReadOnlyList<EvidenceId> evidence, CancellationToken cancellationToken, int attempt = 0)
    {
        if (_missions.TryGetValue(mission.Id, out var currentMission)) mission = currentMission;
        if (mission.SafeAssignments.Length >= mission.AutonomyBudget.MaxSubAssignments) return new(false, "Assignment budget exhausted.", FailureCode: "BudgetExhausted", MadeProgress: false);
        var instance = _registry.CreateInstance(definition.Id);
        var assignment = new AgentAssignment(AssignmentId.New(), mission.Id, definition.Role, instance.Id, objective, criteria, evidence, Workspace: definition.WorkspacePolicy.Mode, Priority: mission.Priority, State: AssignmentState.Queued, Attempt: attempt);
        _assignments[assignment.Id] = assignment;
        _registry.UpdateInstance(instance with { State = SpecialistState.Assigned, AssignmentId = assignment.Id });
        mission = mission with { Assignments = mission.SafeAssignments.Add(assignment.Id) };
        _missions[mission.Id] = mission;
        Publish(new AgentEvent(AgentEventKind.AssignmentCreated, DateTimeOffset.UtcNow, mission.Id, assignment.Id, instance.Id, objective, assignment));
        await _messages.PublishAsync(new AssignmentMessage(
            CreateEnvelope(mission, assignment, instance, instance), assignment), cancellationToken).ConfigureAwait(false);
        var context = await BuildContextAsync(mission, definition, cancellationToken).ConfigureAwait(false);
        UpdateAssignment(assignment with { State = AssignmentState.Running });
        _registry.UpdateInstance(instance with { State = SpecialistState.Running, AssignmentId = assignment.Id });
        AgentAssignmentResult result;
        var requestedCapability = definition.Role switch
        {
            SpecialistRole.Investigator => new CapabilityId("evidence.search"),
            SpecialistRole.Builder => new CapabilityId("code.implement"),
            SpecialistRole.Verifier => new CapabilityId("verify.requirements"),
            _ => new CapabilityId("mission.coordinate")
        };
        var policyDecision = _policy.Authorize(definition, assignment, requestedCapability, definition.Role == SpecialistRole.Builder);
        if (!policyDecision.Allowed)
        {
            result = new AgentAssignmentResult(false, policyDecision.Reason ?? "Assignment denied by policy.", FailureCode: "PolicyDenied", MadeProgress: false);
        }
        else
        {
            var acquired = false;
            try
            {
                await _runningSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired = true;
                result = await _runner.RunAsync(definition, instance, assignment, context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                UpdateAssignment(assignment with { State = AssignmentState.Cancelled });
                throw;
            }
            catch (Exception exception)
            {
                result = new AgentAssignmentResult(false, exception.Message, FailureCode: "AssignmentFailed", MadeProgress: false);
            }
            finally
            {
                if (acquired) _runningSlots.Release();
            }
        }
        _results[assignment.Id] = result;
        var message = definition.Role switch
        {
            SpecialistRole.Investigator => new EvidenceResponseMessage(CreateEnvelope(mission, assignment, instance, instance), result.Summary, result.SafeEvidence) as AgentMessage,
            SpecialistRole.Builder => new ImplementationReadyMessage(CreateEnvelope(mission, assignment, instance, instance), result.Summary, result.SafeEvidence),
            SpecialistRole.Verifier => new VerificationResultMessage(CreateEnvelope(mission, assignment, instance, instance), result.Verification ?? VerificationStatus.Inconclusive, result.Summary, result.SafeEvidence),
            _ => new ProgressSummaryMessage(CreateEnvelope(mission, assignment, instance, instance), result.Summary, result.Succeeded ? 1 : 0)
        };
        await _messages.PublishAsync(message, cancellationToken).ConfigureAwait(false);
        Publish(new AgentEvent(AgentEventKind.MessageObserved, DateTimeOffset.UtcNow, mission.Id, assignment.Id, instance.Id, message.Kind, message));
        UpdateAssignment(assignment with { State = result.Succeeded ? AssignmentState.Completed : AssignmentState.Failed });
        _registry.UpdateInstance(instance with { State = result.Succeeded ? SpecialistState.Idle : SpecialistState.Failed, AssignmentId = null });
        return result;
    }

    private async ValueTask<MissionContext> BuildContextAsync(Mission mission, SpecialistDefinition definition, CancellationToken cancellationToken)
    {
        MemoryContextPackage? package = null;
        if (_memory is not null)
        {
            package = await _memory.CompileAsync(new ContextCompilationRequest(mission.Intent.Objective, new MemorySearchQuery(mission.Intent.Objective, 8, Mode: definition.MemoryPolicy.Mode, MaximumPrivacy: definition.MemoryPolicy.MaximumPrivacy), 2_000, 512), cancellationToken).ConfigureAwait(false);
        }
        return new MissionContext(mission, _assignments.Values.Where(item => item.MissionId == mission.Id).ToArray(), mission.SafeEvidence, package);
    }

    private SpecialistDefinition ResolveLead(string objective, SpecialistRole? role)
    {
        if (role is { } explicitRole)
        {
            var definition = _registry.Definitions.FirstOrDefault(item => item.Role == explicitRole);
            if (definition is not null) return definition;
        }
        var trimmed = objective.TrimStart();
        if (trimmed.StartsWith('@'))
        {
            var name = trimmed[1..].Split([' ', '\t', '\r', '\n'], 2)[0];
            if (_registry.TryResolve(name, out var targeted)) return targeted;
        }
        return _registry.Definitions.First(item => item.Role == SpecialistRole.Coordinator);
    }

    private static bool IsInvestigation(string value) => ContainsAny(value, "find", "where", "investigate", "inspect", "why", "diagnose", "history", "search", "trace");
    private static bool IsVerification(string value) => ContainsAny(value, "verify", "validate", "test", "audit", "review");
    private static bool IsBuild(string value) => ContainsAny(value, "fix", "implement", "repair", "refactor", "add", "create", "build", "change");
    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static MissionSuccessContract InferContract(string objective) => new(objective, ["Produce a useful result."], ["Result is independently checked."], ["Follow capability and mutation policy."]);
    private static string Describe(Mission mission) => mission.State switch { MissionState.Succeeded => "Mission completed and verified.", MissionState.Blocked => "Mission is blocked and needs user or runtime intervention.", MissionState.Cancelled => "Mission cancelled.", _ => $"Mission ended in {mission.State}." };
    private Mission ChangeMission(Mission mission, MissionState state, string detail)
    {
        if (_missions.TryGetValue(mission.Id, out var current)) mission = current;
        var updated = mission with { State = state, CompletedAt = state is MissionState.Succeeded or MissionState.Failed or MissionState.Blocked or MissionState.Cancelled ? DateTimeOffset.UtcNow : mission.CompletedAt };
        _missions[mission.Id] = updated;
        Publish(new AgentEvent(AgentEventKind.MissionStateChanged, DateTimeOffset.UtcNow, mission.Id, Detail: detail, Payload: updated));
        return updated;
    }
    private void UpdateAssignment(AgentAssignment assignment)
    {
        _assignments[assignment.Id] = assignment;
        Publish(new AgentEvent(AgentEventKind.AssignmentStateChanged, DateTimeOffset.UtcNow, assignment.MissionId, assignment.Id, assignment.InstanceId, assignment.State.ToString(), assignment));
    }
    private void Publish(AgentEvent value) => _events.Publish(value);

    private static AgentMessageEnvelope CreateEnvelope(Mission mission, AgentAssignment assignment, SpecialistInstance from, SpecialistInstance to) => new(
        AgentMessageId.New(), mission.Id, assignment.Id, from.Id, to.Id, to.Role, mission.Intent.CorrelationId, DateTimeOffset.UtcNow);

    public ValueTask DisposeAsync()
    {
        foreach (var cancellation in _missionCancellation.Values) cancellation.Cancel();
        _runningSlots.Dispose();
        return ValueTask.CompletedTask;
    }
}

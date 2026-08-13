using System.Collections.Immutable;
using Abraxius.Protocol;

namespace Abraxius.Agents;

public static class BuiltInSpecialists
{
    public static ImmutableArray<SpecialistDefinition> All { get; } =
    [
        Create("athena", SpecialistRole.Coordinator, "Athena", "Mission strategist and coordinator.",
            ["mission.plan", "mission.coordinate", "mission.resolve"], MutationPolicy.Deny,
            WorkspacePolicy.SharedReadOnly, ["Butler"]),
        Create("orion", SpecialistRole.Investigator, "Orion", "Evidence-led repository and systems investigation.",
            ["evidence.search", "repository.investigate", "git.analyze", "diagnose"], MutationPolicy.Deny,
            WorkspacePolicy.SharedReadOnly, ["Scout"]),
        Create("daedalus", SpecialistRole.Builder, "Daedalus", "Constrained implementation and repair.",
            ["code.implement", "code.refactor", "test.create", "build.repair"], MutationPolicy.Proposed,
            WorkspacePolicy.SharedMutable, ["Smith"]),
        Create("argus", SpecialistRole.Verifier, "Argus", "Independent verification and regression detection.",
            ["verify.tests", "verify.requirements", "verify.invariants", "review.diff"], MutationPolicy.Deny,
            WorkspacePolicy.SharedReadOnly, ["Verifier"])
    ];

    private static SpecialistDefinition Create(
        string id,
        SpecialistRole role,
        string displayName,
        string mission,
        IEnumerable<string> capabilities,
        MutationPolicy mutation,
        WorkspacePolicy workspace,
        IEnumerable<string> aliases) => new()
    {
        Id = new SpecialistDefinitionId(id),
        Role = role,
        DisplayName = displayName,
        Mission = new SpecialistMission(mission),
        CapabilityPolicy = new SpecialistCapabilityPolicy(capabilities.Select(static value => new CapabilityId(value)).ToHashSet(), mutation),
        ModelPolicy = new SpecialistModelPolicy(RequireCodingCapability: role == SpecialistRole.Builder, PreferAlternateFamily: role == SpecialistRole.Verifier),
        MemoryPolicy = new SpecialistMemoryPolicy(),
        PlanningPolicy = new SpecialistPlanningPolicy(AllowDelegation: role == SpecialistRole.Coordinator),
        VerificationPolicy = new SpecialistVerificationPolicy(Required: role is SpecialistRole.Coordinator or SpecialistRole.Builder),
        WorkspacePolicy = new SpecialistWorkspacePolicy(workspace, AllowSharedWrites: workspace == WorkspacePolicy.SharedMutable),
        CognitiveBudget = new CognitiveBudget(),
        AutonomyBudget = new AutonomyBudget(),
        Aliases = aliases.ToImmutableArray()
    };
}

public interface ISpecialistRegistry
{
    IReadOnlyList<SpecialistDefinition> Definitions { get; }
    IReadOnlyList<SpecialistInstance> Instances { get; }
    bool TryGet(SpecialistDefinitionId id, out SpecialistDefinition definition);
    bool TryResolve(string nameOrAlias, out SpecialistDefinition definition);
    SpecialistInstance CreateInstance(SpecialistDefinitionId id, SpecialistHostOptions? options = null);
    bool TryGetInstance(SpecialistInstanceId id, out SpecialistInstance instance);
    void UpdateInstance(SpecialistInstance instance);
}

public sealed record SpecialistHostOptions(AgentHostKind Host = AgentHostKind.InProcess);

public sealed class SpecialistRegistry : ISpecialistRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SpecialistDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<SpecialistInstanceId, SpecialistInstance> _instances = [];

    public SpecialistRegistry(IEnumerable<SpecialistDefinition>? definitions = null)
    {
        foreach (var definition in definitions ?? BuiltInSpecialists.All) Register(definition);
    }

    public IReadOnlyList<SpecialistDefinition> Definitions { get { lock (_gate) return _definitions.Values.GroupBy(static definition => definition.Id).Select(static group => group.First()).OrderBy(static d => d.DisplayName, StringComparer.Ordinal).ToArray(); } }
    public IReadOnlyList<SpecialistInstance> Instances { get { lock (_gate) return _instances.Values.ToArray(); } }

    public void Register(SpecialistDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_gate)
        {
            if (_definitions.ContainsKey(definition.Id.Value)) throw new InvalidOperationException($"Specialist '{definition.Id}' is already registered.");
            Validate(definition);
            _definitions.Add(definition.Id.Value, definition);
            foreach (var alias in definition.Aliases)
            {
                if (_definitions.ContainsKey(alias)) throw new InvalidOperationException($"Specialist alias '{alias}' collides with a definition.");
                _definitions.Add(alias, definition);
            }
        }
    }

    public bool TryGet(SpecialistDefinitionId id, out SpecialistDefinition definition) => _definitions.TryGetValue(id.Value, out definition!);

    public bool TryResolve(string nameOrAlias, out SpecialistDefinition definition) => _definitions.TryGetValue(nameOrAlias.Trim(), out definition!);

    public SpecialistInstance CreateInstance(SpecialistDefinitionId id, SpecialistHostOptions? options = null)
    {
        if (!TryGet(id, out var definition)) throw new KeyNotFoundException($"Unknown specialist definition '{id}'.");
        var instance = new SpecialistInstance(SpecialistInstanceId.New(), definition.Id, definition.Role, definition.DisplayName, Host: options?.Host ?? definition.HostKind, UpdatedAt: DateTimeOffset.UtcNow);
        lock (_gate) _instances.Add(instance.Id, instance);
        return instance;
    }

    public bool TryGetInstance(SpecialistInstanceId id, out SpecialistInstance instance) => _instances.TryGetValue(id, out instance!);

    public void UpdateInstance(SpecialistInstance instance)
    {
        lock (_gate) _instances[instance.Id] = instance with { UpdatedAt = DateTimeOffset.UtcNow };
    }

    private static void Validate(SpecialistDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(definition.Id.Value) || string.IsNullOrWhiteSpace(definition.DisplayName)) throw new ArgumentException("Specialist identity is required.");
        if (definition.CognitiveBudget.MaxReplans < 0 || definition.AutonomyBudget.MaxDelegationDepth < 0) throw new ArgumentException("Specialist budgets cannot be negative.");
    }
}

public interface ISpecialistFactory
{
    SpecialistDefinition CreateDomainSpecialist(SpecialistDefinition sourceTemplate, string id, string displayName, SpecialistRole role, IEnumerable<CapabilityId> requestedCapabilities);
}

public sealed class SpecialistFactory : ISpecialistFactory
{
    public SpecialistDefinition CreateDomainSpecialist(SpecialistDefinition sourceTemplate, string id, string displayName, SpecialistRole role, IEnumerable<CapabilityId> requestedCapabilities)
    {
        ArgumentNullException.ThrowIfNull(sourceTemplate);
        var requested = requestedCapabilities.ToHashSet();
        var effective = sourceTemplate.CapabilityPolicy.AllowedCapabilities.Intersect(requested).ToHashSet();
        return sourceTemplate with
        {
            Id = new SpecialistDefinitionId(id),
            DisplayName = displayName,
            Role = role,
            CapabilityPolicy = sourceTemplate.CapabilityPolicy with { AllowedCapabilities = effective },
            Aliases = ImmutableArray<string>.Empty
        };
    }
}

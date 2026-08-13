namespace Abraxius.Agents;

public enum AgentMissionMode { Direct, Investigation, Verification, Build }

public sealed record AgentIntentInterpretation(AgentMissionMode Mode, SpecialistRole? ExplicitRole = null);

public interface IAgentIntentInterpreter
{
    AgentIntentInterpretation Interpret(string objective);
}

public sealed class AgentIntentInterpreter(ISpecialistRegistry registry) : IAgentIntentInterpreter
{
    public AgentIntentInterpretation Interpret(string objective)
    {
        var explicitRole = ParseRole(objective);
        if (explicitRole is not null) return new(AgentMissionMode.Direct, explicitRole);
        if (ContainsAny(objective, "fix", "implement", "repair", "refactor", "add", "create", "build", "change")) return new(AgentMissionMode.Build);
        if (ContainsAny(objective, "verify", "validate", "test", "audit", "review")) return new(AgentMissionMode.Verification);
        if (ContainsAny(objective, "find", "where", "investigate", "inspect", "why", "diagnose", "history", "search", "trace")) return new(AgentMissionMode.Investigation);
        return new(AgentMissionMode.Direct);
    }

    private SpecialistRole? ParseRole(string objective)
    {
        var trimmed = objective.TrimStart();
        if (!trimmed.StartsWith('@')) return null;
        var name = trimmed[1..].Split([' ', '\t', '\r', '\n'], 2)[0];
        return registry.TryResolve(name, out var definition) ? definition.Role : null;
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}

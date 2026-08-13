using Abraxius.Protocol;

namespace Abraxius.Core;

public sealed record EvidenceInput(
    string Kind,
    string? Name,
    ReadOnlyMemory<byte> Data,
    string ContentType = "application/octet-stream",
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record EvidenceItem(EvidenceReference Reference, ReadOnlyMemory<byte> Data);

public interface IEvidenceStore
{
    ValueTask<EvidenceReference> StoreAsync(EvidenceInput input, CancellationToken cancellationToken = default);
    ValueTask<EvidenceItem?> GetAsync(EvidenceId id, CancellationToken cancellationToken = default);
}

public sealed record DelegationRequest
{
    public DelegationRequest(
        string objective,
        IReadOnlyList<EvidenceId> evidence,
        ExecutionConstraints constraints,
        string expectedOutputSchema)
    {
        Objective = string.IsNullOrWhiteSpace(objective)
            ? throw new ArgumentException("A delegation objective is required.", nameof(objective))
            : objective;
        Evidence = evidence?.ToArray() ?? throw new ArgumentNullException(nameof(evidence));
        Constraints = constraints ?? throw new ArgumentNullException(nameof(constraints));
        ExpectedOutput = new OutputContract("delegation", expectedOutputSchema);
    }

    public AgentId? RequestedAgent { get; init; }
    public string Objective { get; init; }
    public IReadOnlyList<EvidenceId> Evidence { get; init; }
    public ExecutionConstraints Constraints { get; init; }
    public OutputContract ExpectedOutput { get; init; }
    public CausalityContext? Causality { get; init; }
}

public sealed record Finding(string Code, string Description, double Confidence);

public sealed record ProposedAction(
    CapabilityId Capability,
    string Objective,
    ActionTarget Target,
    string Operation,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<EvidenceId>? Evidence = null);

public sealed record Diagnosis(
    double Confidence,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<EvidenceId> Evidence,
    IReadOnlyList<ProposedAction> ProposedActions);

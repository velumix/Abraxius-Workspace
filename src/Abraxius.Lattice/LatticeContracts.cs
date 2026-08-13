using Abraxius.Protocol;

namespace Abraxius.Lattice;

public interface ILatticeCapability
{
    CapabilityDescriptor Descriptor { get; }
    ValueTask<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken cancellationToken = default);
}

public interface ILatticePolicy
{
    ValueTask<RuntimeError?> ValidateAsync(CapabilityRequest request, CapabilityDescriptor descriptor, CancellationToken cancellationToken = default);
}

public sealed class AllowListPolicy : ILatticePolicy
{
    private readonly HashSet<string> _allowedCapabilities;
    private readonly HashSet<string> _allowedOperations;

    public AllowListPolicy(
        IEnumerable<string>? allowedCapabilities = null,
        IEnumerable<string>? allowedOperations = null)
    {
        _allowedCapabilities = new HashSet<string>(allowedCapabilities ?? ["filesystem", "git", "demo"], StringComparer.OrdinalIgnoreCase);
        _allowedOperations = new HashSet<string>(allowedOperations ?? ["list_directory", "read_file", "search_files", "status", "diff", "run"], StringComparer.OrdinalIgnoreCase);
    }

    public ValueTask<RuntimeError?> ValidateAsync(CapabilityRequest request, CapabilityDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_allowedCapabilities.Contains(descriptor.Name) || !_allowedOperations.Contains(request.Operation))
        {
            return ValueTask.FromResult<RuntimeError?>(new RuntimeError(
                ErrorCategory.Policy,
                "capability_not_allowed",
                $"Capability '{descriptor.Name}' operation '{request.Operation}' is not permitted."));
        }

        if (!descriptor.ReadOnly && string.IsNullOrWhiteSpace(request.Target))
        {
            return ValueTask.FromResult<RuntimeError?>(new RuntimeError(
                ErrorCategory.Policy,
                "mutation_target_required",
                "Mutation capabilities require an explicit target."));
        }

        return ValueTask.FromResult<RuntimeError?>(null);
    }
}

public sealed class LatticeExecutor
{
    private readonly Dictionary<CapabilityId, ILatticeCapability> _capabilities;
    private readonly ILatticePolicy _policy;

    public LatticeExecutor(IEnumerable<ILatticeCapability> capabilities, ILatticePolicy? policy = null)
    {
        _capabilities = capabilities.ToDictionary(capability => capability.Descriptor.Id);
        _policy = policy ?? new AllowListPolicy(_capabilities.Keys.Select(static id => id.Value));
    }

    public IReadOnlyCollection<CapabilityDescriptor> Discover() =>
        _capabilities.Values.Select(static capability => capability.Descriptor).ToArray();

    public async ValueTask<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken cancellationToken = default)
    {
        if (!_capabilities.TryGetValue(request.Capability, out var capability))
        {
            return new CapabilityResult(false, null, [], Error: new RuntimeError(
                ErrorCategory.Tool,
                "capability_not_found",
                $"Capability '{request.Capability.Value}' was not discovered."));
        }

        var policyError = await _policy.ValidateAsync(request, capability.Descriptor, cancellationToken).ConfigureAwait(false);
        if (policyError is not null)
        {
            return new CapabilityResult(false, null, [], Error: policyError);
        }

        return await capability.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

public sealed class MockLatticeCapability : ILatticeCapability
{
    private readonly TimeSpan _latency;

    public MockLatticeCapability(TimeSpan? latency = null)
    {
        _latency = latency ?? TimeSpan.FromMilliseconds(500);
    }

    public CapabilityDescriptor Descriptor { get; } = new(
        "demo",
        "Deterministic development capability used by the headless and UI demos.",
        "{ target: string, operation: string }",
        ExecutorKind.Tool,
        true,
        ["run", "status", "search_files"]);

    public async ValueTask<CapabilityResult> ExecuteAsync(CapabilityRequest request, CancellationToken cancellationToken = default)
    {
        await Task.Delay(_latency, cancellationToken).ConfigureAwait(false);
        return new CapabilityResult(
            true,
            $"{request.Operation} completed for {request.Target}.",
            [],
            new Dictionary<string, string>
            {
                ["target"] = request.Target,
                ["operation"] = request.Operation
            });
    }
}

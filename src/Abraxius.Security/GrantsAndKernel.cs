using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Abraxius.Security;

public interface IAuthorizationGrantStore
{
    AuthorizationGrant Issue(AuthorizationGrant grant);
    AuthorizationGrant? FindAndConsume(AuthorizationRequest request, DateTimeOffset now);
    bool Revoke(AuthorizationGrantId grantId, string reason);
    int ExpireMission(Abraxius.Agents.MissionId missionId);
    IReadOnlyList<AuthorizationGrant> ListActive(DateTimeOffset now);
}

public sealed class InMemoryAuthorizationGrantStore : IAuthorizationGrantStore
{
    private readonly ConcurrentDictionary<AuthorizationGrantId, AuthorizationGrant> _grants = new();
    public AuthorizationGrant Issue(AuthorizationGrant grant)
    {
        if (grant.ExpiresAt <= grant.IssuedAt) throw new ArgumentException("Grant expiry must be after issuance.", nameof(grant));
        _grants[grant.GrantId] = grant;
        return grant;
    }

    public AuthorizationGrant? FindAndConsume(AuthorizationRequest request, DateTimeOffset now)
    {
        foreach (var pair in _grants)
        {
            var grant = pair.Value;
            if (!Matches(grant, request, now)) continue;
            while (true)
            {
                if (!_grants.TryGetValue(pair.Key, out var current) || !Matches(current, request, now)) break;
                var updated = current with { Uses = current.Uses + 1 };
                if (!_grants.TryUpdate(pair.Key, updated, current)) continue;
                if (updated.MaximumUses is { } maximum && updated.Uses >= maximum) _grants.TryRemove(pair.Key, out _);
                return updated;
            }
        }
        return null;
    }

    public bool Revoke(AuthorizationGrantId grantId, string reason) => _grants.TryRemove(grantId, out _);
    public int ExpireMission(Abraxius.Agents.MissionId missionId)
    {
        var count = 0;
        foreach (var grant in _grants.Values.Where(grant => grant.MissionId == missionId)) if (_grants.TryRemove(grant.GrantId, out _)) count++;
        return count;
    }
    public IReadOnlyList<AuthorizationGrant> ListActive(DateTimeOffset now) => _grants.Values.Where(grant => !grant.IsExpired(now)).OrderBy(static grant => grant.ExpiresAt).ToArray();

    private static bool Matches(AuthorizationGrant grant, AuthorizationRequest request, DateTimeOffset now)
    {
        if (grant.IsExpired(now) || grant.Subject.PrincipalId != request.Subject.PrincipalId) return false;
        if (!grant.Capabilities.Contains(request.Action.Operation)) return false;
        if (!request.Action.Resource.CanonicalUri.StartsWith(grant.ResourcePrefix, StringComparison.OrdinalIgnoreCase)) return false;
        if (grant.MissionId is { } mission && request.Subject.MissionId != mission) return false;
        if (grant.ProjectId is { } project && request.Context.ProjectId != project) return false;
        return true;
    }
}

public interface ISecurityKernel
{
    ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken = default);
    ValueTask RecordExecutionResultAsync(AuthorizationRequest request, AuthorizationDecision decision, bool succeeded, string? resultCode = null, CancellationToken cancellationToken = default);
    AuthorizationExplanation Explain(AuthorizationRequest request);
    bool Lockdown { get; set; }
}

public sealed class SecurityKernel : ISecurityKernel
{
    private readonly IPolicyEngine _policy;
    private readonly IRiskClassifier _risk;
    private readonly IAuthorizationGrantStore _grants;
    private readonly ISecurityAuditStore _audit;
    private readonly IResourceCanonicalizer _resources;

    public SecurityKernel(IPolicyEngine policy, IRiskClassifier risk, IAuthorizationGrantStore grants, ISecurityAuditStore audit, IResourceCanonicalizer resources)
    {
        _policy = policy; _risk = risk; _grants = grants; _audit = audit; _resources = resources;
    }

    public bool Lockdown
    {
        get => _policy is DeterministicPolicyEngine engine && engine.Lockdown;
        set { if (_policy is DeterministicPolicyEngine engine) engine.Lockdown = value; }
    }

    public async ValueTask<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        await _audit.AppendAsync(SecurityAuditEvent.Requested(request), cancellationToken).ConfigureAwait(false);
        var risk = _risk.Classify(request.Action);
        AuthorizationExplanation explanation;

        if (request.Action.Resource.Kind is ResourceKind.File or ResourceKind.Directory && request.Context.WorkspaceRoot is { Length: > 0 } root &&
            !_resources.IsWithin(request.Action.Resource, root))
        {
            var decision = new AuthorizationDecision(AuthorizationDecisionId.New(), AuthorizationOutcome.Deny,
                AuthorizationReasonCode.DeniedOutsideWorkspace, "The resolved path is outside the approved workspace.", risk,
                PolicyRefs: ["workspace:canonical-boundary"]);
            explanation = new(decision, ["Workspace canonical boundary: Deny — resolved path escapes approved root."]);
        }
        else
        {
            var grant = _grants.FindAndConsume(request, request.RequestedAt);
            explanation = _policy.Evaluate(request, risk, grant);
        }

        await _audit.AppendAsync(SecurityAuditEvent.Decided(request, explanation.Decision), cancellationToken).ConfigureAwait(false);
        return explanation.Decision;
    }

    public AuthorizationExplanation Explain(AuthorizationRequest request)
    {
        var risk = _risk.Classify(request.Action);
        if (request.Action.Resource.Kind is ResourceKind.File or ResourceKind.Directory && request.Context.WorkspaceRoot is { Length: > 0 } root && !_resources.IsWithin(request.Action.Resource, root))
        {
            var decision = new AuthorizationDecision(AuthorizationDecisionId.New(), AuthorizationOutcome.Deny, AuthorizationReasonCode.DeniedOutsideWorkspace,
                "The resolved path is outside the approved workspace.", risk, PolicyRefs: ["workspace:canonical-boundary"]);
            return new(decision, ["Workspace canonical boundary: Deny — resolved path escapes approved root."]);
        }
        return _policy.Evaluate(request, risk);
    }

    public ValueTask RecordExecutionResultAsync(AuthorizationRequest request, AuthorizationDecision decision, bool succeeded, string? resultCode = null, CancellationToken cancellationToken = default) =>
        _audit.AppendAsync(SecurityAuditEvent.Executed(request, decision, succeeded, resultCode), cancellationToken);
}

public static class AuthorizationRequestFactory
{
    public static async ValueTask<AuthorizationRequest> CreateAsync(
        IResourceCanonicalizer canonicalizer,
        SecuritySubject subject,
        string capability,
        string operation,
        ResourceKind resourceKind,
        string target,
        AuthorizationContext context,
        IReadOnlyDictionary<string, string>? parameters = null,
        bool mutation = false,
        bool external = false,
        int resourceCount = 1,
        long estimatedBytes = 0,
        SandboxLevel minimumSandbox = SandboxLevel.None,
        CancellationToken cancellationToken = default)
    {
        var resource = await canonicalizer.CanonicalizeAsync(resourceKind, target, cancellationToken).ConfigureAwait(false);
        var action = new ProposedAction(ActionId.New(), subject, capability, operation, resource, parameters, mutation, external,
            ResourceCount: resourceCount, EstimatedBytes: estimatedBytes, MinimumSandbox: minimumSandbox);
        return new AuthorizationRequest(subject, action, context, DateTimeOffset.UtcNow);
    }
}

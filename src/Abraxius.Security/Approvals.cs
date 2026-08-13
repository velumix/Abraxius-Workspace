using System.Collections.Concurrent;
using System.Collections.Immutable;
using Abraxius.Presence;

namespace Abraxius.Security;

public sealed record PendingSecurityApproval(
    NeedsYouId NeedsYouId,
    AuthorizationRequest Request,
    AuthorizationDecision Decision,
    DateTimeOffset CreatedAt,
    string ConsequenceSummary);

public interface ISecurityApprovalService
{
    IReadOnlyList<PendingSecurityApproval> Pending { get; }
    ValueTask<PendingSecurityApproval> RequestAsync(AuthorizationRequest request, AuthorizationDecision decision, AttentionContext attention, CancellationToken cancellationToken = default);
    ValueTask<AuthorizationGrant?> ApproveAsync(NeedsYouId id, GrantScope scope, TimeSpan? duration = null, CancellationToken cancellationToken = default);
    ValueTask<bool> RejectAsync(NeedsYouId id, string? reason = null, CancellationToken cancellationToken = default);
}

public interface ISecurityApprovalSink
{
    ValueTask RequestAsync(AuthorizationRequest request, AuthorizationDecision decision, CancellationToken cancellationToken = default);
}

public sealed class ConfigurableSecurityApprovalSink : ISecurityApprovalSink
{
    private ISecurityApprovalSink _inner = new UnavailableSecurityApprovalSink();
    public void Configure(ISecurityApprovalSink sink) => Interlocked.Exchange(ref _inner, sink ?? throw new ArgumentNullException(nameof(sink)));
    public ValueTask RequestAsync(AuthorizationRequest request, AuthorizationDecision decision, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _inner).RequestAsync(request, decision, cancellationToken);
    private sealed class UnavailableSecurityApprovalSink : ISecurityApprovalSink
    {
        public ValueTask RequestAsync(AuthorizationRequest request, AuthorizationDecision decision, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    }
}

public sealed class SecurityApprovalService(INeedsYouService needsYou, IAuthorizationGrantStore grants, ISecurityAuditStore audit) : ISecurityApprovalService
{
    private readonly ConcurrentDictionary<NeedsYouId, PendingSecurityApproval> _pending = new();
    public IReadOnlyList<PendingSecurityApproval> Pending => _pending.Values.OrderBy(static item => item.CreatedAt).ToArray();

    public async ValueTask<PendingSecurityApproval> RequestAsync(AuthorizationRequest request, AuthorizationDecision decision, AttentionContext attention, CancellationToken cancellationToken = default)
    {
        if (decision.Outcome != AuthorizationOutcome.RequireApproval) throw new ArgumentException("Only approval decisions can create attention requests.", nameof(decision));
        var id = NeedsYouId.New();
        var consequence = Describe(request, decision);
        var item = new NeedsYouItem(id, request.Subject.MissionId, request.Subject.AssignmentId, "security", NeedsYouReason.SecurityDecision,
            NotificationSeverity.AttentionRequired, new NotificationActionId("security.review"), consequence, request.Action.SafeEvidenceRefs,
            DateTimeOffset.UtcNow, State: NeedsYouState.Pending, SourceEventId: decision.DecisionId.ToString());
        var pending = new PendingSecurityApproval(id, request, decision, item.Created, consequence);
        _pending[id] = pending;
        await needsYou.CreateAsync(item, attention, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(new SecurityAuditEvent(SecurityAuditEventId.New(), SecurityAuditEventType.ApprovalRequested, DateTimeOffset.UtcNow,
            request.Subject.PrincipalId, request.Action.Operation, request.Action.Resource.CanonicalUri, request.Subject.MissionId?.ToString(),
            request.Subject.AssignmentId?.ToString(), request.Subject.AgentInstanceId?.ToString(), request.Subject.SkillExecutionId?.ToString(), decision.DecisionId.ToString()), cancellationToken).ConfigureAwait(false);
        return pending;
    }

    public async ValueTask<AuthorizationGrant?> ApproveAsync(NeedsYouId id, GrantScope scope, TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        if (!_pending.TryRemove(id, out var pending)) return null;
        var now = DateTimeOffset.UtcNow;
        var expiry = scope switch
        {
            GrantScope.Once => now.AddMinutes(15),
            GrantScope.Mission => now.AddHours(24),
            GrantScope.Project => now.Add(duration ?? TimeSpan.FromHours(8)),
            GrantScope.Timed => now.Add(duration ?? TimeSpan.FromHours(1)),
            _ => now.AddMinutes(15)
        };
        var grant = grants.Issue(new AuthorizationGrant(AuthorizationGrantId.New(), pending.Request.Subject,
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, pending.Request.Action.Operation), pending.Request.Action.Resource.CanonicalUri,
            scope, now, expiry, "local-user", pending.Decision.HumanExplanation,
            scope == GrantScope.Mission ? pending.Request.Subject.MissionId : null,
            scope == GrantScope.Project ? pending.Request.Context.ProjectId : null,
            scope == GrantScope.Once ? 1 : null));
        await needsYou.ResolveAsync(id, NeedsYouResolution.Approved, $"Approved with {scope} scope.", cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(new SecurityAuditEvent(SecurityAuditEventId.New(), SecurityAuditEventType.ApprovalGranted, now,
            pending.Request.Subject.PrincipalId, pending.Request.Action.Operation, pending.Request.Action.Resource.CanonicalUri,
            pending.Request.Subject.MissionId?.ToString(), pending.Request.Subject.AssignmentId?.ToString(), pending.Request.Subject.AgentInstanceId?.ToString(),
            pending.Request.Subject.SkillExecutionId?.ToString(), pending.Decision.DecisionId.ToString(), grant.GrantId.ToString(), pending.Decision.ReasonCode.ToString()), cancellationToken).ConfigureAwait(false);
        return grant;
    }

    public async ValueTask<bool> RejectAsync(NeedsYouId id, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (!_pending.TryRemove(id, out var pending)) return false;
        await needsYou.ResolveAsync(id, NeedsYouResolution.Rejected, reason ?? "Rejected by user.", cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(new SecurityAuditEvent(SecurityAuditEventId.New(), SecurityAuditEventType.ApprovalRejected, DateTimeOffset.UtcNow,
            pending.Request.Subject.PrincipalId, pending.Request.Action.Operation, pending.Request.Action.Resource.CanonicalUri,
            pending.Request.Subject.MissionId?.ToString(), pending.Request.Subject.AssignmentId?.ToString(), pending.Request.Subject.AgentInstanceId?.ToString(),
            pending.Request.Subject.SkillExecutionId?.ToString(), pending.Decision.DecisionId.ToString(), ReasonCode: pending.Decision.ReasonCode.ToString()), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string Describe(AuthorizationRequest request, AuthorizationDecision decision)
    {
        var subject = request.Subject.SpecialistRole?.ToString() ?? request.Subject.PrincipalId.Value;
        return $"{subject} requests {request.Action.Operation} on {request.Action.Resource.CanonicalUri}. Risk: {decision.Risk}. {decision.HumanExplanation}";
    }
}

public sealed class SecurityRuntime : IAsyncDisposable
{
    private readonly ISecurityAuditStore _audit;
    private readonly ISecretStore _secretStore;
    public SecurityRuntime(ISecurityKernel kernel, IPolicyEngine policies, IAuthorizationGrantStore grants, ISecurityAuditStore audit,
        ISecretBroker secrets, ISecretStore secretStore, ISecurityApprovalService approvals, ISandboxService sandboxes, IModelEgressPolicy egress)
    {
        Kernel = kernel; Policies = policies; Grants = grants; _audit = audit; Secrets = secrets; _secretStore = secretStore;
        Approvals = approvals; Sandboxes = sandboxes; Egress = egress;
    }
    public ISecurityKernel Kernel { get; }
    public IPolicyEngine Policies { get; }
    public IAuthorizationGrantStore Grants { get; }
    public ISecurityAuditStore Audit => _audit;
    public ISecretBroker Secrets { get; }
    public ISecurityApprovalService Approvals { get; }
    public ISandboxService Sandboxes { get; }
    public IModelEgressPolicy Egress { get; }
    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default) => await _audit.InitializeAsync(cancellationToken).ConfigureAwait(false);
    public async ValueTask<SecurityStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var secrets = await Secrets.ListAsync(cancellationToken).ConfigureAwait(false);
        var denials = 0; await foreach (var item in _audit.QueryAsync(100, cancellationToken).ConfigureAwait(false)) if (item.Type == SecurityAuditEventType.AuthorizationDenied) denials++;
        return new SecurityStatus(Policies.Preset, Kernel.Lockdown, Grants.ListActive(DateTimeOffset.UtcNow).Count, Approvals.Pending.Count, secrets.Count, denials,
            new Dictionary<SandboxLevel, bool> { [SandboxLevel.None] = true, [SandboxLevel.RestrictedProcess] = Sandboxes.Capabilities.RestrictedProcess,
                [SandboxLevel.IsolatedWorkspace] = Sandboxes.Capabilities.IsolatedWorkspace, [SandboxLevel.Container] = Sandboxes.Capabilities.Container,
                [SandboxLevel.RemoteSandbox] = Sandboxes.Capabilities.RemoteSandbox }.ToImmutableDictionary());
    }
    public async ValueTask DisposeAsync()
    {
        await _audit.DisposeAsync().ConfigureAwait(false);
        if (_secretStore is IDisposable disposable) disposable.Dispose();
        if (_secretStore is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
    }
}

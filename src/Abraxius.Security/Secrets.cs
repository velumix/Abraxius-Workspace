using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Abraxius.Security;

public sealed record SecretMetadata(
    SecretReference Reference,
    string DisplayName,
    string Provider,
    ImmutableArray<string> AllowedDestinations,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? LastUsedAt = null,
    bool RequiresApproval = true);

public sealed record SecretUseRequest(
    SecuritySubject Subject,
    SecretReference Reference,
    string Destination,
    string Operation,
    AuthorizationContext Context);

public interface ISecretStore
{
    ValueTask<IReadOnlyList<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask<SecretMetadata?> GetMetadataAsync(SecretReference reference, CancellationToken cancellationToken = default);
    ValueTask StoreAsync(SecretMetadata metadata, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default);
    ValueTask<bool> RemoveAsync(SecretReference reference, CancellationToken cancellationToken = default);
    ValueTask<T> UseAsync<T>(SecretReference reference, Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> consumer, CancellationToken cancellationToken = default);
}

public interface ISecretBroker
{
    ValueTask<IReadOnlyList<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default);
    ValueTask<T> UseAsync<T>(SecretUseRequest request, Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> approvedTransport, CancellationToken cancellationToken = default);
}

public sealed class InMemorySecretStore : ISecretStore, IDisposable
{
    private sealed record Entry(SecretMetadata Metadata, char[] Value);
    private readonly ConcurrentDictionary<SecretReference, Entry> _entries = new();
    public ValueTask<IReadOnlyList<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<IReadOnlyList<SecretMetadata>>(_entries.Values.Select(static entry => entry.Metadata).OrderBy(static item => item.DisplayName).ToArray()); }
    public ValueTask<SecretMetadata?> GetMetadataAsync(SecretReference reference, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_entries.TryGetValue(reference, out var entry) ? entry.Metadata : null); }
    public ValueTask StoreAsync(SecretMetadata metadata, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copy = value.ToArray();
        if (_entries.TryGetValue(metadata.Reference, out var prior)) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(prior.Value.AsSpan()));
        _entries[metadata.Reference] = new(metadata, copy); return ValueTask.CompletedTask;
    }
    public ValueTask<bool> RemoveAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var removed = _entries.TryRemove(reference, out var entry); if (entry is not null) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(entry.Value.AsSpan())); return ValueTask.FromResult(removed);
    }
    public async ValueTask<T> UseAsync<T>(SecretReference reference, Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> consumer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!_entries.TryGetValue(reference, out var entry)) throw new KeyNotFoundException($"Secret reference '{reference}' is not configured.");
        return await consumer(entry.Value.AsMemory(), cancellationToken).ConfigureAwait(false);
    }
    public void Dispose()
    {
        foreach (var entry in _entries.Values) CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(entry.Value.AsSpan()));
        _entries.Clear();
    }
}

/// <summary>Explicit development adapter. Environment values are consumed only inside a callback and never persisted.</summary>
public sealed class EnvironmentSecretStore(IReadOnlyDictionary<SecretReference, (string Variable, SecretMetadata Metadata)> mappings) : ISecretStore
{
    public ValueTask<IReadOnlyList<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<IReadOnlyList<SecretMetadata>>(mappings.Values.Select(static value => value.Metadata).ToArray()); }
    public ValueTask<SecretMetadata?> GetMetadataAsync(SecretReference reference, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(mappings.TryGetValue(reference, out var value) ? value.Metadata : null); }
    public ValueTask StoreAsync(SecretMetadata metadata, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default) => throw new NotSupportedException("Environment secret adapter is read-only.");
    public ValueTask<bool> RemoveAsync(SecretReference reference, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);
    public async ValueTask<T> UseAsync<T>(SecretReference reference, Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> consumer, CancellationToken cancellationToken = default)
    {
        if (!mappings.TryGetValue(reference, out var mapping)) throw new KeyNotFoundException("Secret reference is not mapped.");
        var value = Environment.GetEnvironmentVariable(mapping.Variable) ?? throw new KeyNotFoundException("Mapped environment secret is unavailable.");
        var buffer = value.ToCharArray();
        try { return await consumer(buffer.AsMemory(), cancellationToken).ConfigureAwait(false); }
        finally { CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(buffer.AsSpan())); }
    }
}

/// <summary>
/// Presents one logical broker store while keeping writable runtime secrets separate from
/// read-only platform/development adapters. Values are still consumed only through callbacks.
/// </summary>
public sealed class CompositeSecretStore(ISecretStore primary, params ISecretStore[] fallbacks) : ISecretStore, IAsyncDisposable
{
    public async ValueTask<IReadOnlyList<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default)
    {
        var byReference = new Dictionary<SecretReference, SecretMetadata>();
        foreach (var store in Enumerate())
        {
            foreach (var item in await store.ListAsync(cancellationToken).ConfigureAwait(false)) byReference.TryAdd(item.Reference, item);
        }
        return byReference.Values.OrderBy(static item => item.DisplayName).ToArray();
    }

    public async ValueTask<SecretMetadata?> GetMetadataAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        foreach (var store in Enumerate())
        {
            var metadata = await store.GetMetadataAsync(reference, cancellationToken).ConfigureAwait(false);
            if (metadata is not null) return metadata;
        }
        return null;
    }

    public ValueTask StoreAsync(SecretMetadata metadata, ReadOnlyMemory<char> value, CancellationToken cancellationToken = default) =>
        primary.StoreAsync(metadata, value, cancellationToken);

    public async ValueTask<bool> RemoveAsync(SecretReference reference, CancellationToken cancellationToken = default)
    {
        if (await primary.RemoveAsync(reference, cancellationToken).ConfigureAwait(false)) return true;
        foreach (var store in fallbacks)
        {
            if (await store.RemoveAsync(reference, cancellationToken).ConfigureAwait(false)) return true;
        }
        return false;
    }

    public async ValueTask<T> UseAsync<T>(SecretReference reference, Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> consumer, CancellationToken cancellationToken = default)
    {
        foreach (var store in Enumerate())
        {
            if (await store.GetMetadataAsync(reference, cancellationToken).ConfigureAwait(false) is not null)
                return await store.UseAsync(reference, consumer, cancellationToken).ConfigureAwait(false);
        }
        throw new KeyNotFoundException($"Secret reference '{reference}' is not configured.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in Enumerate())
        {
            if (store is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (store is IDisposable disposable) disposable.Dispose();
        }
    }

    private IEnumerable<ISecretStore> Enumerate()
    {
        yield return primary;
        foreach (var fallback in fallbacks) yield return fallback;
    }
}

public sealed class SecretBroker(ISecretStore store, ISecurityKernel security, ISecurityAuditStore audit, IResourceCanonicalizer resources) : ISecretBroker
{
    public ValueTask<IReadOnlyList<SecretMetadata>> ListAsync(CancellationToken cancellationToken = default) => store.ListAsync(cancellationToken);
    public async ValueTask<T> UseAsync<T>(SecretUseRequest request, Func<ReadOnlyMemory<char>, CancellationToken, ValueTask<T>> approvedTransport, CancellationToken cancellationToken = default)
    {
        var metadata = await store.GetMetadataAsync(request.Reference, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Secret is not configured.");
        if (metadata.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow) throw new UnauthorizedAccessException("Secret reference has expired.");
        if (metadata.AllowedDestinations.Length > 0 && !metadata.AllowedDestinations.Any(scope => request.Destination.StartsWith(scope, StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Secret destination is outside its configured scope.");
        var resource = await resources.CanonicalizeAsync(ResourceKind.Secret, request.Reference.Value, cancellationToken).ConfigureAwait(false);
        var action = new ProposedAction(ActionId.New(), request.Subject, "secret", SecurityActions.SecretUse, resource,
            new Dictionary<string, string> { ["destination"] = request.Destination, ["operation"] = request.Operation }, ExternalEffect: true);
        var authorization = new AuthorizationRequest(request.Subject, action, request.Context, DateTimeOffset.UtcNow);
        var decision = await security.AuthorizeAsync(authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.IsAllowed) throw new UnauthorizedAccessException(decision.HumanExplanation);

        var result = await store.UseAsync(request.Reference, approvedTransport, cancellationToken).ConfigureAwait(false);
        await audit.AppendAsync(new SecurityAuditEvent(SecurityAuditEventId.New(), SecurityAuditEventType.SecretUsed, DateTimeOffset.UtcNow,
            request.Subject.PrincipalId, SecurityActions.SecretUse, request.Reference.Value, request.Subject.MissionId?.ToString(),
            request.Subject.AssignmentId?.ToString(), request.Subject.AgentInstanceId?.ToString(), request.Subject.SkillExecutionId?.ToString(),
            decision.DecisionId.ToString(), decision.Grant?.GrantId.ToString(), decision.ReasonCode.ToString()), cancellationToken).ConfigureAwait(false);
        return result;
    }
}

public interface ISecretRedactor
{
    string Redact(string value);
    void RegisterSensitiveValue(ReadOnlySpan<char> value);
}

public sealed class SecretRedactor : ISecretRedactor
{
    private ImmutableArray<string> _values = ImmutableArray<string>.Empty;
    public void RegisterSensitiveValue(ReadOnlySpan<char> value)
    {
        if (value.Length < 4) return;
        ImmutableInterlocked.Update(ref _values, (items, secret) => items.Contains(secret, StringComparer.Ordinal) ? items : items.Add(secret), value.ToString());
    }
    public string Redact(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var result = value;
        foreach (var secret in _values) result = result.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        result = System.Text.RegularExpressions.Regex.Replace(result, "(?i)(authorization\\s*[:=]\\s*(?:bearer\\s+)?)[^\\s,;]+", "$1[REDACTED]");
        result = System.Text.RegularExpressions.Regex.Replace(result, "(?i)((?:api[_-]?key|token|secret|password)\\s*[:=]\\s*)[^\\s,;]+", "$1[REDACTED]");
        return result;
    }
}

public interface IModelEgressPolicy
{
    AuthorizationDecision Evaluate(SecuritySubject subject, DataClassification classification, bool providerIsLocal, string providerId);
}

public sealed class ModelEgressPolicy : IModelEgressPolicy
{
    public AuthorizationDecision Evaluate(SecuritySubject subject, DataClassification classification, bool providerIsLocal, string providerId)
    {
        if (!providerIsLocal && classification is DataClassification.LocalOnly or DataClassification.Secret)
            return new(AuthorizationDecisionId.New(), AuthorizationOutcome.Deny, AuthorizationReasonCode.DeniedLocalOnlyPolicy,
                $"{classification} context cannot be sent to provider '{providerId}'.", RiskClass.ExternalSideEffect, PolicyRefs: ["egress:classification"]);
        return new(AuthorizationDecisionId.New(), AuthorizationOutcome.Allow, AuthorizationReasonCode.AllowedByPolicy,
            providerIsLocal ? "Local provider satisfies egress policy." : "Provider is allowed for this data classification.", RiskClass.ReadOnly, PolicyRefs: ["egress:provider"]);
    }
}

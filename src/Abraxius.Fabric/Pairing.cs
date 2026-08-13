namespace Abraxius.Fabric;

public sealed record PairingInvitation(PairingInvitationId Id, FabricId FabricId, byte[] TokenHash, DateTimeOffset ExpiresAt, bool Used = false);
public sealed record PairingResult(bool Paired, string Reason, FabricNodeDescriptor? Node = null, X509Certificate2? Credential = null);

public interface IFabricNodeRegistry
{
    FabricId FabricId { get; }
    FabricEpoch Epoch { get; }
    IReadOnlyCollection<FabricNodeDescriptor> Nodes { get; }
    bool TryGet(FabricNodeId id, out FabricNodeDescriptor node);
    void Upsert(FabricNodeDescriptor node);
    void Revoke(FabricNodeId id);
    FabricEpoch AdvanceEpoch();
}

public sealed class InMemoryFabricNodeRegistry(FabricId fabricId, FabricEpoch epoch = default) : IFabricNodeRegistry
{
    private readonly ConcurrentDictionary<FabricNodeId, FabricNodeDescriptor> _nodes = new(); private long _epoch = checked((long)epoch.Value);
    public FabricId FabricId { get; } = fabricId; public FabricEpoch Epoch => new(checked((ulong)Volatile.Read(ref _epoch))); public IReadOnlyCollection<FabricNodeDescriptor> Nodes => _nodes.Values.ToArray();
    public bool TryGet(FabricNodeId id, out FabricNodeDescriptor node) => _nodes.TryGetValue(id, out node!);
    public void Upsert(FabricNodeDescriptor node) => _nodes.AddOrUpdate(node.Id, node, (_, current) => current.TrustState is NodeTrustState.Revoked or NodeTrustState.Quarantined ? current : node);
    public void Revoke(FabricNodeId id) => _nodes.AddOrUpdate(id, _ => throw new KeyNotFoundException($"Node {id} is unknown."), (_, node) => node with { TrustState = NodeTrustState.Revoked, Health = FabricNodeHealth.Quarantined, AcceptingLeases = false, Connectivity = FabricConnectivity.Disconnected });
    public FabricEpoch AdvanceEpoch() => new(checked((ulong)Interlocked.Increment(ref _epoch)));
}

public interface INodeCredentialStore
{
    ValueTask StoreAsync(FabricNodeId nodeId, X509Certificate2 certificate, CancellationToken cancellationToken = default);
    ValueTask<X509Certificate2?> GetAsync(FabricNodeId nodeId, CancellationToken cancellationToken = default);
    ValueTask RemoveAsync(FabricNodeId nodeId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryNodeCredentialStore : INodeCredentialStore, IDisposable
{
    private readonly ConcurrentDictionary<FabricNodeId, X509Certificate2> _items = new();
    public ValueTask StoreAsync(FabricNodeId nodeId, X509Certificate2 certificate, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); var owned = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable); _items.AddOrUpdate(nodeId, owned, (_, old) => { old.Dispose(); return owned; }); return ValueTask.CompletedTask; }
    public ValueTask<X509Certificate2?> GetAsync(FabricNodeId nodeId, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(_items.TryGetValue(nodeId, out var value) ? X509CertificateLoader.LoadPkcs12(value.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.Exportable) : null); }
    public ValueTask RemoveAsync(FabricNodeId nodeId, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); if (_items.TryRemove(nodeId, out var value)) value.Dispose(); return ValueTask.CompletedTask; }
    public void Dispose() { foreach (var item in _items.Values) item.Dispose(); _items.Clear(); }
}

public sealed class FabricPairingService : IDisposable
{
    private readonly IFabricNodeRegistry _registry; private readonly INodeCredentialStore _credentials; private readonly ConcurrentDictionary<PairingInvitationId, PairingInvitation> _invitations = new(); private readonly X509Certificate2 _issuer;
    public FabricPairingService(IFabricNodeRegistry registry, INodeCredentialStore credentials)
    {
        _registry = registry; _credentials = credentials; using var key = RSA.Create(3072); var request = new CertificateRequest($"CN=Abraxius Fabric {registry.FabricId}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true)); request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature, true)); _issuer = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(5));
    }
    public (PairingInvitation Invitation, string OneTimeCode) CreateInvitation(TimeSpan lifetime)
    {
        var token = RandomNumberGenerator.GetBytes(32); var code = Convert.ToHexString(token).ToLowerInvariant(); var invitation = new PairingInvitation(PairingInvitationId.New(), _registry.FabricId, SHA256.HashData(token), DateTimeOffset.UtcNow + lifetime); _invitations[invitation.Id] = invitation; return (invitation, code);
    }
    public async ValueTask<PairingResult> PairAsync(PairingInvitationId invitationId, string oneTimeCode, FabricNodeDescriptor claimedNode, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_invitations.TryGetValue(invitationId, out var invitation) || invitation.Used || invitation.ExpiresAt <= DateTimeOffset.UtcNow) return new(false, "Pairing invitation is missing, used, or expired.");
        byte[] token; try { token = Convert.FromHexString(oneTimeCode); } catch (FormatException) { return new(false, "Pairing code is malformed."); }
        if (!CryptographicOperations.FixedTimeEquals(invitation.TokenHash, SHA256.HashData(token))) return new(false, "Pairing code does not match.");
        if (!_invitations.TryUpdate(invitationId, invitation with { Used = true }, invitation)) return new(false, "Pairing invitation was consumed concurrently.");
        using var key = RSA.Create(3072); var request = new CertificateRequest($"CN={claimedNode.Id}, O={_registry.FabricId}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1); request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true)); request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true)); request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2"), new("1.3.6.1.5.5.7.3.1") }, true));
        var serial = RandomNumberGenerator.GetBytes(16); using var issued = request.Create(_issuer, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(1), serial); var credential = issued.CopyWithPrivateKey(key); var fingerprint = new NodeFingerprint(Convert.ToHexString(SHA256.HashData(credential.RawData)).ToLowerInvariant()); var trusted = claimedNode with { Fingerprint = fingerprint, TrustState = NodeTrustState.Trusted };
        _registry.Upsert(trusted); await _credentials.StoreAsync(trusted.Id, credential, cancellationToken).ConfigureAwait(false); return new(true, "Node paired.", trusted, credential);
    }
    public async ValueTask UnpairAsync(FabricNodeId nodeId, CancellationToken cancellationToken = default) { _registry.Revoke(nodeId); await _credentials.RemoveAsync(nodeId, cancellationToken).ConfigureAwait(false); }
    public bool Validate(FabricNodeId nodeId, FabricId fabricId, NodeFingerprint fingerprint) => fabricId == _registry.FabricId && _registry.TryGet(nodeId, out var node) && node.TrustState == NodeTrustState.Trusted && CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(node.Fingerprint.Value), System.Text.Encoding.UTF8.GetBytes(fingerprint.Value));
    public void Dispose() { _issuer.Dispose(); GC.SuppressFinalize(this); }
}

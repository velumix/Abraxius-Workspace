using Abraxius.Fabric.Protocol;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

namespace Abraxius.Fabric;

public sealed class GrpcFabricTransport : IFabricTransport
{
    private readonly FabricId _fabricId; private readonly FabricSessionId _sessionId; private readonly FabricNodeId _localNode; private readonly NodeFingerprint _fingerprint; private readonly GrpcChannel _channel; private readonly FabricControl.FabricControlClient _client;
    public GrpcFabricTransport(FabricId fabricId, FabricSessionId sessionId, FabricNodeId localNode, NodeFingerprint fingerprint, Uri endpoint, X509Certificate2 clientCertificate, NodeFingerprint? pinnedServer = null)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Fabric gRPC endpoints require TLS.", nameof(endpoint));
        _fabricId = fabricId; _sessionId = sessionId; _localNode = localNode; _fingerprint = fingerprint;
        var handler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true, PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10) }; handler.SslOptions.ClientCertificates = new X509CertificateCollection { clientCertificate };
        if (pinnedServer is { } pin) handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, chain, errors) => errors == System.Net.Security.SslPolicyErrors.None && certificate is not null && Fingerprint(certificate).Value.Equals(pin.Value, StringComparison.OrdinalIgnoreCase);
        _channel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = handler }); _client = new(_channel);
    }
    public FabricTransportKind Kind => FabricTransportKind.GrpcHttp2; public bool IsSupported => true;
    public async ValueTask<ProtocolNegotiationResult> NegotiateAsync(FabricNodeId nodeId, FabricProtocolVersion local, CancellationToken cancellationToken = default)
    {
        var request = new ProtocolHello { FabricId = _fabricId.ToString(), NodeId = _localNode.ToString(), MinimumVersion = local.Minimum, MaximumVersion = local.Maximum, CertificateFingerprint = _fingerprint.Value }; request.Features.AddRange(local.Features);
        var response = await _client.NegotiateAsync(request, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false); return new(response.Accepted, response.SelectedVersion, response.Features.ToImmutableHashSet(StringComparer.Ordinal), response.Reason);
    }
    public async ValueTask<RemoteExecutionResult> OfferLeaseAsync(FabricNodeId nodeId, ExecutionLease lease, CancellationToken cancellationToken = default)
    {
        var request = GrpcFabricMapper.ToProto(_fabricId, _sessionId, lease); var response = await _client.ExecuteAsync(request, cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false); return GrpcFabricMapper.FromProto(response);
    }
    public async ValueTask SendControlAsync(FabricNodeId nodeId, FabricControlMessage message, CancellationToken cancellationToken = default)
    {
        using var call = _client.Control(cancellationToken: cancellationToken); await call.RequestStream.WriteAsync(new ControlEnvelope { NodeId = message.NodeId.ToString(), NodeSequence = message.NodeSequence, Priority = message.Priority.ToString(), Kind = message.Kind, Payload = ByteString.CopyFrom(message.Payload.Span), TraceParent = message.TraceParent ?? "" }, cancellationToken).ConfigureAwait(false); await call.RequestStream.CompleteAsync().ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync() { await _channel.ShutdownAsync().ConfigureAwait(false); GC.SuppressFinalize(this); }
    public static NodeFingerprint Fingerprint(X509Certificate certificate) => new(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant());
}

public static class GrpcFabricMapper
{
    public static LeaseOffer ToProto(FabricId fabric, FabricSessionId session, ExecutionLease lease)
    {
        var subject = lease.Authorization.Subject;
        var value = new LeaseOffer { FabricId = fabric.ToString(), SessionId = session.ToString(), LeaseId = lease.Id.ToString(), ExecutionId = lease.ExecutionId.ToString(), TaskId = lease.ExecutionNodeId.ToString(), MissionId = lease.MissionId?.ToString() ?? "", CoordinatorEpoch = lease.CoordinatorEpoch.Value, WorkerNodeId = lease.WorkerNodeId.ToString(), Attempt = checked((uint)lease.Attempt), IdempotencyKey = lease.IdempotencyKey, Capability = lease.Capability, WorkKind = lease.WorkKind.ToString(), Operation = lease.Operation, TtlMilliseconds = checked((long)lease.Ttl.TotalMilliseconds), AuthorizationDecisionId = lease.Authorization.DecisionId.ToString(), GrantId = lease.Authorization.GrantId?.ToString() ?? "", Classification = lease.Authorization.Classification.ToString(), SideEffecting = lease.SideEffecting, TraceParent = lease.TraceParent ?? "", PrincipalId = subject.PrincipalId.Value, PrincipalType = subject.PrincipalType.ToString(), SubjectMissionId = subject.MissionId?.ToString() ?? "", AssignmentId = subject.AssignmentId?.ToString() ?? "", AgentInstanceId = subject.AgentInstanceId?.ToString() ?? "", AuthorizationExpiresUnixMs = lease.Authorization.ExpiresAt.ToUnixTimeMilliseconds(), CpuWeight = lease.Reservation.CpuWeight, MemoryBytes = lease.Reservation.MemoryBytes, VramBytes = lease.Reservation.VramBytes, SpecialistRole = subject.SpecialistRole?.ToString() ?? "" }; value.Parameters.Add(lease.Parameters); value.ResourcePrefixes.AddRange(lease.Authorization.ResourcePrefixes); return value;
    }
    public static ExecutionLease FromProto(LeaseOffer value)
    {
        var classification = Enum.TryParse<DataClassification>(value.Classification, true, out var parsed) ? parsed : DataClassification.Internal; var capability = value.Capability;
        var principalType = Enum.TryParse<PrincipalType>(value.PrincipalType, true, out var type) ? type : PrincipalType.System;
        MissionId? mission = Guid.TryParse(value.SubjectMissionId, out var missionId) ? new(missionId) : null; AssignmentId? assignment = Guid.TryParse(value.AssignmentId, out var assignmentId) ? new(assignmentId) : null; SpecialistInstanceId? agent = Guid.TryParse(value.AgentInstanceId, out var agentId) ? new(agentId) : null; SpecialistRole? role = Enum.TryParse<SpecialistRole>(value.SpecialistRole, true, out var parsedRole) ? parsedRole : null;
        var subject = new SecuritySubject(new PrincipalId(string.IsNullOrWhiteSpace(value.PrincipalId) ? "system:fabric-coordinator" : value.PrincipalId), principalType, MissionId: mission, AssignmentId: assignment, AgentInstanceId: agent, SpecialistRole: role);
        var expiry = value.AuthorizationExpiresUnixMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(value.AuthorizationExpiresUnixMs) : DateTimeOffset.UtcNow;
        var authorization = new RemoteAuthorizationContext(new(Guid.Parse(value.AuthorizationDecisionId)), string.IsNullOrWhiteSpace(value.GrantId) ? null : new(Guid.Parse(value.GrantId)), mission, subject, ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, capability), value.ResourcePrefixes.ToImmutableArray(), null, expiry, classification);
        return new(new(Guid.Parse(value.LeaseId)), new(Guid.Parse(value.ExecutionId)), new(Guid.Parse(value.TaskId)), mission, new(value.CoordinatorEpoch), new(Guid.Parse(value.WorkerNodeId)), checked((int)value.Attempt), value.IdempotencyKey, capability, Enum.Parse<WorkKind>(value.WorkKind, true), value.Operation, value.Parameters.ToImmutableDictionary(StringComparer.Ordinal), authorization, TimeSpan.FromMilliseconds(value.TtlMilliseconds), new(Math.Max(1, value.CpuWeight), Math.Max(0, value.MemoryBytes), Math.Max(0, value.VramBytes)), DateTimeOffset.UtcNow, value.SideEffecting, string.IsNullOrEmpty(value.TraceParent) ? null : value.TraceParent);
    }
    public static LeaseResult ToProto(RemoteExecutionResult result) => new() { LeaseId = result.LeaseId.ToString(), ExecutionId = result.ExecutionId.ToString(), Attempt = checked((uint)result.Attempt), WorkerNodeId = result.WorkerNodeId.ToString(), Status = result.Status.ToString(), Summary = result.Result?.Summary ?? "", ResultHash = result.ResultHash, ValueJson = result.Result?.Value?.GetRawText() ?? "", CoordinatorEpoch = result.CoordinatorEpoch.Value };
    public static RemoteExecutionResult FromProto(LeaseResult value)
    {
        JsonElement? json = null; if (!string.IsNullOrWhiteSpace(value.ValueJson)) { using var document = JsonDocument.Parse(value.ValueJson); json = document.RootElement.Clone(); }
        var work = new WorkResult(ResultId.New(), value.Summary, [], json); return new(new(Guid.Parse(value.ExecutionId)), new(Guid.Parse(value.LeaseId)), checked((int)value.Attempt), new(value.CoordinatorEpoch), new(Guid.Parse(value.WorkerNodeId)), Enum.Parse<LeaseExecutionStatus>(value.Status, true), work, [], value.ResultHash, ImmutableDictionary<string, double>.Empty, DateTimeOffset.UtcNow);
    }
}

public sealed class FabricGrpcService(FabricId fabricId, FabricEpoch epoch, FabricWorker worker, GrpcFabricTransferStore? transfers = null) : FabricControl.FabricControlBase
{
    public override Task<ProtocolWelcome> Negotiate(ProtocolHello request, ServerCallContext context)
    {
        if (!request.FabricId.Equals(fabricId.ToString(), StringComparison.Ordinal)) return Task.FromResult(new ProtocolWelcome { Accepted = false, Reason = "Wrong Fabric identity." });
        var result = worker.Negotiate(new(request.MinimumVersion, request.MaximumVersion, request.MaximumVersion, request.Features.ToImmutableHashSet(StringComparer.Ordinal))); var response = new ProtocolWelcome { Accepted = result.Compatible, SelectedVersion = result.SelectedVersion, Reason = result.Reason, CoordinatorEpoch = epoch.Value }; response.Features.AddRange(result.Features); return Task.FromResult(response);
    }
    public override async Task<LeaseResult> Execute(LeaseOffer request, ServerCallContext context)
    {
        if (!request.FabricId.Equals(fabricId.ToString(), StringComparison.Ordinal)) throw new RpcException(new Status(StatusCode.PermissionDenied, "Wrong Fabric identity.")); var result = await worker.OfferAsync(GrpcFabricMapper.FromProto(request), context.CancellationToken).ConfigureAwait(false); return GrpcFabricMapper.ToProto(result);
    }
    public override async Task Control(IAsyncStreamReader<ControlEnvelope> requestStream, IServerStreamWriter<ControlEnvelope> responseStream, ServerCallContext context)
    {
        while (await requestStream.MoveNext(context.CancellationToken).ConfigureAwait(false)) { var item = requestStream.Current; if (item.Kind.Equals("cancel", StringComparison.OrdinalIgnoreCase) && Guid.TryParse(item.Payload.ToStringUtf8(), out var lease)) worker.Cancel(new(lease)); }
    }
    public override async Task<TransferReceipt> Transfer(IAsyncStreamReader<BlobChunk> requestStream, ServerCallContext context)
    {
        if (transfers is null) return new() { Accepted = false, Reason = "Artifact transfer storage is not configured on this node." };
        return await transfers.ReceiveAsync(requestStream, context.CancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Bounded, resumable gRPC data-plane sink. Authorization is supplied by the authenticated service boundary.</summary>
public sealed class GrpcFabricTransferStore(string root)
{
    private readonly string _root = Path.GetFullPath(root);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public async Task<TransferReceipt> ReceiveAsync(IAsyncStreamReader<BlobChunk> stream, CancellationToken cancellationToken)
    {
        BlobChunk? first = null; FileStream? output = null; SemaphoreSlim? gate = null; string? partPath = null;
        try
        {
            while (await stream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                var chunk = stream.Current; first ??= chunk;
                if (chunk.TransferId != first.TransferId || chunk.BlobId != first.BlobId) return Reject("A transfer stream cannot switch identity.", output?.Length ?? 0);
                if (chunk.Classification.Equals(DataClassification.LocalOnly.ToString(), StringComparison.OrdinalIgnoreCase)) return Reject("LocalOnly content cannot enter a remote transfer stream.", output?.Length ?? 0);
                if (chunk.Content.Length > 1024 * 1024) return Reject("Chunk exceeds the one MiB bounded transfer limit.", output?.Length ?? 0);
                if (!Convert.ToHexString(SHA256.HashData(chunk.Content.Span)).Equals(chunk.ChunkHash, StringComparison.OrdinalIgnoreCase)) return Reject("Chunk hash mismatch.", output?.Length ?? 0);
                if (output is null)
                {
                    Directory.CreateDirectory(_root); gate = _locks.GetOrAdd(chunk.TransferId, static _ => new(1, 1)); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    partPath = Path.Combine(_root, $"{Safe(chunk.TransferId)}-{Safe(chunk.BlobId)}.part"); output = new(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                if (chunk.Offset != output.Length) return Reject("Chunk offset does not match the resumable committed length.", output.Length);
                await output.WriteAsync(chunk.Content.Memory, cancellationToken).ConfigureAwait(false); await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (!chunk.Complete) continue;
                if (output.Length != chunk.TotalLength) return Reject("Final length does not match the declared Artifact length.", output.Length);
                output.Position = 0; var hash = Convert.ToHexString(await SHA256.HashDataAsync(output, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
                if (!hash.Equals(chunk.FinalHash, StringComparison.OrdinalIgnoreCase)) return Reject("Final Artifact hash mismatch.", output.Length);
                var committed = output.Length; output.Dispose(); output = null;
                var finalPath = Path.Combine(_root, hash + ".blob"); if (!File.Exists(finalPath)) File.Move(partPath!, finalPath); else File.Delete(partPath!);
                return new() { Accepted = true, CommittedLength = committed, ContentHash = hash, Reason = "Artifact transfer committed." };
            }
            return new() { Accepted = false, CommittedLength = output?.Length ?? 0, Reason = "Transfer stream ended before BlobComplete." };
        }
        finally { output?.Dispose(); gate?.Release(); }
    }

    private static TransferReceipt Reject(string reason, long committed) => new() { Accepted = false, Reason = reason, CommittedLength = committed };
    private static string Safe(string value) => value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ? value : Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

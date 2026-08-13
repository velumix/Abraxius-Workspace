namespace Abraxius.Fabric;

public sealed record FabricTransferRequest(FabricTransferId Id, ArtifactContentDescriptor Content, FabricNodeId SourceNode, FabricNodeId TargetNode, DataClassification Classification, int ChunkBytes = 128 * 1024);
public sealed record FabricTransferProgress(FabricTransferId Id, long CommittedBytes, long TotalBytes, bool CacheHit, bool Complete, string? Error = null);
public sealed record FabricBlobChunk(FabricTransferId TransferId, ArtifactBlobId BlobId, long Offset, ReadOnlyMemory<byte> Content, string ChunkHash, string FinalHash, long TotalLength, bool Complete);

public interface IResumableBlobReceiver
{
    ValueTask<long> GetCommittedLengthAsync(FabricTransferRequest request, CancellationToken cancellationToken = default);
    ValueTask AppendAsync(FabricTransferRequest request, FabricBlobChunk chunk, CancellationToken cancellationToken = default);
    ValueTask<ArtifactContentDescriptor> CompleteAsync(FabricTransferRequest request, CancellationToken cancellationToken = default);
}

public interface IFabricArtifactTransfer
{
    ValueTask<FabricTransferProgress> TransferAsync(FabricTransferRequest request, IArtifactContentStore source, IResumableBlobReceiver receiver, IProgress<FabricTransferProgress>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class ChunkedFabricArtifactTransfer : IFabricArtifactTransfer
{
    public async ValueTask<FabricTransferProgress> TransferAsync(FabricTransferRequest request, IArtifactContentStore source, IResumableBlobReceiver receiver, IProgress<FabricTransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (request.Classification == DataClassification.LocalOnly && request.SourceNode != request.TargetNode) return new(request.Id, 0, request.Content.Length, false, false, "LocalOnly content cannot leave its physical node.");
        var offset = await receiver.GetCommittedLengthAsync(request, cancellationToken).ConfigureAwait(false); if (offset == request.Content.Length) return new(request.Id, offset, request.Content.Length, true, true);
        if (offset < 0 || offset > request.Content.Length) throw new InvalidDataException("Receiver returned an invalid resume offset.");
        await using var input = await source.OpenReadAsync(request.Content.BlobId, cancellationToken).ConfigureAwait(false); if (input.CanSeek) input.Seek(offset, SeekOrigin.Begin); else await SkipAsync(input, offset, cancellationToken).ConfigureAwait(false);
        var buffer = new byte[Math.Clamp(request.ChunkBytes, 16 * 1024, 1024 * 1024)];
        while (offset < request.Content.Length)
        {
            var wanted = (int)Math.Min(buffer.Length, request.Content.Length - offset); var read = await input.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException("Artifact source ended before its declared length.");
            var bytes = buffer.AsMemory(0, read); var chunkHash = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant(); var next = offset + read;
            await receiver.AppendAsync(request, new(request.Id, request.Content.BlobId, offset, bytes, chunkHash, request.Content.ContentHash, request.Content.Length, next == request.Content.Length), cancellationToken).ConfigureAwait(false); offset = next; progress?.Report(new(request.Id, offset, request.Content.Length, false, false));
        }
        var completed = await receiver.CompleteAsync(request, cancellationToken).ConfigureAwait(false); if (!completed.ContentHash.Equals(request.Content.ContentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Transferred artifact final hash does not match Phase 18 metadata."); var result = new FabricTransferProgress(request.Id, offset, request.Content.Length, false, true); progress?.Report(result); return result;
    }
    private static async ValueTask SkipAsync(Stream stream, long count, CancellationToken cancellationToken) { var buffer = new byte[64 * 1024]; while (count > 0) { var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), cancellationToken).ConfigureAwait(false); if (read == 0) throw new EndOfStreamException(); count -= read; } }
}

public sealed class FileResumableBlobReceiver(string root, IArtifactContentStore target) : IResumableBlobReceiver
{
    private readonly string _root = Path.GetFullPath(root); private readonly ConcurrentDictionary<FabricTransferId, SemaphoreSlim> _locks = new();
    public ValueTask<long> GetCommittedLengthAsync(FabricTransferRequest request, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); var path = PartPath(request); return ValueTask.FromResult(File.Exists(path) ? new FileInfo(path).Length : 0L); }
    public async ValueTask AppendAsync(FabricTransferRequest request, FabricBlobChunk chunk, CancellationToken cancellationToken = default)
    {
        if (!Convert.ToHexString(SHA256.HashData(chunk.Content.Span)).Equals(chunk.ChunkHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Artifact transfer chunk hash mismatch.");
        Directory.CreateDirectory(_root); var gate = _locks.GetOrAdd(request.Id, static _ => new(1, 1)); await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { var path = PartPath(request); await using var output = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); if (output.Length != chunk.Offset) throw new InvalidDataException("Artifact chunk offset does not match committed range."); output.Seek(chunk.Offset, SeekOrigin.Begin); await output.WriteAsync(chunk.Content, cancellationToken).ConfigureAwait(false); await output.FlushAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async ValueTask<ArtifactContentDescriptor> CompleteAsync(FabricTransferRequest request, CancellationToken cancellationToken = default)
    {
        var path = PartPath(request); await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan); var descriptor = await target.PutAsync(input, request.Content.MediaType, request.Content.FileName, cancellationToken).ConfigureAwait(false); if (descriptor.Length != request.Content.Length || !descriptor.ContentHash.Equals(request.Content.ContentHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Completed artifact is corrupt."); File.Delete(path); return descriptor;
    }
    private string PartPath(FabricTransferRequest request) => Path.Combine(_root, $"{request.Id}-{request.Content.BlobId.Value}.part");
}

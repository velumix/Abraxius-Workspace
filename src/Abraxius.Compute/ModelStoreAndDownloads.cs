namespace Abraxius.Compute;

public sealed class ContentAddressedModelStore : IModelStore, IDisposable
{
    private readonly string _root;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ContentAddressedModelStore(string root) { _root = Path.GetFullPath(root); Directory.CreateDirectory(_root); }
    public void Dispose() => _mutex.Dispose();

    public async ValueTask<ModelVariantDescriptor> ImportAsync(Stream content, string fileName, ModelImportMetadata metadata, CancellationToken cancellationToken = default)
    {
        if (!content.CanRead) throw new ArgumentException("Model content must be readable.", nameof(content));
        if (metadata.License.Acceptance == LicenseAcceptance.Required) throw new InvalidOperationException("License acceptance is required before import.");
        var staging = Path.Combine(_root, $".import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        var stagedFile = Path.Combine(staging, "payload");
        try
        {
            string hash;
            long length;
            await using (var output = new FileStream(stagedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1024 * 1024]; length = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hasher.AppendData(buffer, 0, read); length += read;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            }
            var format = await DetectFormatAsync(stagedFile, cancellationToken).ConfigureAwait(false);
            var id = new ModelVariantId($"{metadata.LogicalModel.Value}/{metadata.Revision.Value}/{hash[..16]}");
            var target = Path.Combine(_root, hash[..2], hash);
            var payload = Path.Combine(target, "model" + Extension(format));
            var descriptor = new ModelVariantDescriptor(id, metadata.LogicalModel, metadata.Revision, Path.GetFileNameWithoutExtension(fileName), format,
                metadata.Quantization, metadata.ParameterCount, metadata.Architecture, metadata.ContextMaximum, length,
                ImmutableDictionary<string, string>.Empty.Add("sha256", hash), [], [], metadata.Backends, metadata.License, metadata.Source,
                ModelValidationState.Unvalidated, ModelStorageKind.AbraxiusManaged, payload, metadata.LayerCount, metadata.HiddenSize);
            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Directory.CreateDirectory(target);
                if (!File.Exists(payload)) File.Move(stagedFile, payload);
                var manifestPath = Path.Combine(target, "manifest.json");
                var temporaryManifest = manifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
                await File.WriteAllTextAsync(temporaryManifest, JsonSerializer.Serialize(descriptor, _json), cancellationToken).ConfigureAwait(false);
                File.Move(temporaryManifest, manifestPath, true);
            }
            finally { _mutex.Release(); }
            return descriptor;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }
    }

    public ValueTask<Stream> OpenReadAsync(ModelVariantId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var descriptor = Find(id) ?? throw new FileNotFoundException($"Model variant {id} is not installed.");
        return ValueTask.FromResult<Stream>(new FileStream(descriptor.StorageReference, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    public async ValueTask<bool> VerifyAsync(ModelVariantId id, CancellationToken cancellationToken = default)
    {
        var descriptor = Find(id); if (descriptor is null || !File.Exists(descriptor.StorageReference)) return false;
        await using var stream = new FileStream(descriptor.StorageReference, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        return descriptor.Hashes.TryGetValue("sha256", out var expected) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expected));
    }

    public ValueTask<bool> RemoveAsync(ModelVariantId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var descriptor = Find(id); if (descriptor is null) return ValueTask.FromResult(false);
        var directory = Path.GetDirectoryName(descriptor.StorageReference)!; Directory.Delete(directory, true); return ValueTask.FromResult(true);
    }

    public ValueTask<ImmutableArray<ModelVariantDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(EnumerateDescriptors());
    }

    private ImmutableArray<ModelVariantDescriptor> EnumerateDescriptors()
    {
        if (!Directory.Exists(_root)) return [];
        return [.. Directory.EnumerateFiles(_root, "manifest.json", SearchOption.AllDirectories).Select(path =>
        {
            try { return JsonSerializer.Deserialize<ModelVariantDescriptor>(File.ReadAllText(path), _json); }
            catch (JsonException) { return null; }
            catch (IOException) { return null; }
        }).Where(static value => value is not null).Select(static value => value!)];
    }

    private ModelVariantDescriptor? Find(ModelVariantId id) => EnumerateDescriptors().FirstOrDefault(value => value.Id == id);
    private static async ValueTask<string> DetectFormatAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[16]; await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16, FileOptions.Asynchronous);
        var read = await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false);
        if (read >= 4 && header.AsSpan(0, 4).SequenceEqual("GGUF"u8)) return "gguf";
        // ONNX is protobuf and has no universal magic. Require a non-empty protobuf-like payload;
        // backend load validation still keeps it Unvalidated until parsed successfully.
        if (read >= 2 && header[0] is 0x08 or 0x0A or 0x12) return "onnx";
        throw new InvalidDataException("Unsupported or unrecognized model format. Filename extensions are not trusted.");
    }
    private static string Extension(string format) => format switch { "gguf" => ".gguf", "onnx" => ".onnx", _ => ".bin" };
}

public sealed class CompositeModelInventory(IEnumerable<ILocalInferenceBackend> backends, IModelStore store) : IModelInventory
{
    private readonly ImmutableArray<ILocalInferenceBackend> _backends = [.. backends];
    private ImmutableArray<ModelVariantDescriptor> _variants = [];
    public ImmutableArray<ModelVariantDescriptor> Variants => _variants;
    public ModelVariantDescriptor? Find(ModelVariantId id) => _variants.FirstOrDefault(value => value.Id == id);
    public async ValueTask<ImmutableArray<ModelVariantDescriptor>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<ModelVariantId, ModelVariantDescriptor>();
        foreach (var variant in await store.ListAsync(cancellationToken).ConfigureAwait(false)) result[variant.Id] = variant;
        foreach (var backend in _backends)
            foreach (var variant in await backend.DiscoverModelsAsync(cancellationToken).ConfigureAwait(false))
                result[variant.Id] = variant;
        _variants = [.. result.Values.OrderBy(static value => value.LogicalModel.Value, StringComparer.OrdinalIgnoreCase).ThenBy(static value => value.Id.Value, StringComparer.Ordinal)];
        return _variants;
    }
}

public sealed class HttpModelDownloadManager(HttpClient httpClient, IModelStore store, string downloadRoot) : IModelDownloadManager
{
    public async IAsyncEnumerable<ModelDownloadProgress> DownloadAsync(ModelDownloadDescriptor descriptor, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (descriptor.Metadata.License.Acceptance == LicenseAcceptance.Required) { yield return new(Guid.Empty, DownloadState.Failed, 0, descriptor.SizeBytes, "License acceptance is required."); yield break; }
        Directory.CreateDirectory(downloadRoot); var id = Guid.NewGuid(); var partialKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(descriptor.Uri.AbsoluteUri))).ToLowerInvariant();
        var partial = Path.Combine(downloadRoot, partialKey + ".partial"); var existing = descriptor.SupportsResume && File.Exists(partial) ? new FileInfo(partial).Length : 0;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(downloadRoot))!);
        var required = Math.Max(0, (descriptor.SizeBytes ?? 0) - existing);
        if (drive.AvailableFreeSpace < required + (512L << 20)) { yield return new(id, DownloadState.Failed, existing, descriptor.SizeBytes, "Insufficient disk space including installation headroom."); yield break; }
        using var request = new HttpRequestMessage(HttpMethod.Get, descriptor.Uri);
        if (existing > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (existing > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) existing = 0;
        response.EnsureSuccessStatusCode();
        var total = descriptor.SizeBytes ?? (response.Content.Headers.ContentLength.HasValue ? existing + response.Content.Headers.ContentLength.Value : null);
        yield return new(id, DownloadState.Downloading, existing, total, "Downloading");
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = new FileStream(partial, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var buffer = new byte[1024 * 1024]; var received = existing; var lastReport = Stopwatch.GetTimestamp();
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false); received += read;
                if (Stopwatch.GetElapsedTime(lastReport) >= TimeSpan.FromMilliseconds(250)) { yield return new(id, DownloadState.Downloading, received, total, "Downloading"); lastReport = Stopwatch.GetTimestamp(); }
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        yield return new(id, DownloadState.Verifying, new FileInfo(partial).Length, total, "Verifying SHA-256");
        if (descriptor.Sha256 is not null)
        {
            await using var verify = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(verify, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(descriptor.Sha256.ToLowerInvariant()))) { yield return new(id, DownloadState.Failed, verify.Length, total, "Hash mismatch; partial file retained for diagnostics."); yield break; }
        }
        yield return new(id, DownloadState.Installing, new FileInfo(partial).Length, total, "Installing immutable model variant");
        await using var model = new FileStream(partial, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var variant = await store.ImportAsync(model, descriptor.FileName, descriptor.Metadata, cancellationToken).ConfigureAwait(false);
        File.Delete(partial); yield return new(id, DownloadState.Completed, variant.FileSizeBytes, total, "Completed", variant.Id);
    }
}

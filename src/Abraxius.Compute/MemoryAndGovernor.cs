namespace Abraxius.Compute;

public sealed class CalibratingModelMemoryEstimator : IModelMemoryEstimator
{
    private readonly ConcurrentDictionary<string, Calibration> _calibrations = new(StringComparer.Ordinal);

    public ModelMemoryEstimate Estimate(ModelVariantDescriptor variant, InferenceBackendDescriptor backend, int contextTokens, int parallelism, ImmutableArray<ComputeDevice> devices)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parallelism);
        if (contextTokens > variant.ContextMaximum) throw new ArgumentOutOfRangeException(nameof(contextTokens), "Requested context exceeds the exact variant maximum.");
        var weights = variant.FileSizeBytes;
        var layers = variant.LayerCount ?? InferLayers(variant.ParameterCount);
        var hidden = variant.HiddenSize ?? InferHidden(variant.ParameterCount);
        var kv = checked((long)contextTokens * parallelism * layers * hidden * 2L * Math.Max(1, variant.BytesPerKvElement));
        var scratch = Math.Max(256L << 20, checked(weights / 20 + kv / 10));
        var overhead = Math.Max(128L << 20, weights / 50);
        var deviceBytes = checked(weights + kv + scratch + overhead);
        var hostBytes = devices.All(static value => value.DeviceClass == ComputeDeviceClass.Cpu) ? deviceBytes : Math.Max(512L << 20, weights / 20);
        var key = Key(variant.Id, backend.Id, [.. devices.Select(static value => value.Id)]);
        var confidence = EstimateConfidence.Low;
        if (_calibrations.TryGetValue(key, out var calibration))
        {
            deviceBytes = checked((long)Math.Ceiling(deviceBytes * calibration.DeviceFactor));
            hostBytes = checked((long)Math.Ceiling(hostBytes * calibration.RamFactor));
            confidence = calibration.Samples >= 5 ? EstimateConfidence.High : EstimateConfidence.Medium;
        }
        var totalBudget = devices.Sum(static value => value.DedicatedMemoryBytes ?? 0);
        var headroom = Math.Max(512L << 20, checked((long)(Math.Max(deviceBytes, totalBudget) * (confidence == EstimateConfidence.Low ? .15 : .10))));
        return new(weights, kv, scratch, overhead, hostBytes, deviceBytes, headroom, confidence);
    }

    public void Observe(ModelVariantId variant, BackendId backend, ImmutableArray<ComputeDeviceId> devices, ModelMemoryEstimate estimate, long actualRamBytes, long actualDeviceBytes)
    {
        var key = Key(variant, backend, devices);
        _calibrations.AddOrUpdate(key,
            _ => new(Math.Max(1, (double)actualRamBytes / Math.Max(1, estimate.HostRamBytes)), Math.Max(1, (double)actualDeviceBytes / Math.Max(1, estimate.DeviceMemoryBytes)), 1),
            (_, current) => new((current.RamFactor * current.Samples + Math.Max(1, (double)actualRamBytes / Math.Max(1, estimate.HostRamBytes))) / (current.Samples + 1), (current.DeviceFactor * current.Samples + Math.Max(1, (double)actualDeviceBytes / Math.Max(1, estimate.DeviceMemoryBytes))) / (current.Samples + 1), current.Samples + 1));
    }

    private static int InferLayers(long? parameters) => parameters switch { >= 60_000_000_000 => 80, >= 30_000_000_000 => 64, >= 10_000_000_000 => 48, >= 5_000_000_000 => 32, _ => 24 };
    private static int InferHidden(long? parameters) => parameters switch { >= 60_000_000_000 => 8192, >= 30_000_000_000 => 6656, >= 10_000_000_000 => 5120, >= 5_000_000_000 => 4096, _ => 3072 };
    private static string Key(ModelVariantId variant, BackendId backend, ImmutableArray<ComputeDeviceId> devices) => $"{variant}|{backend}|{string.Join(',', devices.OrderBy(static value => value.Value).Select(static value => value.Value))}";
    private sealed record Calibration(double RamFactor, double DeviceFactor, int Samples);
}

public sealed class ComputeResourceGovernor(ComputePolicyProfile policy) : IComputeResourceGovernor
{
    private readonly object _gate = new();
    private readonly Dictionary<ResourceReservationId, ResourceReservation> _reservations = [];
    public ImmutableArray<ResourceReservation> Reservations { get { lock (_gate) return [.. _reservations.Values]; } }

    public ValueTask<ResourceReservation> ReserveAsync(ResourceReservationRequest request, ComputeResourceSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Expire(DateTimeOffset.UtcNow);
            var active = _reservations.Values.Where(static value => value.State is ReservationState.Granted or ReservationState.Active).ToArray();
            var reservedRam = active.Sum(static value => value.Request.RamBytes);
            if (snapshot.RamAvailableBytes.HasValue && request.RamBytes + reservedRam + policy.MinimumRamHeadroomBytes > snapshot.RamAvailableBytes)
                return ValueTask.FromResult(Rejected(request, "Host RAM headroom would be violated."));
            foreach (var pair in request.DeviceMemoryBytes)
            {
                var state = snapshot.Find(pair.Key);
                if (state?.MemoryBudgetBytes is null) return ValueTask.FromResult(Rejected(request, $"Memory budget for {pair.Key} is unknown."));
                var used = state.MemoryUsedBytes ?? 0;
                var reserved = active.Sum(value => value.Request.DeviceMemoryBytes.GetValueOrDefault(pair.Key));
                var deviceHeadroom = Math.Max(policy.MinimumDeviceHeadroomBytes, checked((long)(state.MemoryBudgetBytes.Value * policy.DeviceHeadroomFraction)));
                if (used + reserved + pair.Value + deviceHeadroom > state.MemoryBudgetBytes.Value)
                    return ValueTask.FromResult(Rejected(request, $"Device memory budget for {pair.Key} would be exceeded."));
            }
            var granted = new ResourceReservation(ResourceReservationId.New(), request, ReservationState.Granted, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + request.ExpectedDuration + TimeSpan.FromMinutes(1));
            _reservations.Add(granted.Id, granted);
            return ValueTask.FromResult(granted);
        }
    }

    public ValueTask ReleaseAsync(ResourceReservationId id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) if (_reservations.TryGetValue(id, out var reservation)) _reservations[id] = reservation with { State = ReservationState.Released };
        return ValueTask.CompletedTask;
    }

    private static ResourceReservation Rejected(ResourceReservationRequest request, string reason) => new(ResourceReservationId.New(), request, ReservationState.Rejected, DateTimeOffset.UtcNow, Reason: reason);
    private void Expire(DateTimeOffset now)
    {
        foreach (var pair in _reservations.Where(pair => pair.Value.ExpiresAt <= now && pair.Value.State is ReservationState.Granted or ReservationState.Active).ToArray())
            _reservations[pair.Key] = pair.Value with { State = ReservationState.Expired };
    }
}

public sealed class BoundedFairInferenceBuffer
{
    private readonly object _gate = new();
    private readonly Queue<LocalInferenceRequest>[] _queues = Enumerable.Range(0, Enum.GetValues<InferencePriority>().Length).Select(static _ => new Queue<LocalInferenceRequest>()).ToArray();
    private readonly int _capacity;
    private readonly int _interactiveBurst;
    private int _count;
    private int _burst;
    public BoundedFairInferenceBuffer(int capacity, int interactiveBurst) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity); _capacity = capacity; _interactiveBurst = Math.Max(1, interactiveBurst); }
    public int Count { get { lock (_gate) return _count; } }
    public bool TryEnqueue(LocalInferenceRequest request) { lock (_gate) { if (_count >= _capacity) return false; _queues[(int)request.Priority].Enqueue(request); _count++; return true; } }
    public LocalInferenceRequest? TryDequeue()
    {
        lock (_gate)
        {
            var firstNormal = (int)InferencePriority.NormalMission;
            var lowerExists = _queues.Skip(firstNormal).Any(static queue => queue.Count > 0);
            var start = _burst >= _interactiveBurst && lowerExists ? firstNormal : 0;
            for (var index = start; index < _queues.Length; index++) if (_queues[index].TryDequeue(out var item)) { _count--; _burst = index < firstNormal ? _burst + 1 : 0; return item; }
            for (var index = 0; index < start; index++) if (_queues[index].TryDequeue(out var item)) { _count--; _burst++; return item; }
            return null;
        }
    }
}

public sealed class BoundedInferenceGate
{
    private readonly object _gate = new();
    private readonly Queue<Waiter>[] _queues = Enumerable.Range(0, Enum.GetValues<InferencePriority>().Length).Select(static _ => new Queue<Waiter>()).ToArray();
    private readonly int _maximumActive;
    private readonly int _maximumQueued;
    private readonly int _interactiveBurst;
    private int _active;
    private int _queued;
    private int _burst;
    public BoundedInferenceGate(int maximumActive, int maximumQueued, int interactiveBurst) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumActive); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumQueued); _maximumActive = maximumActive; _maximumQueued = maximumQueued; _interactiveBurst = Math.Max(1, interactiveBurst); }
    public int Queued { get { lock (_gate) return _queued; } }
    public ValueTask<IAsyncDisposable?> EnterAsync(InferencePriority priority, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_active < _maximumActive) { _active++; return ValueTask.FromResult<IAsyncDisposable?>(new Lease(this)); }
            if (_queued >= _maximumQueued) return ValueTask.FromResult<IAsyncDisposable?>(null);
            var waiter = new Waiter(cancellationToken); _queues[(int)priority].Enqueue(waiter); _queued++; return new(waiter.Completion.Task);
        }
    }
    private void Release()
    {
        Waiter? next = null;
        lock (_gate)
        {
            while ((next = Dequeue()) is not null && next.Cancellation.IsCancellationRequested) next = null;
            if (next is null) _active--;
        }
        next?.Completion.TrySetResult(new Lease(this));
    }
    private Waiter? Dequeue()
    {
        var firstNormal = (int)InferencePriority.NormalMission; var lower = _queues.Skip(firstNormal).Any(static queue => queue.Count > 0); var start = _burst >= _interactiveBurst && lower ? firstNormal : 0;
        for (var index = start; index < _queues.Length; index++) if (_queues[index].TryDequeue(out var item)) { _queued--; _burst = index < firstNormal ? _burst + 1 : 0; return item; }
        for (var index = 0; index < start; index++) if (_queues[index].TryDequeue(out var item)) { _queued--; _burst++; return item; }
        return null;
    }
    private sealed record Waiter(CancellationToken Cancellation) { public TaskCompletionSource<IAsyncDisposable?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); }
    private sealed class Lease(BoundedInferenceGate owner) : IAsyncDisposable { private int _released; public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(); return ValueTask.CompletedTask; } }
}

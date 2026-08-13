namespace Abraxius.Compute;

public sealed class ModelResidencyManager(
    IEnumerable<ILocalInferenceBackend> backends,
    IModelInventory inventory,
    IComputeResourceGovernor governor,
    ComputePolicyProfile policy) : IModelResidencyManager
{
    private readonly ImmutableDictionary<BackendId, ILocalInferenceBackend> _backends = backends.ToImmutableDictionary(static value => value.Descriptor.Id);
    private readonly ConcurrentDictionary<ModelVariantId, ResidentModelInstance> _instances = new();
    private readonly ConcurrentDictionary<ModelVariantId, ResourceReservationId> _reservations = new();
    private readonly ConcurrentDictionary<ModelVariantId, SemaphoreSlim> _loadLocks = new();
    private readonly ConcurrentDictionary<ModelVariantId, DateTimeOffset> _lastEvicted = new();
    public ImmutableArray<ResidentModelInstance> Instances => [.. _instances.Values.OrderBy(static value => value.LastUsedAt)];

    public bool BeginSession(ModelVariantId variant)
    {
        while (_instances.TryGetValue(variant, out var current))
            if (_instances.TryUpdate(variant, current with { ActiveSessions = current.ActiveSessions + 1, State = ModelResidencyState.Busy, LastUsedAt = DateTimeOffset.UtcNow }, current)) return true;
        return false;
    }

    public void EndSession(ModelVariantId variant)
    {
        while (_instances.TryGetValue(variant, out var current))
        {
            var sessions = Math.Max(0, current.ActiveSessions - 1); var next = current with { ActiveSessions = sessions, State = sessions == 0 ? ModelResidencyState.IdleResident : ModelResidencyState.Busy, LastUsedAt = DateTimeOffset.UtcNow };
            if (_instances.TryUpdate(variant, next, current)) return;
        }
    }

    public async ValueTask<ResidentModelInstance> EnsureResidentAsync(LocalInferenceExecutionPlan plan, CancellationToken cancellationToken = default)
    {
        if (_instances.TryGetValue(plan.Variant.Id, out var resident) && resident.State is ModelResidencyState.Resident or ModelResidencyState.IdleResident or ModelResidencyState.Busy)
        {
            resident = resident with { LastUsedAt = DateTimeOffset.UtcNow }; _instances[plan.Variant.Id] = resident; return resident;
        }
        var mutex = _loadLocks.GetOrAdd(plan.Variant.Id, static _ => new SemaphoreSlim(1, 1)); await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_instances.TryGetValue(plan.Variant.Id, out resident) && resident.State is ModelResidencyState.Resident or ModelResidencyState.IdleResident or ModelResidencyState.Busy) return resident;
            if (_lastEvicted.TryGetValue(plan.Variant.Id, out var evicted) && DateTimeOffset.UtcNow - evicted < policy.EffectiveEvictionCooldown)
                throw new InvalidOperationException("Residency cooldown prevents a load/evict thrash cycle.");
            if (!_backends.TryGetValue(plan.Backend.Id, out var backend)) throw new InvalidOperationException($"Backend {plan.Backend.Id} is not registered.");
            _instances[plan.Variant.Id] = new(ModelInstanceId.New(), plan.Variant.Id, plan.Backend.Id, plan.Devices, ModelResidencyState.Loading, 0, 0, plan.ContextTokens, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, TimeSpan.Zero, false, 0);
            try
            {
                resident = await backend.LoadAsync(plan.Variant, plan, cancellationToken).ConfigureAwait(false);
                resident = resident with { State = ModelResidencyState.Resident, LastUsedAt = DateTimeOffset.UtcNow };
                _instances[plan.Variant.Id] = resident; _reservations[plan.Variant.Id] = plan.Reservation.Id; return resident;
            }
            catch
            {
                _instances[plan.Variant.Id] = _instances[plan.Variant.Id] with { State = ModelResidencyState.Failed };
                await governor.ReleaseAsync(plan.Reservation.Id, CancellationToken.None).ConfigureAwait(false); throw;
            }
        }
        finally { mutex.Release(); }
    }

    public async ValueTask<bool> UnloadAsync(ModelVariantId variant, bool allowActiveCancellation = false, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(variant, out var instance)) return false;
        if (instance.ActiveSessions > 0 && !allowActiveCancellation) return false;
        if (!_backends.TryGetValue(instance.BackendId, out var backend)) return false;
        var descriptor = inventory.Find(variant); if (descriptor is null) return false;
        _instances[variant] = instance with { State = ModelResidencyState.Evicting };
        await backend.UnloadAsync(descriptor, cancellationToken).ConfigureAwait(false);
        _instances.TryRemove(variant, out _); _lastEvicted[variant] = DateTimeOffset.UtcNow;
        if (_reservations.TryRemove(variant, out var reservation)) await governor.ReleaseAsync(reservation, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<ImmutableArray<ModelVariantId>> RelievePressureAsync(long requiredBytes, ResourcePressure pressure, CancellationToken cancellationToken = default)
    {
        var freed = 0L; var evicted = ImmutableArray.CreateBuilder<ModelVariantId>(); var now = DateTimeOffset.UtcNow;
        var candidates = _instances.Values.Where(static value => value.ActiveSessions == 0 && value.State is ModelResidencyState.Resident or ModelResidencyState.IdleResident)
            .OrderBy(value => Score(value, now, pressure)).ToArray();
        foreach (var candidate in candidates)
        {
            if (candidate.Pinned && pressure != ResourcePressure.Critical) continue;
            if (await UnloadAsync(candidate.VariantId, cancellationToken: cancellationToken).ConfigureAwait(false)) { evicted.Add(candidate.VariantId); freed += candidate.DeviceMemoryBytes; if (freed >= requiredBytes) break; }
        }
        return evicted.ToImmutable();
    }

    private static double Score(ResidentModelInstance value, DateTimeOffset now, ResourcePressure pressure)
    {
        var idleSeconds = Math.Max(1, (now - value.LastUsedAt).TotalSeconds); var reload = Math.Max(1, value.LoadDuration.TotalSeconds);
        var pinPenalty = value.Pinned && pressure != ResourcePressure.Critical ? 1_000_000 : 0;
        return pinPenalty + reload * 100 - idleSeconds - value.DeviceMemoryBytes / (double)(1L << 30) * 10;
    }
}

public sealed class InferenceAdmissionController(
    IModelInventory models,
    IComputeDeviceInventory devices,
    IComputeTelemetryService telemetry,
    IEnumerable<ILocalInferenceBackend> backends,
    IModelMemoryEstimator estimator,
    IComputeResourceGovernor governor,
    IModelResidencyManager residency) : IInferenceAdmissionController
{
    private readonly ImmutableArray<ILocalInferenceBackend> _backends = [.. backends];
    public async ValueTask<InferenceAdmissionDecision> AdmitAsync(LocalInferenceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ContextTokens <= 0 || request.ExpectedOutputTokens < 0) return Reject("Context/output limits are invalid.");
        if (models.Variants.IsDefaultOrEmpty) await models.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (devices.Current.IsDefaultOrEmpty) await devices.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await telemetry.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var variants = models.Variants.Where(value => value.LogicalModel == request.LogicalModelTarget && value.ValidationState != ModelValidationState.Deprecated)
            .Where(value => request.RequiredVariant is null || value.Id == request.RequiredVariant)
            .Where(value => request.CapabilityRequirements.All(capability => value.ValidatedCapabilities.Contains(capability) || value.ClaimedCapabilities.Contains(capability)))
            .OrderByDescending(static value => value.ValidationState).ThenByDescending(static value => value.Quantization, StringComparer.Ordinal).ToArray();
        if (variants.Length == 0) return Reject("No exact installed model variant satisfies the requested logical model and capabilities.");
        var alternatives = ImmutableArray.CreateBuilder<LocalInferenceOffer>(); var failures = new List<string>();
        foreach (var variant in variants)
        foreach (var backend in _backends.Where(value => variant.CompatibleBackends.Contains(value.Descriptor.Id) && value.Descriptor.Health is BackendHealth.Healthy or BackendHealth.Unknown))
        foreach (var deviceSet in CandidateDeviceSets(devices.Current, backend.Descriptor))
        {
            if (request.ContextTokens > variant.ContextMaximum) { failures.Add($"{variant.Id}: requested context exceeds model maximum"); continue; }
            ModelMemoryEstimate estimate;
            try { estimate = estimator.Estimate(variant, backend.Descriptor, request.ContextTokens, 1, deviceSet); }
            catch (OverflowException) { failures.Add($"{variant.Id}: memory estimate overflow"); continue; }
            var perDevice = Split(estimate.TotalDeviceReservation, deviceSet);
            var reservationRequest = new ResourceReservationRequest(ComputeWorkloadId.New(), request.Priority, [.. deviceSet.Select(static value => value.Id)], estimate.TotalHostReservation, perDevice, CpuWeight(request.Priority), request.Timeout ?? TimeSpan.FromMinutes(10), request.Priority is InferencePriority.Background or InferencePriority.Maintenance, $"inference:{variant.Id}");
            var reservation = await governor.ReserveAsync(reservationRequest, snapshot, cancellationToken).ConfigureAwait(false);
            var state = residency.Instances.FirstOrDefault(value => value.VariantId == variant.Id)?.State ?? ModelResidencyState.Installed;
            var safeContext = MaximumSafeContext(variant, backend.Descriptor, deviceSet, snapshot, estimator);
            alternatives.Add(new(0, snapshot.Timestamp, variant.LogicalModel, variant.Id, backend.Descriptor.Id, [.. deviceSet.Select(static value => value.Id)], safeContext, estimate, state, TimeSpan.Zero, null, null, 0, MaximumPressure(snapshot, deviceSet), ModelAvailability.Installed));
            if (reservation.State != ReservationState.Granted) { failures.Add(reservation.Reason ?? "resource reservation rejected"); continue; }
            var resident = state is ModelResidencyState.Resident or ModelResidencyState.IdleResident or ModelResidencyState.Busy;
            var plan = new LocalInferenceExecutionPlan(variant, backend.Descriptor, [.. deviceSet.Select(static value => value.Id)], request.ContextTokens, 1, estimate, reservation, resident,
                $"{variant.DisplayName} via {backend.Descriptor.Id} on {string.Join(", ", deviceSet.Select(static value => value.Model))}; exact variant; {(resident ? "already resident" : "load required")}; memory and context admitted with headroom.");
            return new(InferenceAdmissionStatus.Granted, plan, alternatives.ToImmutable(), plan.Explanation);
        }
        return new(InferenceAdmissionStatus.AlternativeOffered, null, alternatives.ToImmutable(), failures.Count == 0 ? "No eligible backend/device plan." : string.Join("; ", failures.Distinct(StringComparer.Ordinal).Take(6)));
    }

    private static InferenceAdmissionDecision Reject(string reason) => new(InferenceAdmissionStatus.Rejected, null, [], reason);
    private static IEnumerable<ImmutableArray<ComputeDevice>> CandidateDeviceSets(ImmutableArray<ComputeDevice> all, InferenceBackendDescriptor backend)
    {
        var eligible = all.Where(value => backend.DeviceClasses.Contains(value.DeviceClass)).ToArray();
        foreach (var device in eligible.OrderByDescending(static value => value.DedicatedMemoryBytes ?? value.SharedMemoryBytes ?? 0)) yield return [device];
        var gpus = eligible.Where(static value => value.DeviceClass == ComputeDeviceClass.Gpu).ToImmutableArray(); if (gpus.Length > 1) yield return gpus;
    }
    private static ImmutableDictionary<ComputeDeviceId, long> Split(long total, ImmutableArray<ComputeDevice> devices)
    {
        var count = Math.Max(1, devices.Length); var each = checked((total + count - 1) / count); return devices.ToImmutableDictionary(static value => value.Id, _ => each);
    }
    private static int CpuWeight(InferencePriority priority) => priority switch { InferencePriority.RealtimeVoice => 100, InferencePriority.InteractiveUser => 90, InferencePriority.InteractiveMission => 80, InferencePriority.Verification => 60, InferencePriority.NormalMission => 50, InferencePriority.Background => 20, _ => 10 };
    private static ResourcePressure MaximumPressure(ComputeResourceSnapshot snapshot, ImmutableArray<ComputeDevice> devices) => devices.Select(value => snapshot.Find(value.Id)?.Pressure ?? ResourcePressure.Normal).Append(snapshot.RamPressure).Max();
    private static int MaximumSafeContext(ModelVariantDescriptor variant, InferenceBackendDescriptor backend, ImmutableArray<ComputeDevice> devices, ComputeResourceSnapshot snapshot, IModelMemoryEstimator estimator)
    {
        var low = 1; var high = variant.ContextMaximum; var result = 0;
        while (low <= high)
        {
            var middle = low + (high - low) / 2; ModelMemoryEstimate estimate;
            try { estimate = estimator.Estimate(variant, backend, middle, 1, devices); } catch (OverflowException) { high = middle - 1; continue; }
            var available = devices.Sum(value => snapshot.Find(value.Id)?.MemoryBudgetBytes ?? 0);
            if (available > 0 && estimate.TotalDeviceReservation <= available) { result = middle; low = middle + 1; } else high = middle - 1;
        }
        return result;
    }
}

public sealed class LocalInferenceManager(
    IInferenceAdmissionController admission,
    IModelResidencyManager residency,
    IEnumerable<ILocalInferenceBackend> backends,
    IModelMemoryEstimator estimator,
    IComputeResourceGovernor governor,
    ComputePolicyProfile policy) : ILocalInferenceManager
{
    private static readonly ActivitySource Activity = new("Abraxius.Compute");
    private readonly ImmutableDictionary<BackendId, ILocalInferenceBackend> _backends = backends.ToImmutableDictionary(static value => value.Descriptor.Id);
    private readonly ConcurrentQueue<InferenceTelemetry> _history = new();
    private readonly BoundedInferenceGate _gate = new(policy.MaximumConcurrentInference, policy.MaximumQueuedInference, policy.InteractiveBurstBeforeFairness);
    private long _offerVersion;
    public ValueTask<InferenceAdmissionDecision> PlanAsync(LocalInferenceRequest request, CancellationToken cancellationToken = default) => admission.AdmitAsync(request, cancellationToken);

    public async IAsyncEnumerable<LocalInferenceEvent> InferAsync(LocalInferenceRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var queuedAt = Stopwatch.GetTimestamp();
        await using var queueLease = await _gate.EnterAsync(request.Priority, cancellationToken).ConfigureAwait(false);
        if (queueLease is null) { yield return new LocalInferenceEvent.Failed(DateTimeOffset.UtcNow, "compute_queue_full", "The bounded local inference queue is full."); yield break; }
        var queueDelay = Stopwatch.GetElapsedTime(queuedAt);
        var decision = await admission.AdmitAsync(request, cancellationToken).ConfigureAwait(false);
        if (decision.Status != InferenceAdmissionStatus.Granted || decision.Plan is null) { yield return new LocalInferenceEvent.Failed(DateTimeOffset.UtcNow, "compute_admission", decision.Explanation); yield break; }
        var plan = decision.Plan; using var activity = Activity.StartActivity("compute.inference", ActivityKind.Internal);
        activity?.SetTag("model.variant.id", plan.Variant.Id.Value); activity?.SetTag("compute.backend.id", plan.Backend.Id.Value); activity?.SetTag("compute.device.ids", string.Join(',', plan.Devices.Select(static value => value.Value)));
        ResidentModelInstance? instance = null; string? loadError = null;
        try { instance = await residency.EnsureResidentAsync(plan, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException) { loadError = exception.Message; }
        if (loadError is not null) { yield return new LocalInferenceEvent.Failed(DateTimeOffset.UtcNow, "compute_load", loadError); yield break; }
        if (!_backends.TryGetValue(plan.Backend.Id, out var backend)) { yield return new LocalInferenceEvent.Failed(DateTimeOffset.UtcNow, "compute_backend", "Selected backend disappeared."); yield break; }
        if (!residency.BeginSession(plan.Variant.Id)) { yield return new LocalInferenceEvent.Failed(DateTimeOffset.UtcNow, "compute_residency", "Resident model disappeared before execution."); yield break; }
        yield return new LocalInferenceEvent.Started(DateTimeOffset.UtcNow, plan);
        var session = InferenceSessionId.New(); var firstTokenAt = TimeSpan.Zero; var inferenceStarted = Stopwatch.GetTimestamp(); var output = new StringBuilder(); BackendInferenceEvent.Completed? completed = null;
        try
        {
            await foreach (var item in backend.InferAsync(plan.Variant, request, plan, cancellationToken).ConfigureAwait(false))
            {
                switch (item)
                {
                    case BackendInferenceEvent.Token token:
                        if (firstTokenAt == TimeSpan.Zero) firstTokenAt = Stopwatch.GetElapsedTime(inferenceStarted);
                        output.Append(token.Text); yield return new LocalInferenceEvent.Token(DateTimeOffset.UtcNow, token.Text); break;
                    case BackendInferenceEvent.Completed done: completed = done; break;
                }
            }
        }
        finally
        {
            residency.EndSession(plan.Variant.Id);
            if (plan.ReuseResident) await governor.ReleaseAsync(plan.Reservation.Id, CancellationToken.None).ConfigureAwait(false);
        }
        if (completed is null) { yield return new LocalInferenceEvent.Failed(DateTimeOffset.UtcNow, "compute_stream", "Backend stream ended without a completion record."); yield break; }
        var metrics = new InferenceTelemetry(session, plan.Variant.Id, plan.Backend.Id, plan.Devices, queueDelay, FromNanoseconds(completed.LoadNanoseconds), firstTokenAt,
            completed.PromptTokens, FromNanoseconds(completed.PromptNanoseconds), completed.OutputTokens, FromNanoseconds(completed.GenerationNanoseconds), plan.Memory.HostRamBytes, plan.Memory.TotalDeviceReservation, completed.PeakDeviceBytes, !plan.ReuseResident, DateTimeOffset.UtcNow);
        estimator.Observe(plan.Variant.Id, plan.Backend.Id, plan.Devices, plan.Memory, instance!.RamBytes, completed.PeakDeviceBytes ?? instance.DeviceMemoryBytes);
        _history.Enqueue(metrics); while (_history.Count > 10_000) _history.TryDequeue(out _);
        yield return new LocalInferenceEvent.Completed(DateTimeOffset.UtcNow, completed.Text.Length > 0 ? completed.Text : output.ToString(), metrics);
    }

    public ImmutableArray<LocalInferenceOffer> GetOffers()
    {
        var version = Interlocked.Increment(ref _offerVersion); var now = DateTimeOffset.UtcNow;
        return [.. residency.Instances.Select(value => new LocalInferenceOffer(version, now, new(value.VariantId.Value.Split('/')[0]), value.VariantId, value.BackendId, value.Devices, value.ContextTokens,
            new(0, 0, 0, 0, value.RamBytes, value.DeviceMemoryBytes, 0, EstimateConfidence.High), value.State, value.LoadDuration, null, null, _gate.Queued, ResourcePressure.Normal, ModelAvailability.Resident))];
    }
    private static TimeSpan FromNanoseconds(long nanoseconds) => TimeSpan.FromTicks(nanoseconds / 100);
}

public sealed class ComputeModelProvider(ILocalInferenceManager manager) : IModelProvider
{
    public async ValueTask<ModelResult> InferAsync(ModelRequest request, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp(); string text = ""; InferenceTelemetry? telemetry = null;
        await foreach (var item in manager.InferAsync(Map(request), cancellationToken).ConfigureAwait(false))
        {
            if (item is LocalInferenceEvent.Completed completed) { text = completed.Text; telemetry = completed.Telemetry; }
            else if (item is LocalInferenceEvent.Failed failed) throw new InvalidOperationException($"{failed.Code}: {failed.Message}");
        }
        return new(text, null, telemetry?.Variant.Value ?? request.Model ?? "local", telemetry is null ? null : new(telemetry.PromptTokens, telemetry.OutputTokens), Stopwatch.GetElapsedTime(started), telemetry?.Backend.Value ?? "local-compute");
    }
    public async IAsyncEnumerable<ModelStreamEvent> StreamAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in manager.InferAsync(Map(request), cancellationToken).ConfigureAwait(false))
        {
            if (item is LocalInferenceEvent.Started started) yield return new ModelStreamEvent.Started(item.Timestamp, started.Plan.Variant.Id.Value);
            else if (item is LocalInferenceEvent.Token token) yield return new ModelStreamEvent.Token(item.Timestamp, token.Text);
            else if (item is LocalInferenceEvent.Completed completed) yield return new ModelStreamEvent.Completed(item.Timestamp, new(completed.Text, null, completed.Telemetry.Variant.Value, new(completed.Telemetry.PromptTokens, completed.Telemetry.OutputTokens), completed.Telemetry.GenerationDuration, completed.Telemetry.Backend.Value));
            else if (item is LocalInferenceEvent.Failed failed) throw new InvalidOperationException($"{failed.Code}: {failed.Message}");
        }
    }
    private static LocalInferenceRequest Map(ModelRequest request) => new(new(request.Model ?? "local-default"), request.RequiredCapabilities.Select(static capability => capability switch { ModelCapability.ToolCalling => ModelCapabilityKind.Tools, ModelCapability.StructuredOutput => ModelCapabilityKind.StructuredOutput, ModelCapability.Vision => ModelCapabilityKind.Vision, ModelCapability.Coding => ModelCapabilityKind.Code, ModelCapability.Reasoning => ModelCapabilityKind.Reasoning, _ => ModelCapabilityKind.Chat }).ToImmutableHashSet(),
        request.RequiredContextTokens ?? 4096, request.MaxOutputTokens ?? 1024, request.Priority == WorkPriority.Critical ? InferencePriority.InteractiveUser : InferencePriority.InteractiveMission, request.Stream, request.Tools.Count > 0, request.DataClassification, request.Prompt, SessionKey: request.SessionKey, Timeout: request.Timeout);
}

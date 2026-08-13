using Abraxius.Compute;

namespace Abraxius.Fabric;

public static class ComputeFabricAdapter
{
    public static FabricNodeDescriptor WithCompute(this FabricNodeDescriptor node, ComputeRuntime compute)
    {
        var snapshot = compute.Telemetry.Latest;
        var gpus = compute.Devices.Current.Where(static value => value.DeviceClass is ComputeDeviceClass.Gpu or ComputeDeviceClass.Npu).Select(device =>
        {
            var resource = snapshot?.Find(device.Id);
            var total = resource?.MemoryBudgetBytes ?? device.DedicatedMemoryBytes ?? device.SharedMemoryBytes ?? 0;
            var used = resource?.MemoryUsedBytes ?? 0;
            return new FabricGpuDescriptor(device.Id.Value, device.Vendor, device.Architecture, total, Math.Max(0, total - used), string.Join(',', device.BackendCapabilities), device.ComputeCapabilities);
        }).ToImmutableArray();
        var offers = compute.Inference.GetOffers();
        var models = compute.Models.Variants.Select(variant =>
        {
            var offer = offers.FirstOrDefault(value => value.Variant == variant.Id);
            return new FabricModelDescriptor(variant.LogicalModel.Value, offer?.Backend.Value ?? variant.CompatibleBackends.FirstOrDefault().Value ?? "unavailable", variant.Quantization,
                offer?.MaximumSafeContext ?? variant.ContextMaximum, variant.ValidatedCapabilities.Contains(ModelCapabilityKind.Tools), variant.ValidatedCapabilities.Contains(ModelCapabilityKind.StructuredOutput),
                offer?.EstimatedMemory.TotalDeviceReservation ?? variant.FileSizeBytes, offer?.CurrentResidency is ModelResidencyState.Resident or ModelResidencyState.IdleResident or ModelResidencyState.Busy, offer is not null || variant.CompatibleBackends.Count > 0);
        }).ToImmutableArray();
        var resources = node.Resources with
        {
            CpuUtilization = snapshot?.CpuUtilization ?? node.Resources.CpuUtilization,
            TotalRamBytes = snapshot?.RamTotalBytes ?? node.Resources.TotalRamBytes,
            FreeRamBytes = snapshot?.RamAvailableBytes ?? node.Resources.FreeRamBytes,
            Gpus = gpus,
            CapturedAt = snapshot?.Timestamp ?? node.Resources.CapturedAt
        };
        var capabilities = node.Capabilities.Where(static value => value.Id is not "ModelInference" and not "Embedding").ToImmutableArray();
        if (models.Length > 0) capabilities = capabilities.Add(new("ModelInference", "21", false));
        if (compute.Models.Variants.Any(static value => value.ClaimedCapabilities.Contains(ModelCapabilityKind.Embedding) || value.ValidatedCapabilities.Contains(ModelCapabilityKind.Embedding))) capabilities = capabilities.Add(new("Embedding", "21", false));
        return node with { Models = models, Resources = resources, Capabilities = capabilities, Roles = models.Length > 0 ? node.Roles | FabricNodeRole.ModelHost : node.Roles };
    }
}

using System.Runtime.InteropServices;

namespace Abraxius.Compute;

public sealed class CompositeComputeDeviceInventory(IEnumerable<IComputeDeviceProvider> providers) : IComputeDeviceInventory
{
    private readonly ImmutableArray<IComputeDeviceProvider> _providers = [.. providers];
    private ImmutableArray<ComputeDevice> _current = [];
    public ImmutableArray<ComputeDevice> Current => _current;

    public async ValueTask<ImmutableArray<ComputeDevice>> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var devices = new Dictionary<ComputeDeviceId, ComputeDevice>();
        foreach (var provider in _providers)
            foreach (var device in await provider.DiscoverAsync(cancellationToken).ConfigureAwait(false))
                devices[device.Id] = device;
        _current = [.. devices.Values.OrderBy(static value => value.DeviceClass).ThenBy(static value => value.Id.Value, StringComparer.Ordinal)];
        return _current;
    }
}

public sealed class CpuComputeDeviceProvider : IComputeDeviceProvider
{
    public ValueTask<ImmutableArray<ComputeDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var architecture = RuntimeInformation.ProcessArchitecture.ToString();
        var model = ReadCpuModel() ?? RuntimeInformation.OSDescription;
        var simd = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        if (System.Runtime.Intrinsics.X86.Avx2.IsSupported) simd.Add("AVX2");
        if (System.Runtime.Intrinsics.X86.Avx512F.IsSupported) simd.Add("AVX512F");
        if (System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported) simd.Add("AdvSimd");
        var device = new ComputeDevice(new("cpu:host"), "Host", model, ComputeDeviceClass.Cpu, architecture,
            ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "cpu"), ComputeMemoryArchitecture.Shared,
            null, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, simd.ToImmutable(), null,
            TelemetryCapability.Partial, "cpu:host", Environment.ProcessorCount, null, simd.ToImmutable());
        return ValueTask.FromResult(ImmutableArray.Create(device));
    }

    private static string? ReadCpuModel()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/cpuinfo")) return null;
        foreach (var line in File.ReadLines("/proc/cpuinfo"))
        {
            if (!line.StartsWith("model name", StringComparison.OrdinalIgnoreCase)) continue;
            var separator = line.IndexOf(':', StringComparison.Ordinal);
            return separator >= 0 ? line[(separator + 1)..].Trim() : null;
        }
        return null;
    }
}

public sealed class LinuxDrmComputeDeviceProvider : IComputeDeviceProvider
{
    private const string DrmRoot = "/sys/class/drm";
    public ValueTask<ImmutableArray<ComputeDevice>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(DrmRoot)) return ValueTask.FromResult(ImmutableArray<ComputeDevice>.Empty);
        var result = ImmutableArray.CreateBuilder<ComputeDevice>();
        foreach (var card in Directory.EnumerateDirectories(DrmRoot, "card*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(card);
            if (name.Contains('-', StringComparison.Ordinal)) continue;
            var device = Path.Combine(card, "device");
            var vendorCode = ReadText(Path.Combine(device, "vendor"));
            if (vendorCode is null) continue;
            var vendor = vendorCode.ToLowerInvariant() switch { "0x10de" => "NVIDIA", "0x1002" => "AMD", "0x8086" => "Intel", _ => vendorCode };
            var hardware = ReadText(Path.Combine(device, "device")) ?? name;
            var dedicated = ReadLong(Path.Combine(device, "mem_info_vram_total"));
            var memoryArchitecture = dedicated.HasValue ? ComputeMemoryArchitecture.Dedicated : ComputeMemoryArchitecture.Shared;
            var stable = TryResolveStablePath(device) ?? $"linux-drm:{name}:{vendorCode}:{hardware}";
            result.Add(new(new($"gpu:{stable}"), vendor, hardware, ComputeDeviceClass.Gpu, RuntimeInformation.ProcessArchitecture.ToString(),
                ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "drm"), memoryArchitecture, dedicated,
                memoryArchitecture == ComputeMemoryArchitecture.Shared ? GC.GetGCMemoryInfo().TotalAvailableMemoryBytes : null,
                ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "graphics", "compute"), null,
                TelemetryCapability.Partial, stable));
        }
        return ValueTask.FromResult(result.ToImmutable());
    }

    private static string? ReadText(string path) => File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    private static long? ReadLong(string path) => long.TryParse(ReadText(path), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static string? TryResolveStablePath(string path) { try { return Directory.ResolveLinkTarget(path, true)?.FullName; } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } }
}

public interface IVendorTelemetryProbe
{
    string Vendor { get; }
    ValueTask<ImmutableArray<VendorTelemetryReading>> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed record VendorTelemetryReading(string HardwareIdentity, long? MemoryUsedBytes, long? MemoryBudgetBytes, double? Utilization, double? TemperatureCelsius, double? PowerWatts);

public abstract class VendorTelemetryProvider(string id, string vendor, IVendorTelemetryProbe probe) : IComputeTelemetryProvider
{
    public string Id { get; } = id;
    public async ValueTask<ImmutableArray<DeviceResourceState>> ReadAsync(ImmutableArray<ComputeDevice> devices, CancellationToken cancellationToken = default)
    {
        if (!probe.Vendor.Equals(vendor, StringComparison.OrdinalIgnoreCase)) return [];
        var readings = await probe.ReadAsync(cancellationToken).ConfigureAwait(false);
        return [.. readings.Select(reading =>
        {
            var device = devices.FirstOrDefault(item => item.StableHardwareIdentity?.Equals(reading.HardwareIdentity, StringComparison.OrdinalIgnoreCase) == true);
            if (device is null) return null;
            var pressure = Pressure(reading.MemoryUsedBytes, reading.MemoryBudgetBytes);
            return new DeviceResourceState(device.Id, reading.MemoryUsedBytes, reading.MemoryBudgetBytes, reading.Utilization, reading.TemperatureCelsius, reading.PowerWatts, pressure, DateTimeOffset.UtcNow);
        }).Where(static value => value is not null).Select(static value => value!)];
    }

    private static ResourcePressure Pressure(long? used, long? budget)
    {
        if (!used.HasValue || !budget.HasValue || budget <= 0) return ResourcePressure.Normal;
        var ratio = (double)used.Value / budget.Value;
        return ratio switch { >= .95 => ResourcePressure.Critical, >= .85 => ResourcePressure.High, >= .70 => ResourcePressure.Elevated, _ => ResourcePressure.Normal };
    }
}

public sealed class NvmlTelemetryProvider(IVendorTelemetryProbe probe) : VendorTelemetryProvider("nvml", "NVIDIA", probe);
public sealed class AmdSmiTelemetryProvider(IVendorTelemetryProbe probe) : VendorTelemetryProvider("amd-smi", "AMD", probe);
public sealed class IntelLevelZeroTelemetryProvider(IVendorTelemetryProbe probe) : VendorTelemetryProvider("level-zero", "Intel", probe);
public sealed class DxgiMemoryBudgetProvider(IVendorTelemetryProbe probe) : VendorTelemetryProvider("dxgi", "Microsoft", probe);

public sealed class ComputeTelemetryService(IComputeDeviceInventory inventory, IEnumerable<IComputeTelemetryProvider> providers) : IComputeTelemetryService
{
    private readonly ImmutableArray<IComputeTelemetryProvider> _providers = [.. providers];
    private readonly Process _process = Process.GetCurrentProcess();
    public ComputeResourceSnapshot? Latest { get; private set; }

    public async ValueTask<ComputeResourceSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (inventory.Current.IsDefaultOrEmpty) await inventory.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var states = new Dictionary<ComputeDeviceId, DeviceResourceState>();
        foreach (var provider in _providers)
            foreach (var state in await provider.ReadAsync(inventory.Current, cancellationToken).ConfigureAwait(false))
                states[state.DeviceId] = Merge(states.GetValueOrDefault(state.DeviceId), state);
        foreach (var device in inventory.Current)
            states.TryAdd(device.Id, new(device.Id, null, device.DedicatedMemoryBytes ?? device.SharedMemoryBytes, null, null, null, ResourcePressure.Normal, DateTimeOffset.UtcNow));
        _process.Refresh();
        var gc = GC.GetGCMemoryInfo();
        long? total = gc.TotalAvailableMemoryBytes > 0 ? gc.TotalAvailableMemoryBytes : null;
        long? available = total.HasValue ? Math.Max(0, total.Value - _process.WorkingSet64) : null;
        Latest = new(DateTimeOffset.UtcNow, null, available, total, RamPressure(available, total), [.. states.Values], _process.WorkingSet64, _process.TotalProcessorTime);
        return Latest;
    }

    private static DeviceResourceState Merge(DeviceResourceState? left, DeviceResourceState right) => left is null ? right : right with
    {
        MemoryUsedBytes = right.MemoryUsedBytes ?? left.MemoryUsedBytes,
        MemoryBudgetBytes = right.MemoryBudgetBytes ?? left.MemoryBudgetBytes,
        Utilization = right.Utilization ?? left.Utilization,
        TemperatureCelsius = right.TemperatureCelsius ?? left.TemperatureCelsius,
        PowerWatts = right.PowerWatts ?? left.PowerWatts,
        Pressure = (ResourcePressure)Math.Max((int)left.Pressure, (int)right.Pressure)
    };
    private static ResourcePressure RamPressure(long? available, long? total) => !available.HasValue || !total.HasValue || total == 0 ? ResourcePressure.Normal : ((double)available.Value / total.Value) switch { < .05 => ResourcePressure.Critical, < .15 => ResourcePressure.High, < .30 => ResourcePressure.Elevated, _ => ResourcePressure.Normal };
}

public sealed class StaticVendorTelemetryProbe(string vendor, params VendorTelemetryReading[] readings) : IVendorTelemetryProbe
{
    public string Vendor { get; } = vendor;
    public ValueTask<ImmutableArray<VendorTelemetryReading>> ReadAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(readings.ToImmutableArray()); }
}

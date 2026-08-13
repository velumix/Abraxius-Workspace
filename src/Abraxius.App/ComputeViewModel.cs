using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Abraxius.Compute;

namespace Abraxius.App;

public sealed record ComputeDeviceRow(string Id, string Name, string Kind, string Memory, string Utilization, string Temperature, string Power, string Pressure, string Telemetry);
public sealed record ComputeModelRow(string Id, string Name, string Revision, string Variant, string Size, string State, string Backends, string Capabilities);
public sealed record ComputeWorkloadRow(string Id, string Purpose, string Priority, string Resources, string State);
public sealed record ComputeBackendRow(string Id, string Version, string Health, string Devices, string Features);

public sealed class ComputeViewModel : INotifyPropertyChanged
{
    private readonly ComputeRuntime _runtime;
    private readonly IUiDispatcher _dispatcher;
    private string _status = "COMPUTE DISCOVERING";
    private string _overview = "Inventory refresh is asynchronous.";
    public ComputeViewModel(ComputeRuntime runtime, IUiDispatcher dispatcher) { _runtime = runtime; _dispatcher = dispatcher; RefreshCommand = new AsyncRelayCommand(RefreshAsync); UnloadCommand = new AsyncRelayCommand(UnloadSelectedAsync); }
    public ObservableCollection<ComputeDeviceRow> Devices { get; } = [];
    public ObservableCollection<ComputeModelRow> Models { get; } = [];
    public ObservableCollection<ComputeWorkloadRow> Workloads { get; } = [];
    public ObservableCollection<ComputeBackendRow> Backends { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand UnloadCommand { get; }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; OnPropertyChanged(); } }
    public string Overview { get => _overview; private set { if (_overview == value) return; _overview = value; OnPropertyChanged(); } }
    public ComputeModelRow? SelectedModel { get; set; }

    public async Task RefreshAsync()
    {
        Status = "REFRESHING";
        try
        {
            await _runtime.RefreshAsync().ConfigureAwait(false); var snapshot = _runtime.Telemetry.Latest;
            var devices = _runtime.Devices.Current.Select(device =>
            {
                var state = snapshot?.Find(device.Id); return new ComputeDeviceRow(device.Id.Value, $"{device.Vendor} {device.Model}", device.DeviceClass.ToString(), Memory(device, state), Percent(state?.Utilization), Value(state?.TemperatureCelsius, "°C"), Value(state?.PowerWatts, " W"), state?.Pressure.ToString() ?? "Unknown", device.Telemetry.ToString());
            }).ToArray();
            var residents = _runtime.Residency.Instances.ToDictionary(static value => value.VariantId);
            var models = _runtime.Models.Variants.Select(model => new ComputeModelRow(model.Id.Value, model.DisplayName, model.Revision.Value, $"{model.Format} · {model.Quantization}", Bytes(model.FileSizeBytes), residents.GetValueOrDefault(model.Id)?.State.ToString() ?? "Installed", string.Join(", ", model.CompatibleBackends.Select(static value => value.Value)), string.Join(", ", model.ValidatedCapabilities.Count > 0 ? model.ValidatedCapabilities : model.ClaimedCapabilities))).ToArray();
            var workloads = _runtime.Governor.Reservations.Where(static value => value.State is ReservationState.Granted or ReservationState.Active or ReservationState.Pending).Select(value => new ComputeWorkloadRow(value.Id.ToString(), value.Request.Purpose, value.Request.Priority.ToString(), $"RAM {Bytes(value.Request.RamBytes)} · device {Bytes(value.Request.DeviceMemoryBytes.Values.Sum())}", value.State.ToString())).ToArray();
            var backends = _runtime.Backends.Select(value => new ComputeBackendRow(value.Descriptor.Id.Value, value.Descriptor.Version, value.Descriptor.Health.ToString(), string.Join(", ", value.Descriptor.DeviceClasses), $"stream {value.Descriptor.Streaming} · tools {value.Descriptor.ToolCalling} · embed {value.Descriptor.Embeddings}")).ToArray();
            _dispatcher.Post(() =>
            {
                Replace(Devices, devices); Replace(Models, models); Replace(Workloads, workloads); Replace(Backends, backends);
                Status = $"{devices.Length} DEVICES · {models.Length} MODELS · {residents.Count} RESIDENT";
                Overview = $"RAM {Bytes(snapshot?.RamAvailableBytes)} available / {Bytes(snapshot?.RamTotalBytes)} · pressure {snapshot?.RamPressure.ToString() ?? "Unknown"} · reservations {workloads.Length}. Unknown telemetry remains unknown.";
            });
        }
        catch (Exception exception) { _dispatcher.Post(() => Status = $"UNAVAILABLE · {exception.GetType().Name}"); }
    }

    private async Task UnloadSelectedAsync()
    {
        if (SelectedModel is null) return; await _runtime.Residency.UnloadAsync(new(SelectedModel.Id)).ConfigureAwait(false); await RefreshAsync().ConfigureAwait(false);
    }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source) { target.Clear(); foreach (var item in source) target.Add(item); }
    private static string Memory(ComputeDevice device, DeviceResourceState? state) => $"{Bytes(state?.MemoryUsedBytes)} / {Bytes(state?.MemoryBudgetBytes ?? device.DedicatedMemoryBytes ?? device.SharedMemoryBytes)} · {device.MemoryArchitecture}";
    private static string Bytes(long? bytes) => !bytes.HasValue ? "unknown" : bytes.Value >= 1L << 30 ? $"{bytes.Value / (double)(1L << 30):F1} GiB" : $"{bytes.Value / (double)(1L << 20):F0} MiB";
    private static string Percent(double? value) => value.HasValue ? $"{value:P0}" : "unknown";
    private static string Value(double? value, string suffix) => value.HasValue ? $"{value:F1}{suffix}" : "unknown";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}

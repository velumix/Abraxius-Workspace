using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Abraxius.Fabric;

namespace Abraxius.App;

public sealed record FabricNodeRow(
    FabricNodeId Id,
    string Name,
    string Health,
    string Trust,
    string Platform,
    string Roles,
    string Resources,
    string Capabilities,
    string Connectivity);

public sealed class FabricViewModel : INotifyPropertyChanged
{
    private readonly FabricRuntime _fabric;
    private readonly IUiDispatcher _dispatcher;
    private FabricNodeRow? _selected;
    private string _status = "FABRIC READY";

    public FabricViewModel(FabricRuntime fabric, IUiDispatcher dispatcher)
    {
        _fabric = fabric; _dispatcher = dispatcher;
        RefreshCommand = new RelayCommand(_ => Refresh());
        DrainCommand = new RelayCommand(_ => { if (Selected is { } node) _fabric.Drain(node.Id); Refresh(); });
        ResumeCommand = new RelayCommand(_ => { if (Selected is { } node) _fabric.Resume(node.Id); Refresh(); });
        Refresh();
    }

    public ObservableCollection<FabricNodeRow> Nodes { get; } = [];
    public ICommand RefreshCommand { get; }
    public ICommand DrainCommand { get; }
    public ICommand ResumeCommand { get; }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; OnPropertyChanged(); } }
    public FabricNodeRow? Selected { get => _selected; set { if (Equals(_selected, value)) return; _selected = value; OnPropertyChanged(); OnPropertyChanged(nameof(Inspector)); } }
    public string Inspector => Selected is null
        ? "Select a node to inspect its authenticated identity, protocol, capabilities, and current resource snapshot."
        : $"Node {Selected.Id}\nTrust {Selected.Trust}\nConnectivity {Selected.Connectivity}\nRoles {Selected.Roles}\nCapabilities {Selected.Capabilities}\nResources {Selected.Resources}";

    public void Refresh()
    {
        var snapshot = _fabric.Nodes.OrderByDescending(node => node.Id == _fabric.LocalNode.Id).ThenBy(node => node.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(node => new FabricNodeRow(node.Id, node.DisplayName, node.Health.ToString(), node.TrustState.ToString(), $"{node.Platform} · {node.Architecture}", node.Roles.ToString(),
                $"CPU {node.Resources.CpuUtilization:P0} · RAM {Format(node.Resources.FreeRamBytes)} free · GPU {node.Resources.Gpus.Length}",
                node.Capabilities.Length == 0 ? "No execution capabilities" : string.Join(", ", node.Capabilities.Select(capability => capability.Id)),
                node.Connectivity.ToString())).ToArray();
        _dispatcher.Post(() =>
        {
            var selectedId = Selected?.Id; Nodes.Clear(); foreach (var node in snapshot) Nodes.Add(node);
            Selected = selectedId is { } id ? Nodes.FirstOrDefault(node => node.Id == id) : Nodes.FirstOrDefault();
            Status = $"{Nodes.Count} NODE{(Nodes.Count == 1 ? string.Empty : "S")} · {Nodes.Count(node => node.Connectivity == FabricConnectivity.Connected.ToString())} ONLINE · EPOCH {_fabric.Epoch}";
            OnPropertyChanged(nameof(Nodes)); OnPropertyChanged(nameof(Inspector));
        });
    }

    private static string Format(long bytes) => bytes <= 0 ? "unknown" : $"{bytes / 1024d / 1024d / 1024d:F1} GiB";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Abraxius.Plugin.Contracts;
using Abraxius.Plugins;

namespace Abraxius.App;

public sealed record ExtensionRow(string Id, string Name, string Version, string Publisher, string Trust, string State, string Health, string Permissions, string Contributions, string PackageHash, string Sandbox, string? Error);

public sealed class ExtensionsViewModel : INotifyPropertyChanged
{
    private readonly PluginRuntime _runtime; private readonly IUiDispatcher _dispatcher; private ExtensionRow? _selected; private string _status = "READY";
    public ExtensionsViewModel(PluginRuntime runtime, IUiDispatcher dispatcher) { _runtime = runtime; _dispatcher = dispatcher; RefreshCommand = new AsyncRelayCommand(RefreshAsync); EnableCommand = new AsyncRelayCommand(EnableAsync); DisableCommand = new AsyncRelayCommand(DisableAsync); RestartCommand = new AsyncRelayCommand(RestartAsync); }
    public ObservableCollection<ExtensionRow> Installed { get; } = [];
    public ExtensionRow? Selected { get => _selected; set { if (Equals(_selected, value)) return; _selected = value; OnPropertyChanged(); OnPropertyChanged(nameof(SelectedDetails)); } }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; OnPropertyChanged(); } }
    public string Summary => $"{Installed.Count} installed · {Installed.Count(item => item.State == nameof(PluginLifecycleState.Running))} running · {Installed.Count(item => item.State == nameof(PluginLifecycleState.Quarantined))} quarantined";
    public string SelectedDetails => Selected is null ? "Select an extension to inspect exact package identity, authority, host health, and isolation." : $"Publisher {Selected.Publisher} · trust {Selected.Trust}\nPackage {Selected.PackageHash}\nPermissions {Selected.Permissions}\nContributions {Selected.Contributions}\nSandbox {Selected.Sandbox}\n{Selected.Error ?? "No recorded host error."}";
    public ICommand RefreshCommand { get; } public ICommand EnableCommand { get; } public ICommand DisableCommand { get; } public ICommand RestartCommand { get; }
    public async Task RefreshAsync()
    {
        var rows = await Task.Run(() => _runtime.List().Select(item => new ExtensionRow(item.Package.PluginId.Value, item.Manifest.Name, item.Package.Version.ToString(), item.Manifest.Publisher, item.PublisherTrust.ToString(), item.State.ToString(), item.Health.ToString(), string.Join(", ", item.Manifest.Permissions.Select(static permission => permission.Id)), $"{item.Manifest.Contributions.Length} declared / {_runtime.Contributions.Contributions.Count(value => value.PluginId == item.Package.PluginId && value.PluginVersion == item.Package.Version)} active", item.Package.Sha256, item.Sandbox.ToString(), item.LastError)).ToArray()).ConfigureAwait(false);
        _dispatcher.Post(() => { Installed.Clear(); foreach (var row in rows) Installed.Add(row); Status = "CURRENT"; OnPropertyChanged(nameof(Summary)); });
    }
    private async Task EnableAsync() { if (Selected is null) return; Status = "STARTING"; try { await _runtime.EnableAsync(new PluginId(Selected.Id), PluginVersion.Parse(Selected.Version)); } catch (Exception exception) { Status = $"FAILED · {exception.Message}"; } await RefreshAsync(); }
    private async Task DisableAsync() { if (Selected is null) return; Status = "STOPPING"; try { await _runtime.DisableAsync(new PluginId(Selected.Id), PluginVersion.Parse(Selected.Version)); } catch (Exception exception) { Status = $"FAILED · {exception.Message}"; } await RefreshAsync(); }
    private async Task RestartAsync() { if (Selected is null) return; await DisableAsync(); await EnableAsync(); }
    public event PropertyChangedEventHandler? PropertyChanged; private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

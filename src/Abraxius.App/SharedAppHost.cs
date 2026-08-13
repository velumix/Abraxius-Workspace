using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Abraxius.Distribution;
using Abraxius.Platform;
using Abraxius.Runtime;
using Abraxius.Voice;

namespace Abraxius.App;

/// <summary>Shared Avalonia application composition. Hosts decide how to attach the root view.</summary>
public partial class App : Application, IAsyncDisposable
{
    private AbraxiusRuntimeHost? _runtime;
    private MainViewModel? _viewModel;
    protected AbraxiusRuntimeHost? HostedRuntime => _runtime;
    protected MainViewModel? HostedViewModel => _viewModel;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var viewModel = CreateHostedViewModel(PlatformEnvironmentFactory.CreateCurrent());
            singleView.MainView = new MainView(viewModel);
            _ = viewModel.StartAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    public MainViewModel CreateHostedViewModel(
        IPlatformEnvironment environment,
        IProcessExecutionService? processService = null,
        IAudioCaptureService? audioCapture = null,
        IAudioPlaybackService? audioPlayback = null,
        IUpdateService? updateService = null,
        IUpdateCoordinator? updateCoordinator = null)
    {
        _runtime = AbraxiusRuntimeHost.CreateDefault(new RuntimeHostOptions(
            UseFileEvidence: false,
            UseFileLedger: false));
        _viewModel = new MainViewModel(_runtime, environment, processService, dispatcher: null, audioCapture: audioCapture, audioPlayback: audioPlayback, updateService: updateService, updateCoordinator: updateCoordinator, ownsRuntime: false);
        return _viewModel;
    }

    public async ValueTask DisposeSharedAsync()
    {
        var viewModel = Interlocked.Exchange(ref _viewModel, null);
        var runtime = Interlocked.Exchange(ref _runtime, null);
        if (viewModel is not null) await viewModel.DisposeAsync().ConfigureAwait(false);
        if (runtime is not null) await runtime.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSharedAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

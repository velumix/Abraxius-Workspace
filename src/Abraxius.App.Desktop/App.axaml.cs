using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Abraxius.Distribution;
using Abraxius.Distribution.Desktop;
using Abraxius.Platform;
using Abraxius.Presence;

namespace Abraxius.App.Desktop;

public sealed class DesktopApp : Abraxius.App.App
{
    private readonly StartupHealthMarker _startupHealth = new(StartupHealthMarker.DefaultPath());
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _window;
    private AvaloniaTrayService? _tray;
    private bool _explicitQuit;
    private int _shutdownStarted;

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _startupHealth.BeginStartup();
            var updateService = new VelopackUpdateService();
            var safeMode = Environment.GetEnvironmentVariable("ABRAXIUS_SAFE_MODE") == "1";
            var processService = new Abraxius.Platform.Desktop.DesktopProcessExecutionService();
            var viewModel = CreateHostedViewModel(
                PlatformEnvironmentFactory.CreateCurrent(), processService,
                !safeMode && OperatingSystem.IsLinux() ? new Abraxius.Platform.Desktop.PulseAudioCaptureService() : null,
                !safeMode && OperatingSystem.IsLinux() ? new Abraxius.Platform.Desktop.PulseAudioPlaybackService() : null,
                updateService);

            var runtime = HostedRuntime ?? throw new InvalidOperationException("Desktop runtime was not created.");
            var nativeNotifications = new DesktopNativeNotificationService();
            runtime.Presence.Configure(runtime.Presence.Settings, nativeNotifications, new DesktopNotificationPermissionService(nativeNotifications));
            runtime.Presence.Activation.Activated += (_, activation) => Dispatcher.UIThread.Post(() =>
            {
                RestoreWindow();
                viewModel.OpenPresenceTarget(activation);
            });

            if (!safeMode && updateService.InstallationKind == InstallationKind.AppImageManaged) _ = new LinuxInstallationIntegration().ReconcileAsync().AsTask();
            viewModel.ConfigureUpdateCoordinator(new UpdateCoordinator(updateService, [viewModel], shutdown: _ => QuitAsync()));

            _window = new MainWindow(viewModel);
            desktop.MainWindow = _window;
            _window.Closing += OnWindowClosing;
            _window.PropertyChanged += OnWindowPropertyChanged;
            _window.Activated += (_, _) => runtime.Presence.Background.SetWindowState(WindowPresenceState.VisibleFocused);
            _window.Deactivated += (_, _) => runtime.Presence.Background.SetWindowState(_window.IsVisible ? WindowPresenceState.VisibleUnfocused : WindowPresenceState.Hidden);
            _window.Opened += async (_, _) =>
            {
                try
                {
                    await viewModel.StartAsync().ConfigureAwait(false);
                    _startupHealth.MarkHealthy();
                    Dispatcher.UIThread.Post(() => Forget(InitializeTrayAsync(runtime)));
                }
                catch (Exception exception)
                {
                    DesktopDiagnostics.Write("RUNTIME_STARTUP_FAILURE", exception);
                    viewModel.ReportStatus($"STARTUP ERROR · {exception.GetType().Name}");
                    if (_window is not null) _window.Title = "ABRAXIUS — STARTUP ERROR";
                }
            };
            desktop.ShutdownRequested += OnShutdownRequested;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs args)
    {
        DesktopDiagnostics.Write("UI_THREAD_FAILURE", args.Exception);
        args.Handled = true;
        if (_window is not null) _window.Title = "ABRAXIUS — UI ERROR RECOVERED";
    }

    private async Task InitializeTrayAsync(Abraxius.Runtime.AbraxiusRuntimeHost runtime)
    {
        _tray = new AvaloniaTrayService(
            RestoreWindow,
            () => Forget(runtime.Presence.Activation.RouteAsync(new ActivationRequest(Abraxius.Presence.ActivationKind.TrayOpen, Target: new NotificationTarget(Surface: "needs-you"))).AsTask()),
            () => runtime.Presence.Background.SetMode(runtime.Presence.Background.Snapshot.BackgroundMode is BackgroundExecutionMode.PauseNonCritical or BackgroundExecutionMode.PauseAll ? BackgroundExecutionMode.ContinueNormally : BackgroundExecutionMode.PauseNonCritical),
            ConfirmQuitAsync);
        await _tray.InitializeAsync().ConfigureAwait(true);
        runtime.Presence.Background.Changed += (_, snapshot) => Dispatcher.UIThread.Post(() => Forget(_tray.SetStateAsync(snapshot.Tray).AsTask()));
        await _tray.SetStateAsync(runtime.Presence.Background.Snapshot.Tray).ConfigureAwait(true);
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_explicitQuit || HostedRuntime is null) return;
        var behavior = HostedRuntime.Presence.Settings.CloseButton;
        if (behavior is CloseButtonBehavior.Quit or CloseButtonBehavior.Ask) { e.Cancel = true; Forget(ConfirmQuitAsync().AsTask()); return; }
        e.Cancel = true;
        _window?.Hide();
        HostedRuntime.Presence.Background.SetWindowState(WindowPresenceState.Hidden);
    }

    private void RestoreWindow()
    {
        if (_window is null) return;
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
        HostedRuntime?.Presence.Background.SetWindowState(WindowPresenceState.VisibleFocused);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Window.WindowStateProperty || _window?.WindowState != WindowState.Minimized || HostedRuntime?.Presence.Settings.Minimize != MinimizeBehavior.Tray) return;
        _window.Hide();
        HostedRuntime.Presence.Background.SetWindowState(WindowPresenceState.Hidden);
    }

    private async ValueTask QuitAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0) return;
        _explicitQuit = true;
        if (HostedRuntime is { } runtime) await runtime.Presence.Background.CheckpointAsync("Explicit quit").ConfigureAwait(false);
        if (_tray is not null) await _tray.DisposeAsync().ConfigureAwait(false);
        await DisposeSharedAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => _desktop?.Shutdown());
    }

    private async ValueTask ConfirmQuitAsync()
    {
        if (_window is null || HostedRuntime is null) { await QuitAsync().ConfigureAwait(false); return; }
        var active = HostedRuntime.Presence.Background.Snapshot.ActiveMissionCount;
        if (active == 0 && HostedRuntime.Presence.Settings.CloseButton != CloseButtonBehavior.Ask) { await QuitAsync().ConfigureAwait(false); return; }
        var choice = await ShowQuitPromptAsync(active).ConfigureAwait(true);
        if (choice == QuitChoice.KeepRunning) return;
        if (choice == QuitChoice.CancelAndQuit) HostedRuntime.CancelActiveExecution();
        await QuitAsync().ConfigureAwait(false);
    }

    private async Task<QuitChoice> ShowQuitPromptAsync(int active)
    {
        if (_window is null) return QuitChoice.KeepRunning;
        var choice = QuitChoice.KeepRunning;
        var dialog = new Window
        {
            Title = "Quit Abraxius?", Width = 460, Height = 220, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var keep = new Button { Content = "Keep Running in Tray" };
        var cancel = new Button { Content = "Quit and Cancel" };
        var checkpoint = new Button { Content = "Quit after Checkpoint" };
        keep.Click += (_, _) => { choice = QuitChoice.KeepRunning; dialog.Close(); };
        cancel.Click += (_, _) => { choice = QuitChoice.CancelAndQuit; dialog.Close(); };
        checkpoint.Click += (_, _) => { choice = QuitChoice.CheckpointAndQuit; dialog.Close(); };
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20), Spacing = 16,
            Children =
            {
                new TextBlock { Text = active > 0 ? $"{active} mission(s) are still running." : "Choose how Abraxius should close.", FontSize = 18 },
                new TextBlock { Text = "Keeping Abraxius in the tray preserves the runtime. Quit performs a clean checkpoint and shutdown.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { keep, cancel, checkpoint } }
            }
        };
        await dialog.ShowDialog(_window).ConfigureAwait(true);
        return choice;
    }

    private async void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_explicitQuit) return;
        e.Cancel = true;
        await QuitAsync().ConfigureAwait(false);
    }

    private static void Forget(Task task) => _ = task;
    private enum QuitChoice { KeepRunning, CancelAndQuit, CheckpointAndQuit }
}

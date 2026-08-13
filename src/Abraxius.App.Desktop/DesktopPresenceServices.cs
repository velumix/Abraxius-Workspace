using System.Buffers.Binary;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Abraxius.Presence;

namespace Abraxius.App.Desktop;

public sealed class AvaloniaTrayService(
    Action open,
    Action openNeedsYou,
    Action togglePause,
    Func<ValueTask> quit) : ITrayService
{
    private TrayIcon? _icon;
    private NativeMenuItem? _status;
    private NativeMenuItem? _attention;
    private NativeMenuItem? _pause;
    private bool _supported;
    private TrayPresentationState? _state;

    public bool IsSupported => _supported;

    public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dispatcher.UIThread.VerifyAccess();
        if (Application.Current is null) return ValueTask.CompletedTask;
        try
        {
            var menu = BuildMenu();
            _icon = new TrayIcon
            {
                Icon = TrayGlyphFactory.Create(TrayRuntimeState.Idle),
                ToolTipText = "Abraxius\nIdle",
                Menu = menu,
                IsVisible = true
            };
            _icon.Clicked += (_, _) => open();
            TrayIcon.SetIcons(Application.Current, new TrayIcons { _icon });
            _supported = true;
        }
        catch (InvalidOperationException) { _supported = false; }
        catch (NotSupportedException) { _supported = false; }
        return ValueTask.CompletedTask;
    }

    public ValueTask SetStateAsync(TrayPresentationState state, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _state = state;
        if (!_supported || _icon is null) return ValueTask.CompletedTask;
        Dispatcher.UIThread.Post(() =>
        {
            if (_icon is null) return;
            _icon.ToolTipText = state.Tooltip;
            _icon.Icon = TrayGlyphFactory.Create(state.RuntimeState);
            if (_status is not null) _status.Header = state.MissionCount == 1 ? "1 Mission Running" : $"{state.MissionCount} Missions Running";
            if (_attention is not null) { _attention.Header = $"Needs You ({state.NeedsYouCount})"; _attention.IsEnabled = state.NeedsYouCount > 0; }
            if (_pause is not null) _pause.Header = state.BackgroundWorkCount == 0 && state.MissionCount > 0 ? "Resume Background Work" : "Pause Background Work";
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask ShowMenuAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }

    private NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        _status = new NativeMenuItem("No Missions Running") { IsEnabled = false };
        _attention = new NativeMenuItem("Needs You (0)") { IsEnabled = false };
        _attention.Click += (_, _) => openNeedsYou();
        var openItem = new NativeMenuItem("Open Abraxius"); openItem.Click += (_, _) => open();
        _pause = new NativeMenuItem("Pause Background Work"); _pause.Click += (_, _) => togglePause();
        var quitItem = new NativeMenuItem("Quit Abraxius"); quitItem.Click += (_, _) => _ = quit().AsTask();
        menu.Items.Add(_status); menu.Items.Add(_attention); menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(openItem); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(_pause);
        menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quitItem);
        return menu;
    }

    public ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.Post(() => { _icon?.Dispose(); _icon = null; _supported = false; });
        return ValueTask.CompletedTask;
    }

    private static class TrayGlyphFactory
    {
        public static WindowIcon Create(TrayRuntimeState state)
        {
            const int size = 16;
            const int xorBytes = size * size * 4;
            const int maskBytes = size * 4;
            var bytes = new byte[22 + 40 + xorBytes + maskBytes];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 1);
            bytes[6] = size; bytes[7] = size;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(14), (uint)(40 + xorBytes + maskBytes));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(18), 22);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(22), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(26), size);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(30), size * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(36), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(42), xorBytes);
            var accent = state switch { TrayRuntimeState.AttentionRequired => (B: (byte)70, G: (byte)150, R: (byte)230), TrayRuntimeState.Error => (B: (byte)70, G: (byte)70, R: (byte)220), TrayRuntimeState.UpdateReady => (B: (byte)200, G: (byte)160, R: (byte)80), TrayRuntimeState.Degraded => (B: (byte)110, G: (byte)110, R: (byte)110), _ => (B: (byte)215, G: (byte)215, R: (byte)215) };
            for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
            {
                var diamond = Math.Abs(x - 7.5) + Math.Abs(y - 7.5) <= 6.5 && Math.Abs(x - 7.5) + Math.Abs(y - 7.5) >= 3.5;
                var dot = state == TrayRuntimeState.Working && x >= 11 && y >= 11 || state is TrayRuntimeState.AttentionRequired or TrayRuntimeState.Error or TrayRuntimeState.UpdateReady && x >= 10 && y >= 10;
                if (!diamond && !dot) continue;
                var index = 62 + ((size - 1 - y) * size + x) * 4;
                bytes[index] = accent.B; bytes[index + 1] = accent.G; bytes[index + 2] = accent.R; bytes[index + 3] = 255;
            }
            return new WindowIcon(new MemoryStream(bytes, writable: false));
        }
    }
}

public sealed class DesktopNativeNotificationService : INativeNotificationService
{
    public bool IsAvailable => OperatingSystem.IsLinux() ? IsExecutableOnPath("notify-send") : OperatingSystem.IsMacOS() && IsExecutableOnPath("osascript");

    public async ValueTask<bool> DeliverAsync(AbraxiusNotification notification, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return false;
        var start = new ProcessStartInfo { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        if (OperatingSystem.IsLinux())
        {
            start.FileName = "notify-send"; start.ArgumentList.Add("--app-name=Abraxius"); start.ArgumentList.Add(notification.Title); start.ArgumentList.Add(notification.Body);
        }
        else
        {
            start.FileName = "osascript"; start.ArgumentList.Add("-e"); start.ArgumentList.Add($"display notification {QuoteApple(notification.Body)} with title {QuoteApple(notification.Title)}");
        }
        try
        {
            using var process = Process.Start(start); if (process is null) return false;
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    private static string QuoteApple(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static bool IsExecutableOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        return !string.IsNullOrWhiteSpace(path) && path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Any(directory => File.Exists(Path.Combine(directory, name)));
    }
}

public sealed class DesktopNotificationPermissionService(INativeNotificationService notifications) : INotificationPermissionService
{
    public ValueTask<NotificationPermissionState> GetStateAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(notifications.IsAvailable ? NotificationPermissionState.Granted : NotificationPermissionState.Unavailable); }
    public ValueTask<NotificationPermissionState> RequestAsync(CancellationToken cancellationToken = default) => GetStateAsync(cancellationToken);
}

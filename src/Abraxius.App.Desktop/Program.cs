using Avalonia;
using Velopack;

namespace Abraxius.App.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        DesktopDiagnostics.Install();
        if (args.Any(static argument => string.Equals(argument, "--safe-mode", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.SetEnvironmentVariable("ABRAXIUS_SAFE_MODE", "1");
        }

        try
        {
            // Handles Velopack startup apply/cleanup. In developer builds this is a no-op.
            VelopackApp.Build().Run();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            DesktopDiagnostics.Write("DESKTOP_HOST_FAILURE", exception);
            Environment.ExitCode = 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DesktopApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

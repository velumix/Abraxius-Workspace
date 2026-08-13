using Avalonia;
using Avalonia.Browser;

namespace Abraxius.App.Browser;

public static class Program
{
    public static Task Main(string[] args) =>
        BrowserAppBuilder.StartBrowserAppAsync(BuildAvaloniaApp(), "out", new BrowserPlatformOptions());

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Abraxius.App.App>()
            .LogToTrace();
}

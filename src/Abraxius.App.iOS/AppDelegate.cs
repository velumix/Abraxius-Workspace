using Avalonia;
using Avalonia.iOS;
using Foundation;

namespace Abraxius.App.iOS;

[Register("AppDelegate")]
public sealed class AppDelegate : AvaloniaAppDelegate<Abraxius.App.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).LogToTrace();
}

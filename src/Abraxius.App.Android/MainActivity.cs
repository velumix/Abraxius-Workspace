using Android.App;
using Android.Content.PM;
using Avalonia;
using Avalonia.Android;

namespace Abraxius.App.Android;

[Activity(
    Label = "Abraxius",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public sealed class MainActivity : AvaloniaMainActivity<Abraxius.App.App>
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont().LogToTrace();
}

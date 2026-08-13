using Abraxius.Platform;

namespace Abraxius.App.Embedded;

/// <summary>Embedded Linux host seam. Framebuffer/DRM startup is supplied by the deployment image.
/// This host keeps platform discovery and UI composition independent of a desktop window manager.</summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var environment = PlatformEnvironmentFactory.CreateCurrent();
        Console.WriteLine($"Abraxius embedded host: {environment.Platform.Family} {environment.Platform.Architecture}");
        Console.WriteLine($"Execution mode: {environment.ExecutionMode}; local capabilities: {environment.Capabilities.Values.Count}");
        return environment.Platform.Family == PlatformFamily.EmbeddedLinux ? 0 : 2;
    }
}

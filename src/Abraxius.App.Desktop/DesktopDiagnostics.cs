using System.Text;

namespace Abraxius.App.Desktop;

/// <summary>
/// Crash-safe, local-only diagnostics for the desktop host. This is deliberately
/// independent of the runtime so startup failures can be recorded before the
/// runtime has finished constructing.
/// </summary>
internal static class DesktopDiagnostics
{
    private static readonly object Gate = new();

    public static string LogPath
    {
        get
        {
            var stateRoot = Environment.GetEnvironmentVariable("XDG_STATE_HOME")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "state");
            return Path.Combine(stateRoot, "Abraxius", "desktop.log");
        }
    }

    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Write("UNHANDLED_EXCEPTION", args.ExceptionObject as Exception, args.IsTerminating ? "terminating=true" : "terminating=false");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Write("UNOBSERVED_TASK_EXCEPTION", args.Exception, "observed=true");
            args.SetObserved();
        };
    }

    public static void Write(string category, Exception? exception = null, string? detail = null)
    {
        try
        {
            var builder = new StringBuilder()
                .Append(DateTimeOffset.UtcNow.ToString("O"))
                .Append(' ').Append('[').Append(category).Append(']');
            if (!string.IsNullOrWhiteSpace(detail)) builder.Append(' ').Append(detail);
            if (exception is not null)
            {
                builder.AppendLine()
                    .Append(exception.GetType().FullName).Append(':').Append(' ').Append(exception.Message)
                    .AppendLine()
                    .Append(exception.StackTrace);
            }

            lock (Gate)
            {
                var directory = Path.GetDirectoryName(LogPath);
                if (string.IsNullOrWhiteSpace(directory)) return;
                Directory.CreateDirectory(directory);
                File.AppendAllText(LogPath, builder.AppendLine().AppendLine().ToString());
            }
        }
        catch
        {
            // Diagnostics must never become a second failure during shutdown/startup.
        }
    }
}

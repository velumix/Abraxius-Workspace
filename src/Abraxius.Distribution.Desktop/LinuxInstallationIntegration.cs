using System.Text;
using Abraxius.Distribution;

namespace Abraxius.Distribution.Desktop;

public sealed record LinuxIntegrationOptions(
    string AppImagePath,
    string DesktopEntryPath,
    string IconName = "abraxius",
    string? IconSourcePath = null,
    string ApplicationName = "Abraxius",
    string ApplicationId = AbraxiusShortcutManifest.ApplicationId,
    string? Description = "AI-native execution workstation");

/// <summary>
/// User-level freedesktop integration for a managed AppImage. The AppImage itself remains the
/// stable launch target; the desktop entry is never pointed at a versioned download in Downloads.
/// </summary>
public sealed class LinuxInstallationIntegration : IPlatformInstallationIntegration
{
    private const string ManagedMarker = "X-Abraxius-Managed=true";
    private readonly LinuxIntegrationOptions _options;

    public LinuxInstallationIntegration(LinuxIntegrationOptions? options = null)
    {
        _options = options ?? CreateDefaultOptions();
    }

    public ValueTask<IntegrationState> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<string>();
        var launcherExists = File.Exists(_options.AppImagePath);
        var desktopExists = File.Exists(_options.DesktopEntryPath);
        if (!launcherExists)
        {
            issues.Add($"Managed AppImage is missing: {_options.AppImagePath}");
        }

        if (!desktopExists)
        {
            issues.Add($"Desktop entry is missing: {_options.DesktopEntryPath}");
        }
        else if (!ContainsManagedMarker(_options.DesktopEntryPath))
        {
            issues.Add("The existing desktop entry is not owned by Abraxius.");
        }

        return ValueTask.FromResult(new IntegrationState(
            InstallationKind.AppImageManaged,
            launcherExists,
            launcherExists && desktopExists && issues.Count == 0,
            _options.AppImagePath,
            _options.DesktopEntryPath,
            issues));
    }

    public async ValueTask<IntegrationState> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(_options.DesktopEntryPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new IntegrationState(InstallationKind.AppImageManaged, false, false, _options.AppImagePath, _options.DesktopEntryPath, ["Desktop entry path has no parent directory."]);
        }

        if (File.Exists(_options.DesktopEntryPath) && !ContainsManagedMarker(_options.DesktopEntryPath))
        {
            return new IntegrationState(InstallationKind.AppImageManaged, File.Exists(_options.AppImagePath), false, _options.AppImagePath, _options.DesktopEntryPath, ["Refusing to overwrite a desktop entry that is not owned by Abraxius."]);
        }

        Directory.CreateDirectory(directory);
        await WriteAtomicallyAsync(_options.DesktopEntryPath, BuildDesktopEntry(), cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(_options.IconSourcePath) && File.Exists(_options.IconSourcePath))
        {
            var iconDirectory = Path.Combine(
                Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share"),
                "icons", "hicolor", "256x256", "apps");
            Directory.CreateDirectory(iconDirectory);
            var iconTarget = Path.Combine(iconDirectory, $"{_options.IconName}{Path.GetExtension(_options.IconSourcePath)}");
            File.Copy(_options.IconSourcePath, iconTarget, overwrite: true);
        }

        return await InspectAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IntegrationState> RemoveAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var issues = new List<string>();
        if (File.Exists(_options.DesktopEntryPath))
        {
            if (ContainsManagedMarker(_options.DesktopEntryPath))
            {
                File.Delete(_options.DesktopEntryPath);
            }
            else
            {
                issues.Add("The existing desktop entry is not owned by Abraxius and was left untouched.");
            }
        }

        return ValueTask.FromResult(new IntegrationState(
            InstallationKind.AppImageManaged,
            File.Exists(_options.AppImagePath),
            issues.Count == 0,
            _options.AppImagePath,
            _options.DesktopEntryPath,
            issues));
    }

    public static LinuxIntegrationOptions CreateDefaultOptions()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
        var applicationData = Path.Combine(dataHome, "Abraxius");
        return new LinuxIntegrationOptions(
            Path.Combine(applicationData, "Abraxius.AppImage"),
            Path.Combine(dataHome, "applications", "com.abraxius.Abraxius.desktop"),
            IconSourcePath: null);
    }

    private string BuildDesktopEntry()
    {
        var icon = string.IsNullOrWhiteSpace(_options.IconSourcePath)
            ? _options.IconName
            : Path.GetFileNameWithoutExtension(_options.IconSourcePath);
        var builder = new StringBuilder();
        builder.AppendLine("[Desktop Entry]");
        builder.AppendLine("Type=Application");
        builder.Append("Name=").Append(EscapeValue(_options.ApplicationName)).AppendLine();
        builder.Append("Comment=").Append(EscapeValue(_options.Description ?? string.Empty)).AppendLine();
        builder.Append("Exec=\"").Append(EscapeExecArgument(_options.AppImagePath)).AppendLine("\" %U");
        builder.Append("Icon=").Append(EscapeValue(icon)).AppendLine();
        builder.AppendLine("Terminal=false");
        builder.AppendLine("Categories=Development;Utility;");
        builder.Append("MimeType=x-scheme-handler/").Append(AbraxiusShortcutManifest.UriScheme).AppendLine(";");
        builder.Append("X-Application-ID=").Append(EscapeValue(_options.ApplicationId)).AppendLine();
        builder.AppendLine(ManagedMarker);
        return builder.ToString();
    }

    private static string EscapeValue(string value) => value.Replace("\n", " ", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal);

    private static string EscapeExecArgument(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("`", "\\`", StringComparison.Ordinal)
        .Replace("$", "\\$", StringComparison.Ordinal);

    private static bool ContainsManagedMarker(string path) =>
        File.ReadLines(path).Any(static line => line.Trim().Equals(ManagedMarker, StringComparison.Ordinal));

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = $"{path}.{Environment.ProcessId}.tmp";
        await File.WriteAllTextAsync(temporary, content, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }
}

public sealed class VelopackInstallationIntegration : IPlatformInstallationIntegration
{
    private readonly VelopackUpdateService _updates;

    public VelopackInstallationIntegration(VelopackUpdateService updates)
    {
        _updates = updates;
    }

    public ValueTask<IntegrationState> InspectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var installed = _updates.IsSupported;
        return ValueTask.FromResult(new IntegrationState(
            _updates.InstallationKind,
            installed,
            installed,
            Environment.ProcessPath,
            null,
            installed ? [] : ["This build is not a managed Velopack installation."]));
    }

    public ValueTask<IntegrationState> ReconcileAsync(CancellationToken cancellationToken = default) => InspectAsync(cancellationToken);

    public ValueTask<IntegrationState> RemoveAsync(CancellationToken cancellationToken = default) => InspectAsync(cancellationToken);
}

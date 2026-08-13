using Abraxius.Distribution;
using Abraxius.Distribution.Desktop;
using System.Globalization;
using Xunit;

namespace Abraxius.Distribution.Tests;

public sealed class DistributionTests
{
    [Fact]
    public void SemanticVersionsRejectDowngradesAndOrderPrereleases()
    {
        Assert.True(SemanticVersionComparer.Compare("1.1.0", "1.0.9") > 0);
        Assert.True(SemanticVersionComparer.Compare("1.0.0", "1.0.0-rc.1") > 0);
        Assert.True(SemanticVersionComparer.Compare("1.0.0-beta.2", "1.0.0-beta.10") < 0);
        Assert.Equal(0, SemanticVersionComparer.Compare("1.2.3+build-a", "1.2.3+build-b"));
    }

    [Fact]
    public async Task InMemoryUpdateServiceModelsCheckDownloadAndApply()
    {
        var update = new UpdateInfo("test-update", "0.2.0", UpdateChannel.Stable, ReleaseSeverity.Recommended, "Fixes", 1024, "abc", "Abraxius.nupkg");
        await using var service = new InMemoryUpdateService(update, new BuildInfo("Abraxius", "0.1.0", "0.1.0+test", "test", UpdateChannel.Stable, null, "Abraxius", "velumix/Abraxius"));

        var check = await service.CheckAsync();
        Assert.Equal(UpdateState.Available, check.State);
        var download = await service.DownloadAsync(update);
        Assert.True(download.IsReady);
        var apply = await service.ApplyAsync(update, UpdateApplyMode.NotifyOnly);

        Assert.Equal(UpdateState.RestartRequired, apply.State);
        Assert.Null(apply.Error);
    }

    [Fact]
    public void ReleaseManifestRoundTripsWithMachineReadableEnums()
    {
        var manifest = new ReleaseManifest(
            "1.2.0",
            UpdateChannel.Stable,
            "1.4",
            DateTimeOffset.Parse("2026-08-12T12:00:00Z", CultureInfo.InvariantCulture),
            ReleaseSeverity.Normal,
            new Dictionary<string, ReleasePlatformManifest>
            {
                ["linux-x64"] = new("linux-x64", MinimumOsVersion: "glibc-2.31", PackageSha256: "hash")
            },
            ["Fast startup"]);

        var restored = ReleaseManifestJson.Deserialize(ReleaseManifestJson.Serialize(manifest));

        Assert.Equal(manifest.Version, restored.Version);
        Assert.Equal(UpdateChannel.Stable, restored.Channel);
        Assert.Equal("hash", restored.Platforms["linux-x64"].PackageSha256);
    }

    [Fact]
    public async Task LinuxIntegrationWritesStableManagedLauncherAndIsIdempotent()
    {
        var root = Path.Combine(Path.GetTempPath(), "abraxius-distribution-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var appImage = Path.Combine(root, "Abraxius.AppImage");
            var desktopEntry = Path.Combine(root, "applications", "abraxius.desktop");
            await File.WriteAllTextAsync(appImage, "test appimage");
            var integration = new LinuxInstallationIntegration(new LinuxIntegrationOptions(appImage, desktopEntry));

            var first = await integration.ReconcileAsync();
            var second = await integration.ReconcileAsync();
            var content = await File.ReadAllTextAsync(desktopEntry);

            Assert.True(first.IsHealthy);
            Assert.True(second.IsHealthy);
            Assert.Contains("X-Abraxius-Managed=true", content, StringComparison.Ordinal);
            Assert.Contains($"Exec=\"{appImage}\"", content, StringComparison.Ordinal);

            var removed = await integration.RemoveAsync();
            Assert.True(removed.IsHealthy);
            Assert.False(File.Exists(desktopEntry));
            Assert.True(File.Exists(appImage));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LinuxIntegrationDoesNotOverwriteUnownedDesktopEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), "abraxius-distribution-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var appImage = Path.Combine(root, "Abraxius.AppImage");
            var desktopEntry = Path.Combine(root, "abraxius.desktop");
            await File.WriteAllTextAsync(appImage, "test appimage");
            await File.WriteAllTextAsync(desktopEntry, "[Desktop Entry]\nName=User-owned\n");
            var integration = new LinuxInstallationIntegration(new LinuxIntegrationOptions(appImage, desktopEntry));

            var state = await integration.ReconcileAsync();

            Assert.False(state.IsHealthy);
            Assert.Contains(state.Issues, issue => issue.Contains("not owned", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("User-owned", await File.ReadAllTextAsync(desktopEntry), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void StartupHealthMarkerRequiresRecoveryAfterRepeatedUnhealthyStarts()
    {
        var root = Path.Combine(Path.GetTempPath(), "abraxius-distribution-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var marker = new StartupHealthMarker(Path.Combine(root, "startup-health.json"));
            var first = marker.BeginStartup();
            var second = marker.BeginStartup();

            Assert.False(first.RecoveryRequired);
            Assert.True(second.RecoveryRequired);
            Assert.False(marker.MarkHealthy().RecoveryRequired);
            Assert.False(marker.Current!.RecoveryRequired);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}

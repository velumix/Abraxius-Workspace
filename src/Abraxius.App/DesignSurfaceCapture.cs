using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Abraxius.Design;

namespace Abraxius.App;

/// <summary>
/// Captures the currently rendered workstation root for an explicitly live design request.
/// It never invents a bitmap for synthetic capture: fixture/offscreen rendering is a separate
/// provider boundary and an unavailable capture is reported honestly.
/// </summary>
internal static class AvaloniaDesignSurfaceCapture
{
    public static ValueTask<DesignSurfaceSnapshot> CaptureAsync(
        Control root,
        DesignSurfaceId surface,
        DesignCaptureRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Mode == DesignCaptureMode.SyntheticContent)
        {
            return ValueTask.FromResult(Unavailable(surface, request, "Synthetic capture is selected; no live user content was uploaded."));
        }

        var topLevel = TopLevel.GetTopLevel(root);
        if (topLevel is null || root.Bounds.Width <= 0 || root.Bounds.Height <= 0)
        {
            return ValueTask.FromResult(Unavailable(surface, request, "The surface is not attached to a rendered Avalonia root."));
        }

        try
        {
            var scaling = topLevel.RenderScaling;
            var pixelSize = new PixelSize(
                Math.Max(1, (int)Math.Ceiling(root.Bounds.Width * scaling)),
                Math.Max(1, (int)Math.Ceiling(root.Bounds.Height * scaling)));
            using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scaling, 96 * scaling));
            bitmap.Render(root);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            return ValueTask.FromResult(new DesignSurfaceSnapshot(
                surface,
                request,
                DesignCaptureStatus.Captured,
                stream.ToArray(),
                null,
                DateTimeOffset.UtcNow,
                $"live-root:{pixelSize.Width}x{pixelSize.Height}:requested:{request.Width}x{request.Height}"));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            return ValueTask.FromResult(Unavailable(surface, request, $"Avalonia could not render the current root: {exception.Message}"));
        }
    }

    private static DesignSurfaceSnapshot Unavailable(DesignSurfaceId surface, DesignCaptureRequest request, string reason) =>
        new(surface, request, DesignCaptureStatus.Unavailable, null, reason, DateTimeOffset.UtcNow, "unavailable");
}

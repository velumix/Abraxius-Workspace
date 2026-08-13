namespace Abraxius.Platform;

public enum ViewportClass
{
    Compact,
    Medium,
    Expanded,
    UltraWide
}

public sealed record ViewportProfile(
    ViewportClass Class,
    double Width,
    double Height,
    double Scale,
    bool TouchPrimary,
    bool ReducedMotion,
    bool Portrait)
{
    public static ViewportProfile From(
        double width,
        double height,
        double scale = 1,
        bool touchPrimary = false,
        bool reducedMotion = false)
    {
        var logicalWidth = width / Math.Max(0.1, scale);
        var @class = logicalWidth < 600 ? ViewportClass.Compact :
            logicalWidth < 1024 ? ViewportClass.Medium :
            logicalWidth < 1800 ? ViewportClass.Expanded : ViewportClass.UltraWide;
        return new ViewportProfile(@class, width, height, scale, touchPrimary, reducedMotion, height > width);
    }
}

public sealed record ResponsiveLayoutPolicy(
    bool ShowDesktopSidebars,
    bool UseBottomNavigation,
    bool UseCompactGraph,
    int EventRetention,
    int TargetRefreshRate)
{
    public static ResponsiveLayoutPolicy For(ViewportProfile viewport, PerformanceProfile performance)
    {
        var compact = viewport.Class == ViewportClass.Compact;
        var constrained = performance == PerformanceProfile.Efficiency || viewport.TouchPrimary;
        return new ResponsiveLayoutPolicy(
            !compact,
            compact,
            compact || constrained,
            compact ? 80 : 240,
            constrained ? 30 : 60);
    }
}

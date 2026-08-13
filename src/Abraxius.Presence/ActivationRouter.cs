using Abraxius.Agents;

namespace Abraxius.Presence;

public sealed class ActivationRouter : IActivationRouter
{
    private static readonly HashSet<string> RegisteredActions = new(StringComparer.Ordinal)
    {
        "app.open", "needs-you.review", "needs-you.approve", "needs-you.reject", "mission.open", "mission.retry", "update.review", "update.restart", "notification.dismiss"
    };

    public event EventHandler<ActivationResult>? Activated;

    public ValueTask<ActivationResult> RouteAsync(ActivationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = Validate(request);
        if (result.Accepted) Activated?.Invoke(this, result);
        return ValueTask.FromResult(result);
    }

    private static ActivationResult Validate(ActivationRequest request)
    {
        if (request.Action is { } action && !RegisteredActions.Contains(action.Value)) return new(false, string.Empty, Error: "Unknown notification action.");
        if (request.Target is { } typed) return new(true, ResolveSurface(typed), typed);
        if (request.Kind == ActivationKind.TrayOpen || string.IsNullOrWhiteSpace(request.Route)) return new(true, "home");
        if (!Uri.TryCreate(request.Route, UriKind.Absolute, out var uri) || !string.Equals(uri.Scheme, "abraxius", StringComparison.OrdinalIgnoreCase)) return new(false, string.Empty, Error: "Invalid activation route.");
        var segments = new[] { uri.Host }.Concat(uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (segments.Length == 2 && segments[0] == "needs-you" && Guid.TryParseExact(segments[1], "N", out var needs)) return new(true, "needs-you", new NotificationTarget(NeedsYouId: new NeedsYouId(needs)));
        if (segments.Length >= 2 && segments[0] == "mission" && Guid.TryParseExact(segments[1], "N", out var mission))
        {
            if (segments.Length == 4 && segments[2] == "assignment" && Guid.TryParseExact(segments[3], "N", out var assignment))
                return new(true, "mission", new NotificationTarget(new MissionId(mission), new AssignmentId(assignment)));
            if (segments.Length == 2) return new(true, "mission", new NotificationTarget(new MissionId(mission)));
        }
        if (segments.SequenceEqual(["settings", "updates"])) return new(true, "settings/updates");
        if (segments.SequenceEqual(["home"])) return new(true, "home");
        return new(false, string.Empty, Error: "Activation target is not registered.");
    }

    private static string ResolveSurface(NotificationTarget target) => target.NeedsYouId is not null ? "needs-you" : target.MissionId is not null ? "mission" : target.SkillId is not null ? "skills" : target.UpdateId is not null ? "settings/updates" : target.Surface ?? "home";
}

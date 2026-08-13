using System.Collections.Immutable;
using Abraxius.Agents;
using Abraxius.Protocol;
using Abraxius.Skills;

namespace Abraxius.Presence;

public readonly record struct NotificationId(Guid Value)
{
    public static NotificationId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct NeedsYouId(Guid Value)
{
    public static NeedsYouId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct NotificationActionId(string Value)
{
    public override string ToString() => Value;
}

public enum NotificationCategory { NeedsYou, Mission, Verification, Artifact, Background, System, Connection, Update, Security }
public enum NotificationSeverity { Informational, Completion, AttentionRequired, Warning, Critical }
public enum NotificationPrivacy { Full, Redacted, Hidden }
public enum NotificationDelivery { None, InApp, Native, NativeAndInApp, Critical }
public enum NotificationPermissionState { Unknown, NotRequested, Granted, Denied, Restricted, Unavailable }
public enum NeedsYouReason { ApprovalRequired, ArtifactReview, AmbiguousChoice, VerificationInconclusive, MissingCredential, ExternalPermissionRequired, ConflictingRequirements, BudgetApprovalRequired, SecurityDecision }
public enum NeedsYouState { Pending, Viewed, Resolved, Dismissed, Expired, Cancelled }
public enum NeedsYouResolution { Approved, Rejected, Acknowledged, Cancelled }
public enum BackgroundExecutionMode { ContinueNormally, ReduceBackgroundIntensity, PauseNonCritical, PauseAll }
public enum WindowPresenceState { VisibleFocused, VisibleUnfocused, Hidden, Unavailable }
public enum CloseButtonBehavior { HideToTray, Quit, Ask }
public enum MinimizeBehavior { Taskbar, Tray }
public enum TrayRuntimeState { Idle, Working, AttentionRequired, Error, UpdateReady, Degraded }

public sealed record NotificationTarget(
    MissionId? MissionId = null,
    AssignmentId? AssignmentId = null,
    TaskId? TaskId = null,
    EvidenceId? EvidenceId = null,
    SkillId? SkillId = null,
    NeedsYouId? NeedsYouId = null,
    string? UpdateId = null,
    string? Surface = null)
{
    public string ToSafeRoute()
    {
        if (NeedsYouId is { } needsYou) return $"abraxius://needs-you/{needsYou}";
        if (MissionId is { } mission && AssignmentId is { } assignment) return $"abraxius://mission/{mission}/assignment/{assignment}";
        if (MissionId is { } missionOnly) return $"abraxius://mission/{missionOnly}";
        if (SkillId is { } skill) return $"abraxius://skills/{Uri.EscapeDataString(skill.Value)}";
        if (!string.IsNullOrWhiteSpace(UpdateId)) return "abraxius://settings/updates";
        return string.IsNullOrWhiteSpace(Surface) ? "abraxius://home" : $"abraxius://{Uri.EscapeDataString(Surface)}";
    }
}

public sealed record NotificationAction(NotificationActionId Id, string Label, bool RequiresInteractiveReview = false);

public sealed record AbraxiusNotification(
    NotificationId Id,
    NotificationCategory Category,
    NotificationSeverity Severity,
    string Title,
    string Body,
    NotificationTarget Target,
    ImmutableArray<NotificationAction> Actions,
    string Source,
    DateTimeOffset Timestamp,
    DateTimeOffset? Expiry = null,
    string? DeduplicationKey = null,
    NotificationPrivacy Privacy = NotificationPrivacy.Full,
    string? SourceEventId = null)
{
    public ImmutableArray<NotificationAction> SafeActions => Actions.IsDefault ? ImmutableArray<NotificationAction>.Empty : Actions;
}

public sealed record NeedsYouItem(
    NeedsYouId Id,
    MissionId? MissionId,
    AssignmentId? AssignmentId,
    string Source,
    NeedsYouReason Reason,
    NotificationSeverity Priority,
    NotificationActionId RequestedAction,
    string ContextSummary,
    ImmutableArray<EvidenceId> EvidenceRefs,
    DateTimeOffset Created,
    DateTimeOffset? Deadline = null,
    NeedsYouState State = NeedsYouState.Pending,
    DateTimeOffset? SnoozedUntil = null,
    NeedsYouResolution? Resolution = null,
    string? ResolutionNote = null,
    string? SourceEventId = null)
{
    public bool IsActionable(DateTimeOffset now) => State is NeedsYouState.Pending or NeedsYouState.Viewed && (SnoozedUntil is null || SnoozedUntil <= now);
}

public sealed record QuietHours(bool Enabled, TimeOnly Start, TimeOnly End, string TimeZoneId)
{
    public bool Contains(DateTimeOffset instant)
    {
        if (!Enabled) return false;
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId); }
        catch (TimeZoneNotFoundException) { zone = TimeZoneInfo.Local; }
        catch (InvalidTimeZoneException) { zone = TimeZoneInfo.Local; }
        var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        return Start == End || (Start < End ? time >= Start && time < End : time >= Start || time < End);
    }
}

public sealed record PresenceSettings(
    CloseButtonBehavior CloseButton = CloseButtonBehavior.HideToTray,
    MinimizeBehavior Minimize = MinimizeBehavior.Taskbar,
    BackgroundExecutionMode BackgroundMode = BackgroundExecutionMode.ContinueNormally,
    bool LaunchAtLogin = false,
    bool StartHidden = false,
    bool NativeMissionCompletion = true,
    bool NativeNeedsYou = true,
    bool NativeVerificationFailure = true,
    bool NativeUpdates = true,
    bool NativeAchievements = false,
    bool InAppWhenFocused = true,
    bool NotificationSounds = true,
    NotificationPrivacy PreviewPrivacy = NotificationPrivacy.Redacted,
    QuietHours? QuietHours = null,
    int HistoryLimit = 256,
    int MaximumNativePerMinute = 6)
{
    public QuietHours EffectiveQuietHours => QuietHours ?? new(false, new TimeOnly(23, 0), new TimeOnly(7, 0), TimeZoneInfo.Local.Id);
}

public sealed record AttentionContext(
    WindowPresenceState WindowState,
    PresenceSettings Settings,
    DateTimeOffset Now,
    bool NativeAvailable,
    NotificationPermissionState Permission,
    bool SensitiveProject = false);

public sealed record AttentionDecision(NotificationDelivery Delivery, string Reason, bool CreateNeedsYou = false, bool Redact = false);

public sealed record TrayPresentationState(
    TrayRuntimeState RuntimeState,
    int MissionCount,
    int NeedsYouCount,
    int BackgroundWorkCount,
    string ConnectionState,
    string UpdateState,
    string Tooltip,
    NotificationSeverity? AttentionState = null);

public sealed record PresenceSnapshot(
    WindowPresenceState WindowState,
    BackgroundExecutionMode BackgroundMode,
    bool AdmissionPaused,
    int ActiveMissionCount,
    int PendingNeedsYouCount,
    TrayPresentationState Tray,
    DateTimeOffset UpdatedAt);

public sealed record NotificationDeliveryResult(NotificationDelivery Delivery, bool Delivered, string Reason, AbraxiusNotification Notification);

public sealed record NotificationDiagnostics(
    NotificationPermissionState Permission,
    long Generated,
    long NativeDelivered,
    long InAppDelivered,
    long Suppressed,
    long Coalesced,
    DateTimeOffset? LastDelivery,
    string? LastSuppressionReason);

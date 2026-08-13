using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Abraxius.Protocol;

namespace Abraxius.App;

/// <summary>Compact flame-chart-like view of actual task overlap, rendered without per-task controls.</summary>
public sealed class ExecutionLanesView : Control
{
    public static readonly StyledProperty<UiGraphSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<ExecutionLanesView, UiGraphSnapshot?>(nameof(Snapshot));

    public static readonly StyledProperty<TaskId?> SelectedTaskIdProperty =
        AvaloniaProperty.Register<ExecutionLanesView, TaskId?>(nameof(SelectedTaskId));

    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#0E151C"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#E8EEF3"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#81909C"));
    private static readonly IBrush TrackBrush = new SolidColorBrush(Color.Parse("#17242E"));
    private static readonly IBrush RunningFill = new SolidColorBrush(Color.Parse("#23505A"));
    private static readonly IBrush RunningStroke = new SolidColorBrush(Color.Parse("#43D3C5"));
    private static readonly IBrush SuccessFill = new SolidColorBrush(Color.Parse("#23523D"));
    private static readonly IBrush SuccessStroke = new SolidColorBrush(Color.Parse("#62D99A"));
    private static readonly IBrush FailureFill = new SolidColorBrush(Color.Parse("#5A2B34"));
    private static readonly IBrush FailureStroke = new SolidColorBrush(Color.Parse("#FF6B7A"));
    private static readonly IBrush DefaultFill = new SolidColorBrush(Color.Parse("#2A3843"));
    private static readonly IBrush DefaultStroke = new SolidColorBrush(Color.Parse("#6E9DB7"));
    private static readonly Pen TrackPen = new(TrackBrush, 1);
    private static readonly Typeface Typeface = new("Cascadia Mono,DejaVu Sans Mono");
    private readonly Dictionary<TaskId, Rect> _hitBoxes = new();

    public UiGraphSnapshot? Snapshot
    {
        get => GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public TaskId? SelectedTaskId
    {
        get => GetValue(SelectedTaskIdProperty);
        set => SetValue(SelectedTaskIdProperty, value);
    }

    public event EventHandler<TaskId>? TaskSelected;

    static ExecutionLanesView() => AffectsRender<ExecutionLanesView>(SnapshotProperty, SelectedTaskIdProperty);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var point = e.GetPosition(this);
        foreach (var pair in _hitBoxes)
        {
            if (pair.Value.Contains(point))
            {
                SelectedTaskId = pair.Key;
                TaskSelected?.Invoke(this, pair.Key);
                e.Handled = true;
                return;
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(BackgroundBrush, null, new Rect(Bounds.Size));
        _hitBoxes.Clear();
        var snapshot = Snapshot;
        if (snapshot is null || snapshot.Tasks.Count == 0)
        {
            DrawText(context, "WAITING FOR EXECUTION", new Point(24, 26), MutedBrush, 12);
            return;
        }

        var started = snapshot.Tasks.Where(static task => task.StartedAt is not null).Select(static task => task.StartedAt!.Value).ToArray();
        var baseline = started.Length == 0 ? DateTimeOffset.UtcNow : started.Min();
        var duration = snapshot.Tasks
            .Select(task => DurationFor(task, baseline))
            .DefaultIfEmpty(TimeSpan.FromSeconds(1))
            .Max();
        var pixelsPerSecond = Math.Clamp((Bounds.Width - 180) / Math.Max(0.5, duration.TotalSeconds), 45, 260);

        var groups = snapshot.Tasks
            .GroupBy(static task => task.Executor)
            .OrderBy(static group => group.Key)
            .ToArray();
        var y = 26d;
        foreach (var group in groups)
        {
            DrawText(context, group.Key.ToString().ToUpperInvariant(), new Point(16, y + 5), TextBrush, 10);
            context.DrawLine(TrackPen, new Point(160, y + 18), new Point(Bounds.Width - 12, y + 18));
            var row = 0;
            foreach (var task in group)
            {
                if (task.StartedAt is null)
                {
                    row++;
                    continue;
                }

                var taskStart = Math.Max(0, (task.StartedAt.Value - baseline).TotalSeconds);
                var taskDuration = Math.Max(0.02, DurationFor(task, task.StartedAt.Value).TotalSeconds);
                var rect = new Rect(166 + taskStart * pixelsPerSecond, y + row * 22, Math.Max(8, taskDuration * pixelsPerSecond), 16);
                _hitBoxes[task.TaskId] = rect;
                var colors = ColorsFor(task.State);
                context.DrawRectangle(colors.Fill, new Pen(task.TaskId == SelectedTaskId ? Brushes.White : colors.Stroke, task.TaskId == SelectedTaskId ? 2 : 1), rect, 3);
                if (rect.Width > 58)
                {
                    DrawText(context, Trim(task.Label, Math.Max(8, (int)(rect.Width / 7))), rect.Position + new Vector(6, 2), Brushes.White, 9);
                }

                row++;
            }

            y += Math.Max(38, row * 22 + 18);
        }

        DrawText(context, $"{snapshot.Metrics.MaxObservedConcurrency} max concurrent  ·  queue and execution timing", new Point(16, Math.Max(20, Bounds.Height - 20)), MutedBrush, 9);
    }

    private static TimeSpan DurationFor(UiTaskSnapshot task, DateTimeOffset start)
    {
        if (task.Timing is { } timing)
        {
            return timing.ExecutionLatency;
        }

        return task.State == WorkState.Running ? DateTimeOffset.UtcNow - start : TimeSpan.FromMilliseconds(40);
    }

    private static (IBrush Fill, IBrush Stroke) ColorsFor(WorkState state) => state switch
    {
        WorkState.Running => (RunningFill, RunningStroke),
        WorkState.Succeeded => (SuccessFill, SuccessStroke),
        WorkState.Failed or WorkState.TimedOut => (FailureFill, FailureStroke),
        _ => (DefaultFill, DefaultStroke)
    };

    private static string Trim(string value, int length) => value.Length <= length ? value : value[..Math.Max(0, length - 1)] + "…";

    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush, double size)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, size, brush);
        context.DrawText(formatted, point);
    }
}

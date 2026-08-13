using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Abraxius.Protocol;

namespace Abraxius.App;

/// <summary>
/// A snapshot-driven dependency renderer. Nodes are drawn directly; the visual tree contains one control.
/// </summary>
public sealed class ExecutionGraphView : Control
{
    public static readonly StyledProperty<UiGraphSnapshot?> SnapshotProperty =
        AvaloniaProperty.Register<ExecutionGraphView, UiGraphSnapshot?>(nameof(Snapshot));

    public static readonly StyledProperty<TaskId?> SelectedTaskIdProperty =
        AvaloniaProperty.Register<ExecutionGraphView, TaskId?>(nameof(SelectedTaskId));

    public static readonly StyledProperty<bool> CriticalPathOnlyProperty =
        AvaloniaProperty.Register<ExecutionGraphView, bool>(nameof(CriticalPathOnly));

    public static readonly StyledProperty<bool> ReducedMotionProperty =
        AvaloniaProperty.Register<ExecutionGraphView, bool>(nameof(ReducedMotion));

    private static readonly IBrush BackgroundBrush = Brush("#0E151C");
    private static readonly IBrush GridBrush = Brush("#17242E");
    private static readonly IBrush TextBrush = Brush("#E8EEF3");
    private static readonly IBrush MutedBrush = Brush("#81909C");
    private static readonly IBrush AccentBrush = Brush("#43D3C5");
    private static readonly IBrush SelectedBrush = Brush("#D7FFF8");
    private static readonly IBrush SubduedFill = Brush("#182129");
    private static readonly IBrush SubduedStroke = Brush("#26343E");
    private static readonly IBrush RunningFill = Brush("#173B45");
    private static readonly IBrush SuccessFill = Brush("#193B31");
    private static readonly IBrush SuccessStroke = Brush("#62D99A");
    private static readonly IBrush FailureFill = Brush("#45252B");
    private static readonly IBrush FailureStroke = Brush("#FF6B7A");
    private static readonly IBrush CancelledFill = Brush("#332E42");
    private static readonly IBrush CancelledStroke = Brush("#B7A4FF");
    private static readonly IBrush ReadyFill = Brush("#253544");
    private static readonly IBrush ReadyStroke = Brush("#6E9DB7");
    private static readonly IBrush DefaultFill = Brush("#202832");
    private static readonly IBrush DefaultStroke = Brush("#536271");
    private static readonly IBrush SurfaceAggregateBrush = Brush("#19252E");
    private static readonly Pen AggregatePen = new(Brush("#334A57"), 1);
    private static readonly Pen GridPen = new(GridBrush, 1);
    private static readonly Pen EdgePen = new(Brush("#324754"), 1.2);
    private static readonly Pen CriticalEdgePen = new(Brush("#43D3C5"), 2.1);
    private static readonly Typeface Typeface = new("Cascadia Mono,DejaVu Sans Mono");
    private readonly Dictionary<TaskId, Rect> _hitBoxes = new();
    private readonly Dictionary<TaskId, Rect> _layout = new();
    private readonly HashSet<TaskId> _criticalPath = [];
    private int _layoutKey;
    private Vector _pan = new(28, 28);
    private Point _panStart;
    private Vector _panOrigin;
    private bool _panning;
    private double _zoom = 1;

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

    public bool CriticalPathOnly
    {
        get => GetValue(CriticalPathOnlyProperty);
        set => SetValue(CriticalPathOnlyProperty, value);
    }

    public bool ReducedMotion
    {
        get => GetValue(ReducedMotionProperty);
        set => SetValue(ReducedMotionProperty, value);
    }

    public event EventHandler<TaskId>? TaskSelected;

    static ExecutionGraphView()
    {
        AffectsRender<ExecutionGraphView>(SnapshotProperty, SelectedTaskIdProperty, CriticalPathOnlyProperty, ReducedMotionProperty);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetPosition(this);
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsMiddleButtonPressed || properties.IsRightButtonPressed)
        {
            _panning = true;
            _panStart = point;
            _panOrigin = _pan;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (properties.IsLeftButtonPressed)
        {
            foreach (var pair in _hitBoxes)
            {
                if (pair.Value.Contains(point))
                {
                    TaskSelected?.Invoke(this, pair.Key);
                    e.Handled = true;
                    return;
                }
            }
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_panning)
        {
            return;
        }

        _pan = _panOrigin + e.GetPosition(this) - _panStart;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _panning = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var before = e.GetPosition(this);
        var factor = e.Delta.Y > 0 ? 1.12 : 1 / 1.12;
        var nextZoom = Math.Clamp(_zoom * factor, 0.35, 2.5);
        var modelPoint = ToModel(before);
        _zoom = nextZoom;
        var after = ToScreen(modelPoint);
        _pan += before - after;
        InvalidateVisual();
        e.Handled = true;
    }

    public void FitToExecution()
    {
        _zoom = 1;
        _pan = new Vector(28, 28);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(BackgroundBrush, null, new Rect(Bounds.Size));
        DrawGrid(context);

        var snapshot = Snapshot;
        if (snapshot is null || snapshot.Tasks.Count == 0)
        {
            DrawText(context, "WAITING FOR EXECUTION", new Point(28, 28), MutedBrush, 13);
            return;
        }

        EnsureLayout(snapshot.Tasks);
        if (CriticalPathOnly)
        {
            EnsureCriticalPath(snapshot.Tasks);
        }
        else
        {
            _criticalPath.Clear();
        }
        _hitBoxes.Clear();

        if (snapshot.Tasks.Count > 2_000)
        {
            DrawAggregatedOverview(context, snapshot.Tasks);
            return;
        }

        var detailed = snapshot.Tasks.Count <= 250 && _zoom >= 0.72;
        DrawEdges(context, snapshot.Tasks);
        foreach (var task in snapshot.Tasks)
        {
            if (!_layout.TryGetValue(task.TaskId, out var modelRect))
            {
                continue;
            }

            var screenRect = ToScreen(modelRect);
            if (!screenRect.Intersects(new Rect(Bounds.Size)))
            {
                continue;
            }

            _hitBoxes[task.TaskId] = screenRect;
            if (CriticalPathOnly && !_criticalPath.Contains(task.TaskId))
            {
                DrawNode(context, task, screenRect, detailed, subdued: true);
                continue;
            }

            DrawNode(context, task, screenRect, detailed, subdued: false);
        }

        DrawLegend(context);
    }

    private void DrawEdges(DrawingContext context, IReadOnlyList<UiTaskSnapshot> tasks)
    {
        foreach (var task in tasks)
        {
            if (!_layout.TryGetValue(task.TaskId, out var target))
            {
                continue;
            }

            foreach (var dependency in task.Dependencies)
            {
                if (!_layout.TryGetValue(dependency, out var source))
                {
                    continue;
                }

                var important = SelectedTaskId == task.TaskId || SelectedTaskId == dependency ||
                    (_criticalPath.Contains(task.TaskId) && _criticalPath.Contains(dependency));
                if (CriticalPathOnly && !important)
                {
                    continue;
                }

                var start = ToScreen(new Point(source.Right, source.Center.Y));
                var end = ToScreen(new Point(target.Left, target.Center.Y));
                context.DrawLine(important ? CriticalEdgePen : EdgePen, start, end);
            }
        }
    }

    private void DrawNode(DrawingContext context, UiTaskSnapshot task, Rect rect, bool detailed, bool subdued)
    {
        var colors = ColorsFor(task.State);
        var fill = subdued ? SubduedFill : colors.Fill;
        var stroke = subdued ? SubduedStroke : colors.Stroke;
        if (SelectedTaskId == task.TaskId)
        {
            stroke = SelectedBrush;
        }

        if (!detailed)
        {
            var center = rect.Center;
            context.DrawEllipse(fill, new Pen(stroke, SelectedTaskId == task.TaskId ? 2 : 1), center, 8, 8);
            return;
        }

        context.DrawRectangle(fill, new Pen(stroke, SelectedTaskId == task.TaskId ? 2 : 1), rect, 5);
        var indicator = task.State switch
        {
            WorkState.Succeeded => "✓",
            WorkState.Failed or WorkState.TimedOut => "!",
            WorkState.Running => "●",
            WorkState.Cancelled => "×",
            WorkState.Skipped => "–",
            _ => "·"
        };
        DrawText(context, $"{indicator} {Trim(task.Label, 23)}", rect.Position + new Vector(10, 8), TextBrush, 11);
        DrawText(context, task.State.ToString().ToUpperInvariant(), rect.Position + new Vector(10, 29), MutedBrush, 9);
        if (task.Timing is { } timing)
        {
            DrawText(context, $"{timing.TotalLatency.TotalMilliseconds:F0}ms", rect.Position + new Vector(rect.Width - 62, 29), MutedBrush, 9);
        }
    }

    private void DrawAggregatedOverview(DrawingContext context, IReadOnlyList<UiTaskSnapshot> tasks)
    {
        foreach (var depth in _layout.Values.GroupBy(rect => (int)Math.Round((rect.X - 20) / 204)).OrderBy(static group => group.Key))
        {
            var x = 28 + depth.Key * 204;
            var running = tasks.Where(task => _layout.TryGetValue(task.TaskId, out var rect) && (int)Math.Round((rect.X - 20) / 204) == depth.Key).Count(static task => task.State == WorkState.Running);
            var completed = tasks.Where(task => _layout.TryGetValue(task.TaskId, out var rect) && (int)Math.Round((rect.X - 20) / 204) == depth.Key).Count(static task => task.State == WorkState.Succeeded);
            var total = depth.Count();
            var rect = ToScreen(new Rect(x, 38, 174, 48));
            context.DrawRectangle(SurfaceAggregateBrush, AggregatePen, rect, 5);
            DrawText(context, $"PHASE {depth.Key + 1}", rect.Position + new Vector(10, 8), TextBrush, 10);
            DrawText(context, $"{completed}/{total} complete · {running} active", rect.Position + new Vector(10, 27), MutedBrush, 9);
        }

        DrawText(context, "DETAIL HIDDEN · ZOOM IN TO INSPECT TASKS", new Point(28, Math.Max(100, Bounds.Height - 28)), MutedBrush, 10);
    }

    private void DrawLegend(DrawingContext context)
    {
        DrawText(context, "WHEEL ZOOM  ·  MIDDLE DRAG PAN  ·  CLICK TASK TO INSPECT", new Point(20, Math.Max(20, Bounds.Height - 20)), MutedBrush, 9);
    }

    private void DrawGrid(DrawingContext context)
    {
        for (var x = 0d; x < Bounds.Width; x += 48)
        {
            context.DrawLine(GridPen, new Point(x, 0), new Point(x, Bounds.Height));
        }

        for (var y = 0d; y < Bounds.Height; y += 48)
        {
            context.DrawLine(GridPen, new Point(0, y), new Point(Bounds.Width, y));
        }
    }

    private void EnsureLayout(IReadOnlyList<UiTaskSnapshot> tasks)
    {
        var key = tasks.Count;
        foreach (var task in tasks)
        {
            key = HashCode.Combine(key, task.TaskId.GetHashCode(), task.Dependencies.Count);
            foreach (var dependency in task.Dependencies)
            {
                key = HashCode.Combine(key, dependency.GetHashCode());
            }
        }

        if (_layoutKey == key && _layout.Count == tasks.Count)
        {
            return;
        }

        _layoutKey = key;
        _layout.Clear();
        var depth = new Dictionary<TaskId, int>(tasks.Count);
        var remaining = new Dictionary<TaskId, int>(tasks.Count);
        var dependents = new Dictionary<TaskId, List<TaskId>>(tasks.Count);
        foreach (var task in tasks)
        {
            depth[task.TaskId] = 0;
            remaining[task.TaskId] = 0;
            dependents[task.TaskId] = [];
        }

        foreach (var task in tasks)
        {
            foreach (var dependency in task.Dependencies)
            {
                if (!remaining.ContainsKey(dependency))
                {
                    continue;
                }

                remaining[task.TaskId]++;
                dependents[dependency].Add(task.TaskId);
            }
        }

        var ready = new Queue<TaskId>(tasks.Count);
        foreach (var task in tasks)
        {
            if (remaining[task.TaskId] == 0)
            {
                ready.Enqueue(task.TaskId);
            }
        }

        while (ready.TryDequeue(out var current))
        {
            foreach (var dependent in dependents[current])
            {
                depth[dependent] = Math.Max(depth[dependent], depth[current] + 1);
                if (--remaining[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }

        var rowByDepth = new Dictionary<int, int>();
        foreach (var task in tasks)
        {
            var taskDepth = depth.GetValueOrDefault(task.TaskId);
            var row = rowByDepth.GetValueOrDefault(taskDepth);
            rowByDepth[taskDepth] = row + 1;
            _layout[task.TaskId] = new Rect(20 + taskDepth * 204, 20 + row * 78, 174, 54);
        }
    }

    private void EnsureCriticalPath(IReadOnlyList<UiTaskSnapshot> tasks)
    {
        _criticalPath.Clear();
        var score = new Dictionary<TaskId, double>(tasks.Count);
        var byId = tasks.ToDictionary(static task => task.TaskId);
        foreach (var task in tasks.OrderBy(task => _layout.GetValueOrDefault(task.TaskId).X))
        {
            var duration = task.Timing?.ExecutionLatency.TotalMilliseconds ?? 1;
            var dependencyScore = task.Dependencies.Count == 0 ? 0 : task.Dependencies.Max(dependency => score.GetValueOrDefault(dependency));
            score[task.TaskId] = dependencyScore + duration;
        }

        var current = tasks.OrderByDescending(task => score.GetValueOrDefault(task.TaskId)).FirstOrDefault();
        while (current is not null)
        {
            _criticalPath.Add(current.TaskId);
            current = current.Dependencies
                .Where(byId.ContainsKey)
                .OrderByDescending(dependency => score.GetValueOrDefault(dependency))
                .Select(dependency => byId[dependency])
                .FirstOrDefault();
        }
    }

    private Point ToScreen(Point model) => new(model.X * _zoom + _pan.X, model.Y * _zoom + _pan.Y);
    private Rect ToScreen(Rect model) => new(ToScreen(model.Position), model.Size * _zoom);
    private Point ToModel(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    private static string Trim(string value, int length) => value.Length <= length ? value : value[..Math.Max(0, length - 1)] + "…";

    private static (IBrush Fill, IBrush Stroke) ColorsFor(WorkState state) => state switch
    {
        WorkState.Running => (RunningFill, AccentBrush),
        WorkState.Succeeded => (SuccessFill, SuccessStroke),
        WorkState.Failed or WorkState.TimedOut => (FailureFill, FailureStroke),
        WorkState.Cancelled => (CancelledFill, CancelledStroke),
        WorkState.Ready or WorkState.Queued => (ReadyFill, ReadyStroke),
        _ => (DefaultFill, DefaultStroke)
    };

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush, double size)
    {
        var formatted = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface, size, brush);
        context.DrawText(formatted, point);
    }
}

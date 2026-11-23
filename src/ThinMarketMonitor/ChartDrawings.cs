using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal enum DrawingKind { HorizontalRay, Ray, Segment }

internal readonly record struct ChartPoint(double Time, double Price)
{
    public bool Valid => double.IsFinite(Time) && double.IsFinite(Price);
}

internal sealed record ChartDrawing(Guid Id, DrawingKind Kind, ChartPoint Start, ChartPoint End)
{
    public bool Valid => Id != Guid.Empty && Enum.IsDefined(Kind) && Start.Valid && End.Valid &&
        (Kind == DrawingKind.HorizontalRay || Start != End);
}

internal static class ChartGeometry
{
    public static (Point Start, Point End)? Clip(Rect bounds, Point origin, Vector direction, bool ray)
    {
        double first = 0, last = ray ? double.PositiveInfinity : 1;
        if (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y) || !double.IsFinite(direction.X) || !double.IsFinite(direction.Y) ||
            direction == new Vector() ||
            !Restrict(origin.X, direction.X, bounds.Left, bounds.Right) ||
            !Restrict(origin.Y, direction.Y, bounds.Top, bounds.Bottom) || !double.IsFinite(last)) return null;
        return (origin + direction * first, origin + direction * last);

        bool Restrict(double value, double delta, double min, double max)
        {
            if (delta == 0) return value >= min && value <= max;
            double a = (min - value) / delta, b = (max - value) / delta;
            first = Math.Max(first, Math.Min(a, b));
            last = Math.Min(last, Math.Max(a, b));
            return first <= last;
        }
    }

    public static ChartPoint Snap(ChartPoint point, Candle[] candles, long barTime)
    {
        if (candles.Length == 0) return point;
        int lo = 0, hi = candles.Length;
        double target = point.Time - barTime / 2d;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (candles[mid].Time < target) lo = mid + 1;
            else hi = mid;
        }
        int right = Math.Min(lo, candles.Length - 1), left = Math.Max(0, lo - 1);
        var bar = Math.Abs(candles[left].Time - target) <= Math.Abs(candles[right].Time - target) ? candles[left] : candles[right];
        double price = new[] { bar.Open, bar.High, bar.Low, bar.Close }.MinBy(price => Math.Abs(price - point.Price));
        return new(bar.Time + barTime / 2d, price);
    }
}

internal sealed partial class CandlePlot
{
    private ChartDrawing[] drawings = [];
    private DrawingKind? drawingTool;
    private Guid? selectedDrawing;
    private ChartPoint? drawingStart, drawingPreview;
    private AnchorMove? movingAnchor;
    private sealed record AnchorMove(ChartDrawing Original, bool IsStart, View View, ChartDrawing Preview);
    private readonly Pen drawingLine = new(Terminal.Brush("Accent"), 1.4);
    private readonly Pen selectedLine = new(Terminal.Brush("Text"), 1.5);
    internal Func<ModifierKeys> Magnet { get; set; } = () => ModifierKeys.Shift;
    internal event Action<ChartDrawing[]>? DrawingsChanged;
    internal bool HasSelectedDrawing => selectedDrawing.HasValue;
    internal bool IsDrawing => drawingTool.HasValue || movingAnchor != null;

    internal void SetDrawings(ChartDrawing[] value)
    {
        drawings = value;
        if (!drawings.Any(drawing => drawing.Id == selectedDrawing)) selectedDrawing = null;
        InvalidateVisual();
    }

    internal void BeginDrawing(DrawingKind kind)
    {
        CancelGesture();
        CancelDrawing();
        selectedDrawing = null;
        drawingTool = kind;
        Cursor = Cursors.Cross;
        InvalidateVisual();
    }

    internal bool CancelDrawing()
    {
        if (!IsDrawing) return false;
        drawingTool = null;
        drawingStart = drawingPreview = null;
        if (movingAnchor != null) CancelGesture();
        ClearValue(CursorProperty);
        InvalidateVisual();
        return true;
    }

    internal bool DeleteDrawing()
    {
        if (selectedDrawing is not { } id) return false;
        CancelDrawing();
        CommitDrawings(drawings.Where(drawing => drawing.Id != id).ToArray());
        return true;
    }

    private bool DrawingClick(Point at, View view)
    {
        if (drawingTool is { } kind)
        {
            var point = DrawingPoint(at, view);
            if (kind == DrawingKind.HorizontalRay || drawingStart.HasValue)
            {
                var drawing = new ChartDrawing(Guid.NewGuid(), kind, drawingStart ?? point, point);
                if (!drawing.Valid) return true;
                CancelDrawing();
                selectedDrawing = drawing.Id;
                CommitDrawings([.. drawings, drawing]);
            }
            else drawingStart = drawingPreview = point;
            InvalidateVisual();
            return true;
        }
        var selected = drawings.FirstOrDefault(drawing => drawing.Id == selectedDrawing);
        if (selected != null)
        {
            bool start = (at - Screen(selected.Start, view)).Length <= 8;
            bool end = selected.Kind != DrawingKind.HorizontalRay && (at - Screen(selected.End, view)).Length <= 8;
            if ((start || end) && CaptureMouse())
            {
                movingAnchor = new(selected, start, view, selected);
                return true;
            }
        }
        selectedDrawing = drawings.LastOrDefault(drawing => NearLine(drawing, at, view))?.Id;
        InvalidateVisual();
        return HasSelectedDrawing;
    }

    private bool MoveDrawing(Point at)
    {
        if (movingAnchor is { } move)
        {
            var point = DrawingPoint(at, move.View);
            movingAnchor = move with { Preview = move.IsStart ? move.Original with { Start = point } : move.Original with { End = point } };
        }
        else if (drawingStart.HasValue && CurrentView() is { } view) drawingPreview = DrawingPoint(at, view);
        else return false;
        InvalidateVisual();
        return true;
    }

    private bool ReleaseDrawing()
    {
        if (movingAnchor is not { } move) return false;
        movingAnchor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        if (move.Preview.Valid)
            CommitDrawings(drawings.Select(drawing => drawing.Id == move.Preview.Id ? move.Preview : drawing).ToArray());
        return true;
    }

    private void CommitDrawings(ChartDrawing[] value)
    {
        SetDrawings(value);
        DrawingsChanged?.Invoke(value);
    }

    private ChartPoint DrawingPoint(Point at, View view)
    {
        at = view.Bound(at);
        var point = new ChartPoint(view.Time(at.X), view.Price(at.Y));
        var modifier = Magnet();
        return modifier != ModifierKeys.None && (Keyboard.Modifiers & modifier) == modifier
            ? ChartGeometry.Snap(point, candles, BarTime) : point;
    }

    private static Point Screen(ChartPoint point, View view) => new(view.X(point.Time), view.Y(point.Price));

    private static (Point Start, Point End)? VisibleLine(ChartDrawing drawing, View view)
    {
        var start = Screen(drawing.Start, view);
        var delta = drawing.Kind == DrawingKind.HorizontalRay ? new Vector(1, 0) : Screen(drawing.End, view) - start;
        return ChartGeometry.Clip(view.Plot, start, delta, drawing.Kind != DrawingKind.Segment);
    }

    private static bool NearLine(ChartDrawing drawing, Point at, View view)
    {
        if (VisibleLine(drawing, view) is not { } line) return false;
        var direction = line.End - line.Start;
        if (direction.LengthSquared == 0) return false;
        double along = Math.Clamp(Vector.Multiply(at - line.Start, direction) / direction.LengthSquared, 0, 1);
        return (at - (line.Start + along * direction)).Length <= 6;
    }

    private void DrawAnnotations(DrawingContext dc, View view)
    {
        foreach (var drawing in drawings)
        {
            if (movingAnchor is { } move && drawing.Id == move.Original.Id) Paint(move.Preview, true);
            else Paint(drawing, drawing.Id == selectedDrawing);
        }
        if (drawingTool is { } kind && drawingStart is { } first && drawingPreview is { } preview)
            Paint(new(Guid.Empty, kind, first, preview), true);

        void Paint(ChartDrawing drawing, bool selected)
        {
            if (VisibleLine(drawing, view) is { } line) dc.DrawLine(selected ? selectedLine : drawingLine, line.Start, line.End);
            if (!selected) return;
            Handle(Screen(drawing.Start, view));
            if (drawing.Kind != DrawingKind.HorizontalRay) Handle(Screen(drawing.End, view));
        }

        void Handle(Point point)
        {
            if (view.Plot.Contains(point)) dc.DrawEllipse(accent, null, point, 3.5, 3.5);
        }
    }
}

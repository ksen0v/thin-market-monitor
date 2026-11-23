using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class CandlePlot
{
    private readonly DrawingVisual crosshair = new();
    private readonly Pen crosshairStroke = CrosshairPen();
    private (Rect Bounds, RectangleGeometry Clip)[] crosshairPanes = [];
    private Rect crosshairPricePane = Rect.Empty;
    private double[]? crosshairPaneHeights;
    private Point? crosshairPosition;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? crosshair : throw new ArgumentOutOfRangeException(nameof(index));

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (drag == null && !IsMouseCaptured) SetCrosshairPosition(e.GetPosition(this));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetCrosshairPosition(null);
    }

    private void UpdateCrosshairPanes(View? view)
    {
        var price = view?.Plot ?? Rect.Empty;
        var heights = view?.PaneHeights;
        if (price == crosshairPricePane && crosshairPaneHeights.AsSpan().SequenceEqual(heights.AsSpan())) return;
        crosshairPricePane = price;
        crosshairPaneHeights = heights;
        crosshairPanes = view is { } current
            ? InterestPanes(current).Select(pane => pane.Graph).Prepend(price).Select(ClipPane).ToArray()
            : [];
        DrawCrosshair();

        static (Rect, RectangleGeometry) ClipPane(Rect bounds)
        {
            var clip = new RectangleGeometry(bounds);
            clip.Freeze();
            return (bounds, clip);
        }
    }

    private void SetCrosshairPosition(Point? point)
    {
        if (crosshairPosition == point) return;
        crosshairPosition = point;
        DrawCrosshair();
    }

    private void DrawCrosshair()
    {
        using var dc = crosshair.RenderOpen();
        if (crosshairPosition is not { } at) return;
        int active = 0;
        while (active < crosshairPanes.Length && !crosshairPanes[active].Bounds.Contains(at)) active++;
        if (active == crosshairPanes.Length) return;

        var top = new Point(at.X, crosshairPanes[0].Bounds.Top);
        var bottom = new Point(at.X, crosshairPanes[^1].Bounds.Bottom);
        // Keep the time guide aligned across panes without drawing through headers or scales.
        for (int i = 0; i < crosshairPanes.Length; i++)
        {
            var (bounds, clip) = crosshairPanes[i];
            dc.PushClip(clip);
            dc.DrawLine(crosshairStroke, at, top);
            dc.DrawLine(crosshairStroke, at, bottom);
            if (i == active)
            {
                dc.DrawLine(crosshairStroke, at, new Point(bounds.Left, at.Y));
                dc.DrawLine(crosshairStroke, at, new Point(bounds.Right, at.Y));
            }
            dc.Pop();
        }
    }

    private static Pen CrosshairPen()
    {
        var brush = Terminal.Brush("Muted").CloneCurrentValue();
        brush.Opacity = .72;
        var pen = new Pen(brush, 1) { DashStyle = new DashStyle([2.5, 3.5], 0), DashCap = PenLineCap.Flat };
        pen.Freeze();
        return pen;
    }
}

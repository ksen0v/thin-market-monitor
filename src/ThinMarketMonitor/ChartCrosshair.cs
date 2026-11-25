using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class CandlePlot
{
    private readonly DrawingVisual crosshair = new();
    private readonly Pen crosshairStroke = CrosshairPen();
    private readonly Brush crosshairLabelBackground = Terminal.Brush("Control");
    private readonly Brush crosshairLabelText = Terminal.Brush("Text");
    private View? crosshairView;
    private double crosshairPriceStep;
    private Rect lastPriceBadge = Rect.Empty;
    private IReadOnlyList<InterestScale?> crosshairInterestScales = [];
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

    private void UpdateCrosshairAxes(View? view, double priceStep, Rect priceBadge, IReadOnlyList<InterestScale?> interestScales)
    {
        crosshairView = view;
        crosshairPriceStep = priceStep;
        lastPriceBadge = priceBadge;
        crosshairInterestScales = interestScales;
        var price = view?.Plot ?? Rect.Empty;
        var heights = view?.PaneHeights;
        if (price != crosshairPricePane || !crosshairPaneHeights.AsSpan().SequenceEqual(heights.AsSpan()))
        {
            crosshairPricePane = price;
            crosshairPaneHeights = heights;
            crosshairPanes = view is { } current
                ? InterestPanes(current).Select(pane => pane.Graph).Prepend(price).Select(ClipPane).ToArray()
                : [];
        }
        if (crosshairPosition.HasValue) DrawCrosshair();

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
        if (crosshairPosition is not { } at || crosshairView is not { } view) return;
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
        DrawCrosshairLabels(dc, view, at, active);
    }

    private void DrawCrosshairLabels(DrawingContext dc, View view, Point at, int active)
    {
        var moment = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(view.Time(at.X))).ToLocalTime();
        var time = Text(moment.ToString(Frame < 60 ? "dd.MM.yy HH:mm:ss" : "dd.MM.yy HH:mm", Russian), crosshairLabelText);
        FitAxisLabel(time, view.Plot.Width - 8);
        double width = Math.Min(view.Plot.Width, time.Width + 8);
        double x = Math.Clamp(at.X - width / 2, view.Plot.Left, Math.Max(view.Plot.Left, view.Plot.Right - width));
        DrawCrosshairLabel(dc, time, new Rect(x, ActualHeight - TimeAxisHeight, width, TimeAxisHeight), centered: true);

        var pane = crosshairPanes[active].Bounds;
        string? value = null;
        if (active == 0)
        {
            double resolution = Math.Min(crosshairPriceStep / 10, view.Span / pane.Height);
            value = ChartAxis.PriceLabel(view.Price(at.Y), resolution);
        }
        else if (active <= crosshairInterestScales.Count && crosshairInterestScales[active - 1] is { } scale)
        {
            var interestView = view with { Plot = pane, Low = scale.Low, High = scale.High };
            double resolution = Math.Min(scale.Step / 10, interestView.Span / pane.Height);
            value = scale.CursorLabel(interestView.Price(at.Y), resolution);
        }
        if (value == null) return;
        var number = Text(value, crosshairLabelText);
        double numberX = pane.Right;
        double axisWidth = ActualWidth - pane.Right;
        FitAxisLabel(number, axisWidth - 7);
        double height = Math.Min(pane.Height, number.Height + 3);
        double y = Math.Clamp(at.Y - height / 2, pane.Top, Math.Max(pane.Top, pane.Bottom - height));
        if (active == 0 && !lastPriceBadge.IsEmpty && y < lastPriceBadge.Bottom + 1 && y + height > lastPriceBadge.Top - 1)
        {
            double above = lastPriceBadge.Top - height - 1;
            double below = lastPriceBadge.Bottom + 1;
            if (above >= pane.Top && (at.Y <= lastPriceBadge.Top + lastPriceBadge.Height / 2 || below + height > pane.Bottom))
                y = above;
            else if (below + height <= pane.Bottom)
                y = below;
            else
            {
                axisWidth = Math.Min(pane.Width - 1, number.Width + 8);
                numberX = pane.Right - axisWidth - 1;
            }
        }
        DrawCrosshairLabel(dc, number, new Rect(numberX, y, axisWidth, height), centered: false);
    }

    private void DrawCrosshairLabel(DrawingContext dc, FormattedText label, Rect bounds, bool centered)
    {
        dc.DrawRectangle(crosshairLabelBackground, null, bounds);
        dc.PushClip(new RectangleGeometry(bounds));
        dc.DrawText(label, new Point(bounds.Left + (centered ? (bounds.Width - label.Width) / 2 : 4),
            bounds.Top + (bounds.Height - label.Height) / 2));
        dc.Pop();
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

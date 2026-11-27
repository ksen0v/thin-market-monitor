using System.Globalization;
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
    private Rect crosshairPriceBadge = Rect.Empty;
    private Geometry? crosshairPriceClip;
    private IReadOnlyList<InterestScale?> crosshairInterestScales = [];
    private (Rect Bounds, RectangleGeometry Clip)[] crosshairPanes = [];
    private Rect crosshairPricePane = Rect.Empty;
    private double[]? crosshairPaneHeights;
    private Point? crosshairPosition;
    private readonly Pen alertLine = new(Terminal.Brush("Accent"), 1) { DashStyle = new DashStyle([5d, 3d], 0) };
    private double[] priceAlerts = [];
    internal event Action<(Rect Bounds, double Price)?>? AlertTargetChanged;
    internal Func<bool>? KeepCrosshairOnAlert { get; set; }
    internal void HideCrosshair() => SetCrosshairPosition(null);

    internal void SetPriceAlertLevels(double[] levels)
    {
        if (priceAlerts.AsSpan().SequenceEqual(levels)) return;
        priceAlerts = levels;
        InvalidateVisual();
    }

    private void DrawPriceAlerts(DrawingContext dc, View view)
    {
        foreach (double price in priceAlerts)
        {
            double y = view.Y(price);
            if (y < view.Plot.Top || y > view.Plot.Bottom) continue;
            dc.DrawLine(alertLine, new Point(view.Plot.Left, y), new Point(view.Plot.Right, y));
        }
    }

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
        if (KeepCrosshairOnAlert?.Invoke() != true) SetCrosshairPosition(null);
    }

    private void UpdateCrosshairAxes(View? view, double priceStep, Rect priceBadge, IReadOnlyList<InterestScale?> interestScales)
    {
        crosshairView = view;
        crosshairPriceStep = priceStep;
        crosshairInterestScales = interestScales;
        var price = view?.Plot ?? Rect.Empty;
        var heights = view?.PaneHeights;
        if (priceBadge != crosshairPriceBadge || price != crosshairPricePane)
        {
            crosshairPriceBadge = priceBadge;
            crosshairPriceClip = priceBadge.IsEmpty ? null : new CombinedGeometry(GeometryCombineMode.Exclude,
                new RectangleGeometry(price), new RectangleGeometry(priceBadge));
            crosshairPriceClip?.Freeze();
        }
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
        if (crosshairPosition is not { } at || crosshairView is not { } view)
        {
            AlertTargetChanged?.Invoke(null);
            return;
        }
        int active = 0;
        while (active < crosshairPanes.Length && !crosshairPanes[active].Bounds.Contains(at)) active++;
        if (active == crosshairPanes.Length)
        {
            AlertTargetChanged?.Invoke(null);
            return;
        }

        var top = new Point(at.X, crosshairPanes[0].Bounds.Top);
        var bottom = new Point(at.X, crosshairPanes[^1].Bounds.Bottom);
        // Keep the time guide aligned across panes without drawing through headers or scales.
        for (int i = 0; i < crosshairPanes.Length; i++)
        {
            var (bounds, clip) = crosshairPanes[i];
            dc.PushClip(i == 0 ? crosshairPriceClip ?? clip : clip);
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
        string date = ChartAxis.DateLabel(moment, seconds: Frame < 60);
        string? gap = CandleGapLabel(moment.ToUnixTimeMilliseconds());
        var time = Text(gap == null ? date : $"{date} · {gap}", crosshairLabelText);
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
        if (value == null)
        {
            AlertTargetChanged?.Invoke(null);
            return;
        }
        var number = Text(value, crosshairLabelText);
        double axisWidth = ActualWidth - pane.Right;
        FitAxisLabel(number, axisWidth - 7);
        double height = Math.Min(pane.Height, number.Height + 3);
        double y = Math.Clamp(at.Y - height / 2, pane.Top, Math.Max(pane.Top, pane.Bottom - height));
        DrawCrosshairLabel(dc, number, new Rect(pane.Right, y, axisWidth, height), centered: false);
        if (active == 0 && Instrument != null && pane.Height >= ChartSurface.AlertButtonSize &&
            double.TryParse(value, NumberStyles.Float, Russian, out var price) && PriceAlert.IsPrice(price))
        {
            double size = ChartSurface.AlertButtonSize;
            double buttonY = Math.Clamp(at.Y - size / 2, pane.Top, pane.Bottom - size);
            AlertTargetChanged?.Invoke((new Rect(pane.Right - size - 1, buttonY, size, size), price));
        }
        else AlertTargetChanged?.Invoke(null);
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

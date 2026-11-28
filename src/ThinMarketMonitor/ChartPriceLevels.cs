using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class CandlePlot
{
    private readonly Pen alertLine = new(Terminal.Brush("Accent"), 1) { DashStyle = new DashStyle([5d, 3d], 0) };
    private readonly Pen selectedAlertLine = new(Terminal.Brush("Text"), 1.5) { DashStyle = new DashStyle([5d, 3d], 0) };
    private readonly Brush drawingBadge = Terminal.Brush("Control");
    private readonly Brush warningBadge = Terminal.Brush("AccentSurface");
    private readonly Brush levelText = Terminal.Brush("Text");
    private PriceAlert[] priceAlertLevels = [];
    private Guid? selectedPriceAlert;
    private double? priceAlertPreview;
    private List<PriceLevelBadge>? priceLevelBadges;

    private readonly record struct PriceLevel(double Price, double Y, bool Selected, bool IsAlert = false);
    private readonly record struct PriceLevelBadge(PriceLevel Level, Rect Bounds);

    internal event Action<Guid, double>? PriceAlertMoved;
    internal event Action<Guid>? PriceAlertRemoved;

    internal void SetPriceAlertLevels(PriceAlert[] levels)
    {
        if (priceAlertLevels.AsSpan().SequenceEqual(levels)) return;
        var previous = SelectedAlert();
        priceAlertLevels = levels;
        var selected = SelectedAlert();
        if (selected == null) selectedPriceAlert = null;
        if (previous?.Price != selected?.Price) CancelPriceAlertMove();
        InvalidateVisual();
    }

    private PriceAlert? SelectedAlert() => selectedPriceAlert is { } id
        ? Array.Find(priceAlertLevels, alert => alert.Id == id) : null;

    private bool SelectPriceAlert(Point at, View view)
    {
        PriceAlert? match = null;
        double nearest = 5;
        foreach (var alert in priceAlertLevels)
        {
            double y = view.Y(alert.Price), distance = Math.Abs(at.Y - y);
            if (y < view.Plot.Top || y > view.Plot.Bottom || distance > nearest) continue;
            nearest = distance;
            match = alert;
        }
        selectedPriceAlert = match?.Id;
        if (match == null) return false;
        selectedDrawing = null;
        Start(Operation.PriceAlert, at, view, MouseButton.Left);
        InvalidateVisual();
        return true;
    }

    private static string CursorPrice(View view, double y, double step) =>
        ChartAxis.PriceLabel(view.Price(y), Math.Min(step / 10, view.Span / view.Plot.Height));

    private void MovePriceAlert(View view, double offset)
    {
        priceAlertPreview = null;
        if (Math.Abs(offset) < 3 || SelectedAlert() is not { } alert) return;
        double y = Math.Clamp(view.Y(alert.Price) + offset, view.Plot.Top, view.Plot.Bottom);
        if (double.TryParse(CursorPrice(view, y, PriceGridStep(view)), NumberStyles.Float, Russian, out double price) &&
            PriceAlert.IsPrice(price)) priceAlertPreview = price;
    }

    private void ReleasePriceAlert()
    {
        var alert = SelectedAlert();
        double? next = priceAlertPreview;
        CancelGesture();
        if (alert != null && next is { } price && alert.Price != price) PriceAlertMoved?.Invoke(alert.Id, price);
    }

    internal bool CancelPriceAlertMove()
    {
        if (drag?.Mode != Operation.PriceAlert) return false;
        CancelGesture();
        return true;
    }

    internal bool DeleteChartAnnotation()
    {
        if (SelectedAlert() is not { } alert) return DeleteDrawing();
        CancelPriceAlertMove();
        selectedPriceAlert = null;
        PriceAlertRemoved?.Invoke(alert.Id);
        InvalidateVisual();
        return true;
    }

    private IEnumerable<PriceLevel> VisiblePriceAlerts(View view)
    {
        foreach (var alert in priceAlertLevels)
        {
            bool selected = alert.Id == selectedPriceAlert;
            double price = selected ? priceAlertPreview ?? alert.Price : alert.Price;
            double y = view.Y(price);
            if (y >= view.Plot.Top && y <= view.Plot.Bottom) yield return new(price, y, selected, IsAlert: true);
        }
    }

    private void DrawPriceAlerts(DrawingContext dc, View view)
    {
        if (priceAlertLevels.Length == 0) return;
        foreach (var level in VisiblePriceAlerts(view))
            dc.DrawLine(level.Selected ? selectedAlertLine : alertLine, new Point(view.Plot.Left, level.Y),
                new Point(view.Plot.Right, level.Y));
    }

    private void LayoutPriceLevelBadges(View view)
    {
        priceLevelBadges = null;
        if (priceAlertLevels.Length == 0 && drawings.Length == 0) return;
        const double height = 16, gap = 2;
        var plot = view.Plot;
        if (plot.Height < height + 2 * gap) return;

        double axisWidth = ActualWidth - plot.Right;
        var alerts = VisiblePriceAlerts(view).OrderBy(level => level.Y)
            .Select(level => new PriceLevelBadge(level, new Rect(plot.Right, level.Y - height / 2, axisWidth - 2, height)))
            .ToArray();
        var levels = new List<PriceLevel>();
        foreach (var drawing in drawings)
        {
            if (!drawing.Horizontal) continue;
            var placed = movingAnchor is { } move && move.Original.Id == drawing.Id ? move.Preview : drawing;
            if (VisibleLine(placed, view) is { } line)
                levels.Add(new(placed.Start.Price, line.End.Y, drawing.Id == selectedDrawing));
        }
        if (levels.Count == 0 && alerts.Length == 0) return;

        var occupied = alerts.Select(badge => badge.Bounds).ToList();
        priceLevelBadges = [];
        foreach (var level in levels.OrderByDescending(level => level.Selected).ThenBy(level => level.Y))
        {
            var top = NearestBadgeTop(level.Y - height / 2, plot, height, gap, occupied);
            if (!top.HasValue) break;
            var bounds = new Rect(plot.Right + 5, top.Value, axisWidth - 7, height);
            priceLevelBadges.Add(new(level, bounds));
            int index = occupied.FindIndex(other => other.Top > bounds.Top);
            occupied.Insert(index < 0 ? occupied.Count : index, bounds);
        }
        priceLevelBadges.AddRange(alerts.OrderBy(badge => badge.Level.Selected));
    }

    private void DrawPriceLevelBadges(DrawingContext dc, View view)
    {
        if (priceLevelBadges == null) return;
        var plot = view.Plot;
        dc.PushClip(new RectangleGeometry(new Rect(plot.Right, plot.Top, ActualWidth - plot.Right, plot.Height)));
        foreach (var (level, bounds) in priceLevelBadges)
        {
            double center = bounds.Top + bounds.Height / 2;
            if (!level.IsAlert) dc.DrawLine(new Pen(level.Selected ? levelText : accent, 1),
                new Point(plot.Right, level.Y), new Point(bounds.Left, center));
            dc.DrawRoundedRectangle(level.Selected ? accent : level.IsAlert ? warningBadge : drawingBadge, null, bounds, 3, 3);
            var foreground = level.Selected ? onAccent : levelText;
            var label = Text(PriceAlert.Format(level.Price), foreground);
            double width = bounds.Width - 8;
            FitAxisLabel(label, width);
            for (int precision = 10; label.Width > width && precision >= 4; precision -= 2)
            {
                label = Text(level.Price.ToString($"G{precision}", Russian), foreground);
                FitAxisLabel(label, width);
            }
            dc.PushClip(new RectangleGeometry(bounds));
            dc.DrawText(label, new Point(bounds.Left + 4, center - label.Height / 2));
            dc.Pop();
        }
        dc.Pop();
    }
}

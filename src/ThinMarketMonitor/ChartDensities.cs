using System.Windows;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class CandlePlot
{
    private DensityChartLevel[] densityLevels = [];
    private DensityChartLevel[] densityLabelOrder = [];
    private readonly Dictionary<(string Color, bool Large), Pen> densityPens = [];

    internal void SetDensityLevels(DensityChartLevel[] levels)
    {
        if (ReferenceEquals(densityLevels, levels)) return;
        bool redraw = densityLevels.Length != levels.Length ||
            densityLevels.Zip(levels).Any(pair => pair.First.Price != pair.Second.Price ||
                pair.First.Level.Key.Side != pair.Second.Level.Key.Side ||
                pair.First.Level.FirstSeen != pair.Second.Level.FirstSeen || pair.First.Level.Color != pair.Second.Level.Color ||
                pair.First.Level.Large != pair.Second.Level.Large || pair.First.Level.Usd != pair.Second.Level.Usd ||
                pair.First.Level.Market.Key != pair.Second.Level.Market.Key);
        densityLevels = levels;
        if (!redraw) return;
        densityLabelOrder = levels.OrderByDescending(entry => entry.Level.Usd).ToArray();
        densityPens.Clear();
        InvalidateVisual();
    }

    private void DrawDensityRays(DrawingContext dc, View view)
    {
        foreach (var entry in densityLevels)
        {
            if (!DensityRay(view, entry, out double start, out double y)) continue;
            var level = entry.Level;
            var pen = DensityPen(level);
            double x = Math.Max(view.Plot.Left, start);
            dc.DrawLine(pen, new Point(x, y), new Point(view.Plot.Right, y));
            if (start >= view.Plot.Left)
                dc.DrawEllipse(pen.Brush, null, new Point(x, y), level.Large ? 2.4 : 1.8, level.Large ? 2.4 : 1.8);
        }
    }

    private void DrawDensityLabels(DrawingContext dc, View view, Rect priceBadge)
    {
        const double height = 17, gap = 2, logo = 12, margin = 3;
        var plot = view.Plot;
        if (densityLabelOrder.Length == 0 || plot.Height < height + 2 * gap || plot.Width < 78) return;
        var badges = new List<Rect>();
        if (!priceBadge.IsEmpty) badges.Add(priceBadge);
        dc.PushClip(new RectangleGeometry(plot));
        foreach (var entry in densityLabelOrder)
        {
            if (!DensityRay(view, entry, out _, out double y)) continue;
            var level = entry.Level;
            bool buy = level.Key.Side == BookSide.Bid;
            var label = Text(DensityLabel(level), Terminal.Brush(buy ? "Required" : "Excluded"));
            double width = margin * 3 + logo + label.Width;
            if (width + 2 * gap > plot.Width) continue;
            double x = plot.Right - width - gap;
            double? top = NearestBadgeTop(y - height / 2, plot, height, gap, badges);
            if (!top.HasValue) break;
            var badge = new Rect(x, top.Value, width, height);
            double center = badge.Top + height / 2;
            if (Math.Abs(center - y) > .5)
                dc.DrawLine(DensityPen(level), new Point(plot.Right - gap, y), new Point(plot.Right - gap, center));
            dc.DrawRoundedRectangle(Terminal.Brush(buy ? "RequiredSurface" : "ExcludedSurface"), DensityPen(level), badge, 3, 3);
            dc.DrawImage(Terminal.VenueLogoSource(level.Market.Venue), new Rect(x + margin, center - logo / 2, logo, logo));
            dc.DrawText(label, new Point(x + logo + margin * 2, center - label.Height / 2));
            int next = badges.FindIndex(other => other.Top > badge.Top);
            badges.Insert(next < 0 ? badges.Count : next, badge);
        }
        dc.Pop();
    }

    private static double? NearestBadgeTop(double desired, Rect plot, double height, double gap, List<Rect> occupied)
    {
        double first = plot.Top + gap;
        double last = plot.Bottom - height - gap;
        double? closest = null;
        foreach (var other in occupied)
        {
            Consider(Math.Min(last, other.Top - height - gap));
            first = Math.Max(first, other.Bottom + gap);
        }
        Consider(last);
        return closest;

        void Consider(double limit)
        {
            if (first > limit) return;
            double top = Math.Clamp(desired, first, limit);
            if (closest == null || Math.Abs(top - desired) < Math.Abs(closest.Value - desired)) closest = top;
        }
    }

    internal static string DensityLabel(DensityLevel level) =>
        $"${ChartVolume.CompactUsd(level.Usd, "K")} · {level.Market.Source.Segment}";

    private static bool DensityRay(View view, DensityChartLevel entry, out double start, out double y)
    {
        start = view.X(entry.Level.FirstSeen);
        y = view.Y(entry.Price);
        return start < view.Plot.Right && y >= view.Plot.Top && y <= view.Plot.Bottom;
    }

    private Pen DensityPen(DensityLevel level)
    {
        var key = (level.Color, level.Large);
        if (densityPens.TryGetValue(key, out var pen)) return pen;
        pen = new Pen(DensityTint.Read(level.Color), level.Large ? 1.8 : 1);
        pen.Freeze();
        return densityPens[key] = pen;
    }
}

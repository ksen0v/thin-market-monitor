using System.Windows;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class CandlePlot
{
    private DensityLevel[] densityLevels = [];
    private DensityLevel[] densityLabelOrder = [];
    private readonly Dictionary<(string Color, bool Large), Pen> densityPens = [];

    internal void SetDensityLevels(DensityLevel[] levels)
    {
        if (ReferenceEquals(densityLevels, levels)) return;
        bool redraw = densityLevels.Length != levels.Length ||
            densityLevels.Zip(levels).Any(pair => pair.First.Key != pair.Second.Key ||
                pair.First.FirstSeen != pair.Second.FirstSeen || pair.First.Color != pair.Second.Color ||
                pair.First.Large != pair.Second.Large || pair.First.Usd != pair.Second.Usd ||
                pair.First.Market.Source != pair.Second.Market.Source);
        densityLevels = levels;
        if (!redraw) return;
        densityLabelOrder = levels.OrderByDescending(level => level.Usd).ToArray();
        densityPens.Clear();
        InvalidateVisual();
    }

    private void DrawDensityRays(DrawingContext dc, View view)
    {
        foreach (var level in densityLevels)
        {
            if (!DensityRay(view, level, out double start, out double y)) continue;
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
        dc.PushClip(new RectangleGeometry(plot));
        foreach (var level in densityLabelOrder)
        {
            if (!DensityRay(view, level, out _, out double y)) continue;
            bool buy = level.Key.Side == BookSide.Bid;
            var label = Text(DensityLabel(level), Terminal.Brush(buy ? "Required" : "Excluded"));
            double width = margin * 3 + logo + label.Width;
            if (width + 2 * gap > plot.Width) continue;
            double top = Math.Clamp(y - height / 2, plot.Top + gap, plot.Bottom - height - gap);
            double x = plot.Right - width - gap;
            var badge = new Rect(x, top, width, height);
            if (!Free(badge))
            {
                var above = new Rect(x, top - height - gap, width, height);
                var below = new Rect(x, top + height + gap, width, height);
                if (Free(above)) badge = above;
                else if (Free(below)) badge = below;
                else continue;
            }
            double center = badge.Top + height / 2;
            if (Math.Abs(center - y) > .5)
                dc.DrawLine(DensityPen(level), new Point(plot.Right - gap, y), new Point(plot.Right - gap, center));
            dc.DrawRoundedRectangle(Terminal.Brush(buy ? "RequiredSurface" : "ExcludedSurface"), DensityPen(level), badge, 3, 3);
            dc.DrawImage(Terminal.VenueLogoSource(level.Market.Venue), new Rect(x + margin, center - logo / 2, logo, logo));
            dc.DrawText(label, new Point(x + logo + margin * 2, center - label.Height / 2));
            badges.Add(badge);
            if (badges.Count * height >= plot.Height - 2 * gap) break;
        }
        dc.Pop();

        bool Free(Rect badge) => badge.Top >= plot.Top + gap && badge.Bottom <= plot.Bottom - gap &&
            !badge.IntersectsWith(priceBadge) && !badges.Any(other => other.Top < badge.Bottom + gap && other.Bottom > badge.Top - gap);
    }

    internal static string DensityLabel(DensityLevel level) =>
        $"${ChartVolume.CompactUsd(level.Usd, "K")} · {level.Market.Source.Segment}";

    private static bool DensityRay(View view, DensityLevel level, out double start, out double y)
    {
        start = view.X(level.FirstSeen);
        y = view.Y(level.Key.Price);
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

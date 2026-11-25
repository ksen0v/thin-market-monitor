using System.Windows;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class CandlePlot
{
    private DensityLevel[] densityLevels = [];
    private readonly Dictionary<(string Color, bool Large), Pen> densityPens = [];

    internal void SetDensityLevels(DensityLevel[] levels)
    {
        if (ReferenceEquals(densityLevels, levels)) return;
        bool redraw = densityLevels.Length != levels.Length ||
            densityLevels.Zip(levels).Any(pair => pair.First.Key != pair.Second.Key ||
                pair.First.FirstSeen != pair.Second.FirstSeen || pair.First.Color != pair.Second.Color ||
                pair.First.Large != pair.Second.Large);
        densityLevels = levels;
        if (!redraw) return;
        densityPens.Clear();
        InvalidateVisual();
    }

    private void DrawDensityRays(DrawingContext dc, View view)
    {
        foreach (var level in densityLevels)
        {
            double start = view.X(level.FirstSeen);
            double y = view.Y(level.Key.Price);
            if (start >= view.Plot.Right || y < view.Plot.Top || y > view.Plot.Bottom) continue;
            var key = (level.Color, level.Large);
            if (!densityPens.TryGetValue(key, out var pen))
            {
                pen = new Pen(DensityTint.Read(level.Color), level.Large ? 1.8 : 1);
                pen.Freeze();
                densityPens.Add(key, pen);
            }
            double x = Math.Max(view.Plot.Left, start);
            dc.DrawLine(pen, new Point(x, y), new Point(view.Plot.Right, y));
            if (start >= view.Plot.Left)
                dc.DrawEllipse(pen.Brush, null, new Point(x, y), level.Large ? 2.4 : 1.8, level.Large ? 2.4 : 1.8);
        }
    }
}

using System.Windows;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class CandlePlot
{
    private static double PriceGridStep(View view) => ChartAxis.NiceStep(view.Span, Math.Max(2, (int)(view.Plot.Height / 42)));

    private ChartTimeTick[] TimeGridTicks(View view) => ChartAxis.TimeTicks(view.Start, view.End, BarTime,
        Math.Max(2, (int)(view.Plot.Width / 82)), TimeZoneInfo.Local);

    private void DrawPriceGrid(DrawingContext dc, View view, double priceStep, Rect priceBadge)
    {
        var plot = view.Plot;
        double lastBottom = double.NegativeInfinity;
        foreach (double level in ChartAxis.NumericTicks(view.Low, view.High, priceStep).Reverse())
        {
            double y = Math.Clamp(view.Y(level), plot.Top, plot.Bottom);
            dc.DrawLine(gridLine, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(ChartAxis.PriceLabel(level, priceStep), axis);
            FitAxisLabel(label, ActualWidth - plot.Right - 7);
            double labelY = Math.Clamp(y - label.Height / 2, plot.Top, Math.Max(plot.Top, plot.Bottom - label.Height));
            double labelBottom = labelY + label.Height;
            if ((!priceBadge.IsEmpty && labelBottom + 2 >= priceBadge.Top && labelY - 2 <= priceBadge.Bottom)
                || labelY < lastBottom + 2) continue;
            dc.DrawText(label, new Point(plot.Right + 5, labelY));
            lastBottom = labelBottom;
        }
    }

    private void DrawTimeGrid(DrawingContext dc, View time, Rect plot, ChartTimeTick[] marks)
    {
        foreach (var tick in marks)
        {
            double x = time.X(tick.Time);
            dc.DrawLine(gridLine, new Point(x, plot.Top), new Point(x, plot.Bottom));
        }
    }

    private void DrawTimeScale(DrawingContext dc, View view, ChartTimeTick[] marks)
    {
        double right = double.NegativeInfinity;
        foreach (var tick in marks)
        {
            var label = Text(tick.Label, axis);
            FitAxisLabel(label, view.Plot.Width);
            double left = Math.Clamp(view.X(tick.Time) - label.Width / 2, view.Plot.Left,
                Math.Max(view.Plot.Left, view.Plot.Right - label.Width));
            if (left < right + 7) continue;
            dc.DrawText(label, new Point(left, ActualHeight - TimeAxisHeight + 2));
            right = left + label.Width;
        }
    }

    private static void FitAxisLabel(FormattedText label, double width)
    {
        if (label.Width > width) label.SetFontSize(Math.Max(7, 11 * width / label.Width));
    }
}

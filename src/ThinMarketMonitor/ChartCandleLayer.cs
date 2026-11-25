using System.Windows;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed class CandlePixels
{
    internal readonly record struct Column(double X, double Open, double High, double Low, double Close, double? Volume, bool Observed)
    {
        public bool Rising => Close >= Open;
    }

    private readonly List<Column> columns = [];
    private double scale;
    private bool merge;
    private double lastPixel;
    public IReadOnlyList<Column> Columns => columns;
    public int Known { get; private set; }
    public int Total { get; private set; }
    public double BodyWidth { get; private set; }

    public void Reset(double spacing, double dpi)
    {
        columns.Clear();
        scale = dpi;
        merge = spacing * dpi < 1;
        BodyWidth = merge ? 1 / dpi : spacing * .67;
        Known = Total = 0;
    }

    public void Add(Column bar)
    {
        Total++;
        if (bar.Volume.HasValue) Known++;
        if (!merge)
        {
            columns.Add(bar);
            return;
        }
        double pixel = Math.Floor(bar.X * scale);
        if (columns.Count != 0 && pixel == lastPixel)
        {
            var previous = columns[^1];
            columns[^1] = previous with
            {
                High = Math.Max(previous.High, bar.High), Low = Math.Min(previous.Low, bar.Low), Close = bar.Close,
                Volume = previous.Volume == null ? bar.Volume : previous.Volume + (bar.Volume ?? 0),
                Observed = previous.Observed || bar.Observed || previous.Volume.HasValue != bar.Volume.HasValue
            };
        }
        else columns.Add(bar with { X = (pixel + .5) / scale });
        lastPixel = pixel;
    }
}

internal sealed partial class CandlePlot
{
    private readonly CandlePixels candlePixels = new();

    private ReadOnlySpan<Candle> VisibleCandles(double start, double end, bool includeEnd = false)
    {
        int first = CandleIndex(start - BarTime, after: true);
        return candles.AsSpan(first, CandleIndex(end, after: includeEnd) - first);
    }

    private int CandleIndex(double time, bool after)
    {
        int left = 0, right = candles.Length;
        while (left < right)
        {
            int middle = left + (right - left) / 2;
            long stamp = candles[middle].Time;
            if (stamp < time || (after && stamp == time)) left = middle + 1;
            else right = middle;
        }
        return left;
    }

    private void DrawCandleSeries(DrawingContext dc, View view)
    {
        double width = candlePixels.BodyWidth;
        Pen rising = width < 1 ? new(up, width) : upLine;
        Pen falling = width < 1 ? new(down, width) : downLine;
        foreach (var bar in candlePixels.Columns)
        {
            dc.DrawLine(bar.Rising ? rising : falling, new(bar.X, view.Y(bar.High)), new(bar.X, view.Y(bar.Low)));
            double top = view.Y(Math.Max(bar.Open, bar.Close));
            double height = Math.Max(1, view.Y(Math.Min(bar.Open, bar.Close)) - top);
            dc.DrawRectangle(bar.Rising ? up : down, null, new Rect(bar.X - width / 2, top, width, height));
        }
    }
}

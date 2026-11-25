using System.Windows;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed partial class CandlePlot
{
    private readonly Dictionary<(string Instrument, bool Usd), (double Low, double High)> interestRanges = [];
    private InterestWindow? lastInterestWindow;
    private ChartPaneWeights? paneWeights;
    internal event Action<InterestWindow>? InterestRangeChanged;

    private readonly record struct InterestPane(InterestLayer Layer, Rect Header, Rect Graph);
    private sealed record InterestAxis(string Instrument, Venue Venue, string Unit, InterestScale Scale, bool Estimated, bool Usd);
    private readonly record struct InterestSeries(InterestPoint[] Points, InterestCandle[]? Candles, long Interval)
    {
        public bool HasData => (Candles?.Length ?? Points.Length) > 0;
    }

    internal void SetPaneWeights(ChartPaneWeights? weights)
    {
        paneWeights = weights;
        InvalidateVisual();
    }

    internal double[] MeasurePanes(double height) =>
        ChartPaneLayout.Measure(height - Top - TimeAxisHeight, indicators?.Interest ?? [], paneWeights);

    internal static IEnumerable<Rect> PaneDividers(double width, double[] sizes)
    {
        double y = Top + sizes[0];
        for (int i = 1; i < sizes.Length; i++)
        {
            yield return new(Left, y, Math.Max(0, width - Left), ChartPaneLayout.DividerHeight);
            y += sizes[i] + ChartPaneLayout.DividerHeight;
        }
    }

    private void ReportInterestRange(View view)
    {
        if (indicators?.Options.HasInterest != true || InterestRangeChanged == null) return;
        // Request OI only over the portion of the visible window with chart history.
        long from = (long)Math.Max(view.Start, candles[0].Time);
        long until = (long)Math.Min(view.End, candles[^1].Time + BarTime);
        if (until <= from) return;
        long quantum = Math.Max(60_000, BarTime);
        var requested = new InterestWindow(ExchangeApi.CandleStart(from, quantum), ExchangeApi.CandleStart(until, quantum) + quantum);
        if (lastInterestWindow == requested) return;
        lastInterestWindow = requested;
        InterestRangeChanged.Invoke(requested);
    }

    private IEnumerable<InterestPane> InterestPanes(View view)
    {
        if (indicators is not { Interest.Length: > 0 } data || view.PaneHeights is not { } sizes) yield break;
        int index = 0;
        foreach (var divider in PaneDividers(ActualWidth, sizes))
        {
            double headerHeight = ChartPaneLayout.HeaderHeight;
            double graphHeight = sizes[index + 1] - headerHeight - 1;
            if (graphHeight >= 14)
                yield return new(data.Interest[index], new(view.Plot.Left, divider.Bottom, divider.Width, headerHeight),
                    new(view.Plot.Left, divider.Bottom + headerHeight, view.Plot.Width, graphHeight));
            index++;
        }
    }

    private InterestPane? InterestPaneAt(Point at, View view, bool axisOnly)
    {
        foreach (var pane in InterestPanes(view))
            if (axisOnly ? at.X >= pane.Graph.Right && at.Y >= pane.Graph.Top && at.Y <= pane.Graph.Bottom : pane.Graph.Contains(at))
                return pane;
        return null;
    }

    private static string InterestInstrument(InterestLayer layer) => layer.Market?.Key ?? layer.Venue.ToString();
    private static int InterestIntervals(Rect graph) => Math.Clamp((int)(graph.Height / 23), 1, 6);

    private InterestAxis? GetInterestAxis(InterestPane pane, View view) =>
        GetInterestAxis(pane, ReadInterestSeries(pane.Layer, view));

    private InterestAxis? GetInterestAxis(InterestPane pane, InterestSeries series)
    {
        string instrument = InterestInstrument(pane.Layer);
        if (drag is { Mode: Operation.Ruler, Interest: { } measuring } && measuring.Instrument == instrument)
            return measuring;
        if (pane.Layer.Reading is not { } reading) return null;
        bool usd = InterestMode(pane.Layer.Venue).Usd;
        int intervals = InterestIntervals(pane.Graph);
        var scale = interestRanges.TryGetValue((instrument, usd), out var fixedRange)
            ? InterestScale.Fixed(fixedRange.Low, fixedRange.High, intervals)
            : series.Candles is { Length: > 0 } bars ? InterestScale.Create(bars.Min(bar => bar.Low), bars.Max(bar => bar.High), intervals)
            : InterestScale.Create(series.Points, intervals);
        return scale is { } value ? new(instrument, pane.Layer.Venue, usd ? "USD" : reading.Unit, value,
            usd && (series.Candles?.Any(bar => bar.Estimated) ?? series.Points.Any(point => point.Estimated)), usd) : null;
    }

    private void SetInterestRange(InterestAxis scale, double low, double span)
    {
        double high = low + span;
        if (InterestScale.Fixed(low, high, 4).HasValue)
            interestRanges[InterestRangeKey(scale)] = (low, high);
    }

    private static (string Instrument, bool Usd) InterestRangeKey(InterestAxis axis) => (axis.Instrument, axis.Usd);

    private bool StartInterestAxisDrag(Point at, View view, bool reset)
    {
        if (InterestPaneAt(at, view, axisOnly: true) is not { } pane ||
            GetInterestAxis(pane, view) is not { } measure) return false;
        if (reset)
        {
            interestRanges.Remove(InterestRangeKey(measure));
            InvalidateVisual();
            return true;
        }
        return Start(Operation.InterestPrice, at, InterestView(view, pane, measure.Scale), System.Windows.Input.MouseButton.Left, measure);
    }

    private static View InterestView(View chart, InterestPane pane, InterestScale scale) =>
        new(pane.Graph, chart.Start, chart.End, scale.Low, scale.High);

    private InterestPresentation InterestMode(Venue venue) => indicators?.Options.PresentationFor(venue) ?? new();

    private InterestSeries ReadInterestSeries(InterestLayer layer, View view)
    {
        if (!InterestMode(layer.Venue).Candles || layer.Reading is not { } reading)
            return new(VisibleInterest(layer, view.Start, view.End), null, 0);
        long cadence = reading.Period == 0 ? 60_000 : reading.Period * 1000L;
        long interval = Math.Max(cadence, BarTime);
        long first = ExchangeApi.CandleStart((long)Math.Floor(view.Start), interval);
        double end = Math.Ceiling(view.End / interval) * interval;
        var points = VisibleInterest(layer, first - interval, end);
        var bars = InterestCandle.Build(points, interval, cadence + cadence / 3)
            .Where(bar => bar.Time >= first && bar.Time < view.End).ToArray();
        return new([], bars, interval);
    }

    private InterestPoint[] VisibleInterest(InterestLayer layer, double from, double until)
    {
        if (layer.Reading is not { } reading) return [];
        bool usd = InterestMode(layer.Venue).Usd;
        return reading.Points.Where(point => point.Time >= from && point.Time < until)
            .Select(point => point with { Value = point.InUnit(usd) }).Where(point => point.Value >= 0 && double.IsFinite(point.Value)).ToArray();
    }

    private IReadOnlyList<InterestScale?> DrawInterest(DrawingContext dc, View view, ChartTimeTick[] timeTicks)
    {
        ReportInterestRange(view);
        if (view.PaneHeights == null) return [];
        var scales = new List<InterestScale?>(view.PaneHeights.Length - 1);
        foreach (var pane in InterestPanes(view))
        {
            var graph = pane.Graph;
            var data = ReadInterestSeries(pane.Layer, view);
            var numeric = GetInterestAxis(pane, data);
            scales.Add(numeric?.Scale);
            var color = InterestBrush(pane.Layer.Venue);
            DrawInterestTitle(dc, pane, numeric, color);
            DrawTimeGrid(dc, view, graph, timeTicks);
            if (!data.HasData || numeric == null)
            {
                if (numeric == null) DrawText(dc, "—", graph.Right + 5, graph.Top + 1);
                else DrawInterestGrid(dc, InterestView(view, pane, numeric.Scale), numeric.Scale, color);
                string local = pane.Layer.Status ?? (pane.Layer.Reading is { Points.Length: 0 } reading
                    ? debugMode ? reading.Status ?? "Нет данных" : "Нет данных" : null)
                    ?? (InterestMode(pane.Layer.Venue).Usd && pane.Layer.Reading is { Points.Length: > 0 } set &&
                        !set.Points.Any(point => point.Usd.HasValue) ? "Нет оценки в USD" : "Нет точек в диапазоне");
                dc.PushClip(new RectangleGeometry(graph));
                DrawText(dc, local, graph.Left + 5, graph.Top + Math.Max(0, (graph.Height - 12) / 2));
                dc.Pop();
                continue;
            }

            var series = InterestView(view, pane, numeric.Scale);
            DrawInterestGrid(dc, series, numeric.Scale, color);
            dc.PushClip(new RectangleGeometry(graph));
            if (data.Candles is { } bars)
            {
                candlePixels.Reset(series.Plot.Width * data.Interval / (series.End - series.Start), VisualTreeHelper.GetDpi(this).DpiScaleX);
                foreach (var bar in bars)
                    candlePixels.Add(new(series.X(bar.Time + data.Interval / 2.0), bar.Open, bar.High, bar.Low, bar.Close, null, false));
                DrawCandleSeries(dc, series);
            }
            else
            {
                var line = new Pen(color, 1.45);
                Point? previous = null;
                long last = 0;
                int period = pane.Layer.Reading!.Period;
                long spacing = period == 0 ? 85_000 : period * 1300L;
                foreach (var point in data.Points)
                {
                    var at = new Point(series.X(point.Time), series.Y(point.Value));
                    if (previous is { } before && point.Time - last <= spacing) dc.DrawLine(line, before, at);
                    else dc.DrawEllipse(color, null, at, 1.7, 1.7);
                    (previous, last) = (at, point.Time);
                }
            }
            dc.Pop();
        }
        return scales;
    }

    private void DrawInterestTitle(DrawingContext dc, InterestPane pane, InterestAxis? numeric, Brush color)
    {
        var header = pane.Header;
        dc.DrawLine(gridLine, header.TopLeft, header.TopRight);
        string unit = numeric == null ? (InterestMode(pane.Layer.Venue).Usd ? "USD" : pane.Layer.Reading?.Unit ?? "") :
            numeric.Estimated ? "≈ USD" : numeric.Unit;
        string source = pane.Layer.Venue.Label();
        string? symbol = pane.Layer.Market?.Symbol;
        string ending = (unit.Length == 0 ? "" : " · " + unit) + (pane.Layer.Reading?.LoadingHistory == true ? " · загрузка…" : "");
        var caption = Text($"OI · {source}" + (symbol == null ? "" : " " + symbol) + ending, color);
        double contentWidth = Math.Max(0, header.Width - ChartSurface.ControlsWidth - 6);
        double available = Math.Max(0, contentWidth - 21);
        if (caption.Width > available) caption = Text($"{source}{ending}", color);
        if (caption.Width > available) caption = Text(source, color);
        FitAxisLabel(caption, available);
        dc.PushClip(new RectangleGeometry(new Rect(header.Location, new Size(contentWidth, header.Height))));
        dc.DrawImage(Terminal.VenueLogoSource(pane.Layer.Venue), new Rect(header.Left + 4, header.Top + 4, 13, 13));
        dc.DrawText(caption, new(header.Left + 21, header.Top + 3));
        dc.Pop();
    }

    private void DrawInterestGrid(DrawingContext dc, View series, InterestScale scale, Brush brush)
    {
        var graph = series.Plot;
        double lastLabel = double.PositiveInfinity;
        foreach (var tick in scale.Ticks())
        {
            double y = Math.Clamp(series.Y(tick), graph.Top, graph.Bottom);
            dc.DrawLine(gridLine, new(graph.Left, y), new(graph.Right + 3, y));
            if (lastLabel - y < 16) continue;
            double available = ActualWidth - graph.Right - 6;
            var label = Text(scale.Label(tick), brush);
            FitAxisLabel(label, available);
            double top = Math.Clamp(y - label.Height / 2, graph.Top - label.Height / 4, graph.Bottom - label.Height * .75);
            dc.DrawText(label, new(graph.Right + 5, top));
            lastLabel = y;
        }
    }

    private static Brush InterestBrush(Venue venue) => interestColors[(int)venue % interestColors.Length];
}

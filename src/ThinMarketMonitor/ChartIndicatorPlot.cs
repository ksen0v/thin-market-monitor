using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinMarketMonitor;

internal sealed record IndicatorDisplay(Market Market, ChartIndicatorOptions Options, ExecutionTape? Trades, ExecutionTape? Liquidations,
    string ProfileStatus, string LiquidationStatus, InterestLayer[] Interest);

internal sealed partial class CandlePlot
{
    private static readonly Brush volumeUp = Ink(113, 171, 110, 120);
    private static readonly Brush volumeDown = Ink(196, 119, 100, 120);
    private static readonly Brush observedVolume = Ink(116, 136, 165, 150);
    private static readonly Brush volumeProfile = Ink(94, 167, 243, 212);
    private static readonly Brush liquidationNeutral = Ink(187, 134, 232, 230);
    private static readonly Brush longLiquidation = Ink(242, 133, 114, 230);
    private static readonly Brush shortLiquidation = Ink(241, 184, 97, 230);
    private static readonly Brush[] interestColors =
    [
        Ink(238, 198, 95), Ink(255, 156, 101), Ink(222, 222, 219), Ink(112, 207, 190),
        Ink(120, 164, 248), Ink(152, 215, 131), Ink(227, 130, 224), Ink(231, 159, 107),
        Ink(170, 204, 217), Ink(183, 142, 238), Ink(254, 118, 134), Ink(239, 239, 147), Ink(106, 224, 153)
    ];
    private IndicatorDisplay? indicators;
    private string? interestMarketKey;
    private Venue? interestScaleVenue;
    private readonly List<(Venue Venue, Rect Hit)> interestLegend = [];
    private (string Caption, string Detail) lastSummary;
    internal event Action<string, string>? IndicatorSummary;

    private sealed record InterestAxis(Venue Venue, string Unit, InterestScale Scale);
    private InterestLayer? InterestScaleLayer => indicators?.Interest.FirstOrDefault(layer => layer.Venue == (drag?.Interest?.Venue ?? interestScaleVenue))
        ?? indicators?.Interest.FirstOrDefault(layer => layer.Reading is { Points.Length: > 0 })
        ?? indicators?.Interest.FirstOrDefault();
    internal Venue? InterestScaleVenue => InterestScaleLayer?.Venue;

    private double InterestPaneHeight => indicators?.Options.HasInterest == true
        ? Math.Min(Math.Clamp(ActualHeight * .30, 51, 155), Math.Max(0, ActualHeight - 50)) : 0;

    internal void UpdateIndicators(IndicatorDisplay? value)
    {
        if (value is { } next && next.Market.Key != interestMarketKey)
        {
            interestMarketKey = next.Market.Key;
            interestScaleVenue = null;
        }
        if (drag?.Interest is { } measuring &&
            (value == null || value.Market.Key != indicators?.Market.Key || !value.Interest.Any(layer => layer.Venue == measuring.Venue)))
            CancelGesture();
        if (value == null) lastSummary = default;
        indicators = value;
        InvalidateVisual();
    }

    internal bool SelectInterestScale(Venue venue)
    {
        if (indicators?.Interest.Any(layer => layer.Venue == venue) != true) return false;
        interestScaleVenue = venue;
        InvalidateVisual();
        return true;
    }

    private bool ClickInterestLegend(Point at)
    {
        foreach (var (venue, hit) in interestLegend)
            if (hit.Contains(at)) return SelectInterestScale(venue);
        return false;
    }

    private (int Known, int Total) DrawCandleVolume(DrawingContext dc, View view, double thickness)
    {
        if (indicators is not { Options.Volume: true } data) return default;
        double maximum = 0;
        int total = 0, known = 0;
        foreach (var candle in candles)
        {
            if (!Intersects(candle, view)) continue;
            total++;
            if (ChartVolume.Amount(data.Market, candle) is { } amount)
            {
                known++;
                maximum = Math.Max(maximum, amount);
            }
        }
        if (!(maximum > 0)) return (known, total);
        double height = Math.Clamp(view.Plot.Height * 0.23, 8, 56);
        foreach (var candle in candles)
        {
            if (!Intersects(candle, view) || ChartVolume.Amount(data.Market, candle) is not (> 0 and var amount)) continue;
            double bar = Math.Max(0.8, height * amount / maximum);
            Brush brush = candle.ObservedVolume ? observedVolume : candle.Close >= candle.Open ? volumeUp : volumeDown;
            dc.DrawRectangle(brush, null, new Rect(view.X(candle.Time + BarTime / 2d) - thickness / 2,
                view.Plot.Bottom - bar, thickness, bar));
        }
        return (known, total);
    }

    private bool Intersects(Candle candle, View view) => candle.Time < view.End && candle.Time + BarTime > view.Start;

    private (ExecutionProfile? Volume, ExecutionProfile? Liquidations) DrawProfiles(DrawingContext dc, View view)
    {
        if (indicators is not { } data) return default;
        int levels = Math.Clamp((int)(view.Plot.Height / 4), 8, 60);
        double row = view.Plot.Height / levels;
        double width = Math.Clamp(view.Plot.Width * .13, 13, 80);
        double right = view.Plot.Right - 2;
        ExecutionProfile? liquidations = Paint(data.Liquidations, liquidations: true);
        ExecutionProfile? volume = Paint(data.Trades, liquidations: false);
        return (volume, liquidations);

        ExecutionProfile? Paint(ExecutionTape? tape, bool liquidations)
        {
            if (tape == null) return null;
            var profile = ExecutionProfile.Calculate(tape.Events, view.Start, view.End, view.Low, view.High, levels);
            double largest = profile.Buckets.Max(bucket => bucket.Total);
            for (int i = 0; largest > 0 && i < levels; i++)
            {
                var bucket = profile.Buckets[i];
                double cursor = right, y = view.Plot.Bottom - (i + 1) * row;
                Part(bucket.Neutral, liquidations ? liquidationNeutral : volumeProfile);
                Part(bucket.Long, longLiquidation);
                Part(bucket.Short, shortLiquidation);

                void Part(double size, Brush brush)
                {
                    if (size <= 0) return;
                    double span = Math.Min(width, Math.Max(.6, width * size / largest));
                    dc.DrawRectangle(brush, null, new Rect(cursor - span, y, span, Math.Max(.6, row - .6)));
                    cursor -= span;
                }
            }
            if (largest > 0)
                dc.DrawLine(new Pen(liquidations ? liquidationNeutral : volumeProfile, 1),
                    new Point(right, view.Plot.Top), new Point(right, view.Plot.Bottom));
            right -= width + 4;
            return profile;
        }
    }

    private Rect InterestGraph(View view) => new(view.Plot.Left, view.Plot.Bottom + 17,
        view.Plot.Width, Math.Max(0, InterestPaneHeight - 18));

    private static int InterestIntervals(Rect graph) => Math.Clamp((int)(graph.Height / 23), 1, 6);

    private InterestAxis? GetInterestAxis(View view, Rect graph)
    {
        if (graph.Height < 15 || InterestScaleLayer is not { Reading: { } reading } layer) return null;
        return InterestScale.Create(VisiblePoints(reading, view), InterestIntervals(graph)) is { } scale
            ? new(layer.Venue, reading.Unit, scale) : null;
    }

    private void DrawInterest(DrawingContext dc, View view, int columns)
    {
        interestLegend.Clear();
        if (indicators is not { Options.HasInterest: true } data || InterestPaneHeight < 34) return;
        var graph = InterestGraph(view);
        var selected = InterestScaleLayer;
        var numeric = drag?.Interest ?? GetInterestAxis(view, graph);
        DrawInterestLegend(dc, view.Plot, data.Interest, selected, numeric);

        for (int i = 0; i < columns; i++)
        {
            double x = graph.Left + graph.Width * i / (columns - 1);
            dc.DrawLine(gridLine, new(x, graph.Top), new(x, graph.Bottom));
        }
        if (numeric != null)
        {
            var scale = numeric.Scale;
            var axisView = new View(graph, view.Start, view.End, scale.Low, scale.High);
            Brush brush = InterestBrush(numeric.Venue);
            double lastLabel = double.PositiveInfinity;
            foreach (var tick in scale.Ticks())
            {
                double y = Math.Clamp(axisView.Y(tick), graph.Top, graph.Bottom);
                dc.DrawLine(gridLine, new(graph.Left, y), new(graph.Right + 3, y));
                bool topTick = y - graph.Top < .5;
                if (graph.Height < 23 && !topTick) continue;
                if ((!topTick && y - graph.Top < 17) || lastLabel - y < 17) continue;
                DrawInterestTick(dc, graph, scale.Label(tick), brush, y);
                lastLabel = y;
            }
        }
        else
        {
            DrawText(dc, "—", graph.Right + 5, graph.Top + 1);
            if (data.Interest.All(layer => layer.Reading == null || !layer.Reading.Points.Any(point => point.Time >= view.Start && point.Time < view.End)))
                DrawText(dc, "Нет точек в диапазоне", graph.Left + 5, graph.Top + Math.Max(0, (graph.Height - 12) / 2));
        }

        dc.PushClip(new RectangleGeometry(graph));
        foreach (var layer in data.Interest)
        {
            if (layer.Reading is not { } reading) continue;
            var points = VisiblePoints(reading, view);
            bool primary = numeric?.Venue == layer.Venue;
            var scale = primary ? numeric!.Scale : InterestScale.Create(points, InterestIntervals(graph));
            if (scale is not { } range) continue;
            var series = new View(graph, view.Start, view.End, range.Low, range.High);
            var brush = InterestBrush(layer.Venue);
            var line = new Pen(brush, primary ? 1.55 : 1.15);
            dc.PushOpacity(primary ? 1 : .6);
            Point? previous = null;
            long last = 0;
            long spacing = reading.Period == 0 ? 85_000 : reading.Period * 1300L;
            foreach (var point in points)
            {
                var at = new Point(series.X(point.Time), series.Y(point.Value));
                if (previous is { } before && point.Time - last <= spacing) dc.DrawLine(line, before, at);
                else dc.DrawEllipse(brush, null, at, 1.7, 1.7);
                (previous, last) = (at, point.Time);
            }
            dc.Pop();
        }
        dc.Pop();
    }

    private void DrawInterestLegend(DrawingContext dc, Rect plot, InterestLayer[] layers, InterestLayer? selected, InterestAxis? numeric)
    {
        double y = plot.Bottom + 1;
        dc.DrawLine(gridLine, new(plot.Left, y), new(ActualWidth, y));
        var region = new Rect(plot.Left, y, ActualWidth - plot.Left, 16);
        dc.PushClip(new RectangleGeometry(region));
        var title = Text("OI", axis);
        dc.DrawText(title, new(region.Left + 3, y + 2));
        double x = region.Left + title.Width + 10;
        if (selected != null) DrawSource(selected, primary: true);
        foreach (var layer in layers)
            if (layer.Venue != selected?.Venue) DrawSource(layer, primary: false);
        dc.Pop();

        void DrawSource(InterestLayer layer, bool primary)
        {
            Brush color = InterestBrush(layer.Venue);
            string unit = numeric?.Unit ?? layer.Reading?.Unit ?? "";
            string label = layer.Venue.Label() + (primary && unit.Length > 0 ? $" · {unit}" : "");
            if (primary ? numeric == null : layer.Reading is not { Points.Length: > 0 }) label += " —";
            var caption = Text(label, color);
            double width = caption.Width + 8;
            if (x + width > region.Right - 2) return;
            var hit = new Rect(x, y + 1, width, 15);
            if (primary)
            {
                dc.DrawRoundedRectangle(interestLegendFill, null, hit, 3, 3);
                dc.DrawLine(new Pen(color, 1), new(x + 4, hit.Bottom), new(hit.Right - 4, hit.Bottom));
            }
            dc.DrawText(caption, new(x + 4, y + 2));
            interestLegend.Add((layer.Venue, hit));
            x += width + 4;
        }
    }

    private void DrawInterestTick(DrawingContext dc, Rect graph, string text, Brush brush, double y)
    {
        double available = ActualWidth - graph.Right - 6;
        var label = Text(text, brush);
        if (label.Width > available) label.SetFontSize(Math.Max(7, 11 * available / label.Width));
        double top = Math.Clamp(y - label.Height / 2, graph.Top - label.Height / 4, graph.Bottom - label.Height * .75);
        dc.DrawText(label, new(graph.Right + 5, top));
    }

    private static readonly Brush interestLegendFill = Ink(255, 255, 255, 13);
    private static Brush InterestBrush(Venue venue) => interestColors[(int)venue % interestColors.Length];

    private void SummarizeIndicators(View? range, (int Known, int Total) volumeBars = default, ExecutionProfile? profile = null,
        ExecutionProfile? liquidations = null)
    {
        if (indicators is not { } data) return;
        var shortParts = new List<string>();
        var details = new List<string>();
        string unit = ChartVolume.Unit(data.Market);
        if (data.Options.Volume)
        {
            shortParts.Add($"Объём {unit}");
            string coverage = volumeBars.Known > 0
                ? $"данные {volumeBars.Known}/{volumeBars.Total}"
                : "в диапазоне данных нет";
            details.Add($"Объём свечей · {unit} · {coverage}. Серые столбцы показывают наблюдаемые сделки, без гарантии полного охвата.");
        }
        if (data.Options.Profile)
        {
            shortParts.Add($"Профиль {unit} · {RangeLabel(profile)}");
            details.Add($"{data.ProfileStatus} · {unit} · {RangeLabel(profile)}. Охват неполный.");
        }
        if (data.Options.Liquidations)
        {
            string liquidationUnit = data.Market.Venue == Venue.Gate ? "события" : unit;
            shortParts.Add($"Ликв. {liquidationUnit} · {RangeLabel(liquidations)}");
            details.Add($"{data.LiquidationStatus} · {liquidationUnit} · {RangeLabel(liquidations)}. Охват неполный. " +
                "Красный — long, золотой — short, фиолетовый — сторона неизвестна.");
        }
        if (data.Options.HasInterest)
        {
            shortParts.Add("OI · " + string.Join(", ", data.Interest.Select(layer => layer.Venue.Label())));
            details.Add("Открытый интерес · щёлкните по бирже в легенде для её шкалы и линейки. У каждого контракта свои единицы.");
            foreach (var layer in data.Interest)
            {
                string label = layer.Market == null ? layer.Venue.Label() : $"{layer.Venue.Label()} · {layer.Market.Symbol} · {layer.Market.Contract!.Product}";
                var reading = layer.Reading;
                if (reading == null) { details.Add($"{label} · {layer.Status ?? "Нет данных"}"); continue; }
                var points = range is { } current ? VisiblePoints(reading, current) : [];
                string coverage = points.Length == 0 ? "нет точек в диапазоне" :
                    $"{Stamp(points[0].Time)}–{Stamp(points[^1].Time)} · {points.Min(p => p.Value).ToString("G6", Russian)}–{points.Max(p => p.Value).ToString("G6", Russian)}";
                details.Add($"{label} · {reading.Unit} · {ChartCell.PeriodLabel(reading.Period)} · {coverage}" +
                    (reading.Status == null ? "" : $" · {reading.Status}"));
            }
        }
        if (range == null) shortParts.Add("ожидание свечей");
        var summary = (string.Join("   ·   ", shortParts), string.Join("\n", details));
        if (lastSummary == summary) return;
        lastSummary = summary;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (lastSummary == summary && indicators != null) IndicatorSummary?.Invoke(summary.Item1, summary.Item2);
        }, DispatcherPriority.Background);
    }

    private static InterestPoint[] VisiblePoints(InterestReading reading, View view) =>
        reading.Points.Where(point => point.Time >= view.Start && point.Time < view.End &&
            point.Value >= 0 && double.IsFinite(point.Value)).ToArray();

    private static string RangeLabel(ExecutionProfile? profile) => profile is { First: { } first, Last: { } last }
        ? $"{Stamp(first)}–{Stamp(last)}" : "нет сделок в диапазоне";
    private static string Stamp(long time) => DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime().ToString("dd.MM HH:mm:ss");
    private static Brush Ink(byte red, byte green, byte blue, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}

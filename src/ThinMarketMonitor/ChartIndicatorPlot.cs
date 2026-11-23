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
    private (string Caption, string Detail) lastSummary;
    internal event Action<string, string>? IndicatorSummary;

    private double InterestPaneHeight => indicators?.Options.HasInterest == true
        ? Math.Min(Math.Clamp(ActualHeight * .30, 27, 105), Math.Max(0, ActualHeight - 59)) : 0;

    internal void UpdateIndicators(IndicatorDisplay? value)
    {
        indicators = value;
        InvalidateVisual();
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

    private void DrawInterest(DrawingContext dc, View view)
    {
        if (indicators is not { Options.HasInterest: true } data || InterestPaneHeight < 17) return;
        var area = new Rect(view.Plot.Left, view.Plot.Bottom + 1, view.Plot.Width, InterestPaneHeight - 1);
        dc.PushClip(new RectangleGeometry(area));
        dc.DrawLine(gridLine, area.TopLeft, area.TopRight);
        double x = area.Left + 3;
        var title = Text("OI", axis);
        dc.DrawText(title, new(x, area.Top + 1));
        x += title.Width + 8;
        foreach (var layer in data.Interest)
        {
            var brush = interestColors[(int)layer.Venue % interestColors.Length];
            var caption = Text(layer.Reading == null ? layer.Venue.Label() + " —" : $"{layer.Venue.Label()} {layer.Reading.Unit}", brush);
            if (x + caption.Width < area.Right + 1) dc.DrawText(caption, new(x, area.Top + 1));
            x += caption.Width + 8;
            if (layer.Reading is not { } reading || area.Height < 25) continue;
            var points = VisiblePoints(reading, view);
            if (points.Length == 0) continue;
            double lo = points.Min(point => point.Value), hi = points.Max(point => point.Value);
            double height = area.Height - 18;
            var line = new Pen(brush, 1.25);
            Point? previous = null;
            long last = 0;
            long spacing = reading.Period == 0 ? 85_000 : reading.Period * 1300L;
            foreach (var point in points)
            {
                double fraction = hi > lo ? (point.Value - lo) / (hi - lo) : .5;
                var at = new Point(view.X(point.Time), area.Bottom - 2 - fraction * height);
                if (previous is { } before && point.Time - last <= spacing) dc.DrawLine(line, before, at);
                else dc.DrawEllipse(brush, null, at, 1.7, 1.7);
                (previous, last) = (at, point.Time);
            }
        }
        dc.Pop();
    }

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
            details.Add("Открытый интерес · отдельная шкала каждого контракта.");
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
        reading.Points.Where(point => point.Time >= view.Start && point.Time < view.End).ToArray();

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

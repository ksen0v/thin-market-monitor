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
    private bool debugMode;
    private (string Caption, string Detail) lastSummary;
    internal event Action<string, string>? IndicatorSummary;
    internal event Action<string?, InterestLayer[], ChartIndicatorOptions>? InterestPanesChanged;

    internal void SetDebugMode(bool enabled)
    {
        debugMode = enabled;
        lastSummary = default;
        InvalidateVisual();
    }

    internal void UpdateIndicators(IndicatorDisplay? value)
    {
        if (drag?.Interest is { } measuring &&
            (value == null || value.Market.Key != indicators?.Market.Key || value.Options.PresentationFor(measuring.Venue).Usd != measuring.Usd ||
                !value.Interest.Any(layer => InterestInstrument(layer) == measuring.Instrument)))
            CancelGesture();
        indicators = value;
        int count = value?.Interest.Length ?? 0;
        MinHeight = count > 0 ? Top + TimeAxisHeight + ChartPaneLayout.MinimumHeight(count) : 0;
        if (value == null)
        {
            lastSummary = default;
            lastInterestWindow = null;
        }
        InterestPanesChanged?.Invoke(value?.Market.Key, value?.Interest ?? [], value?.Options ?? ChartIndicatorOptions.Off);
        InvalidateVisual();
    }

    private (int Known, int Total) DrawCandleVolume(DrawingContext dc, View view)
    {
        if (indicators is not { Options.Volume: true }) return default;
        double maximum = 0;
        foreach (var bar in candlePixels.Columns)
            if (bar.Volume is { } amount && double.IsFinite(amount)) maximum = Math.Max(maximum, amount);
        if (maximum > 0)
        {
            double limit = Math.Clamp(view.Plot.Height * .23, 8, 56);
            double width = candlePixels.BodyWidth;
            foreach (var bar in candlePixels.Columns)
            {
                if (bar.Volume is not (> 0 and < double.PositiveInfinity and var amount)) continue;
                double height = Math.Max(.8, limit * (amount / maximum));
                Brush brush = bar.Observed ? observedVolume : bar.Rising ? volumeUp : volumeDown;
                dc.DrawRectangle(brush, null, new Rect(bar.X - width / 2, view.Plot.Bottom - height, width, height));
            }
        }
        return (candlePixels.Known, candlePixels.Total);
    }

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

    private void SummarizeIndicators(View? range, (int Known, int Total) volumeBars = default, ExecutionProfile? profile = null,
        ExecutionProfile? liquidations = null)
    {
        if (indicators is not { } data) return;
        if (!debugMode && !data.Options.Profile && !data.Options.Liquidations) return;
        var shortParts = new List<string>();
        var details = new List<string>();
        string unit = ChartVolume.Unit(data.Market);
        if (debugMode && data.Options.Volume)
        {
            shortParts.Add($"Объём {unit}");
            string coverage = volumeBars.Known > 0
                ? $"данные {volumeBars.Known}/{volumeBars.Total}"
                : "в диапазоне данных нет";
            details.Add($"Объём свечей · {unit} · {coverage}. Серые столбцы показывают наблюдаемые сделки, без гарантии полного охвата.");
        }
        if (data.Options.Profile)
        {
            shortParts.Add($"Профиль {unit} · {CoverageLabel(profile)}");
            details.Add(debugMode ? $"{data.ProfileStatus} · {unit} · {RangeLabel(profile)}. Охват неполный."
                : "Охват исполненных сделок неполный.");
        }
        if (data.Options.Liquidations)
        {
            string liquidationUnit = data.Market.Venue == Venue.Gate ? "события" : unit;
            shortParts.Add($"Ликв. {liquidationUnit} · {CoverageLabel(liquidations)}");
            const string sides = "Красный — long, золотой — short, фиолетовый — сторона неизвестна.";
            details.Add(debugMode
                ? $"{data.LiquidationStatus} · {liquidationUnit} · {RangeLabel(liquidations)}. Охват неполный. {sides}"
                : sides);
        }
        if (debugMode && data.Options.HasInterest)
        {
            shortParts.Add("OI · " + string.Join(", ", data.Interest.Select(layer => layer.Venue.Label())));
            details.Add("Открытый интерес · потяните границу панели для изменения высоты. Шкала справа управляет масштабом значений.");
            if (data.Interest.Any(layer => data.Options.PresentationFor(layer.Venue).Usd))
                details.Add("USD · значения бирж и расчёт по цене того же контракта (≈). USDT и USDC оценены по номиналу.");
            foreach (var layer in data.Interest)
            {
                string label = layer.Market == null ? layer.Venue.Label() : $"{layer.Venue.Label()} · {layer.Market.Symbol} · {layer.Market.Contract!.Product}";
                var reading = layer.Reading;
                if (reading == null) { details.Add($"{label} · {layer.Status ?? "Нет данных"}"); continue; }
                var points = range is { } current ? VisibleInterest(layer, current.Start, current.End) : [];
                string coverage = points.Length == 0 ? "нет точек в диапазоне" :
                    $"{Stamp(points[0].Time)}–{Stamp(points[^1].Time)} · {points.Min(p => p.Value).ToString("G6", Russian)}–{points.Max(p => p.Value).ToString("G6", Russian)}";
                string interestUnit = data.Options.PresentationFor(layer.Venue).Usd
                    ? (points.Any(p => p.Estimated) ? "≈ USD" : "USD") : reading.Unit;
                details.Add($"{label} · {interestUnit} · {ChartCell.PeriodLabel(reading.Period)} · {coverage}" +
                    (reading.Status == null ? "" : $" · {reading.Status}"));
            }
        }
        if (debugMode && range == null) shortParts.Add("ожидание свечей");
        var summary = (string.Join("   ·   ", shortParts), string.Join("\n", details));
        if (lastSummary == summary) return;
        lastSummary = summary;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (lastSummary == summary && indicators != null) IndicatorSummary?.Invoke(summary.Item1, summary.Item2);
        }, DispatcherPriority.Background);

        string CoverageLabel(ExecutionProfile? events) => debugMode ? RangeLabel(events) :
            events is { Count: > 0 } ? "неполные данные" : "нет данных";
    }

    private static string RangeLabel(ExecutionProfile? profile) => profile is { First: { } first, Last: { } last }
        ? $"{Stamp(first)}–{Stamp(last)}" : "нет сделок в диапазоне";
    private static string Stamp(long time) => ChartAxis.DateLabel(DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime(), seconds: true);
    private static Brush Ink(byte red, byte green, byte blue, byte alpha = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }
}

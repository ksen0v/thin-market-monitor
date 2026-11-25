using System.Globalization;

namespace ThinMarketMonitor;

internal sealed record InterestPresentation(bool Usd = false, bool Candles = true);

internal sealed record ChartIndicatorOptions(bool Volume = true, bool Profile = false, bool Liquidations = false,
    Dictionary<Venue, string>? Interest = null, bool InterestUsd = false,
    Dictionary<Venue, InterestPresentation>? InterestPresentation = null)
{
    public static ChartIndicatorOptions Default { get; } = new();
    public static ChartIndicatorOptions Off { get; } = new(Volume: false);
    public bool HasInterest => Interest is { Count: > 0 };
    public bool HasDetails => Profile || Liquidations || HasInterest;
    public bool Empty => !Volume && !HasDetails;
    internal InterestPresentation PresentationFor(Venue venue) =>
        InterestPresentation?.GetValueOrDefault(venue) ?? new(Usd: InterestUsd);

    public ChartIndicatorOptions Normalized(bool keepContracts = true) => this with
    {
        Interest = (Interest ?? []).Where(pair => IsInterestSource(pair.Key))
            .ToDictionary(pair => pair.Key, pair => keepContracts ? pair.Value ?? "" : ""),
        InterestPresentation = InterestPresentation?.Where(pair => IsInterestSource(pair.Key) && pair.Value != null)
            .ToDictionary(pair => pair.Key, pair => pair.Value)
    };

    internal static bool IsInterestSource(Venue venue) => Enum.IsDefined(venue) && venue != Venue.Alpha;
}

internal readonly record struct InterestPoint(long Time, double Value, double? Usd = null, bool Estimated = false)
{
    public double InUnit(bool usd) => usd ? Usd ?? double.NaN : Value;
}
internal readonly record struct InterestCandle(long Time, double Open, double High, double Low, double Close, bool Estimated)
{
    internal static InterestCandle[] Build(ReadOnlySpan<InterestPoint> points, long interval, long maximumGap)
    {
        var result = new List<InterestCandle>();
        InterestPoint? previous = null;
        foreach (var point in points)
        {
            long start = ExchangeApi.CandleStart(point.Time, interval);
            if (result.Count != 0 && result[^1].Time == start)
            {
                var current = result[^1];
                result[^1] = current with
                {
                    High = Math.Max(current.High, point.Value), Low = Math.Min(current.Low, point.Value),
                    Close = point.Value, Estimated = current.Estimated || point.Estimated
                };
            }
            else
            {
                // An adjacent, actually observed close can open the next bar. Gaps stay gaps.
                var first = previous is { } prior && result[^1].Time + interval == start && point.Time - prior.Time <= maximumGap
                    ? prior : point;
                double open = first.Value;
                result.Add(new(start, open, Math.Max(open, point.Value), Math.Min(open, point.Value), point.Value,
                    point.Estimated || first.Estimated));
            }
            previous = point;
        }
        return result.ToArray();
    }
}
internal readonly record struct InterestWindow(long From, long Until);
internal sealed record InterestReading(string Unit, int Period, InterestPoint[] Points, string? Status = null, bool LoadingHistory = false);
internal sealed record InterestLayer(Venue Venue, Market? Market, InterestReading? Reading, string? Status = null);
internal enum LiquidationSide { Unknown, Long, Short }
internal readonly record struct ChartExecution(long Time, double Price, double Size, string? Id = null,
    LiquidationSide Side = LiquidationSide.Unknown);

internal static class ChartVolume
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    internal static string CompactUsd(double usd, string thousands = "k")
    {
        if (usd is > 0 and < .01) return "<0,01";
        var (unit, suffix) = usd switch
        {
            >= 999_995_000_000 => (1e12, "T"),
            >= 999_995_000 => (1e9, "B"),
            >= 999_995 => (1e6, "M"),
            >= 999.995 => (1e3, thousands),
            _ => (1d, "")
        };
        return (usd / unit).ToString("#,0.##", Russian) + suffix;
    }

    public static bool UsesBase(Market market) =>
        market.Venue == Venue.Hyperliquid || market is { Venue: Venue.Kraken, Contract.Unit: QuantityUnit.Base };
    public static string Unit(Market market) => UsesBase(market) ? (market.Contract?.NativeBase ?? market.Base) : market.Quote;
    public static double? Amount(Market market, Candle candle) => UsesBase(market) ? candle.BaseVolume : candle.QuoteVolume;
    public static double TradeAmount(Market market, Trade trade)
    {
        if (trade.Size <= 0 || !double.IsFinite(trade.Size) || market.Contract is { Multiplier: <= 0 }) return double.NaN;
        return UsesBase(market) ? trade.Size * (market.Contract?.Multiplier ?? 1) : market.Turnover(trade.Size, trade.Price);
    }

    internal static (double? Usd, bool Estimated, bool Partial) MeasureTurnover(Market market, ReadOnlySpan<Candle> candles,
        long interval, double start, double end)
    {
        if (!UsdQuotes.Supported(market)) return default;
        double from = Math.Min(start, end), until = Math.Max(start, end);
        double total = 0;
        bool known = false, estimated = false, partial = false;
        long? previous = null;
        foreach (var candle in candles)
        {
            if (candle.Time + interval <= from) continue;
            if (candle.Time > until) break;
            partial |= previous is { } last && candle.Time > last + interval;
            previous = candle.Time;

            double? amount = candle.QuoteVolume;
            if (amount is not (>= 0 and < double.PositiveInfinity))
            {
                amount = candle.Close > 0 ? candle.BaseVolume * candle.Close : null;
                if (amount is not (>= 0 and < double.PositiveInfinity))
                {
                    partial = true;
                    continue;
                }
                estimated = true;
            }
            known = true;
            partial |= candle.ObservedVolume;
            total += amount.Value;
        }
        return (known && double.IsFinite(total) ? total : null, estimated, partial);
    }
}

internal sealed class ExecutionTape(int capacity = 12_000)
{
    private readonly Queue<ChartExecution> events = new();
    private readonly HashSet<string> ids = new(StringComparer.Ordinal);
    public IEnumerable<ChartExecution> Events => events;

    public void Add(ChartExecution item)
    {
        if (item.Time <= 0 || !double.IsFinite(item.Price) || item.Price <= 0 || !double.IsFinite(item.Size) || item.Size <= 0) return;
        if (!string.IsNullOrEmpty(item.Id) && !ids.Add(item.Id)) return;
        events.Enqueue(item);
        if (events.Count <= capacity) return;
        if (events.Dequeue().Id is { Length: > 0 } id) ids.Remove(id);
    }
}

internal readonly record struct ExecutionBucket(double Neutral, double Long, double Short)
{
    public double Total => Neutral + Long + Short;
    public ExecutionBucket With(ChartExecution item) => item.Side switch
    {
        LiquidationSide.Long => this with { Long = Long + item.Size },
        LiquidationSide.Short => this with { Short = Short + item.Size },
        _ => this with { Neutral = Neutral + item.Size }
    };
}

internal sealed record ExecutionProfile(ExecutionBucket[] Buckets, long? First, long? Last, int Count)
{
    public static ExecutionProfile Calculate(IEnumerable<ChartExecution> executions, double from, double until, double low,
        double high, int levels)
    {
        var buckets = new ExecutionBucket[levels];
        long first = long.MaxValue, last = 0;
        int count = 0;
        foreach (var execution in executions)
        {
            if (execution.Time < from || execution.Time >= until) continue;
            first = Math.Min(first, execution.Time);
            last = Math.Max(last, execution.Time);
            count++;
            if (execution.Price < low || execution.Price > high) continue;
            int bin = Math.Min(levels - 1, (int)((execution.Price - low) / (high - low) * levels));
            buckets[bin] = buckets[bin].With(execution);
        }
        return new(buckets, count > 0 ? first : null, count > 0 ? last : null, count);
    }
}

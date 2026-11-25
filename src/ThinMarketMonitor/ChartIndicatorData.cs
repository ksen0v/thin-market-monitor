namespace ThinMarketMonitor;

internal sealed record ChartIndicatorOptions(bool Volume = true, bool Profile = false, bool Liquidations = false,
    Dictionary<Venue, string>? Interest = null, bool InterestUsd = false)
{
    public static ChartIndicatorOptions Default { get; } = new();
    public static ChartIndicatorOptions Off { get; } = new(Volume: false);
    public bool HasInterest => Interest is { Count: > 0 };
    public bool HasDetails => Profile || Liquidations || HasInterest;
    public bool Empty => !Volume && !HasDetails;
    public ChartIndicatorOptions Normalized(bool keepContracts = true) => this with
    {
        Interest = (Interest ?? []).Where(pair => IsInterestSource(pair.Key))
            .ToDictionary(pair => pair.Key, pair => keepContracts ? pair.Value ?? "" : "")
    };

    internal static bool IsInterestSource(Venue venue) => Enum.IsDefined(venue) && venue != Venue.Alpha;
}

internal readonly record struct InterestPoint(long Time, double Value, double? Usd = null, bool Estimated = false)
{
    public double InUnit(bool usd) => usd ? Usd ?? double.NaN : Value;
}
internal readonly record struct InterestWindow(long From, long Until);
internal sealed record InterestReading(string Unit, int Period, InterestPoint[] Points, string? Status = null, bool LoadingHistory = false);
internal sealed record InterestLayer(Venue Venue, Market? Market, InterestReading? Reading, string? Status = null);
internal enum LiquidationSide { Unknown, Long, Short }
internal readonly record struct ChartExecution(long Time, double Price, double Size, string? Id = null,
    LiquidationSide Side = LiquidationSide.Unknown);

internal static class ChartVolume
{
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

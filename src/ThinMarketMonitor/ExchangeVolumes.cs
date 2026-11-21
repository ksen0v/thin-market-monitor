using System.Collections.Concurrent;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private const long Minute = 60_000;
    private readonly ConcurrentDictionary<Market, QuoteVolumeHistory> closedVolumes = new();
    private long nextVolumePrune;

    internal static bool HasBulkDay(Venue venue) => venue != Venue.Alpha;

    internal static bool HasBulkDay(Market m) => m.Venue != Venue.Alpha &&
        !(m.Venue == Venue.Okx && m.Contract is { Unit: QuantityUnit.Base }) &&
        !(m.Venue == Venue.Gate && m.Contract?.Product.StartsWith("delivery", StringComparison.Ordinal) == true);

    public async Task<double?> AlphaDayVolume(Market market, CancellationToken ct)
    {
        string symbol = Uri.EscapeDataString(market.Symbol);
        await PaceHistory(Venue.Alpha, ct);
        var row = Envelope(await Get(Alpha + $"alpha-trade/ticker?symbol={symbol}", ct), "000000");
        double amount = row.Number("quoteVolume");
        double closing = row.Number("closeTime");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return row.Optional("symbol") == market.Symbol && double.IsFinite(amount) && amount >= 0 &&
            closing >= now - 300_000 && closing <= now + 60_000 ? amount : null;
    }

    public async Task<Dictionary<string, double>> DayVolumes(Venue venue, CancellationToken ct)
    {
        string url = venue switch
        {
            Venue.Binance => Binance + "ticker/24hr?type=FULL",
            Venue.Bybit => Bybit + "tickers?category=spot",
            Venue.Okx => Okx + "market/tickers?instType=SPOT",
            Venue.Bitget => Bitget + "market/tickers",
            Venue.Mexc => Mexc + "ticker/24hr",
            Venue.KuCoin => KuCoin + "v1/market/allTickers",
            Venue.Gate => Gate + "tickers",
            _ => throw new ArgumentOutOfRangeException(nameof(venue))
        };
        return DecodeDay(venue, await Get(url, ct), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    internal static Dictionary<string, double> DecodeDay(Venue venue, JsonElement root, long now)
    {
        var rows = venue switch
        {
            Venue.Bybit => BybitResult(root).GetProperty("list"),
            Venue.Okx => Envelope(root, "0"),
            Venue.Bitget => Envelope(root, "00000"),
            Venue.KuCoin => Envelope(root, "200000").GetProperty("ticker"),
            _ => root
        };
        var (name, amount, timestamp) = venue switch
        {
            Venue.Binance or Venue.Mexc => ("symbol", "quoteVolume", "closeTime"),
            Venue.Bybit => ("symbol", "turnover24h", ""),
            Venue.Okx => ("instId", "volCcy24h", "ts"),
            Venue.Bitget => ("symbol", "quoteVolume", "ts"),
            Venue.KuCoin => ("symbol", "volValue", ""),
            Venue.Gate => ("currency_pair", "quote_volume", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(venue))
        };
        var output = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in rows.EnumerateArray())
        {
            double value = row.Number(amount);
            if (double.IsFinite(value) && value >= 0 &&
                (timestamp.Length == 0 || row.Number(timestamp) >= now - 300_000 && row.Number(timestamp) <= now + 60_000))
                output[row.Str(name)] = value;
        }
        return output;
    }

    internal VolumeSnapshot? CachedHistoricalVolumes(Market market, int[] periods)
    {
        if (periods.Length == 0 || !closedVolumes.TryGetValue(market, out var series)) return null;
        long end = CandleNow / Minute * Minute;
        return BestCompleteVolumes(market, series.Copy(end - periods.Max() * 1000L - Minute, end), periods, end);
    }

    public async Task<VolumeSnapshot?> HistoricalVolumes(Market market, int[] periods, CancellationToken ct)
    {
        if (periods.Length == 0 || !UsdQuotes.Supported(market) || !HasHistoricalQuoteVolume(market)) return null;
        long end = CandleNow / Minute * Minute;
        int lookback = periods.Max();
        long start = end - lookback * 1000L - Minute;
        long ticks = Environment.TickCount64, pruneAt = Volatile.Read(ref nextVolumePrune);
        if (ticks >= pruneAt && Interlocked.CompareExchange(ref nextVolumePrune, ticks + 300_000, pruneAt) == pruneAt)
            foreach (var entry in closedVolumes)
                if (ticks - entry.Value.LastRead > 1_800_000) closedVolumes.TryRemove(entry);
        var series = closedVolumes.GetOrAdd(market, _ => new());
        var known = series.Copy(start, end);
        var prior = BestCompleteVolumes(market, known, periods, end);
        if (prior is { } cached && cached.Time == end && cached.Amounts.Count == periods.Length) return cached;

        if (known.Count == 0)
            await AddRange(start, end);
        else
        {
            long earliest = known.Keys.Min(), latest = known.Keys.Max();
            // Fetch the new edge first. Reuse exact, already settled minutes from the previous window.
            if (latest + Minute < end) await AddRange(latest + Minute, end);
            if (earliest > start && prior?.Amounts.Count != periods.Length) await AddRange(start, earliest);
            if (earliest == start && latest + Minute == end && prior?.Amounts.Count != periods.Length)
                await AddRange(start, end);
        }
        return BestCompleteVolumes(market, series.Copy(start, end), periods, end);

        async Task AddRange(long from, long until)
        {
            var fresh = await ReadVolumeBars(market, from, until, ct);
            ct.ThrowIfCancellationRequested();
            series.Merge(fresh, end, lookback);
        }
    }

    private static VolumeSnapshot? BestCompleteVolumes(Market market, Dictionary<long, double> bars, int[] periods, long end)
    {
        if (bars.Count == 0) return null;
        bool gapsAreEmpty = market.Venue == Venue.KuCoin;
        var latest = CompleteVolumes(bars, periods, end, gapsAreEmpty);
        if (latest.Count == periods.Length) return new(end, latest);
        // Some exchanges publish the last closed bar a few seconds into the next minute.
        var previous = CompleteVolumes(bars, periods, end - Minute, gapsAreEmpty);
        if (previous.Count > latest.Count) return new(end - Minute, previous);
        return latest.Count == 0 ? null : new(end, latest);
    }

    private async Task<Dictionary<long, double>> ReadVolumeBars(Market market, long start, long end, CancellationToken ct)
    {
        if (market.Contract != null || market.Venue.Extended())
        {
            var results = await CandlePage(market, 60, end, (int)((end - start) / Minute), ct);
            return results.Where(c => c.QuoteVolume.HasValue && c.Time >= start && c.Time < end)
                .ToDictionary(c => c.Time, c => c.QuoteVolume!.Value);
        }
        var bars = new Dictionary<long, double>();
        string symbol = Uri.EscapeDataString(market.Symbol);
        int pageMinutes = market.Venue == Venue.Okx ? 100 : 240;
        for (long left = start; left < end; left += pageMinutes * 60_000L)
        {
            long right = Math.Min(end, left + pageMinutes * 60_000L);
            int limit = (int)((right - left) / 60_000);
            string url = market.Venue switch
            {
                Venue.Binance => Binance + $"klines?symbol={symbol}&interval=1m&startTime={left}&endTime={right - 1}&limit={limit}",
                Venue.Bybit => Bybit + $"kline?category=spot&symbol={symbol}&interval=1&start={left}&end={right - 1}&limit={limit}",
                Venue.Okx => Okx + $"market/history-candles?instId={symbol}&bar=1m&after={right}&before={left - 1}&limit={limit}",
                Venue.Bitget => Bitget + $"market/candles?symbol={symbol}&granularity=1min&startTime={left - 1}&endTime={right - 1}&limit={limit + 1}",
                Venue.Mexc => Mexc + $"klines?symbol={symbol}&interval=1m&startTime={left}&endTime={right - 1}&limit={limit}",
                Venue.KuCoin => KuCoin + $"v1/market/candles?symbol={symbol}&type=1min&startAt={left / 1000}&endAt={(right - 1) / 1000}",
                Venue.Gate => Gate + $"candlesticks?currency_pair={symbol}&interval=1m&from={left / 1000}&to={(right - 1) / 1000}",
                Venue.Alpha => Alpha + $"alpha-trade/klines?symbol={symbol}&interval=1m&startTime={left}&endTime={right - 1}&limit={limit}",
                _ => throw new ArgumentOutOfRangeException(nameof(market))
            };
            await PaceHistory(market.Venue, ct);
            var root = await Get(url, ct);
            var rows = market.Venue switch
            {
                Venue.Bybit => BybitResult(root).GetProperty("list"),
                Venue.Okx => Envelope(root, "0"),
                Venue.Bitget => Envelope(root, "00000"),
                Venue.KuCoin => Envelope(root, "200000"),
                Venue.Alpha => Envelope(root, "000000"),
                _ => root
            };
            foreach (var row in rows.EnumerateArray())
                if (DecodeVolumeBar(market.Venue, row) is { } candle && candle.Time >= left && candle.Time < right)
                    bars[candle.Time] = candle.QuoteVolume;
        }

        return bars;
    }

    private sealed class QuoteVolumeHistory
    {
        private readonly Dictionary<long, double> bars = new();
        private long lastRead = Environment.TickCount64;
        public long LastRead => Volatile.Read(ref lastRead);

        public Dictionary<long, double> Copy(long start, long end)
        {
            Volatile.Write(ref lastRead, Environment.TickCount64);
            lock (bars) return bars.Where(x => x.Key >= start && x.Key < end).ToDictionary();
        }

        public void Merge(Dictionary<long, double> incoming, long end, int lookback)
        {
            lock (bars)
            {
                long oldest = end - Math.Max(3600, lookback) * 1000L - 2 * Minute;
                foreach (long time in bars.Keys.Where(time => time < oldest).ToArray()) bars.Remove(time);
                foreach (var (time, volume) in incoming) if (time >= oldest && time < end) bars[time] = volume;
            }
        }
    }

    internal static (long Time, double QuoteVolume)? DecodeVolumeBar(Venue venue, JsonElement row)
    {
        int amountIndex = venue switch
        {
            Venue.Gate => 1, Venue.Bybit or Venue.KuCoin => 6, _ => 7
        };
        if (row.GetArrayLength() <= amountIndex ||
            (venue == Venue.Okx && (row.GetArrayLength() < 9 || row[8].Str() != "1")) ||
            (venue == Venue.Gate && (row.GetArrayLength() < 8 || !row[7].Str().Equals("true", StringComparison.OrdinalIgnoreCase))))
            return null;
        long time = row[0].Int() * (venue is Venue.Gate or Venue.KuCoin ? 1000 : 1);
        double volume = row[amountIndex].Num();
        return time % 60_000 == 0 && double.IsFinite(volume) && volume >= 0 ? (time, volume) : null;
    }

    internal static Dictionary<int, double> CompleteVolumes(IReadOnlyDictionary<long, double> bars, int[] periods, long end,
        bool documentedEmptyGaps)
    {
        var result = new Dictionary<int, double>();
        foreach (int seconds in periods)
        {
            long start = end - seconds * 1000L;
            bool enclosed = documentedEmptyGaps && bars.ContainsKey(start) && bars.ContainsKey(end - 60_000);
            bool complete = true;
            double sum = 0;
            for (long minute = start; minute < end; minute += 60_000)
            {
                if (bars.TryGetValue(minute, out double value)) sum += value;
                else if (!enclosed) { complete = false; break; }
            }
            if (complete && double.IsFinite(sum)) result[seconds] = sum;
        }
        return result;
    }
}

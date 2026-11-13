using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    internal static bool HasBulkDay(Venue venue) => venue != Venue.Alpha;

    public async Task<double?> AlphaDayVolume(Market market, CancellationToken ct)
    {
        string symbol = Uri.EscapeDataString(market.Symbol);
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

    public async Task<Dictionary<int, double>> HistoricalVolumes(Market market, int[] periods,
        Func<CancellationToken, Task> pace, CancellationToken ct)
    {
        if (periods.Length == 0) return new();
        // Leave a full minute for the exchange to finalize the last included bar.
        long end = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 60_000 - 1) * 60_000;
        long start = end - periods.Max() * 1000L;
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
            await pace(ct);
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

        return CompleteVolumes(bars, periods, end, market.Venue == Venue.KuCoin);
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

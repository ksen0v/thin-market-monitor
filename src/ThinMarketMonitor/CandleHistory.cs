using System.Globalization;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    public async Task<Trade[]> RecentTrades(Market m, CancellationToken ct)
    {
        string s = Uri.EscapeDataString(m.Symbol);
        string? p = m.Contract?.Product;
        string? url = m.Venue switch
        {
            Venue.Binance => (p == "usdm" ? Usdm : p == "coinm" ? Coinm : Binance) + $"aggTrades?symbol={s}&limit=500",
            Venue.Bybit => Bybit + $"recent-trade?category={p ?? "spot"}&symbol={s}&limit={(p == null ? 60 : 500)}",
            Venue.Bitget => p == null ? Bitget + $"market/fills?symbol={s}&limit=100" : Mix + $"fills?symbol={s}&productType={p}&limit=100",
            Venue.Mexc => p == null ? Mexc + $"trades?symbol={s}&limit=500" : MexcContract + $"deals/{s}",
            Venue.KuCoin => p == null ? KuCoin + $"v1/market/histories?symbol={s}" : KuContract + $"trade/history?symbol={s}",
            Venue.Gate => p == null ? Gate + $"trades?currency_pair={s}&limit=200" : GateContract + $"{p}/trades?contract={s}&limit=200",
            _ => null
        };
        if (url == null) return [];
        await PaceHistory(m.Venue, ct);
        var root = await Get(url, ct);
        var rows = m.Venue switch
        {
            Venue.Bybit => BybitResult(root).GetProperty("list"),
            Venue.Bitget => Envelope(root, "00000"),
            Venue.Mexc when p != null => Envelope(root, "0"),
            Venue.KuCoin => Envelope(root, "200000"),
            _ => root
        };
        return rows.EnumerateArray().Select(row =>
        {
            var (time, price, id) = m.Venue switch
            {
                Venue.Binance => ("T", "p", "a"),
                Venue.Bybit => ("time", "price", "execId"),
                Venue.Bitget => ("ts", "price", "tradeId"),
                Venue.Mexc when p != null => ("t", "p", "i"),
                Venue.Gate => ("create_time_ms", "price", "id"),
                Venue.KuCoin when p != null => ("ts", "price", "tradeId"),
                Venue.KuCoin => ("time", "price", "tradeId"),
                _ => ("time", "price", "id")
            };
            double ms = row.Number(time);
            if (m.Venue == Venue.KuCoin && ms > 1e16) ms /= 1_000_000;
            if (m.Venue == Venue.Gate && !double.IsFinite(ms)) ms = row.Number("create_time") * 1000;
            return new Trade(m.Symbol, (long)ms, row.Number(price), Id: row.Optional(id));
        }).Where(t => t.Time > 0 && t.Price > 0 && double.IsFinite(t.Price)).OrderBy(t => t.Time).ToArray();
    }

    public static bool NativeSeconds(Market m) => m.Venue is Venue.Alpha or Venue.Okx || (m.Venue == Venue.Binance && m.Contract == null);

    public async Task<Candle[]> CandlePage(Market m, int frame, long before, int count, CancellationToken ct)
    {
        int sourceFrame = frame < 60 ? 1 : frame;
        long low = Math.Max(0, before - (long)frame * count * 1000);
        var bars = new List<Candle>();
        for (long end = before; end > low;)
        {
            int max = m.Venue == Venue.Okx ? 100 : m is { Venue: Venue.Bitget, Contract: not null } ? 190 : 240;
            int limit = (int)Math.Min(max, Math.Ceiling((end - low) / (sourceFrame * 1000d)));
            if (frame < 60 && limit < (end - low) / 1000)
                limit = Math.Max(frame, limit / frame * frame);
            long start = Math.Max(low, end - sourceFrame * 1000L * limit);
            bars.AddRange(await RawPage(m, sourceFrame, start, end, limit, ct));
            end = start;
        }
        return AggregateBars(bars, frame).Where(x => x.Time < before).ToArray();
    }

    private async Task<Candle[]> RawPage(Market m, int frame, long from, long until, int limit, CancellationToken ct)
    {
        string symbol = Uri.EscapeDataString(m.Symbol);
        string common = frame switch { 1 => "1s", 60 => "1m", 300 => "5m", 900 => "15m",
            3600 => "1h", 14400 => "4h", 86400 => "1d", _ => throw new ArgumentOutOfRangeException(nameof(frame)) };
        string kuFrame = frame switch { 60 => "1min", 300 => "5min", 900 => "15min", 3600 => "1hour", 14400 => "4hour", _ => "1day" };
        string byFrame = frame == 86400 ? "D" : (frame / 60).ToString(CultureInfo.InvariantCulture);
        string okFrame = frame >= 3600 ? (frame == 86400 ? "1Dutc" : common.ToUpperInvariant()) : common;
        string bitFrame = frame >= 3600 ? (frame == 86400 ? "1Dutc" : common.ToUpperInvariant()) : common;
        string gateFrame = frame == 3600 ? "1h" : frame == 86400 ? "1d" : common;
        string? p = m.Contract?.Product;
        string url = m.Venue switch
        {
            Venue.Binance => (p == "usdm" ? Usdm : p == "coinm" ? Coinm : Binance) +
                $"klines?symbol={symbol}&interval={common}&startTime={from}&endTime={until - 1}&limit={limit}",
            Venue.Alpha => Alpha + $"alpha-trade/klines?symbol={symbol}&interval={common}&startTime={from}&endTime={until - 1}&limit={limit}",
            Venue.Bybit => Bybit + $"kline?category={p ?? "spot"}&symbol={symbol}&interval={byFrame}&start={from}&end={until - 1}&limit={limit}",
            Venue.Okx => Okx + $"market/history-candles?instId={symbol}&bar={okFrame}&after={until}&before={from - 1}&limit={limit}",
            Venue.Bitget when p != null => Mix + $"history-candles?symbol={symbol}&productType={p}&granularity={bitFrame}&startTime={from - 1}&endTime={until - 1}&limit={Math.Min(200, limit + 1)}",
            Venue.Bitget => Bitget + $"market/candles?symbol={symbol}&granularity={(frame < 3600 ? kuFrame : frame == 86400 ? bitFrame : common)}&startTime={from - 1}&endTime={until - 1}&limit={limit + 1}",
            Venue.Mexc when p != null => MexcContract + $"kline/{symbol}?interval={frame switch { 60 => "Min1", 300 => "Min5", 900 => "Min15", 3600 => "Min60", 14400 => "Hour4", _ => "Day1" }}&start={from / 1000}&end={(until - 1) / 1000}",
            Venue.Mexc => Mexc + $"klines?symbol={symbol}&interval={(frame == 3600 ? "60m" : common)}&startTime={from}&endTime={until - 1}&limit={limit}",
            Venue.KuCoin when p != null => KuContract + $"kline/query?symbol={symbol}&granularity={frame / 60}&from={from}&to={until - 1}",
            Venue.KuCoin => KuCoin + $"v1/market/candles?symbol={symbol}&type={kuFrame}&startAt={from / 1000}&endAt={(until - 1) / 1000}",
            Venue.Gate when p != null => GateContract + $"{p}/candlesticks?contract={symbol}&interval={gateFrame}&from={from / 1000}&to={(until - 1) / 1000}",
            Venue.Gate => Gate + $"candlesticks?currency_pair={symbol}&interval={gateFrame}&from={from / 1000}&to={(until - 1) / 1000}",
            _ => throw new ArgumentOutOfRangeException(nameof(m))
        };
        if (m.Venue == Venue.Gate && frame is 900 or 14400)
        {
            var source = new List<Candle>();
            int smaller = frame == 900 ? 300 : 3600;
            long step = 120 * smaller * 1000L;
            for (long at = from; at < until; at += step)
                source.AddRange(await RawPage(m, smaller, at, Math.Min(until, at + step), 120, ct));
            return AggregateBars(source, frame);
        }
        await PaceHistory(m.Venue, ct);
        var root = await Get(url, ct);
        Candle[] decoded = DecodeCandles(m, root);
        return decoded.Where(c => c.Time >= from && c.Time < until && c.Low > 0 &&
            double.IsFinite(c.High) && c.High >= Math.Max(c.Open, c.Close) && c.Low <= Math.Min(c.Open, c.Close))
            .DistinctBy(c => c.Time).OrderBy(c => c.Time).ToArray();
    }

    private readonly SemaphoreSlim[] historyGates = Enum.GetValues<Venue>().Select(_ => new SemaphoreSlim(1)).ToArray();
    private readonly long[] nextHistory = new long[Enum.GetValues<Venue>().Length];
    private async Task PaceHistory(Venue venue, CancellationToken ct)
    {
        int i = (int)venue;
        var gate = historyGates[i];
        await gate.WaitAsync(ct);
        try
        {
            long wait = nextHistory[i] - Environment.TickCount64;
            if (wait > 0) await Task.Delay((int)wait, ct);
            nextHistory[i] = Environment.TickCount64 + (venue is Venue.Mexc or Venue.Alpha ? 480 : 230);
        }
        finally { gate.Release(); }
    }

    internal static Candle[] DecodeCandles(Market m, JsonElement root)
    {
        var rows = m.Venue switch
        {
            Venue.Bybit => BybitResult(root).GetProperty("list"),
            Venue.Okx => Envelope(root, "0"),
            Venue.Bitget => Envelope(root, "00000"),
            Venue.KuCoin => Envelope(root, "200000"),
            Venue.Alpha => Envelope(root, "000000"),
            Venue.Mexc when m.Contract != null => Envelope(root, "0"),
            _ => root
        };
        if (m is { Venue: Venue.Mexc, Contract: not null })
        {
            var times = rows.GetProperty("time");
            if (times.ValueKind != JsonValueKind.Array) return [];
            return Enumerable.Range(0, times.GetArrayLength()).Select(i =>
                new Candle(times[i].Int() * 1000, rows.GetProperty("open")[i].Num(), rows.GetProperty("high")[i].Num(),
                    rows.GetProperty("low")[i].Num(), rows.GetProperty("close")[i].Num(),
                    rows.TryGetProperty("amount", out var amounts) && amounts.ValueKind == JsonValueKind.Array && i < amounts.GetArrayLength()
                        ? ValidVolume(amounts[i].Num()) : null)).ToArray();
        }
        return rows.EnumerateArray().Select(row =>
        {
            var p = m.Contract;
            if (m.Venue == Venue.Gate && p != null)
            {
                double? volume = p.Unit == QuantityUnit.Quote ? ValidVolume(m.Turnover(row.Number("v"), 1))
                    : ValidVolume(row.Number("sum"));
                return new Candle(row.GetProperty("t").Int() * 1000, row.Number("o"), row.Number("h"), row.Number("l"), row.Number("c"), volume);
            }
            if (p == null && m.Venue == Venue.KuCoin)
                return new Candle(row[0].Int() * 1000, row[1].Num(), row[3].Num(), row[4].Num(), row[2].Num(), At(row, 6));
            if (p == null && m.Venue == Venue.Gate)
                return new Candle(row[0].Int() * 1000, row[5].Num(), row[3].Num(), row[4].Num(), row[2].Num(), At(row, 1));
            double? amount = m.Venue switch
            {
                Venue.Binance when p?.Product == "coinm" => At(row, 5) is { } size ? ValidVolume(m.Turnover(size, 1)) : null,
                Venue.Bybit when p?.Product == "inverse" => At(row, 5),
                Venue.Bybit or Venue.KuCoin => At(row, 6),
                Venue.Bitget when p != null => At(row, 6),
                _ => At(row, 7)
            };
            return new Candle(row[0].Int(), row[1].Num(), row[2].Num(), row[3].Num(), row[4].Num(), amount);
        }).ToArray();
    }

    private static double? At(JsonElement arr, int index) => arr.GetArrayLength() > index ? ValidVolume(arr[index].Num()) : null;
    private static double? ValidVolume(double x) => double.IsFinite(x) && x >= 0 ? x : null;

    internal static Candle[] AggregateBars(IEnumerable<Candle> input, int frame)
    {
        long ms = frame * 1000L;
        return input.OrderBy(x => x.Time).DistinctBy(x => x.Time).GroupBy(x => x.Time / ms * ms)
            .Select(g => new Candle(g.Key, g.First().Open, g.Max(c => c.High), g.Min(c => c.Low), g.Last().Close,
                g.All(c => c.QuoteVolume.HasValue) ? g.Sum(c => c.QuoteVolume!.Value) : null)).ToArray();
    }

    internal static Candle[] TradeBars(IEnumerable<Trade> trades, int frame) =>
        AggregateBars(trades.Where(t => t.Price > 0 && double.IsFinite(t.Price))
            .OrderBy(t => t.Time).GroupBy(t => t.Time / (frame * 1000L) * (frame * 1000L))
            .Select(g => new Candle(g.Key, g.First().Price, g.Max(t => t.Price), g.Min(t => t.Price), g.Last().Price)), frame);
}

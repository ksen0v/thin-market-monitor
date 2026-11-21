using System.Collections.Concurrent;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private sealed class TickerCache
    {
        public readonly SemaphoreSlim Gate = new(1);
        public JsonElement Root;
        public long Time;
    }
    private readonly ConcurrentDictionary<string, TickerCache> tickers = new(StringComparer.Ordinal);

    // Shared by volume/overview tickers and spread batches. The original receipt time is preserved.
    private Task<(JsonElement Root, long Time)> TickerBatch(string url, CancellationToken ct) =>
        CachedTicker(url, token => Get(url, token), ct);

    private async Task<(JsonElement Root, long Time)> CachedTicker(string url, Func<CancellationToken, Task<JsonElement>> fetch, CancellationToken ct)
    {
        var cache = tickers.GetOrAdd(url, _ => new());
        await cache.Gate.WaitAsync(ct);
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (cache.Time == 0 || now - cache.Time >= 2000)
            {
                cache.Root = await fetch(ct);
                cache.Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            return (cache.Root, cache.Time);
        }
        catch { cache.Time = 0; throw; }
        finally { cache.Gate.Release(); }
    }

    internal static string? TickerUrl(Market m, bool book = false)
    {
        string? p = m.Contract?.Product;
        return m.Venue switch
        {
            Venue.Binance => (p == "usdm" ? Usdm : p == "coinm" ? Coinm : Binance) +
                (book ? "ticker/bookTicker" : "ticker/24hr"),
            Venue.Bybit => Bybit + "tickers?category=" + (p ?? "spot"),
            Venue.Okx => Okx + "market/tickers?instType=" + (p ?? "SPOT"),
            Venue.Bitget => p == null ? Bitget + "market/tickers" : Mix + "tickers?productType=" + p,
            Venue.Mexc => p == null ? Mexc + (book ? "ticker/bookTicker" : "ticker/24hr") : MexcContract + "ticker",
            Venue.KuCoin => p == null ? KuCoin + "v1/market/allTickers" : KuContract + (book ? "allTickers" : "contracts/active"),
            Venue.Gate => p == null ? Gate + "tickers" : p.StartsWith("futures/", StringComparison.Ordinal) ||
                book && p.StartsWith("delivery/", StringComparison.Ordinal) ? GateContract + p + "/tickers" : null,
            _ => null
        };
    }

    internal static bool SpreadSupported(Market m) => m.Contract?.Product switch
    {
        null => Enum.IsDefined(m.Venue),
        "usdm" or "coinm" => m.Venue == Venue.Binance,
        "linear" => m.Venue is Venue.Bybit or Venue.Htx or Venue.Weex,
        "inverse" => m.Venue is Venue.Bybit or Venue.Htx,
        "SWAP" or "FUTURES" => m.Venue == Venue.Okx,
        "USDT-FUTURES" or "USDC-FUTURES" or "COIN-FUTURES" => m.Venue == Venue.Bitget,
        "contract" => m.Venue is Venue.Mexc or Venue.KuCoin,
        "delivery" => m.Venue == Venue.Htx,
        "derivatives" => m.Venue == Venue.Kraken,
        "perp" => m.Venue is Venue.Lighter or Venue.Hyperliquid,
        "futures/usdt" or "futures/btc" or "futures/usd1" or "delivery/usdt" or "delivery/btc" => m.Venue == Venue.Gate,
        _ => false
    };

    private static JsonElement TickerRows(Market m, JsonElement root)
    {
        bool spot = m.Contract == null;
        return m.Venue switch
        {
            Venue.Bybit => BybitResult(root).GetProperty("list"),
            Venue.Okx => Envelope(root, "0"),
            Venue.Bitget => Envelope(root, "00000"),
            Venue.Mexc when !spot => Envelope(root, "0"),
            Venue.KuCoin when spot => Envelope(root, "200000").GetProperty("ticker"),
            Venue.KuCoin => Envelope(root, "200000"),
            _ => root
        };
    }

    internal static string TickerSymbolField(Market m) => m.Venue == Venue.Okx ? "instId" :
        m.Venue == Venue.Gate ? m.Contract == null ? "currency_pair" : "contract" : "symbol";

    public async Task<Dictionary<string, SpreadQuote>> BestQuotes(Market[] markets, CancellationToken ct)
    {
        var output = new Dictionary<string, SpreadQuote>(StringComparer.Ordinal);
        foreach (var group in markets.Where(SpreadSupported).Where(m => !StreamedBook(m.Venue))
                     .GroupBy(m => (m.Source, m.Contract?.Product)))
        {
            var first = group.First();
            if (first.Venue.Extended())
            {
                await AddExtendedQuotes(group.ToArray(), output, ct);
                continue;
            }
            var (root, time) = await TickerBatch(TickerUrl(first, book: true)!, ct);
            // Generation times belong to the snapshot, not the last trade (KuCoin futures ts).
            if (first.Venue == Venue.Bybit) time = SnapshotTime(root.Number("time"), time);
            if (first.Venue == Venue.KuCoin && first.Contract == null)
                time = SnapshotTime(root.GetProperty("data").Number("time"), time);
            var lookup = group.ToDictionary(m => m.Symbol, StringComparer.Ordinal);
            string field = TickerSymbolField(first);
            foreach (var row in TickerRows(first, root).EnumerateArray())
                if (lookup.TryGetValue(row.Optional(field), out var market))
                {
                    string? stamp = market.Venue switch
                    {
                        Venue.Binance when market.Contract != null => "time",
                        Venue.Okx or Venue.Bitget => "ts",
                        Venue.Mexc when market.Contract != null => "timestamp",
                        _ => null
                    };
                    output[market.Key] = DecodeBestQuote(market, row,
                        stamp == null ? time : SnapshotTime(row.Number(stamp), time));
                }
        }
        return output;
    }

    private static long SnapshotTime(double timestamp, long received) => double.IsFinite(timestamp) &&
        timestamp > 0 && timestamp < long.MaxValue && timestamp <= received + 3000 ? Math.Min(received, (long)timestamp) : 0;

    internal static SpreadQuote DecodeBestQuote(Market m, JsonElement row, long time)
    {
        bool spot = m.Contract == null;
        var (bid, ask, bidSize, askSize) = m.Venue switch
        {
            Venue.Binance => ("bidPrice", "askPrice", "bidQty", "askQty"),
            Venue.Mexc when spot => ("bidPrice", "askPrice", "bidQty", "askQty"),
            Venue.Mexc => ("bid1", "ask1", "", ""),
            Venue.Bybit => ("bid1Price", "ask1Price", "bid1Size", "ask1Size"),
            Venue.Okx => ("bidPx", "askPx", "bidSz", "askSz"),
            Venue.Bitget => ("bidPr", "askPr", "bidSz", "askSz"),
            Venue.KuCoin when spot => ("buy", "sell", "bestBidSize", "bestAskSize"),
            Venue.KuCoin => ("bestBidPrice", "bestAskPrice", "bestBidSize", "bestAskSize"),
            Venue.Gate when spot || m.Contract!.Product.StartsWith("delivery/", StringComparison.Ordinal) =>
                ("highest_bid", "lowest_ask", "", ""),
            Venue.Gate => ("highest_bid", "lowest_ask", "highest_size", "lowest_size"),
            Venue.Alpha => ("b", "a", "B", "A"),
            _ => ("", "", "", "")
        };
        bool Size(string field) => field.Length == 0 || row.Number(field) is > 0 and var size && double.IsFinite(size);
        return new(Size(bidSize) ? row.Number(bid) : double.NaN,
            Size(askSize) ? row.Number(ask) : double.NaN, time);
    }
}

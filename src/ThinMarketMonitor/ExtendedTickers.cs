using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    internal static bool StreamedBook(Venue venue) => venue is Venue.Alpha or Venue.Hyperliquid or Venue.Lighter;
    internal static bool HasHistoricalQuoteVolume(Market market) => market.Venue != Venue.Hyperliquid &&
        market is not { Venue: Venue.Kraken, Contract.Unit: QuantityUnit.Base };

    private static string? ExtendedTickerUrl(Market market, bool book) => market.Venue switch
    {
        Venue.Htx => HtxMarket(market) + (market.Contract == null ? "tickers" : book ? "bbo" : "detail/batch_merged"),
        Venue.Kraken => market.Contract == null ? KrakenSpot + "Ticker" : KrakenFutures + "tickers",
        Venue.Weex => WeexMarket(market) + (book ? "ticker/bookTicker" : "ticker/24hr"),
        Venue.Lighter when !book => Lighter + "orderBookDetails",
        _ => null
    };

    private static IEnumerable<(string Symbol, JsonElement Row)> ExtendedTickers(Market m, JsonElement root)
    {
        if (m is { Venue: Venue.Kraken, Contract: null })
        {
            foreach (var entry in KrakenResult(root).EnumerateObject()) yield return (entry.Name, entry.Value);
            yield break;
        }
        var rows = m.Venue switch
        {
            Venue.Htx => HtxField(root, m.Contract == null ? "data" : "ticks"),
            Venue.Kraken => KrakenFuturesField(root, "tickers"),
            Venue.Lighter => LighterField(root, m.Contract == null ? "spot_order_book_details" : "order_book_details"),
            _ => root
        };
        string key = m.Venue == Venue.Lighter ? "market_id" : m.Venue == Venue.Htx && m.Contract?.Product is "linear" or "inverse"
            ? "contract_code" : "symbol";
        foreach (var row in rows.EnumerateArray()) yield return (row.Optional(key), row);
    }

    private async Task<(JsonElement Tick, long Time)?> HtxDeliveryTicker(Market m, CancellationToken ct)
    {
        // Batch turnover uses rolling aliases, which can change before the catalog refreshes.
        var (root, time) = await TickerBatch(HtxMarket(m) + "detail/merged?symbol=" + Uri.EscapeDataString(m.Symbol), ct);
        if (root.Optional("status") == "error") return null;
        if (root.Optional("ch") != $"market.{m.Symbol}.detail.merged") throw new JsonException("HTX вернул другой срочный контракт");
        return (HtxField(root, "tick"), time);
    }

    private async Task AddExtendedSnapshots(Market[] markets, Dictionary<string, MarketSnapshot> output, CancellationToken ct)
    {
        var first = markets[0];
        if (first is { Venue: Venue.Htx, Contract.Product: "delivery" })
        {
            foreach (var m in markets)
                if (await HtxDeliveryTicker(m, ct) is { } snapshot)
                    output[m.Key] = DecodeExtendedSnapshot(m, snapshot.Tick);
            return;
        }
        if (first.Venue == Venue.Hyperliquid)
        {
            if (first.Contract == null)
            {
                var (root, _) = await CachedTicker("hyperliquid:spot-contexts", token => HyperInfo("spotMetaAndAssetCtxs", token), ct);
                var lookup = root[1].EnumerateArray().GroupBy(x => x.Optional("coin")).ToDictionary(g => g.Key, g => g.First());
                foreach (var m in markets)
                    if (lookup.TryGetValue(m.Symbol, out var row)) output[m.Key] = DecodeExtendedSnapshot(m, row);
            }
            else
            {
                foreach (var group in markets.GroupBy(m => m.Symbol.Contains(':') ? m.Symbol.Split(':', 2)[0] : ""))
                {
                    var (root, _) = await CachedTicker("hyperliquid:perp-contexts:" + group.Key,
                        token => HyperInfo("metaAndAssetCtxs", token, dex: group.Key), ct);
                    var universe = root[0].GetProperty("universe");
                    var values = root[1];
                    if (universe.GetArrayLength() != values.GetArrayLength()) throw new JsonException("Несовпадение контекстов Hyperliquid");
                    var lookup = group.ToDictionary(m => m.Symbol, StringComparer.Ordinal);
                    for (int i = 0; i < universe.GetArrayLength(); i++)
                        if (lookup.TryGetValue(universe[i].Optional("name"), out var m))
                            output[m.Key] = DecodeExtendedSnapshot(m, values[i]);
                }
            }
            return;
        }
        var (snapshots, _) = await TickerBatch(ExtendedTickerUrl(first, false)!, ct);
        var bySymbol = markets.ToLookup(m => m.Symbol, StringComparer.Ordinal);
        foreach (var (symbol, row) in ExtendedTickers(first, snapshots))
            foreach (var market in bySymbol[symbol])
                if (TickerMatches(market, row)) output[market.Key] = DecodeExtendedSnapshot(market, row);
    }

    private static bool TickerMatches(Market market, JsonElement row)
    {
        if (market.Venue == Venue.Kraken && IsTrue(row, "suspended")) return false;
        if (market.Venue == Venue.Lighter && row.Optional("status") != "active") return false;
        return true;
    }

    internal static MarketSnapshot DecodeExtendedSnapshot(Market m, JsonElement row)
    {
        bool spot = m.Contract == null;
        double volume = m.Venue switch
        {
            Venue.Htx when spot => row.Number("vol"),
            Venue.Htx when m.Contract?.Unit == QuantityUnit.Quote => m.Turnover(row.Number("vol"), 1),
            Venue.Htx => row.Number("trade_turnover"),
            Venue.Kraken when spot => ArrayNumber(row, "v", 1) * ArrayNumber(row, "p", 1),
            Venue.Kraken => row.Number("volumeQuote"),
            Venue.Weex => row.Number("quoteVolume"),
            Venue.Lighter => row.Number("daily_quote_token_volume"),
            Venue.Hyperliquid => row.Number("dayNtlVlm"),
            _ => double.NaN
        };
        double change = m.Venue switch
        {
            Venue.Htx when row.Number("open") is > 0 and var open => (row.Number("close") / open - 1) * 100,
            Venue.Kraken when !spot => row.Number("change24h"),
            Venue.Weex when row.Number("openPrice") is > 0 and var open => (row.Number("lastPrice") / open - 1) * 100,
            Venue.Lighter when row.Number("daily_trades_count") > 0 => row.Number("daily_price_change"),
            // The spot Kraken o is midnight UTC. Hyperliquid markPx is not a last trade.
            _ => double.NaN
        };
        return new(UsdQuotes.Supported(m) ? ValidVolume(volume) : null, double.IsFinite(change) ? change : null);
    }

    private async Task AddExtendedQuotes(Market[] markets, Dictionary<string, SpreadQuote> output, CancellationToken ct)
    {
        var first = markets[0];
        if (first is { Venue: Venue.Htx, Contract.Product: "delivery" })
        {
            foreach (var m in markets)
                if (await HtxDeliveryTicker(m, ct) is { } snapshot)
                    output[m.Key] = DecodeExtendedQuote(m, snapshot.Tick, snapshot.Time);
            return;
        }
        var (root, time) = await TickerBatch(ExtendedTickerUrl(first, true)!, ct);
        var lookup = markets.ToLookup(m => m.Symbol, StringComparer.Ordinal);
        foreach (var (symbol, row) in ExtendedTickers(first, root))
            foreach (var market in lookup[symbol])
                if (TickerMatches(market, row)) output[market.Key] = DecodeExtendedQuote(market, row, time);
    }

    private static double ArrayNumber(JsonElement row, string field, int item) =>
        row.TryGetProperty(field, out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > item
            ? values[item].Num() : double.NaN;

    internal static SpreadQuote DecodeExtendedQuote(Market m, JsonElement row, long receipt)
    {
        bool spot = m.Contract == null;
        return m.Venue switch
        {
            Venue.Htx when spot => SizedBook(row.Number("bid"), row.Number("ask"), row.Number("bidSize"), row.Number("askSize"), receipt),
            Venue.Htx => SizedBook(ArrayNumber(row, "bid", 0), ArrayNumber(row, "ask", 0), ArrayNumber(row, "bid", 1),
                ArrayNumber(row, "ask", 1), SnapshotTime(row.Number("ts"), receipt)),
            Venue.Kraken when spot => SizedBook(ArrayNumber(row, "b", 0), ArrayNumber(row, "a", 0), ArrayNumber(row, "b", 2),
                ArrayNumber(row, "a", 2), receipt),
            Venue.Kraken => SizedBook(row.Number("bid"), row.Number("ask"), row.Number("bidSize"), row.Number("askSize"), receipt),
            Venue.Weex => SizedBook(row.Number("bidPrice"), row.Number("askPrice"), row.Number("bidQty"), row.Number("askQty"),
                spot ? receipt : SnapshotTime(row.Number("time"), receipt)),
            _ => default
        };
    }

    internal static SpreadQuote SizedBook(double bid, double ask, double bidSize, double askSize, long time) =>
        new(double.IsFinite(bidSize) && bidSize > 0 ? bid : double.NaN,
            double.IsFinite(askSize) && askSize > 0 ? ask : double.NaN, time);
}

using System.Text.Json;

namespace ThinMarketMonitor;

internal record MarketSnapshot(double? Turnover, double? Change);
internal sealed partial class ExchangeApi
{
    public async Task<Dictionary<string, MarketSnapshot>> Snapshots(Market[] markets, CancellationToken ct)
    {
        var output = new Dictionary<string, MarketSnapshot>();
        foreach (var group in markets.GroupBy(m => m.Source).SelectMany(source => source.GroupBy(m => m.Contract?.Product)))
        {
            string? p = group.Key;
            var venue = group.First().Venue;
            string? url = venue switch
            {
                Venue.Binance => (p == "usdm" ? Usdm : p == "coinm" ? Coinm : Binance) + "ticker/24hr",
                Venue.Bybit => Bybit + "tickers?category=" + (p ?? "spot"),
                Venue.Okx => Okx + "market/tickers?instType=" + (p ?? "SPOT"),
                Venue.Bitget => p == null ? Bitget + "market/tickers" : Mix + "tickers?productType=" + p,
                Venue.Mexc => p == null ? Mexc + "ticker/24hr" : MexcContract + "ticker",
                Venue.KuCoin => p == null ? KuCoin + "v1/market/allTickers" : KuContract + "contracts/active",
                Venue.Gate => p == null ? Gate + "tickers" : p.StartsWith("futures/", StringComparison.Ordinal) ? GateContract + p + "/tickers" : null,
                _ => null
            };
            if (url == null) continue;
            var root = await Get(url, ct);
            var rows = venue switch
            {
                Venue.Bybit => BybitResult(root).GetProperty("list"),
                Venue.Okx => Envelope(root, "0"),
                Venue.Bitget => Envelope(root, "00000"),
                Venue.Mexc when p != null => Envelope(root, "0"),
                Venue.KuCoin when p == null => Envelope(root, "200000").GetProperty("ticker"),
                Venue.KuCoin => Envelope(root, "200000"),
                _ => root
            };
            var lookup = group.ToDictionary(m => m.Symbol, StringComparer.Ordinal);
            string name = venue == Venue.Okx ? "instId" : venue == Venue.Gate ? p == null ? "currency_pair" : "contract" : "symbol";
            foreach (var row in rows.EnumerateArray())
                if (lookup.TryGetValue(row.Optional(name), out var m)) output[m.Key] = DecodeSnapshot(m, row);
        }
        return output;
    }

    public async Task<MarketSnapshot?> AlphaSnapshot(Market market, CancellationToken ct)
    {
        await PaceHistory(Venue.Alpha, ct);
        var row = Envelope(await Get(Alpha + $"alpha-trade/ticker?symbol={Uri.EscapeDataString(market.Symbol)}", ct), "000000");
        return row.Optional("symbol") == market.Symbol ? DecodeSnapshot(market, row) : null;
    }

    internal static MarketSnapshot DecodeSnapshot(Market m, JsonElement row)
    {
        string? p = m.Contract?.Product;
        double amount = m.Venue switch
        {
            Venue.Binance when p == "coinm" => m.Turnover(row.Number("volume"), 1),
            Venue.Binance or Venue.Alpha => row.Number("quoteVolume"),
            Venue.Bybit => row.Number(p == "inverse" ? "volume24h" : "turnover24h"),
            Venue.Okx when p == null => row.Number("volCcy24h"),
            Venue.Okx when m.Contract?.Unit == QuantityUnit.Quote => m.Turnover(row.Number("vol24h"), 1),
            Venue.Okx => double.NaN, // This ticker reports base units. Historical candles have quote turnover.
            Venue.Bitget => row.Number("quoteVolume"),
            Venue.Mexc => row.Number(p == null ? "quoteVolume" : "amount24"),
            Venue.KuCoin => row.Number(p == null ? "volValue" : "turnoverOf24h"),
            Venue.Gate => row.Number(p == null ? "quote_volume" : "volume_24h_quote"),
            _ => double.NaN
        };
        double change = m.Venue switch
        {
            Venue.Binance or Venue.Alpha => row.Number("priceChangePercent"),
            Venue.Mexc when p == null => row.Number("priceChangePercent") * 100,
            Venue.Mexc => row.Number("riseFallRate") * 100,
            Venue.Bybit => row.Number("price24hPcnt") * 100,
            Venue.Bitget => row.Number("change24h") * 100,
            Venue.KuCoin => row.Number(p == null ? "changeRate" : "priceChgPct") * 100,
            Venue.Gate => row.Number("change_percentage"),
            Venue.Okx => row.Number("open24h") is > 0 and var open ? (row.Number("last") / open - 1) * 100 : double.NaN,
            _ => double.NaN
        };
        return new(UsdQuotes.Supported(m.Quote) && (m.Contract?.Multiplier ?? 1) > 0 ? ValidVolume(amount) : null,
            double.IsFinite(change) ? change : null);
    }
}

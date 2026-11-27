using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private const string BitgetInstruments = "https://api.bitget.com/api/v3/market/instruments?category=";
    // Some venues group listed ETFs with the underlying indices or commodities.
    private static readonly HashSet<string> KnownExchangeTradedFunds = new(StringComparer.Ordinal)
    {
        "AMDL", "AMZU", "ARKK", "ARTY", "BITO", "CONL", "DRAM", "EIDO", "EWT", "EWJ", "EWY", "EWZ", "EWZS",
        "GDX", "GLD", "IBIT", "IEFA", "IGV", "INDA", "IWM", "KODEX200", "KORU", "KSTR", "KWEB", "MAGS",
        "MSFU", "MSTU", "NVDL", "QQQ", "SHLD", "SLV", "SMH", "SOXL", "SOXS", "SOXX", "SPY", "SQQQ", "TBT",
        "TMF", "TQQQ", "TSLL", "TZA", "URNM", "USO", "UVXY", "XBI", "XLE", "XLF", "XLI", "XLK", "XLU", "XLV",
    };
    // The Lighter book catalog has no asset-class field. Scope its documented markets to the official deployer.
    // https://docs.lighter.xyz/trading/real-world-assets-rwas/market-specifications
    private static readonly HashSet<string> LighterStockFunds = new(StringComparer.Ordinal)
    {
        "AAPL", "AMD", "AMZN", "ANTHROPIC", "ASML", "BABA", "BB", "BMNR", "BOT", "CBRS", "COIN", "CRCL",
        "CRWV", "CXMT", "DELL", "DRAM", "EWY", "GEV", "GOOGL", "HOOD", "HYUNDAIUSD", "IBM", "INTC", "IWM",
        "LITE", "META", "MINIMAX", "MRNA", "MRVL", "MSFT", "MSTR", "MU", "NBIS", "NOW", "NVDA", "OPENAI",
        "ORCL", "OURA", "PLTR", "QQQ", "RKLB", "SAMSUNGUSD", "SHEIN", "SKHY", "SKHYNIXUSD", "SMIC", "SNDK",
        "SOXL", "SPCX", "SPY", "STABLECOINX", "STRC", "TENCENT", "TSLA", "TSM", "TTWO", "UNITREE",
        "WEN", "XIAOMI", "ZHIPU",
    };
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> stockSymbols = new(StringComparer.Ordinal);

    private async Task<IReadOnlySet<string>> LoadStockSymbols(string key, Func<CancellationToken, Task<JsonElement>> fetch,
        Func<JsonElement, IEnumerable<string>> select, CancellationToken ct)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            var symbols = select(await fetch(attempt.Token)).ToHashSet(StringComparer.Ordinal);
            return stockSymbols[key] = symbols;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException
                                   or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            // Optional classification can fail independently of the instrument catalog.
            return stockSymbols.GetValueOrDefault(key) ?? new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private Task<IReadOnlySet<string>> BitgetStockSymbols(string product, CancellationToken ct) =>
        LoadStockSymbols(BitgetInstruments + product, token => Get(BitgetInstruments + product, token),
            root => Envelope(root, "00000").EnumerateArray()
                .Where(row => StockFundClass(row.Optional("instrumentType"))).Select(row => row.Str("symbol")), ct);

    private Task<IReadOnlySet<string>> GateStockAssets(CancellationToken ct) =>
        LoadStockSymbols(Gate + "currencies", token => Get(Gate + "currencies", token), rows =>
            rows.EnumerateArray().Where(row => HasTag(row, "category", tag => tag is "stocks" or "ondo-stocks" or "gstocks" or "xstocks") ||
                row.TryGetProperty("tokenized_security_info", out var info) && info.ValueKind == JsonValueKind.Object &&
                info.Optional("underlying").Length > 0).Select(row => Asset(row.Str("currency"))), ct);

    private Task<IReadOnlySet<string>> HyperStockSymbols(CancellationToken ct) =>
        LoadStockSymbols("hyperliquid:perpCategories", token => HyperInfo("perpCategories", token),
            DecodeHyperStockSymbols, ct);

    internal static IEnumerable<string> DecodeHyperStockSymbols(JsonElement entries) => entries.EnumerateArray()
        .Where(entry => entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() == 2 &&
            (StockFundClass(entry[1].Str()) || entry[1].Str() == "preipo"))
        .Select(entry => entry[0].Str());

    private static Market MarkStockFund(Market market) =>
        market with { Identifiers = (market.Identifiers ?? new()) with { StockOrFund = true } };

    private static MarketIdentifiers? SpotKinds(Venue venue, JsonElement row)
    {
        bool leveraged = venue switch
        {
            Venue.Gate => LeveragedAssetName.IsMatch(row.Optional("base_name")),
            Venue.Bybit => LeveragedAssetName.IsMatch(row.Optional("symbolType")),
            Venue.Htx => row.Optional("tags").Split(',').Contains("etp", StringComparer.OrdinalIgnoreCase),
            _ => false
        };
        bool stock = venue switch
        {
            Venue.Bybit => StockFundClass(row.Optional("symbolType")),
            Venue.Okx => row.Optional("instCategory") == "3",
            Venue.KuCoin => StockFundClass(row.Optional("market")),
            Venue.Bitget => StockFundClass(row.Optional("instrumentType")),
            Venue.Mexc => HasTag(row, "conceptPlates", tag => tag == "Tokenized Stocks"),
            Venue.Htx => row.Optional("tags").Split(',').Any(StockFundClass),
            _ => false
        };
        return leveraged || stock ? new(LeveragedToken: leveraged, StockOrFund: stock) : null;
    }

    private static bool ContractIsStockFund(Venue venue, JsonElement row, string baseCoin) => venue switch
    {
        Venue.Binance => row.Optional("contractType") == "TRADIFI_PERPETUAL" && StockFundClass(row.Optional("underlyingType")),
        Venue.Bybit => StockFundClass(row.Optional("symbolType")),
        Venue.Okx => row.Optional("instCategory") == "3",
        Venue.Bitget => StockFundClass(row.Optional("instrumentType")),
        Venue.KuCoin => StockFundClass(row.Optional("assetClass")) || row.Optional("subMarketType") == "US.STOCK",
        Venue.Gate => StockFundClass(row.Optional("contract_type")) ||
            row.Optional("contract_type") is "indices" or "metals" && KnownExchangeTradedFunds.Contains(baseCoin),
        Venue.Mexc => HasTag(row, "conceptPlate", tag => tag == "mc-trade-zone-ETF") ||
            HasTag(row, "conceptPlate", tag => tag == "mc-trade-zone-Stock") &&
            (!HasTag(row, "conceptPlate", tag => tag == "mc-trade-zone-stockindex") ||
                KnownExchangeTradedFunds.Contains(baseCoin) || baseCoin == "QQQSTOCK"),
        Venue.Kraken => StockFundClass(row.Optional("category")) || row.Optional("category") == "Pre-IPO",
        Venue.Htx => HasTag(row, "labels", StockFundClass) ||
            KnownExchangeTradedFunds.Contains(baseCoin) && HasTag(row, "labels", tag => tag is "indices" or "commodities"),
        Venue.Weex => row.Optional("contractType") == "TRADIFI_PERPETUAL" &&
            (StockFundClass(row.Optional("underlyingType")) || row.Optional("underlyingType") is "Indices" or "Metals" &&
                KnownExchangeTradedFunds.Contains(baseCoin)),
        _ => false
    };

    private static bool StockFundClass(string label) =>
        label.ToLowerInvariant() is "stock" or "stocks" or "xstocks" or "etf" or "etfs" or "fund" or "funds" or "equity" or "equities";

    private static bool HasTag(JsonElement row, string field, Func<string, bool> matches) =>
        row.TryGetProperty(field, out var tags) && tags.ValueKind == JsonValueKind.Array &&
        tags.EnumerateArray().Any(tag => matches(tag.Str()));
}

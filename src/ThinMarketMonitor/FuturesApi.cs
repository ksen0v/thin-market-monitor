using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private const string Usdm = "https://fapi.binance.com/fapi/v1/";
    private const string Coinm = "https://dapi.binance.com/dapi/v1/";
    private const string Mix = "https://api.bitget.com/api/v2/mix/market/";
    private const string MexcContract = "https://contract.mexc.com/api/v1/contract/";
    private const string KuContract = "https://api-futures.kucoin.com/api/v1/";
    private const string GateContract = "https://api.gateio.ws/api/v4/";

    public Task<Catalog> LoadCatalog(Venue venue, CancellationToken ct) => LoadCatalog(new MarketSource(venue), ct);

    private async Task<Market[]> FuturesMarkets(Venue venue, CancellationToken ct)
    {
        (string Product, string Url)[] feeds = venue switch
        {
            Venue.Binance => [("usdm", Usdm + "exchangeInfo"), ("coinm", Coinm + "exchangeInfo")],
            Venue.Bybit => [("linear", Bybit + "instruments-info?category=linear&limit=1000"),
                ("inverse", Bybit + "instruments-info?category=inverse&limit=1000")],
            Venue.Okx => [("SWAP", Okx + "public/instruments?instType=SWAP"), ("FUTURES", Okx + "public/instruments?instType=FUTURES")],
            Venue.Bitget => new[] { "USDT-FUTURES", "USDC-FUTURES", "COIN-FUTURES" }
                .Select(p => (p, Mix + "contracts?productType=" + p)).ToArray(),
            Venue.Mexc => [("contract", MexcContract + "detail")],
            Venue.KuCoin => [("contract", KuContract + "contracts/active")],
            Venue.Gate => new[] { "futures/usdt", "futures/btc", "futures/usd1", "delivery/usdt", "delivery/btc" }
                .Select(p => (p, GateContract + p + "/contracts")).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(venue))
        };
        var lists = await Task.WhenAll(feeds.Select(async feed =>
        {
            var stocks = venue == Venue.Bitget ? BitgetStockSymbols(feed.Product, ct) : null;
            string cursor = "";
            var seen = new HashSet<string>();
            var markets = new List<Market>();
            do
            {
                var root = await Get(feed.Url + (cursor.Length == 0 ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), ct);
                JsonElement rows = venue switch
                {
                    Venue.Binance => root.GetProperty("symbols"),
                    Venue.Bybit => BybitResult(root).GetProperty("list"),
                    Venue.Okx => Envelope(root, "0"),
                    Venue.Bitget => Envelope(root, "00000"),
                    Venue.Mexc => Envelope(root, "0"),
                    Venue.KuCoin => Envelope(root, "200000"),
                    _ => root
                };
                foreach (var row in rows.EnumerateArray())
                    if (DecodeContract(venue, feed.Product, row) is { } m) markets.Add(m);
                cursor = venue == Venue.Bybit ? BybitResult(root).Optional("nextPageCursor") : "";
                if (cursor.Length > 0 && (!seen.Add(cursor) || seen.Count > 30))
                    throw new JsonException("Неполная пагинация фьючерсов Bybit");
            } while (cursor.Length > 0);
            if (stocks == null) return markets;
            var identified = await stocks;
            return markets.Select(market => identified.Contains(market.Symbol) ? MarkStockFund(market) : market);
        }));
        return Validate(lists.SelectMany(x => x));
    }

    internal static Market? DecodeContract(Venue venue, string product, JsonElement row)
    {
        string symbol = row.Optional(venue == Venue.Okx ? "instId" : venue == Venue.Gate ? "name" : "symbol");
        string baseCoin, quote;
        string? listingAsset = null;
        double multiplier = 1;
        var unit = QuantityUnit.Base;
        bool stream = true;
        switch (venue)
        {
            case Venue.Binance:
                if (row.Str(product == "coinm" ? "contractStatus" : "status") != "TRADING") return null;
                baseCoin = row.Str("baseAsset"); quote = row.Str("quoteAsset");
                if (product == "coinm") { unit = QuantityUnit.Quote; multiplier = row.Number("contractSize"); }
                if (product == "usdm" && row.Optional("contractType") == "TRADIFI_PERPETUAL" &&
                    row.Optional("underlyingType") == "EQUITY") listingAsset = UsStockListingAsset(baseCoin);
                break;
            case Venue.Bybit:
                if (row.Str("status") != "Trading" || row.Optional("isPreListing") == "True") return null;
                baseCoin = row.Str("baseCoin"); quote = row.Str("quoteCoin");
                if (product == "inverse") unit = QuantityUnit.Quote;
                if (product == "linear" && row.Optional("contractType") == "LinearPerpetual" &&
                    row.Optional("symbolType") == "stock" && row.Optional("marketRegion") == "US")
                    listingAsset = UsStockListingAsset(row.Optional("underlyingTicker"));
                break;
            case Venue.Okx:
                if (row.Str("state") != "live" || row.Optional("ruleType") == "pre_market") return null;
                var underlying = row.Str("uly").Split('-');
                if (underlying.Length < 2) return null;
                baseCoin = underlying[0]; quote = underlying[1];
                if ((product is "SWAP" or "FUTURES") && row.Optional("instCategory") == "3" &&
                    row.Optional("ctType") == "linear") listingAsset = UsStockListingAsset(baseCoin);
                multiplier = row.Number("ctVal");
                if (row.Number("ctMult") is > 0 and < double.PositiveInfinity) multiplier *= row.Number("ctMult");
                unit = row.Str("ctValCcy") == quote ? QuantityUnit.Quote : QuantityUnit.Base;
                if (unit == QuantityUnit.Base && row.Str("ctValCcy") != baseCoin) multiplier = double.NaN;
                break;
            case Venue.Bitget:
                if (row.Str("symbolStatus") != "normal") return null;
                baseCoin = row.Str("baseCoin"); quote = row.Str("quoteCoin");
                // Linear trades report base quantity. The coin-margined size unit is not assumed.
                if (product == "COIN-FUTURES") multiplier = double.NaN;
                break;
            case Venue.Mexc:
                if (row.Str("state") != "0" || row.Optional("isHidden") == "True" || row.Optional("preMarket") == "True") return null;
                baseCoin = row.Str("baseCoin"); quote = row.Str("quoteCoin");
                multiplier = row.Number("contractSize");
                if (row.Optional("settleCoin") == baseCoin) multiplier = double.NaN;
                break;
            case Venue.KuCoin:
                if (row.Str("status") != "Open" || row.Optional("marketStage") is "PRE_MARKET" or "PREMARKET") return null;
                baseCoin = row.Str("baseCurrency"); quote = row.Str("quoteCurrency");
                multiplier = Math.Abs(row.Number("multiplier"));
                unit = row.Optional("isInverse") == "True" ? QuantityUnit.Quote : QuantityUnit.Base;
                break;
            case Venue.Gate:
                if (row.GetProperty("in_delisting").GetBoolean() || row.Optional("is_pre_market") == "True" ||
                    row.Optional("status") is not ("" or "trading")) return null;
                var pair = (product.StartsWith("delivery", StringComparison.Ordinal) ? row.Str("underlying") : symbol).Split('_');
                if (pair.Length < 2) return null;
                baseCoin = pair[0]; quote = pair[1];
                multiplier = row.Number("quanto_multiplier");
                unit = row.Str("type") == "inverse" ? QuantityUnit.Quote : QuantityUnit.Base;
                stream = !product.StartsWith("delivery", StringComparison.Ordinal);
                break;
            default: return null;
        }
        if (symbol.Length == 0 || baseCoin.Length == 0 || quote.Length == 0) throw new JsonException("Пустой контракт");
        listingAsset ??= DenominatedListingAsset(venue, baseCoin);
        bool perpetual = venue switch
        {
            Venue.Binance => row.Optional("contractType") is "PERPETUAL" or "TRADIFI_PERPETUAL",
            Venue.Bybit => row.Optional("contractType") is "LinearPerpetual" or "InversePerpetual",
            Venue.Okx => product == "SWAP",
            Venue.Bitget => row.Optional("symbolType") == "perpetual",
            Venue.Mexc => row.Optional("futureType") == "1",
            Venue.KuCoin => row.TryGetProperty("expireDate", out var expire) && expire.ValueKind == JsonValueKind.Null,
            Venue.Gate => stream,
            _ => false
        };
        bool stockFund = ContractIsStockFund(venue, row, baseCoin);
        return new(venue, symbol, Underlying(venue, baseCoin), quote, new(product,
            double.IsFinite(multiplier) && multiplier > 0 ? multiplier : 0, unit, stream, perpetual, baseCoin),
            listingAsset == null && !stockFund ? null : new(ListingAsset: listingAsset, StockOrFund: stockFund));
    }

    private static string? UsStockListingAsset(string ticker) =>
        ticker is "AAPL" or "AMD" ? "equity:US:" + ticker : null;

    private static string? DenominatedListingAsset(Venue venue, string asset) => ExtendedDenomination(venue, asset)?.Asset;

    // Catalogs keep the exchange's symbols and prices. Only a chart overlay projects prices into another market's units.
    private static (string Asset, int Units)? ExtendedDenomination(Venue venue, string asset) => (venue, asset) switch
    {
        (Venue.Weex or Venue.Lighter, "1000PEPE") or (Venue.Hyperliquid, "kPEPE") => ("PEPE", 1000),
        (Venue.Weex or Venue.Lighter, "1000FLOKI") or (Venue.Hyperliquid, "kFLOKI") => ("FLOKI", 1000),
        (Venue.Weex or Venue.Lighter, "1000SHIB") or (Venue.Hyperliquid, "kSHIB") => ("SHIB", 1000),
        (Venue.Weex or Venue.Lighter or Venue.KuCoin or Venue.Mexc, "1000BONK") or (Venue.Hyperliquid, "kBONK") => ("BONK", 1000),
        _ => null
    };

    internal static string Underlying(Venue venue, string asset)
    {
        if (venue is Venue.KuCoin or Venue.Bitget && asset == "XBT") return "BTC";
        return ContractDenomination(venue, asset)?.Asset ?? asset;
    }

    private static (string Asset, int Units)? ContractDenomination(Venue venue, string asset) => (venue, asset) switch
    {
        (Venue.Binance or Venue.Bybit or Venue.Bitget or Venue.Gate, "1000SHIB") => ("SHIB", 1000),
        (Venue.Binance or Venue.Bybit or Venue.Bitget or Venue.Gate, "1000PEPE") => ("PEPE", 1000),
        (Venue.Binance or Venue.Bybit, "1000BONK") => ("BONK", 1000),
        (Venue.Binance or Venue.Bybit, "1000FLOKI") => ("FLOKI", 1000),
        (Venue.Binance or Venue.Bybit, "1000SATS") => ("SATS", 1000),
        (Venue.Bybit, "10000LADYS") => ("LADYS", 10000),
        _ => null
    };

    internal static int PriceDenomination(Market market)
    {
        if (market.Contract is not { } contract) return 1;
        string native = contract.NativeBase ?? (market.Venue == Venue.Hyperliquid ? market.Symbol : market.Base);
        var denomination = ContractDenomination(market.Venue, native) ?? ExtendedDenomination(market.Venue, native);
        return denomination is { } known && known.Asset == market.ListingAsset ? known.Units : 1;
    }
}

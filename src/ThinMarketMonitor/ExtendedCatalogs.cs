using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace ThinMarketMonitor;

internal sealed partial class ExchangeApi
{
    private const string HtxSpot = "https://api.huobi.pro/";
    private const string HtxContract = "https://api.hbdm.com/";
    private const string KrakenSpot = "https://api.kraken.com/0/public/";
    private const string KrakenFutures = "https://futures.kraken.com/derivatives/api/v3/";
    private const string KrakenCharts = "https://futures.kraken.com/api/charts/v1/trade/";
    private const string WeexSpot = "https://api-spot.weex.com/api/v3/";
    private const string WeexFutures = "https://api-contract.weex.com/capi/v3/market/";
    private const string Lighter = "https://mainnet.zklighter.elliot.ai/api/v1/";
    private const string Hyperliquid = "https://api.hyperliquid.xyz/info";
    internal HyperliquidStream HyperStream { get; } = new();

    private async Task<Market[]> ExtendedMarkets(MarketSource source, CancellationToken ct)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (source.Venue == Venue.Htx && source.Segment == Segment.Futures)
        {
            var requests = new[]
            {
                ("linear", HtxContract + "linear-swap-api/v1/swap_contract_info"),
                ("inverse", HtxContract + "swap-api/v1/swap_contract_info"),
                ("delivery", HtxContract + "api/v1/contract_contract_info")
            };
            var groups = await Task.WhenAll(requests.Select(async item =>
                DecodeHtxContracts(await Get(item.Item2, ct), item.Item1, now)));
            return Validate(groups.SelectMany(x => x));
        }
        if (source.Venue == Venue.Hyperliquid)
        {
            var (spot, _) = await CachedTicker("hyperliquid:spot-contexts", token => HyperInfo("spotMetaAndAssetCtxs", token), ct);
            if (source.Segment == Segment.Spot) return Validate(DecodeHyperSpot(spot));
            var perps = await HyperInfo("allPerpMetas", ct);
            return Validate(DecodeHyperPerps(perps, spot[0]));
        }
        if (source.Venue == Venue.Lighter)
        {
            var (root, _) = await TickerBatch(Lighter + "orderBookDetails", ct);
            var assets = await Get(Lighter + "assetDetails", ct);
            return Validate(DecodeLighter(source.Segment, root, assets, now));
        }
        var url = source.Venue switch
        {
            Venue.Htx => HtxSpot + "v1/common/symbols",
            Venue.Kraken => source.Segment == Segment.Spot ? KrakenSpot + "AssetPairs" : KrakenFutures + "instruments",
            Venue.Weex => source.Segment == Segment.Spot ? WeexSpot + "exchangeInfo" : WeexFutures + "exchangeInfo",
            _ => throw new ArgumentOutOfRangeException(nameof(source))
        };
        return Validate(DecodeExtendedCatalog(source, await Get(url, ct), now));
    }

    internal static IEnumerable<Market> DecodeExtendedCatalog(MarketSource source, JsonElement root, long now)
    {
        bool spot = source.Segment == Segment.Spot;
        switch (source.Venue)
        {
            case Venue.Htx:
                foreach (var row in HtxField(root, "data").EnumerateArray())
                    if (row.Optional("state") == "online")
                        yield return new(Venue.Htx, row.Str("symbol"), Asset(row.Str("base-currency")), Asset(row.Str("quote-currency")),
                            Identifiers: row.Optional("tags").Split(',').Contains("etp", StringComparer.OrdinalIgnoreCase)
                                ? new(LeveragedToken: true) : null);
                break;
            case Venue.Weex:
                foreach (var row in root.GetProperty("symbols").EnumerateArray())
                {
                    if (spot ? row.Optional("status") != "TRADING" || IsFalse(row, "enableTrade") :
                        row.Optional("contractType") is not ("PERPETUAL" or "TRADIFI_PERPETUAL") ||
                        row.Optional("status") is not ("" or "TRADING") || !IsTrue(row, "forwardContractFlag")) continue;
                    string asset = Asset(row.Str("baseAsset")), quote = Asset(row.Str("quoteAsset"));
                    bool tradFi = row.Optional("contractType") == "TRADIFI_PERPETUAL";
                    string? listingAsset = tradFi
                        ? (row.Optional("underlyingType") == "Stocks" ? UsStockListingAsset(asset) : null) ?? "weex:" + asset
                        : DenominatedListingAsset(Venue.Weex, asset);
                    // The public trade q and candle volume are already base units, independent of order lot size.
                    yield return new(Venue.Weex, row.Str("symbol"), asset, quote, spot ? null : new("linear"),
                        spot ? null : new(ListingAsset: listingAsset, Collateral: row.Optional("marginAsset")));
                }
                break;
            case Venue.Kraken when spot:
                foreach (var entry in KrakenResult(root).EnumerateObject())
                {
                    var row = entry.Value;
                    if (row.Optional("status") != "online" || entry.Name.EndsWith(".d", StringComparison.Ordinal)) continue;
                    var parts = row.Optional("wsname").Split('/');
                    if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace)) continue;
                    string asset = KrakenAsset(parts[0]), quote = KrakenAsset(parts[1]);
                    yield return new(Venue.Kraken, entry.Name, asset, quote,
                        Identifiers: new(Rest: row.Str("altname"), Stream: asset + "/" + quote));
                }
                break;
            case Venue.Kraken:
                foreach (var row in KrakenFuturesField(root, "instruments").EnumerateArray())
                {
                    if (!IsTrue(row, "tradeable") || IsTrue(row, "suspended") ||
                        row.Optional("type") is not ("flexible_futures" or "futures_inverse" or "futures_vanilla") ||
                        IsoTime(row.Optional("lastTradingTime")) is > 0 and var expiry && expiry <= now) continue;
                    string symbol = row.Str("symbol");
                    if (!(symbol.StartsWith("PF_", StringComparison.Ordinal) || symbol.StartsWith("FF_", StringComparison.Ordinal) ||
                          symbol.StartsWith("PI_", StringComparison.Ordinal) || symbol.StartsWith("FI_", StringComparison.Ordinal))) continue;
                    bool inverse = row.Optional("type") == "futures_inverse";
                    double size = inverse ? row.Number("contractSize") : 1;
                    if (!double.IsFinite(size) || size <= 0) continue;
                    yield return new(Venue.Kraken, symbol, KrakenAsset(row.Str("base")), KrakenAsset(row.Str("quote")),
                        new("derivatives", size, inverse ? QuantityUnit.Quote : QuantityUnit.Base));
                }
                break;
        }
    }

    internal static IEnumerable<Market> DecodeHtxContracts(JsonElement root, string product, long now)
    {
        foreach (var row in HtxField(root, "data").EnumerateArray())
        {
            if (row.Optional("contract_status") != "1" || row.Number("delivery_time") is > 0 and var expiration && expiration <= now) continue;
            string symbol = row.Str("contract_code"), asset = Asset(row.Str("symbol"));
            string quote = product == "linear" ? Asset(row.Str("trade_partition")) : "USD";
            double size = row.Number("contract_size");
            if (quote.Length == 0 || !double.IsFinite(size) || size <= 0) continue;
            yield return new(Venue.Htx, symbol, asset, quote,
                new(product, size, product == "linear" ? QuantityUnit.Base : QuantityUnit.Quote),
                new(Collateral: product == "linear" ? quote : asset));
        }
    }

    internal static IEnumerable<Market> DecodeLighter(Segment segment, JsonElement root, JsonElement assets, long now)
    {
        var tokens = LighterField(assets, "asset_details").EnumerateArray().ToDictionary(t => t.Str("asset_id"), t => Asset(t.Str("symbol")));
        bool spot = segment == Segment.Spot;
        var books = LighterField(root, spot ? "spot_order_book_details" : "order_book_details");
        var duplicateNames = books.EnumerateArray().GroupBy(row => row.Str("symbol"), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in books.EnumerateArray())
        {
            if (row.Optional("market_type") != (spot ? "spot" : "perp") || row.Optional("status") != "active" ||
                IsTrue(row, "is_frozen") || row.Number("end_timestamp") is > 0 and var end && end <= now / (end < 1e12 ? 1000 : 1)) continue;
            if (row.TryGetProperty("market_config", out var config) && IsTrue(config, "hidden")) continue;
            string id = row.Str("market_id");
            if (!int.TryParse(id, out int index) || index < 0) continue;
            string nativeName = row.Str("symbol");
            string asset = Asset(nativeName), quote = "USD";
            if (spot && (!tokens.TryGetValue(row.Str("base_asset_id"), out asset!) ||
                         !tokens.TryGetValue(row.Str("quote_asset_id"), out quote!))) continue;
            string deployer = row.Optional("market_deployer");
            string? listingAsset = spot ? null : deployer switch
            {
                "lighter" => DenominatedListingAsset(Venue.Lighter, asset),
                "" => null,
                _ => "lighter:" + id
            };
            bool unitSize = spot || row.Number("multiplier") == 1;
            string? colibri = !duplicateNames.Contains(nativeName) && (!spot ||
                nativeName.Equals($"{asset}/{quote}", StringComparison.OrdinalIgnoreCase)) ? nativeName : null;
            yield return new(Venue.Lighter, id, asset, quote, spot ? null : new("perp", unitSize ? 1 : 0),
                new(ListingAsset: listingAsset, Collateral: spot ? null : tokens.GetValueOrDefault("3"),
                    Display: spot ? $"{asset}/{quote}" : $"{asset}/USD · {id}", ColibriSymbol: colibri));
        }
    }

    internal static IEnumerable<Market> DecodeHyperSpot(JsonElement root)
    {
        var meta = root[0];
        var tokens = meta.GetProperty("tokens").EnumerateArray().ToDictionary(x => x.Str("index"));
        var duplicates = tokens.Values.GroupBy(x => Asset(x.Str("name"))).Where(g => g.Count() > 1)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        var contexts = root[1].EnumerateArray().Where(x => x.Number("dayNtlVlm") > 0 || x.Number("dayBaseVlm") > 0)
            .Select(x => x.Str("coin")).ToHashSet(StringComparer.Ordinal);
        foreach (var row in meta.GetProperty("universe").EnumerateArray())
        {
            string name = row.Str("name");
            if (name != "PURR/USDC" && name != "@" + row.Str("index")) continue;
            // Spot metadata also exposes pairs with no observed market activity. Keep an unknown listing out of matching.
            if (!contexts.Contains(name) || !tokens.TryGetValue(row.GetProperty("tokens")[0].Str(), out var baseToken) ||
                !tokens.TryGetValue(row.GetProperty("tokens")[1].Str(), out var quoteToken)) continue;
            string asset = Asset(baseToken.Str("name")), quote = Asset(quoteToken.Str("name"));
            string? identity = duplicates.Contains(asset) ? "hyperliquid:" + baseToken.Str("tokenId") : null;
            // USDC is the protocol's canonical spot asset 0. A deployed token with that name alone is insufficient.
            bool trustedQuote = quote == "USDC" ? quoteToken.Optional("index") == "0" :
                !UsdQuotes.Supported(quote) || IsTrue(quoteToken, "isCanonical");
            yield return new(Venue.Hyperliquid, name, asset, quote, Identifiers: new(ListingAsset: identity,
                Display: duplicates.Contains(asset) ? $"{asset}/{quote} · {name}" : null, TrustedUsdQuote: trustedQuote));
        }
    }

    internal static IEnumerable<Market> DecodeHyperPerps(JsonElement metas, JsonElement spotMeta)
    {
        var tokens = spotMeta.GetProperty("tokens").EnumerateArray().ToDictionary(x => x.Str("index"), x => Asset(x.Str("name")));
        foreach (var meta in metas.EnumerateArray())
        {
            string? collateral = tokens.GetValueOrDefault(meta.Optional("collateralToken"));
            foreach (var row in meta.GetProperty("universe").EnumerateArray())
            {
                if (IsTrue(row, "isDelisted")) continue;
                string symbol = row.Str("name");
                if (symbol.Length == 0 || symbol[0] is '@' or '#') continue;
                int colon = symbol.IndexOf(':');
                string dex = colon < 0 ? "" : symbol[..colon];
                string asset = Asset(colon < 0 ? symbol : symbol[(colon + 1)..]);
                if (asset.Length == 0) continue;
                // Hyperliquid specifies the oracle denomination for its core perps separately from margin.
                string quote = dex.Length != 0 ? "USD" : asset is "PURR" or "HYPE" ? "USDC" : "USDT";
                yield return new(Venue.Hyperliquid, symbol, asset, quote, new("perp"),
                    new(ListingAsset: dex.Length == 0 ? DenominatedListingAsset(Venue.Hyperliquid, symbol) : "hyperliquid:" + symbol,
                        Collateral: collateral, Display: $"{symbol}/{quote}"));
            }
        }
    }

    private static string Asset(string name) => name.Trim().ToUpperInvariant();
    internal static string KrakenAsset(string name) => Asset(name) switch { "XBT" => "BTC", "XDG" => "DOGE", var asset => asset };
    internal static bool IsTrue(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;
    private static bool IsFalse(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.False;
    internal static long IsoTime(string value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var date) ? date.ToUnixTimeMilliseconds() : 0;
    private static JsonElement HtxField(JsonElement root, string field) => root.Optional("status") == "ok"
        ? root.GetProperty(field) : throw new JsonException($"HTX · {root.Optional("err-msg")}");
    private static JsonElement KrakenResult(JsonElement root) => root.GetProperty("error").GetArrayLength() == 0
        ? root.GetProperty("result") : throw new JsonException($"Kraken · {root.GetProperty("error")}");
    private static JsonElement KrakenFuturesField(JsonElement root, string field) => root.Optional("result") == "success"
        ? root.GetProperty(field) : throw new JsonException($"Kraken Futures · {root.Optional("error")}");
    private static JsonElement LighterField(JsonElement root, string field) => root.Optional("code") == "200"
        ? root.GetProperty(field) : throw new JsonException($"Lighter · {root.Optional("message")}");

    private async Task<JsonElement> HyperInfo(string type, CancellationToken ct, string? coin = null, string? dex = null, object? req = null)
    {
        var body = new Dictionary<string, object> { ["type"] = type };
        if (coin != null) body["coin"] = coin;
        if (!string.IsNullOrEmpty(dex)) body["dex"] = dex;
        if (req != null) body["req"] = req;
        await PaceHistory(Venue.Hyperliquid, ct);
        using var response = await http.PostAsJsonAsync(Hyperliquid, body, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.Clone();
    }
}

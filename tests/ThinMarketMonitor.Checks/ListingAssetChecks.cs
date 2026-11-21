using System.Text.Json;
using ThinMarketMonitor;

internal static class ListingAssetChecks
{
    private static readonly MarketSource WeexFutures = new(Venue.Weex, Segment.Futures);

    public static void Run()
    {
        using var weex = JsonDocument.Parse("""
            {"symbols":[
              {"symbol":"1000PEPEUSDT","baseAsset":"1000PEPE","quoteAsset":"USDT","marginAsset":"USDT","forwardContractFlag":true,"contractType":"PERPETUAL"},
              {"symbol":"1000FLOKIUSDT","baseAsset":"1000FLOKI","quoteAsset":"USDT","marginAsset":"USDT","forwardContractFlag":true,"contractType":"PERPETUAL"},
              {"symbol":"1000BONKUSDT","baseAsset":"1000BONK","quoteAsset":"USDT","marginAsset":"USDT","forwardContractFlag":true,"contractType":"PERPETUAL"},
              {"symbol":"1000SHIBUSDT","baseAsset":"1000SHIB","quoteAsset":"USDT","marginAsset":"USDT","forwardContractFlag":true,"contractType":"PERPETUAL"},
              {"symbol":"AAPLUSDT","baseAsset":"AAPL","quoteAsset":"USDT","marginAsset":"USDT","forwardContractFlag":true,"contractType":"TRADIFI_PERPETUAL","underlyingType":"Stocks"},
              {"symbol":"AMDUSDT","baseAsset":"AMD","quoteAsset":"USDT","marginAsset":"USDT","forwardContractFlag":true,"contractType":"TRADIFI_PERPETUAL","underlyingType":"Stocks"},
              {"symbol":"XAUUSDT","baseAsset":"XAU","quoteAsset":"USDT","marginAsset":"USDT","forwardContractFlag":true,"contractType":"TRADIFI_PERPETUAL","underlyingType":"Metals"},
              {"symbol":"1234PEPEUSDT","baseAsset":"1234PEPE","quoteAsset":"USDT","marginAsset":"USDT","forwardContractFlag":true,"contractType":"PERPETUAL"}
            ]}
            """);
        var contracts = ExchangeApi.DecodeExtendedCatalog(WeexFutures, weex.RootElement, 0).ToArray();
        var pepe = contracts[0];
        Check(pepe.ListingAsset == "PEPE" && pepe.Base == "1000PEPE" && pepe.Key == "Weex:Futures:linear:1000PEPEUSDT" &&
            pepe.RestSymbol == pepe.Symbol && pepe.StreamSymbol == pepe.Symbol && pepe.Ticker == pepe.Symbol &&
            Math.Abs(pepe.Turnover(2, 0.0034) - 0.0068) < 1e-12 &&
            Settings.Deserialize("""{"SignalBlacklist":["1000PEPE"]}""").SignalBlocked(pepe),
            "WEEX retains its ticker, saved instrument and blacklist keys, and quote turnover");
        Check(contracts[6].ListingAsset == "weex:XAU" && contracts[7].ListingAsset == "1234PEPE",
            "other WEEX TradFi and unverified numeric prefixes retain their identities");

        var catalogs = new Dictionary<MarketSource, Catalog> { [WeexFutures] = new(contracts) };
        foreach (var venue in new[] { Venue.Binance, Venue.Bybit, Venue.Okx })
            catalogs[new(venue)] = new(new[] { "PEPE", "FLOKI", "AAPLX", "AMDB", "AMD" }
                .Select(asset => new Market(venue, asset + "USDT", asset, "USDT")).ToArray());
        catalogs[new(Venue.Binance, Segment.Futures)] = new([
            Binance("1000PEPE"), Binance("1000FLOKI"), Binance("AAPL", stock: true), Binance("AMD", stock: true)]);
        catalogs[new(Venue.Bybit, Segment.Futures)] = new([
            Bybit("1000PEPE"), Bybit("1000FLOKI"), Bybit("AAPL", underlying: "AAPL"), Bybit("AMDSTOCK", underlying: "AMD")]);
        var okxFutures = new MarketSource(Venue.Okx, Segment.Futures);
        catalogs[okxFutures] = new([
            Okx("PEPE"), Okx("FLOKI"), Okx("AAPL", stock: true), Okx("AMD", stock: true)]);
        var hiddenByAny = catalogs.Keys.Where(source => source != WeexFutures).ToArray();

        IReadOnlyList<Market> Overview(params MarketSource[] excluded) =>
            ListingFilter.Apply(catalogs, source => excluded.Contains(source) ? ListingRule.Exclude : ListingRule.Ignore, WeexFutures).Markets;
        Check(Overview().SequenceEqual(contracts), "without exclusions all original contracts are visible");
        foreach (var source in hiddenByAny)
        {
            var remaining = Overview(source);
            Check(!remaining.Contains(contracts[0]) && !remaining.Contains(contracts[1]), $"{source} alone excludes both 1000x contracts");
            Check(remaining.Contains(contracts[4]) == (source.Segment == Segment.Spot) &&
                remaining.Contains(contracts[5]) == (source.Segment == Segment.Spot),
                $"{source} excludes the shares only via confirmed Futures contracts");
        }
        Check(Overview(hiddenByAny).SequenceEqual(new[] { contracts[2], contracts[3], contracts[6], contracts[7] }),
            "the six exclusions together retain the unrelated markets");

        var settings = new Settings { Rules = [], FuturesRules = new() { [Venue.Weex] = ListingRule.Require },
            OverviewFuturesRules = new() { [Venue.Bybit] = ListingRule.Exclude } };
        Check(ListingFilter.Apply(catalogs, settings.Rule).Markets.Count == contracts.Length &&
            ListingFilter.Apply(catalogs, settings.OverviewRule, WeexFutures).Markets.Count == contracts.Length - 4,
            "signal and overview rules are independent");
        catalogs[okxFutures] = new([Okx("AAPL", stock: true, dated: true), Okx("AMD", stock: true, dated: true)]);
        Check(!Overview(okxFutures).Contains(contracts[4]) && !Overview(okxFutures).Contains(contracts[5]),
            "listed OKX stock futures with a USD quote also match the share");
        catalogs[okxFutures] = new([Binance("AMD"), Bybit("AMDSTOCK"), Okx("AAPL")]);
        Check(Overview(okxFutures).SequenceEqual(contracts), "non-equity classification cannot exclude the stocks");
        catalogs[okxFutures] = new([], "Unavailable");
        var pending = ListingFilter.Apply(catalogs, source => source == okxFutures ? ListingRule.Exclude : ListingRule.Ignore, WeexFutures);
        Check(pending.Markets.Count == 0 && pending.Missing.SequenceEqual(new[] { okxFutures }),
            "an unavailable exclusion catalog is never treated as empty");

        CheckExtendedAliases();
        Console.WriteLine("WEEX listing exclusions, stock identities, exact instrument keys and scoped denomination aliases passed.");
    }

    private static Market Binance(string asset, bool stock = false) => Decode(Venue.Binance, "usdm", new
    {
        symbol = asset + "USDT", baseAsset = asset, quoteAsset = "USDT", status = "TRADING",
        contractType = stock ? "TRADIFI_PERPETUAL" : "PERPETUAL", underlyingType = stock ? "EQUITY" : "COIN"
    });

    private static Market Bybit(string asset, string? underlying = null) => Decode(Venue.Bybit, "linear", new
    {
        symbol = asset + "USDT", baseCoin = asset, quoteCoin = "USDT", status = "Trading", contractType = "LinearPerpetual",
        symbolType = underlying == null ? "" : "stock", marketRegion = underlying == null ? "" : "US", underlyingTicker = underlying ?? ""
    });

    private static Market Okx(string asset, bool stock = false, bool dated = false) => Decode(Venue.Okx, dated ? "FUTURES" : "SWAP", new
    {
        instId = dated ? asset + "-USD_UM_XPERP-310613" : asset + "-USDT-SWAP", uly = asset + (dated ? "-USD" : "-USDT"),
        state = "live", ctType = "linear", instCategory = stock ? "3" : "1", ctVal = "0.1", ctMult = "1", ctValCcy = asset
    });

    private static Market Decode(Venue venue, string product, object row) =>
        ExchangeApi.DecodeContract(venue, product, JsonSerializer.SerializeToElement(row)) ??
        throw new InvalidOperationException($"Catalog fixture rejected for {venue} {product}");

    private static void CheckExtendedAliases()
    {
        using var lighter = JsonDocument.Parse("""
            {"code":200,"order_book_details":[
              {"market_id":4,"symbol":"1000PEPE","market_type":"perp","status":"active","market_deployer":"lighter","multiplier":1},
              {"market_id":384,"symbol":"1000PEPE","market_type":"perp","status":"active","market_deployer":"external","multiplier":1}
            ]}
            """);
        using var tokens = JsonDocument.Parse("""{"code":200,"asset_details":[{"asset_id":3,"symbol":"USDC"}]}""");
        var lighterContracts = ExchangeApi.DecodeLighter(Segment.Futures, lighter.RootElement, tokens.RootElement, 0).ToArray();
        Check(lighterContracts.Select(m => m.ListingAsset).SequenceEqual(new[] { "PEPE", "lighter:384" }) &&
            lighterContracts.All(m => m.Base == "1000PEPE"), "Lighter resolves its native perp only");
        using var hyper = JsonDocument.Parse("""
            [{"collateralToken":0,"universe":[{"name":"kFLOKI"},{"name":"dex:kFLOKI"},{"name":"KFLOKI"}]}]
            """);
        using var hyperTokens = JsonDocument.Parse("""{"tokens":[{"index":0,"name":"USDC"}]}""");
        Check(ExchangeApi.DecodeHyperPerps(hyper.RootElement, hyperTokens.RootElement).Select(m => m.ListingAsset)
            .SequenceEqual(new[] { "FLOKI", "hyperliquid:dex:kFLOKI", "KFLOKI" }), "Hyperliquid keeps deployed and different symbols distinct");

        var mexc = Decode(Venue.Mexc, "contract", new { symbol = "1000BONK_USDT", baseCoin = "1000BONK",
            quoteCoin = "USDT", settleCoin = "USDT", state = 0, contractSize = 1 });
        var kucoin = Decode(Venue.KuCoin, "contract", new { symbol = "1000BONKUSDTM", baseCurrency = "1000BONK",
            quoteCurrency = "USDT", status = "Open", multiplier = 1 });
        Check(mexc.ListingAsset == "BONK" && kucoin.ListingAsset == "BONK" &&
            mexc.Base == "1000BONK" && kucoin.Base == "1000BONK", "MEXC and KuCoin keep their confirmed 1000BONK contracts");
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Listing assets: " + message);
    }
}

using System.Net.Http;
using System.Text.Json;
using ThinMarketMonitor;

internal static class ExtendedVenueChecks
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    public static void Run()
    {
        const long now = 1795265850000;
        var kraken = ExchangeApi.DecodeExtendedCatalog(new(Venue.Kraken),
            Json("""{"error":[],"result":{"XXBTZUSD":{"altname":"XBTUSD","wsname":"XBT/USD","status":"online"},"XBTUSDC":{"altname":"XBTUSDC","wsname":"XBT/USDC","status":"online"},"XBTUSDT":{"altname":"XBTUSDT","wsname":"XBT/USDT","status":"online"},"XETHZUSD":{"altname":"ETHUSD","wsname":"ETH/USD","status":"cancel_only"}}}"""), now).ToArray();
        Check(kraken.Length == 3 && kraken[0] is { Symbol: "XXBTZUSD", Base: "BTC", Quote: "USD", RestSymbol: "XBTUSD", StreamSymbol: "BTC/USD" },
            "Kraken aliases preserve the REST key, WS pair and actual USD quote");
        var htx = ExchangeApi.DecodeHtxContracts(Json("""{"status":"ok","data":[{"symbol":"BTC","contract_code":"BTC-USD","contract_status":1,"contract_size":100},{"symbol":"ETH","contract_code":"ETH-USD","contract_status":0,"contract_size":10},{"symbol":"SOL","contract_code":"SOL-USD","contract_status":1,"contract_size":10,"delivery_time":"1795260000000"}]}"""),
            "inverse", now).Single();
        Check(htx.Turnover(2, 50000) == 200 && htx.Quote == "USD", "HTX inverse quote-face size and expiration");
        var htxLive = ExtendedStreams.DecodeTrades(htx, Json("""{"ch":"market.BTC-USD.trade.detail","tick":{"data":[{"id":1000049973727650000,"ts":1795265850000,"price":50000,"amount":2},{"id":1000049973727650001,"ts":1795265850000,"price":50001,"amount":3}]}}"""));
        Check(htxLive.Length == 2 && htxLive[0].Size == 2 && htxLive[0].Id != htxLive[1].Id, "HTX gzip payload maps exact derivative trade identifiers");
        var weex = ExchangeApi.DecodeExtendedCatalog(new(Venue.Weex, Segment.Futures),
            Json("""{"symbols":[{"symbol":"BTCUSDT","baseAsset":"BTC","quoteAsset":"USDT","marginAsset":"USDT","contractType":"PERPETUAL","forwardContractFlag":true,"contractVal":0.0001},{"symbol":"BTCUSD","baseAsset":"BTC","quoteAsset":"USD","contractType":"PERPETUAL","forwardContractFlag":false}]}"""), now).Single();
        var weexTrades = ExtendedStreams.DecodeTrades(weex, Json("""{"e":"trade","s":"BTCUSDT","d":[{"T":1795265850000,"t":"uuid","p":"50000","q":"0.0021","v":"105"}]}"""));
        Check(weexTrades.Length == 1 && Math.Abs(weex.Turnover(weexTrades[0].Size, weexTrades[0].Price) - 105) < 1e-10 &&
            ExtendedStreams.DecodeTrades(weex, Json("""{"e":"tradeSnapshot","s":"BTCUSDT","d":[]}""")).Length == 0,
            "WEEX reports base quantity and historical snapshots cannot trigger price/volume");
        const string metadata = """
            [{"tokens":[{"index":0,"name":"USDC"},{"index":1,"name":"PURR","tokenId":"purr"},{"index":2,"name":"HYPE","tokenId":"hype"},
            {"index":3,"name":"USDT0"},{"index":4,"name":"USDC"},{"index":5,"name":"HYPE","tokenId":"copy"}],
            "universe":[{"name":"PURR/USDC","index":0,"tokens":[1,0]},{"name":"@107","index":107,"tokens":[2,0]},
            {"name":"@108","index":108,"tokens":[2,3]},{"name":"@109","index":109,"tokens":[2,4]},{"name":"@110","index":110,"tokens":[5,0]},
            {"name":"@111","index":111,"tokens":[2,0]}]},
            [{"coin":"PURR/USDC","dayNtlVlm":"10"},{"coin":"@107","dayNtlVlm":"20"},{"coin":"@108","dayNtlVlm":"30"},
            {"coin":"@109","dayNtlVlm":"40"},{"coin":"@110","dayNtlVlm":"50"},{"coin":"@111","dayNtlVlm":"0"}]]
            """;
        var meta = Json(metadata);
        var spot = ExchangeApi.DecodeHyperSpot(meta).ToArray();
        Check(spot.Length == 5 && spot[0].Symbol == "PURR/USDC" && spot[1] is { Symbol: "@107", Quote: "USDC" } &&
            spot[2].Quote == "USDT0" && spot[1].ListingAsset != spot[4].ListingAsset && !UsdQuotes.Supported(spot[3]),
            "Hyperliquid spot IDs, actual quote assets, inactive and duplicate token identities");
        var perps = ExchangeApi.DecodeHyperPerps(Json("""[{"collateralToken":0,"universe":[{"name":"BTC"},{"name":"HYPE"},{"name":"OLD","isDelisted":true},{"name":"#1"}]},{"collateralToken":3,"universe":[{"name":"xyz:BTC"}]}]"""), meta[0]).ToArray();
        Check(perps.Length == 3 && perps[0].Quote == "USDT" && perps[1].Quote == "USDC" &&
            perps[2].Symbol == "xyz:BTC" && perps[2].ListingAsset != perps[0].ListingAsset &&
            perps[2].Quote == "USD" && perps[2].Identifiers?.Collateral == "USDT0", "Hyperliquid oracle denomination, HIP-3 namespace, collateral and delisted exclusion");
        var lighter = ExchangeApi.DecodeLighter(Segment.Spot,
            Json("""{"code":200,"spot_order_book_details":[{"symbol":"ETH/USDC","market_id":2048,"market_type":"spot","status":"active","base_asset_id":1,"quote_asset_id":3},{"symbol":"OLD/USDC","market_id":2050,"market_type":"spot","status":"inactive","base_asset_id":1,"quote_asset_id":3}]}"""),
            Json("""{"code":200,"asset_details":[{"asset_id":1,"symbol":"ETH"},{"asset_id":3,"symbol":"USDC"}]}"""), now).Single();
        Check(lighter is { Symbol: "2048", Quote: "USDC", Contract: null } &&
            ExtendedStreams.DecodeBook(Venue.Lighter, Json("""{"type":"update/ticker","channel":"ticker:2048","last_updated_at":1795265850000000,"ticker":{"b":{"price":"99","size":"2"},"a":{"price":"101","size":"1"}}}"""))
                is { Symbol: "2048", Quote.Bid: 99, Quote.Ask: 101, Quote.Time: now },
            "Lighter numeric market ID, explicit spot assets and microsecond ticker timestamp");
        var hlBook = ExtendedStreams.DecodeBook(Venue.Hyperliquid,
            Json("""{"channel":"bbo","data":{"coin":"@107","time":1795265850000,"bbo":[null,{"px":"101","sz":"1"}]}}"""));
        Check(hlBook?.Symbol == "@107" && hlBook?.Quote.Percent(now) == null, "one-sided books remain unknown");
        var ksBars = ExchangeApi.DecodeExtendedCandles(kraken[1],
            Json("""{"error":[],"result":{"XBTUSDC":[[1795265820,"100","102","99","101","100.5","3",12]],"last":1795265820}}""")).Single();
        Check(ksBars.Time == 1795265820000 && ksBars.QuoteVolume == 301.5, "Kraken candle turnover uses same-window VWAP");
        Check(ExchangeApi.DecodeExtendedCandles(spot[1],
            Json("""[{"t":1795265820000,"o":"100","h":"102","l":"99","c":"101","v":"9"}]""")).Single().QuoteVolume == null,
            "Hyperliquid base size is not converted to fictitious exact quote turnover");
        var sources = new Dictionary<MarketSource, Catalog> { [kraken[0].Source] = new(kraken), [perps[0].Source] = new(perps) };
        var results = ListingFilter.Apply(sources, source => source.Venue is Venue.Kraken or Venue.Hyperliquid &&
            sources.ContainsKey(source) ? ListingRule.Require : ListingRule.Ignore);
        Check(results.Markets.Count == 4 && results.Markets.All(m => m.ListingAsset == "BTC") &&
            results.Markets.Select(m => m.Key).Distinct().Count() == 4 && results.Markets.Any(m => m.Quote == "USDC"),
            "listing intersection uses the asset and leaves USDT, USDC, USD and futures distinct");
        Check(ExtendedStreams.ChunkSize(Venue.Weex) == 100 && HyperliquidStream.SubscriptionLimit < 1000 &&
            MarketSource.All.Count(s => s.Venue.Extended()) == 10 && Settings.Deserialize("{}").Rule(new(Venue.Weex)) == ListingRule.Ignore,
            "new sources are neutral and configured per-venue channel bounds apply");
        Console.WriteLine("HTX, Kraken, WEEX, Lighter and Hyperliquid adapters, USDC identities and missing-data fixtures passed.");
    }

    public static async Task<int> Live()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(16) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(110));
        var ct = timeout.Token;
        var api = new ExchangeApi(http);
        var catalogs = await Task.WhenAll(MarketSource.All.Where(s => s.Venue.Extended())
            .Select(async source => (Source: source, Catalog: await api.LoadCatalog(source, ct))));
        var probes = new List<Market>();
        foreach (var entry in catalogs)
        {
            Console.WriteLine($"Catalog {entry.Source.Label}: {entry.Catalog.Markets.Count} ({entry.Catalog.Markets.Count(m => m.Quote == "USDC")} USDC), {entry.Catalog.Error ?? "OK"}");
            foreach (var product in entry.Catalog.Markets.GroupBy(m => m.Contract?.Product ??
                (m.Quote is "USDT" or "USDC" ? m.Quote : "other")))
            {
                if (entry.Source.Segment == Segment.Spot && product.Key == "other") continue;
                var selected = product.FirstOrDefault(m => m.Symbol is "@107" or "2048" or "PF_XBTUSD" or "1") ??
                    product.Where(m => (m.Base is "BTC" or "ETH") && m.Quote is "USDT" or "USDC" or "USD")
                        .OrderBy(m => m.Base == "BTC" ? 0 : 1).FirstOrDefault() ?? product.First();
                probes.Add(selected);
            }
            if (entry.Source == new MarketSource(Venue.Hyperliquid)) Add("PURR/USDC");
            if (entry.Source == new MarketSource(Venue.Hyperliquid, Segment.Futures)) Add("xyz:XYZ100");
            if (entry.Source == new MarketSource(Venue.Kraken)) Add("XXBTZUSD");
            void Add(string symbol)
            {
                var market = entry.Catalog.Markets.FirstOrDefault(m => m.Symbol == symbol);
                if (market != null && !probes.Contains(market)) probes.Add(market);
            }
        }
        using var streams = new MarketStreams(api, probes, Settings.Defaults, Settings.DefaultVolumeGrowth, probes, referenceSeconds: 60);
        using var spread = new SpreadFilterMonitor(api, probes);
        var history = probes.Select(async m =>
        {
            static long Boundary(int frame) => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / (frame * 1000L) + 1) * frame * 1000L;
            try
            {
                var minute = await api.CandlePage(m, 60, Boundary(60), 8, ct);
                var day = await api.CandlePage(m, 86400, Boundary(86400), 4, ct);
                var trades = await api.RecentTrades(m, ct);
                var snapshot = (await api.Snapshots([m], ct)).GetValueOrDefault(m.Key);
                Console.WriteLine($"REST {m.Label} {m.Symbol}: candles {minute.Length}m/{day.Length}d; recent {trades.Length}; 24h {snapshot?.Turnover?.ToString("G7") ?? "—"}");
                return minute.Length > 0 && day.Length > 0 && trades.Length > 0;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"REST {m.Label} {m.Symbol}: {ex.Message}");
                return false;
            }
        }).ToArray();
        var seenTrade = new HashSet<string>();
        var seenBook = new HashSet<string>();
        for (int i = 0; i < 32; i++)
        {
            await Task.Delay(750, ct);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var market in probes)
            {
                if (streams.HasSizedTrade(market, now)) seenTrade.Add(market.Key);
                if (spread.Quote(market)?.Percent(now) != null) seenBook.Add(market.Key);
            }
            if (seenTrade.Count == probes.Count && seenBook.Count == probes.Count && history.All(t => t.IsCompleted)) break;
        }
        var coverage = streams.Status();
        foreach (var market in probes)
            Console.WriteLine($"Live {market.Label} {market.Symbol}: trade={seenTrade.Contains(market.Key)}; BBO={seenBook.Contains(market.Key)}");
        foreach (var status in coverage.Where(s => !s.Connected)) Console.WriteLine($"Trade feed {status.Source}: {status.Detail}");
        Console.WriteLine(spread.Coverage(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        Console.WriteLine($"Coverage {seenTrade.Count}/{probes.Count} trades, {seenBook.Count}/{probes.Count} BBO; {coverage.Count(x => x.Connected)}/{coverage.Length} subscriptions connected.");
        var checks = await Task.WhenAll(history);
        return catalogs.All(c => c.Catalog.Error == null) && checks.All(x => x) &&
            coverage.All(c => c.Connected) && seenBook.Count == probes.Count && seenTrade.Count == probes.Count ? 0 : 1;
    }
}

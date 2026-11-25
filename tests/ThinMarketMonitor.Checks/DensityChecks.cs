using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ThinMarketMonitor;

internal static class DensityChecks
{
    private const long Start = 1_800_000_000_000;
    private static readonly Market Market = new(Venue.Bybit, "TESTUSDT", "TEST", "USDT");
    private static readonly DensityVenueRule Rule = new(Spot: true, SmallUsd: 200, LargeUsd: 900, MaxDistance: 5, MinSeconds: 6);
    private static readonly DensityBook Present = new([new(99, 1), new(98, 10), new(80, 1000)], [new(101, 1)]);
    private static readonly DensityBook Absent = new([new(99, 1), new(97, 1)], [new(101, 1)]);

    public static async Task<int> Live()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
        var api = new ExchangeApi(http);
        var result = await Task.WhenAll(MarketSource.All.Select(async source =>
        {
            try
            {
                var catalog = await api.LoadCatalog(source, timeout.Token);
                if (catalog.Error != null) throw new InvalidOperationException(catalog.Error);
                var markets = catalog.Markets.Where(ExchangeApi.SupportsDensity).ToArray();
                var market = markets.FirstOrDefault(m => m.Venue == Venue.Weex &&
                    (m.Base == "WXT" && m.Contract == null || m.Base == "AMD" && m.Contract != null)) ??
                    markets.FirstOrDefault(m => m.Base == "BTC" && m.Quote is "USDT" or "USD" or "USDC") ?? markets.First();
                var depths = ExchangeApi.DensityDepths(market);
                foreach (int depth in new[] { depths[0], depths[^1] }.Distinct())
                {
                    var book = await api.ReadDensityBook(market, depth, 5, timeout.Token);
                    if (book.Bids.Length == 0 || book.Asks.Length == 0) throw new InvalidOperationException("empty book");
                    string limit = depth == int.MaxValue ? "full" : depth.ToString();
                    Console.WriteLine($"{source} {market.Symbol}: {book.Bids.Length}/{book.Asks.Length} at depth {limit}");
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"{source}: {ex.Message}");
                return false;
            }
        }));
        return result.All(ok => ok) ? 0 : 1;
    }

    public static async Task Run()
    {
        var tracker = new DensityTracker(Market, Rule);
        var notices = new List<DensityLevel>();
        DensityLevel[] Apply(DensityBook book, int seconds, bool flicker = false) =>
            tracker.Apply(book, Start + 1000L * seconds, 12_000, flicker, false, notices.Add);
        Require(Apply(Present, 0).Length == 0 && Apply(Present, 3).Length == 0, "minimum observed lifetime");
        var level = Apply(Present, 6).Single();
        Require(level.Key == new DensityKey(BookSide.Bid, 98) && level.FirstSeen == Start &&
            level.Usd == 980 && level.Large && level.Color == Rule.LargeColor && level.Distance == 2,
            "threshold, midpoint distance, color and first-seen time");
        Require(Apply(Present, 9).Length == 1 && notices.Count == 1, "one notification per appearance");
        Require(Apply(Absent, 12).Length == 0, "removed price is no longer visible");
        Require(Apply(Present, 15).Length == 0 && Apply(Present, 21).Single().FirstSeen == Start + 15_000 &&
            notices.Count == 2, "reappearance begins a new age");
        Require(Apply(Present, 45).Length == 0, "a stale gap does not count as presence");

        tracker = new(Market, Rule);
        notices.Clear();
        Apply(Present, 0, true);
        Apply(Absent, 3, true);
        Apply(Present, 6, true);
        Apply(Absent, 9, true);
        Apply(Present, 12, true);
        Require(Apply(Present, 18, true).Length == 0 && notices.Count == 0, "repeated quick disappearance suppressed");
        Require(Apply(Present, 21).Length == 1, "flicker suppression is optional");

        var mirrored = new DensityBook([new(99.8, 5), new(99, 8), new(98, 10), new(97, 60)],
            [new(100.2, 5), new(101, 8), new(102, 10)]);
        tracker = new(Market, Rule);
        tracker.Apply(mirrored, Start, 12_000, false, true, _ => { });
        var asymmetric = tracker.Apply(mirrored, Start + 6000, 12_000, false, true, _ => { });
        Require(asymmetric.Length == 1 && asymmetric[0].Key.Price == 97, "only matched mirrored levels are removed");

        using var doc = JsonDocument.Parse("""{"retCode":0,"result":{"b":[["99",1],[98,10],[80,1],["skipped"]],"a":[[101,1],[102,2],[120,1]]}}""");
        var decoded = ExchangeApi.DecodeDensityBook(Market, doc.RootElement, 20, 5);
        Require(decoded.Bids.Length == 2 && decoded.Asks.Length == 2 && !decoded.Limited && decoded.ReadDepth == 3,
            "stop decoding at the first out-of-range price");
        using var lighter = JsonDocument.Parse("""
            {"code":200,"bids":[{"price":"99","remaining_base_amount":"5"},{"price":"99","remaining_base_amount":"3"}],
                "asks":[{"price":"101","remaining_base_amount":"2"}]}
            """);
        var aggregated = ExchangeApi.DecodeDensityBook(Market with { Venue = Venue.Lighter }, lighter.RootElement, 2, 5);
        Require(aggregated.Bids.Single() == new BookLevel(99, 8) && aggregated.BidLimited, "aggregate individual orders and retain depth coverage");
        using var kraken = JsonDocument.Parse("""
            {"result":"success","orderBook":{"bids":[[1,140],[98,2],[99,1]],"asks":[[101,1],[102,2],[110,8]]}}
            """);
        var derivative = ExchangeApi.DecodeDensityBook(Market with { Venue = Venue.Kraken, Contract = new("derivatives") },
            kraken.RootElement, int.MaxValue, 5);
        Require(derivative.Bids.Select(b => b.Price).SequenceEqual([99d, 98d]) && derivative.Asks.Length == 2,
            "ascending futures bids are read from the best price");
        using var alpha = JsonDocument.Parse("""
            {"code":"000000","data":{"symbol":"ALPHA_1USDT","bids":[["99","2"]],"asks":[["101","3"]]}}
            """);
        Require(ExchangeApi.DecodeDensityBook(Market with { Venue = Venue.Alpha, Symbol = "ALPHA_1USDT" },
            alpha.RootElement, 20, 5).Bids.Single() == new BookLevel(99, 2), "Alpha uses its own success code and symbol");
        var inverse = new DensityTracker(Market with { Venue = Venue.Binance, Contract = new("coinm", 100, QuantityUnit.Quote) }, Rule);
        inverse.Apply(Present, Start, 12_000, false, false, _ => { });
        Require(inverse.Apply(Present, Start + 6000, 12_000, false, false, _ => { }).Single().Usd == 1000,
            "inverse contract quantities convert to quote value");
        Require(Settings.Deserialize("{}").Densities.Venues.Count == 0, "existing installs are opted out until sources are chosen");

        using var handler = new DepthHandler();
        using var http = new HttpClient(handler);
        using var monitor = new DensityMonitor(new(http));
        var options = new DensityOptions
        {
            Popup = true, Venues = { [Venue.Bybit] = Rule with { MinSeconds = 0 } }
        };
        var catalogs = new Dictionary<MarketSource, Catalog>
        {
            [Market.Source] = new([Market]),
            [new(Venue.Bybit, Segment.Futures)] = new([Market with { Contract = new("linear") }])
        };
        monitor.Configure(options, catalogs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!monitor.TryRead(out _)) await Task.Delay(80, timeout.Token);
        Require(monitor.Visible().Length == 1 && monitor.Status().Single().Markets == 1, "configured segment only");
        Require(handler.Queries.Take(2).SequenceEqual(["?category=spot&symbol=TESTUSDT&limit=20",
            "?category=spot&symbol=TESTUSDT&limit=50"]), "start small and request more depth when price range requires it");
        options.Venues.Clear();
        monitor.Configure(options, catalogs);
        Require(monitor.Visible().Length == 0 && monitor.Status().Length == 0, "disabled venue removes levels and scan");
    }

    private sealed class DepthHandler : HttpMessageHandler
    {
        public readonly ConcurrentQueue<string> Queries = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string query = request.RequestUri!.Query;
            Queries.Enqueue(query);
            int count = query.EndsWith("limit=20", StringComparison.Ordinal) ? 20 : 50;
            double[][] Side(double start, double step) => Enumerable.Range(0, count)
                .Select(i => new double[] { start + step * i, i == 1 && step < 0 ? 10 : 1 }).ToArray();
            string body = JsonSerializer.Serialize(new { retCode = 0, result = new { b = Side(99.9, -.15), a = Side(100.1, .15) } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Density: " + message);
    }
}

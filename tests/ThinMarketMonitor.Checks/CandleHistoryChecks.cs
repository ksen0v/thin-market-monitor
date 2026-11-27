using System.Net;
using System.Net.Http;
using System.Text.Json;
using ThinMarketMonitor;

internal static class CandleHistoryChecks
{
    private const long Minute = 60_000;
    private const long Open = 1795291380000; // Observed WXTUSDT 1m candle, 2026-11-21 20:03 UTC.
    private static readonly Market Wxt = new(Venue.Weex, "WXTUSDT", "WXT", "USDT");

    public static async Task Run()
    {
        using var books = new WeexCandles();
        using var http = new HttpClient(books);
        var api = new ExchangeApi(http, new FixedTime(Open + 30_447));
        var ct = CancellationToken.None;
        const long before = Open + Minute;
        var initial = await api.CandlePage(Wxt, 60, before, 220, ct);
        CheckContiguous(initial, before - 220 * Minute, 220, "WXT initial window");
        Check(books.Paths is ["/api/v3/market/klines"] && initial[^1].QuoteVolume == 3745.25382,
            "WXT uses the latest Spot endpoint for the open candle and keeps quote turnover");

        books.Paths.Clear();
        var previous = await api.CandlePage(Wxt, 60, initial[0].Time, 180, ct);
        CheckContiguous(previous, before - 400 * Minute, 180, "WXT older window");
        Check(books.Paths.Count == 2 && books.Paths.All(p => p == "/api/v3/market/historyKlines") &&
            previous[^1].Time + Minute == initial[0].Time, "the two inclusive pages end before the already loaded candle");

        books.Paths.Clear();
        books.LatestCount = 100;
        var capped = await api.CandlePage(Wxt, 60, before, 220, ct);
        CheckContiguous(capped, initial[0].Time, 220, "WXT latest endpoint capped at 100");
        Check(books.Paths.Count == 3, "a capped latest response is supplemented before its oldest candle");

        books.FailHistory = true;
        var partial = new List<Candle>();
        try
        {
            await api.CandlePage(Wxt, 60, before, 220, ct, partial.AddRange);
            throw new InvalidOperationException("WEEX HTTP error returned as a successful history");
        }
        catch (HttpRequestException ex)
        {
            Check(ex.StatusCode == HttpStatusCode.BadRequest && ex.Message.Contains("-1142") && partial.Count == 100,
                "the error is explicit and previously downloaded chart candles are available");
        }
        books.FailHistory = false;
        books.Paths.Clear();
        var futures = new Market(Venue.Weex, "BTCUSDT", "BTC", "USDT", new("linear"));
        Check((await api.CandlePage(futures, 60, Open - Minute, 10, ct)).Length == 10 &&
            books.Paths is ["/capi/v3/market/historyKlines"], "futures use the exchange's LAST contract candles");
        books.Paths.Clear();
        Check((await api.CandlePage(Wxt, 15, before, 60, ct)).Length == 0 && books.Paths.Count == 0,
            "unsupported native seconds do not fabricate minute-derived candles");
        books.NoHistory = true;
        Check((await api.CandlePage(Wxt, 60, Open - Minute, 10, ct)).Length == 0, "a valid empty range is distinct from an error");

        var live = new LiveCandles(60);
        live.Add(new Trade(Wxt.Symbol, Open + 30_800, .01306));
        live.AddHistory(initial, Open + 30_447);
        live.AddHistory(previous, Open + 31_000);
        var joined = live.Snapshot();
        CheckContiguous(joined, previous[0].Time, 400, "WXT REST/live join");
        Check(joined[^1].Close == .01306 && joined[^1].Open == initial[^1].Open, "the fresh trade updates the existing open candle");

        var htx = new Market(Venue.Htx, "btcusdc", "BTC", "USDC");
        long dayStart = DateTimeOffset.Parse("2026-11-21T16:00:00Z").ToUnixTimeMilliseconds();
        long htxOffset = ExchangeApi.CandleOffset(htx, 86400);
        var daily = new LiveCandles(86400, htxOffset);
        daily.Add(new Trade(htx.Symbol, dayStart + 1_800_000, 60_001));
        daily.AddHistory([new(dayStart, 59_900, 60_000, 59_890, 60_000)], dayStart + 1_799_500);
        Check(daily.Snapshot() is [{ Time: var time, Close: 60001 }] && time == dayStart &&
            ExchangeApi.CandleOffset(htx, 14400) == 0, "the HTX daily stream joins the actual UTC+8 candle");
        await KuCoinTruncation();
        Console.WriteLine("WEEX WXT Spot history, ranges, partial errors, contract routing and HTX daily/live join passed.");
    }

    private static async Task KuCoinTruncation()
    {
        var market = new Market(Venue.KuCoin, "BTWUSDTM", "BTW", "USDT", new("contract"));
        long before = DateTimeOffset.Parse("2026-11-25T19:56:00+03:00").ToUnixTimeMilliseconds();
        using var handler = new KuCoinCandles();
        using var http = new HttpClient(handler);
        var api = new ExchangeApi(http);
        var ct = CancellationToken.None;
        var bars = await api.CandlePage(market, 60, before, 480, ct);
        CheckContiguous(bars, before - 480 * Minute, 480, "the 40-minute BTW gaps are recovered");
        Check(handler.Windows.SequenceEqual([200, 200, 80]), "KuCoin Futures never requests more than 200 buckets");

        handler.Windows.Clear();
        long fiveEnd = ExchangeApi.CandleStart(before, 5 * Minute);
        handler.Missing = fiveEnd - 5 * Minute;
        var five = await api.CandlePage(market, 300, fiveEnd, 240, ct);
        Check(five.Length == 239 && five.All(bar => bar.Time != handler.Missing) && handler.Windows.SequenceEqual([200, 40]),
            "5m ranges use the caps as well; a real missing interval does not become a synthetic candle");

        handler.Windows.Clear();
        handler.FailSecond = true;
        var partial = new List<Candle>();
        try
        {
            await api.CandlePage(market, 60, before, 480, ct, partial.AddRange);
            throw new InvalidOperationException("failed KuCoin page was reported as success");
        }
        catch (HttpRequestException)
        {
            Check(partial.Count == 199, "keep valid pages when a subsequent history request fails");
        }
        handler.FailSecond = false;
        handler.Missing = null;
        Check(ExchangeApi.TailCandleCount(60, before, before - 40 * Minute) == 44 &&
            ExchangeApi.TailCandleCount(300, before, before - 20 * Minute) == 8 &&
            ExchangeApi.TailCandleCount(60, before, before) == 4, "right-edge refresh catches up after an interruption");
        var remainder = await api.CandlePage(market, 60, before, ExchangeApi.TailCandleCount(60, before, before - 40 * Minute), ct);
        var openChart = new LiveCandles(60);
        openChart.AddHistory(bars[..^40], before - 40 * Minute);
        openChart.Add(new(market.Symbol, before - 5000, .92, Id: "live"));
        openChart.AddHistory(remainder, before - 10_000);
        var merged = openChart.Snapshot();
        CheckContiguous(merged, bars[0].Time, 480, "an open chart catches up without duplicate candles");
        Check(merged[^1].Close == .92 && merged[^1].QuoteVolume == 1234,
            "page recovery preserves a later live close and a real exchange turnover");
        Console.WriteLine("KuCoin BTW Futures caps, partial failures, real gaps and live reconnect recovery passed.");
    }

    private sealed class KuCoinCandles : HttpMessageHandler
    {
        public readonly List<int> Windows = [];
        public long? Missing { get; set; }
        public bool FailSecond { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            var query = uri.Query.TrimStart('?').Split('&').Select(kv => kv.Split('=', 2)).ToDictionary(kv => kv[0], kv => kv[1]);
            Check(uri.Host == "api-futures.kucoin.com" && query["symbol"] == "BTWUSDTM", "BTW exact futures history");
            long from = long.Parse(query["from"]), to = long.Parse(query["to"]), period = int.Parse(query["granularity"]) * Minute;
            int count = (int)((to + 1 - from) / period);
            Windows.Add(count);
            if (FailSecond && Windows.Count == 2) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            // The live product returned the earliest 200 entries even when 240 were requested.
            var bars = Enumerable.Range(0, Math.Min(count, 200)).Select(i => from + i * period).Where(time => time != Missing)
                .Select(time => new object[] { time, .9, .906, .897, .901, 200, 1234 }).ToArray();
            string json = JsonSerializer.Serialize(new { code = "200000", data = bars });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private static void CheckContiguous(Candle[] bars, long first, int count, string detail) =>
        Check(bars.Length == count && bars.Select((bar, i) => bar.Time == first + i * Minute).All(x => x), detail);

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private sealed class FixedTime(long now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(now);
    }

    private sealed class WeexCandles : HttpMessageHandler
    {
        public readonly List<string> Paths = [];
        public int LatestCount { get; set; } = 301;
        public bool FailHistory { get; set; }
        public bool NoHistory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Paths.Add(uri.AbsolutePath);
            var query = uri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1]);
            bool spot = uri.Host == "api-spot.weex.com";
            Check(query["symbol"] == (spot ? Wxt.Symbol : "BTCUSDT") && query["interval"] == "1m", "exact symbol and frame");
            if (uri.AbsolutePath.EndsWith("/klines", StringComparison.Ordinal))
                return Reply(Enumerable.Range(0, LatestCount).Select(i => Bar(Open - i * Minute)).ToArray());
            if (FailHistory)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"code":-1142,"msg":"Parameter 'endTime' is invalid."}""")
                });
            long from = long.Parse(query["startTime"]), end = long.Parse(query["endTime"]);
            int limit = int.Parse(query["limit"]);
            Check(limit <= 100 && end < Open && end == from + limit * Minute - 1 &&
                (spot ? !query.ContainsKey("priceType") : query["priceType"] == "LAST"), "closed millisecond ranges respect WEEX limits");
            return Reply(NoHistory ? [] : Enumerable.Range(0, limit).Reverse().Select(i => Bar(from + i * Minute)).ToArray());
        }

        // The public WXT Spot array shape and prices match the observed open minute.
        private static object[] Bar(long time) => [time, "0.013045", "0.013046", "0.013043", "0.013043",
            "287100", time + Minute, "3745.253820", 11, "8200", "106.953"];

        private static Task<HttpResponseMessage> Reply(object[][] bars) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(bars))
        });
    }
}

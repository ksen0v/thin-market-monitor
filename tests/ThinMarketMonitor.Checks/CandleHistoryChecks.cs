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
        Console.WriteLine("WEEX WXT Spot history, ranges, partial errors, contract routing and HTX daily/live join passed.");
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

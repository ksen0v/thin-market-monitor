using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ThinMarketMonitor;

internal static class HistoricalVolumeFilterChecks
{
    public static async Task Run()
    {
        const long minute = 60_000;
        long open = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / minute * minute;
        var clock = new Clock(open + 20_000);
        using var candles = new Fixture();
        using var http = new HttpClient(candles);
        var api = new ExchangeApi(http, clock);
        var market = new Market(Venue.Binance, "BTCUSDT", "BTC", "USDT");
        var ct = CancellationToken.None;

        var first = await api.HistoricalVolumes(market, [300], ct);
        Require(first is { } cold && cold.Time == open && cold.Amounts[300] == 50 && candles.Ranges.Count == 1,
            "initial result sums five closed historical turnover candles");
        var repeated = await api.HistoricalVolumes(market, [60, 300], ct);
        Require(repeated is { } warm && warm.Amounts[60] == 10 && warm.Amounts[300] == 50 && candles.Ranges.Count == 1,
            "the same history immediately serves additional periods and threshold changes without another request");
        using (var monitor = new VolumeFilterMonitor(api, [market], [new(300, true, 49)]))
            Require(monitor.Evaluate(clock.Now).Markets.SequenceEqual([market]), "a newly applied filter immediately uses the saved history");

        clock.Now += minute;
        var next = await api.HistoricalVolumes(market, [300], ct);
        Require(next is { } delta && delta.Time == open + minute && delta.Amounts[300] == 50 &&
            candles.Ranges.Last() == (open, open + minute), "one added minute refreshes the complete window without refetching its overlap");

        clock.Now += minute;
        candles.SkipFrom = open + minute;
        var late = await api.HistoricalVolumes(market, [300], ct);
        Require(late is { } retained && retained.Time == open + minute && retained.Amounts[300] == 50,
            "a late candle retains the most recent complete period");
        var other = market with { Symbol = "ETHUSDT", Base = "ETH" };
        candles.SkipFrom = long.MinValue;
        Require(await api.HistoricalVolumes(other, [300], ct) == null &&
            api.CachedHistoricalVolumes(other, [300]) == null, "missing candle history never stands in for zero or another instrument");
        Console.WriteLine("Volume filters use confirmed historical windows, reuse cached turnover and fetch only the new tail.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Clock(long now) : TimeProvider
    {
        public long Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public ConcurrentQueue<(long Start, long End)> Ranges { get; } = new();
        public long SkipFrom { get; set; } = long.MaxValue;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = request.RequestUri!.Query.TrimStart('?').Split('&')
                .Select(parameter => parameter.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1]);
            long start = long.Parse(query["startTime"]);
            long end = long.Parse(query["endTime"]) + 1;
            Ranges.Enqueue((start, end));
            var bars = new List<object[]>();
            for (long time = start; time < end && time < SkipFrom; time += 60_000)
                bars.Add([time, "1", "1", "1", "1", "10", time + 59_999, "10"]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(bars)) });
        }
    }
}

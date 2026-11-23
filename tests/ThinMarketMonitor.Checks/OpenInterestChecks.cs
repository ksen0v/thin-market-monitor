using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ThinMarketMonitor;

internal static class OpenInterestChecks
{
    private const long Now = 1_795_387_800_000;
    private const long FiveMinutes = 300_000;
    private const long Day = 86_400_000;
    private static readonly Market Binance = new(Venue.Binance, "BTCUSDT", "BTC", "USDT", new("usdm", Perpetual: true));
    private static readonly Market Bybit = new(Venue.Bybit, "ETHUSDT", "ETH", "USDT", new("linear", Perpetual: true));

    public static async Task Run()
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        long oldDay = ExchangeApi.CandleStart(Now, Day) - 38 * Day;
        long oldSample = oldDay + FiveMinutes;
        long restSample = Now - 28 * FiveMinutes;
        int archives = 0, earlier = 0, markCalls = 0;
        using var http = new HttpClient(new Responses(uri =>
        {
            string query = uri.Query;
            if (uri.Host == "data.binance.vision")
            {
                archives++;
                var stamp = DateTimeOffset.FromUnixTimeMilliseconds(oldSample).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                return new(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Archive($"""
                        create_time,symbol,sum_open_interest,sum_open_interest_value
                        {stamp},ETHUSDT,69,12000000
                        {stamp},BTCUSDT,15,1632040
                        {stamp},BTCUSDT,15,1632040
                        """))
                };
            }
            if (uri.Host == "fapi.binance.com")
            {
                if (query.Contains("startTime=", StringComparison.Ordinal))
                {
                    earlier++;
                    return Reply(new[] { BinanceItem(restSample, 45, 2_500_000), BinanceItem(Now, 0, 0) });
                }
                return Reply(new[] { BinanceItem(Now - FiveMinutes, 51, 3_100_000), BinanceItem(Now, 50, 3_000_000) });
            }
            if (uri.AbsolutePath.EndsWith("/mark-price-kline", StringComparison.Ordinal))
            {
                markCalls++;
                return Reply(new
                {
                    retCode = 0, result = new { symbol = "ETHUSDT", category = "linear", list = new[]
                    {
                        new[] { (Now - FiveMinutes).ToString(CultureInfo.InvariantCulture), "3000", "3050", "2900", "3040" }
                    } }
                });
            }
            return Reply(new
            {
                retCode = 0, result = new { symbol = "ETHUSDT", category = "linear", list = new[]
                {
                    new { timestamp = Now, openInterest = "40" },
                    new { timestamp = Now - FiveMinutes, openInterest = "20" }
                } }
            });
        }));
        var api = new ExchangeApi(http, new FrozenTime());
        var latest = await api.OpenInterest(Binance, 60, ct.Token);
        Check(latest.Points.Last() is { Time: Now, Value: 50, Usd: 3_000_000 } && earlier == 0 && archives == 0,
            "first paint uses the bounded latest page");
        var visible = new InterestWindow(restSample - FiveMinutes, Now + 1);
        var previous = await api.OpenInterest(Binance, 60, ct.Token, visible);
        Check(previous.Points is [{ Value: 45, Usd: 2_500_000 }, { Value: 51 }, { Value: 50 }] && earlier == 1,
            "panning into an older visible window loads a bounded REST range and preserves the live overlap");
        await api.OpenInterest(Binance, 60, ct.Token, visible);
        Check(earlier == 1, "unchanged views reuse the same cached page");

        var archive = await api.OpenInterest(Binance, 60, ct.Token, new(oldDay, oldDay + Day));
        Check(archives == 1 && archive.Points.Count(point => point.Time == oldSample) == 1 &&
            archive.Points.Any(point => point is { Value: 15, Usd: 1_632_040 }),
            "a viewport beyond Binance's REST retention uses its verified symbol's archive and deduplicates timestamps");

        var linear = await api.OpenInterest(Bybit, 60, ct.Token);
        Check(linear.Points is [{ Value: 10, Usd: 30_000, Estimated: true }, { Value: 20, Usd: null }] && markCalls == 1,
            "linear OI uses one sided size and same-bucket mark, missing marks stay unknown");
        var inverse = new Market(Venue.Binance, "BTCUSD_PERP", "BTC", "USD", new("coinm", 100, QuantityUnit.Quote, Perpetual: true));
        using var inverseJson = JsonDocument.Parse("""
            [{"pair":"BTCUSD","contractType":"PERPETUAL","sumOpenInterest":"301","sumOpenInterestValue":"0.5","timestamp":1795387800000}]
            """);
        Check(ExchangeApi.DecodeInterest(inverse, inverseJson.RootElement, 300, Now) is [{ Value: 301, Usd: 30_100 }],
            "inverse contract face value stays separate from Binance's base-coin notional");
        Console.WriteLine("Open interest USD units, matching prices, leftward paging, cache and Binance archive passed.");
    }

    private static object BinanceItem(long time, double size, double usd) =>
        new { symbol = Binance.Symbol, timestamp = time, sumOpenInterest = size, sumOpenInterestValue = usd };

    private static HttpResponseMessage Reply(object payload) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload)) };

    private static byte[] Archive(string csv)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(zip.CreateEntry("metrics.csv").Open())) writer.Write(csv);
        return stream.ToArray();
    }

    private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); }

    private sealed class FrozenTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(Now);
    }

    private sealed class Responses(Func<Uri, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(reply(request.RequestUri!));
    }
}

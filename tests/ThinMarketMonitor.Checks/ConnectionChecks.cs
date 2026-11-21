using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Authentication;
using ThinMarketMonitor;

internal static class ConnectionChecks
{
    public static async Task Ui()
    {
        using var handler = new SimulatedVpn();
        using var http = new HttpClient(handler);
        var settings = new Settings { Rules = new() { [Venue.Binance] = ListingRule.Require }, PriceModule = false };
        using var window = new MainWindow(false, settings, testHttp: http);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var reload = typeof(MainWindow).GetMethod("ReloadCatalogs", flags)!;
        var catalogs = (Dictionary<MarketSource, Catalog>)typeof(MainWindow).GetField("catalogs", flags)!.GetValue(window)!;
        MarketSource spot = new(Venue.Binance);
        MarketSource ignored = new(Venue.Weex, Segment.Futures);
        await (Task)reload.Invoke(window, [new MarketSource[] { spot, ignored }])!;
        Check(catalogs[spot] is { ConnectionFailed: true, Error: var detail } &&
            detail!.Contains("TLS") && detail.Contains("EOF"), "TLS errors keep the inner cause in the catalog status");
        var dueField = typeof(MainWindow).GetField("nextRetry", flags)!;
        var due = (DateTimeOffset)dueField.GetValue(window)!;
        Check(due - DateTimeOffset.UtcNow is { TotalSeconds: > 0 and <= 15 }, "first connection retry is scheduled promptly");
        Check((typeof(MainWindow).GetMethod("CatalogsToRetry", flags)!.Invoke(window, null) as MarketSource[])!.SequenceEqual([spot]),
            "the retry prioritizes a source used by the listing filter");

        handler.Dropped = false;
        typeof(MainWindow).GetField("nextRefresh", flags)!.SetValue(window, DateTimeOffset.UtcNow.AddMinutes(5));
        dueField.SetValue(window, DateTimeOffset.UtcNow.AddSeconds(-1));
        typeof(MainWindow).GetMethod("Tick", flags)!.Invoke(window, [null, EventArgs.Empty]);
        for (int i = 0; i < 60 && catalogs[spot].ConnectionFailed; i++) await Task.Delay(25);
        Check(catalogs[spot] is { Error: null, Markets.Count: 1 } && catalogs[ignored].ConnectionFailed,
            "a recovered connection repopulates the required catalog without waiting for all venues");
        Check((DateTimeOffset)dueField.GetValue(window)! == DateTimeOffset.MaxValue, "successful recovery clears the retry");

        int requests = handler.Requests;
        typeof(MainWindow).GetField("networkChanged", flags)!.SetValue(window, Environment.TickCount64 - 2000);
        typeof(MainWindow).GetMethod("Tick", flags)!.Invoke(window, [null, EventArgs.Empty]);
        var loadingField = typeof(MainWindow).GetField("loading", flags)!;
        for (int i = 0; i < 200 && (bool)loadingField.GetValue(window)!; i++) await Task.Delay(25);
        Check(handler.Requests > requests && catalogs[spot].Error == null &&
            (DateTimeOffset)typeof(MainWindow).GetField("nextRefresh", flags)!.GetValue(window)! > DateTimeOffset.UtcNow,
            "a settled address change schedules a new catalog load before the ordinary deadline");

        var invalidCert = new HttpRequestException(HttpRequestError.SecureConnectionError, "handshake",
            new AuthenticationException("RemoteCertificateChainErrors"));
        Check(ExchangeApi.ConnectionProblem(invalidCert)?.Contains("RemoteCertificateChainErrors") == true &&
            ExchangeApi.ConnectionProblem(new HttpRequestException("Bad request", null, HttpStatusCode.BadRequest)) == null,
            "certificate failures retain their diagnostic and API errors are not treated as a VPN outage");
        Console.WriteLine("TLS inner-cause reporting and bounded automatic catalog recovery passed.");
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }

    private sealed class SimulatedVpn : HttpMessageHandler
    {
        private int requests;
        public int Requests => Volatile.Read(ref requests);
        public bool Dropped { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref requests);
            if (Dropped)
                throw new HttpRequestException(HttpRequestError.SecureConnectionError, "SSL connection could not be established",
                    new IOException("Received an unexpected EOF from the transport stream."));
            if (request.RequestUri is not { Host: "api.binance.com", AbsolutePath: "/api/v3/exchangeInfo" })
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"symbols":[{"symbol":"BTCUSDT","baseAsset":"BTC","quoteAsset":"USDT","status":"TRADING","isSpotTradingAllowed":true}]}
                    """)
            });
        }
    }
}

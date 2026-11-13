using System.Net.Http;
using System.Text.Json;
using ThinMarketMonitor;

if (args is ["--live"])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    var api = new ExchangeApi(http);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
    var catalogs = await Task.WhenAll(Enum.GetValues<Venue>().Select(async v => (Venue: v, Catalog: await api.LoadCatalog(v, timeout.Token))));
    var probes = new List<Market>();
    bool pass = true;
    foreach (var (venue, catalog) in catalogs)
    {
        if (catalog.Error != null)
        {
            Console.WriteLine($"{venue}: catalog error: {catalog.Error}");
            pass = false;
            continue;
        }
        var candidates = catalog.Markets.Where(x => x.Quote == "USDT").ToArray();
        Market chosen = venue == Venue.Alpha
            ? candidates.FirstOrDefault(x => x.Symbol == "ALPHA_1228USDT") ?? candidates.First()
            : candidates.First(x => x.Base == "BTC");
        probes.Add(chosen);
        Console.WriteLine($"{venue}: {catalog.Markets.Count} markets; probe {chosen.Label}");
    }

    using var streams = new MarketStreams(api, probes, Settings.Defaults);
    var candleChecks = probes.Select(async market =>
    {
        try
        {
            int count = (await api.Candles(market, false, timeout.Token)).Length;
            Console.WriteLine($"{market.Venue}: {count} minute candles");
            return count > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{market.Venue}: candle error: {ex.Message}");
            return false;
        }
    }).ToArray();
    for (int i = 0; i < 18; i++)
    {
        await Task.Delay(1500, timeout.Token);
        if (probes.All(m => streams.HasFreshTrade(m, Now()))) break;
    }
    foreach (var market in probes)
    {
        bool fresh = streams.HasFreshTrade(market, Now());
        var status = streams.Status().First(x => x.Venue == market.Venue);
        Console.WriteLine($"{market.Venue}: fresh trade={fresh}; {status.Detail}");
        pass &= fresh;
    }
    return pass & (await Task.WhenAll(candleChecks)).All(x => x) ? 0 : 1;
}

var ku = new Market(Venue.KuCoin, "NEW-USDT", "NEW", "USDT");
var btc = new Market(Venue.KuCoin, "BTC-USDT", "BTC", "USDT");
var gate = new Market(Venue.Gate, "NEW_USDT", "NEW", "USDT");
var binance = new Market(Venue.Binance, "BTCUSDT", "BTC", "USDT");
var catalogsFixture = new Dictionary<Venue, Catalog>
{
    [Venue.KuCoin] = new([ku, btc]), [Venue.Binance] = new([binance]), [Venue.Gate] = new([gate]),
    [Venue.Bybit] = new([binance with { Venue = Venue.Bybit }]), [Venue.Okx] = new([binance with { Venue = Venue.Okx }])
};
var rules = new Dictionary<Venue, ListingRule>
{
    [Venue.KuCoin] = ListingRule.Require, [Venue.Binance] = ListingRule.Exclude,
    [Venue.Bybit] = ListingRule.Exclude, [Venue.Okx] = ListingRule.Exclude
};
Require(ListingFilter.Apply(catalogsFixture, rules).Markets.SequenceEqual([ku]), "required minus three excludes; ignored Gate");
rules[Venue.Gate] = ListingRule.Require;
Require(ListingFilter.Apply(catalogsFixture, rules).Markets.SequenceEqual([ku]), "intersection");
catalogsFixture[Venue.Binance] = new([], "unavailable");
var incomplete = ListingFilter.Apply(catalogsFixture, rules);
Require(incomplete.Markets.Count == 0 && incomplete.Missing.SequenceEqual([Venue.Binance]), "unknown exclusion fails closed");

Require(ChartPlacement.Pick(ku.Key, [(null, false, 0), (gate.Key, false, 2)]) == 0, "fill first free");
Require(ChartPlacement.Pick(ku.Key, [(btc.Key, true, 0), (gate.Key, false, 3), (binance.Key, false, 2)]) == 2, "replace oldest unlocked");
Require(ChartPlacement.Pick(ku.Key, [(btc.Key, true, 0), (gate.Key, true, 2)]) == -1, "all pinned");
Require(ChartPlacement.Pick(ku.Key, [(ku.Key, false, 1), (null, false, 0)]) == -1, "duplicate signal");

const long start = 1_700_000_000_000;
var detector = new SpikeDetector(Settings.Defaults);
Require(detector.Observe(ku, new(ku.Symbol, start, 100), start) == null, "warm-up");
Require(detector.Observe(ku, new(ku.Symbol, start + 15_000, 104), start + 15_000)?.Seconds == 15, "fresh 4% after 15 seconds");
Require(detector.Observe(ku, new(ku.Symbol, start + 16_000, 105), start + 16_000) == null, "same spike latches");
var stale = new SpikeDetector(Settings.Defaults);
stale.Observe(ku, new(ku.Symbol, start, 100), start);
Require(stale.Observe(ku, new(ku.Symbol, start + 15_000, 200), start + 21_000) == null, "stale current trade");
Require(stale.Observe(ku, new(ku.Symbol, start + 23_000, 200), start + 23_000) == null, "stale baseline");
foreach (var rule in Settings.Defaults.Skip(1))
{
    var independent = new SpikeDetector(Settings.Defaults.Select(x => x with { Enabled = x.Seconds == rule.Seconds }).ToArray());
    independent.Observe(ku, new(ku.Symbol, start, 100), start);
    long end = start + rule.Seconds * 1000;
    Require(independent.Observe(ku, new(ku.Symbol, end, 100 + rule.Percent), end)?.Seconds == rule.Seconds,
        $"independent {rule.Seconds} second window");
}

Require(MarketStreams.Subscriptions(Venue.Bybit, Enumerable.Repeat("BTCUSDT", 21).ToArray()).Count() == 3,
    "Bybit request batching");
using (var message = JsonDocument.Parse("""{"data":{"e":"aggTrade","s":"ALPHA_105USDT","T":1700000000000,"p":"0.23"}}"""))
{
    Trade? received = null;
    MarketStreams.DecodeJson(Venue.Alpha, message.RootElement, x => received = x);
    Require(received == new Trade("ALPHA_105USDT", start, 0.23), "Alpha combined trade payload");
}

string asset = Updater.Asset;
string url = $"https://github.com/{Updater.Repository}/releases/download/v0.2.0/{asset}";
using var release = JsonDocument.Parse(JsonSerializer.Serialize(new
{
    tag_name = "v0.2.0", draft = false, prerelease = false,
    assets = new[] { new { name = asset, state = "uploaded", browser_download_url = url, size = 10, digest = "sha256:" + new string('a', 64) } }
}));
Require(Updater.Parse(release.RootElement, new(0, 1, 0))?.Download.ToString() == url, "new stable release");
Require(Updater.Parse(release.RootElement, new(0, 2, 0)) == null, "same version");
Console.WriteLine("Filtering, chart placement, spike windows, WebSocket messages and update metadata passed.");
return 0;

static void Require(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
}
static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

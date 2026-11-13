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

    using var streams = new MarketStreams(api, probes, Settings.Defaults, Settings.DefaultVolumes, probes);
    var candleChecks = probes.Select(async market =>
    {
        try
        {
            int count = (await api.Candles(market, false, timeout.Token)).Length;
            Console.WriteLine($"{market.Venue}: {count} minute candles");
            int[] periods = [300, 3600];
            var history = await api.HistoricalVolumes(market, periods, _ => Task.CompletedTask, timeout.Token);
            var daily = ExchangeApi.HasBulkDay(market.Venue)
                ? (await api.DayVolumes(market.Venue, timeout.Token)).GetValueOrDefault(market.Symbol, -1)
                : await api.AlphaDayVolume(market, timeout.Token) ?? -1;
            Console.WriteLine($"{market.Venue}: 5m={history.GetValueOrDefault(300, -1):F2}; 1h={history.GetValueOrDefault(3600, -1):F2}; 24h={daily:F2}");
            return count > 0 && periods.All(history.ContainsKey) && daily >= 0;
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
        if (probes.All(m => streams.HasSizedTrade(m, Now())) && streams.Status().All(s => s.Connected)) break;
    }
    foreach (var market in probes)
    {
        bool fresh = streams.HasFreshTrade(market, Now());
        bool sized = streams.HasSizedTrade(market, Now());
        var status = streams.Status().First(x => x.Venue == market.Venue);
        Console.WriteLine($"{market.Venue}: fresh trade={fresh}; size and ID={sized}; {status.Detail}");
        pass &= fresh && sized && status.Connected;
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
Require(Math.Abs(CandlePlot.PriceChange(200, 250) - 25) < 1e-8 &&
    Math.Abs(CandlePlot.PriceChange(200, 150) + 25) < 1e-8 &&
    double.IsNaN(CandlePlot.PriceChange(0, 100)), "signed ruler price change");

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
using (var message = JsonDocument.Parse("""{"data":{"e":"aggTrade","s":"ALPHA_105USDT","T":1700000000000,"p":"0.23","q":"5","a":123}}"""))
{
    Trade? received = null;
    MarketStreams.DecodeJson(Venue.Alpha, message.RootElement, x => received = x);
    Require(received == new Trade("ALPHA_105USDT", start, 0.23, 5, "123"), "Alpha combined trade payload");
}

foreach (var (venue, payload) in new (Venue, string)[]
{
    (Venue.Binance, """{"e":"aggTrade","s":"T","T":1700000000000,"p":"2","q":"3","a":99}"""),
    (Venue.Bybit, """{"topic":"publicTrade.T","data":[{"s":"T","T":1700000000000,"p":"2","v":"3","i":"99"}]}"""),
    (Venue.Okx, """{"arg":{"channel":"trades","instId":"T"},"data":[{"ts":"1700000000000","px":"2","sz":"3","tradeId":"99","count":"3"}]}"""),
    (Venue.Bitget, """{"arg":{"channel":"trade","instId":"T"},"data":[{"ts":"1700000000000","price":"2","size":"3","tradeId":"99"}]}"""),
    (Venue.KuCoin, """{"type":"message","topic":"/market/match:T","data":{"symbol":"T","time":"1700000000000000000","price":"2","size":"3","tradeId":"99"}}"""),
    (Venue.Gate, """{"channel":"spot.trades","event":"update","result":{"currency_pair":"T","create_time_ms":"1700000000000.15","price":"2","amount":"3","id":99}}""")
})
{
    using var json = JsonDocument.Parse(payload);
    Trade? trade = null;
    MarketStreams.DecodeJson(venue, json.RootElement, x => trade = x);
    Require(trade == new Trade("T", start, 2, 3, "99"), $"{venue} trade size and ID");
}

byte[] Field(int number, byte[] content) => [.. Varint((ulong)number * 8 + 2), .. Varint((ulong)content.Length), .. content];
var mexcItem = Field(1, "2"u8.ToArray()).Concat(Field(2, "3"u8.ToArray())).Concat(Varint(32))
    .Concat(Varint((ulong)start)).Concat(Field(5, "99"u8.ToArray())).ToArray();
Trade? mexcTrade = null;
MexcTrades.Decode([.. Field(3, "T"u8.ToArray()), .. Field(314, Field(1, mexcItem))], x => mexcTrade = x);
Require(mexcTrade == new Trade("T", start, 2, 3, "99"), "MEXC Protobuf trade size and ID");

var rolling = new RollingVolume(15);
rolling.Add(100, 7);
rolling.Add(101, 5);
rolling.Add(100, 3);
Require(rolling.Sum(102, 2) == 15 && rolling.Sum(116, 15) == 5, "rolling sums and out-of-order trades");
rolling.Add(124, 23);
Require(rolling.Sum(125, 15) == 23, "wrap clears expired buckets");
rolling.Add(200, 11);
Require(rolling.Sum(201, 15) == 11, "long gap clears buckets");

var impulse = new VolumeImpulse([new(15, true, 100)]);
impulse.ResetCoverage(start);
Trade At(int seconds, double size, string id) => new(ku.Symbol, start + seconds * 1000L, 2, size, id);
Signal? Observe(Trade trade, bool allowed = true) => impulse.Observe(ku, trade, trade.Time, allowed);
Require(Observe(At(2, 30, "a")) == null && Observe(At(2, 30, "a")) == null, "volume waits for a complete window and deduplicates");
Require(Observe(At(16, 1, "b")) == null, "duplicate turnover not counted");
Require(Observe(At(17, 30, "c")) == null, "newest second not counted");
Require(Observe(At(18, 30, "d")) == null, "one-second out-of-order buffer");
Require(Observe(At(20, 1, "e")) is { Module: SignalModule.Volume, Value: 122 }, "absolute USD signal");
Require(Observe(At(21, 100, "f")) == null, "volume spike latches");
impulse.ResetCoverage(null);
Require(Observe(At(40, 100, "g")) == null && !impulse.HasCoverage(start + 40_000), "disconnect invalidates windows");
impulse.ResetCoverage(start + 40_000);
Require(Observe(At(41, 100, "g"), allowed: false) == null, "excluded instruments cannot signal");

VolumeRule[] conditions = [new(300, true, 100), new(86400, true, 300), new(3600, false, 10000)];
var unsupported = btc with { Quote = "EUR" };
double available = 99;
double? FixtureVolume(Market market, int seconds) => market == ku ? seconds == 300 ? available : 400 : null;
var filtered = VolumeFilterMonitor.Apply([ku, gate, unsupported], conditions, FixtureVolume);
Require(filtered.Markets.Length == 0 && filtered.Pending == 1 && filtered.Unsupported == 1, "AND conditions and unknown volumes");
available = 100;
filtered = VolumeFilterMonitor.Apply([ku, gate, unsupported], conditions, FixtureVolume);
Require(filtered.Markets.SequenceEqual([ku]), "market can re-enter after fresh data");
long close = 120_000;
Require(ExchangeApi.CompleteVolumes(new Dictionary<long, double> { [0] = 5 }, [120], close, false).Count == 0,
    "incomplete history is unknown");
Require(ExchangeApi.CompleteVolumes(new Dictionary<long, double> { [0] = 5, [60_000] = 8 }, [120], close, false)[120] == 13,
    "history sums quote turnover");
using (var bar = JsonDocument.Parse("""["0","1","1","1","1","100","999","25","1"]"""))
    Require(ExchangeApi.DecodeVolumeBar(Venue.Okx, bar.RootElement)?.QuoteVolume == 25, "OKX quote volume index");

var migrated = Settings.Deserialize("""
    {"Grid":3,"Thresholds":[{"Seconds":15,"Enabled":false,"Percent":1},{"Seconds":60,"Enabled":true,"Percent":3},
    {"Seconds":900,"Enabled":true,"Percent":10}],"Cells":[{"Key":"KuCoin:BTC-USDT","Pinned":true,"Order":12}]}
    """);
Require(migrated.Grid == 3 && migrated.Cells[0] is { Pinned: true, Order: 12 } &&
    migrated.Thresholds[0] == new Threshold(15, false, 1) && migrated.Thresholds[1].Percent == 3 &&
    migrated.Thresholds[2].Seconds == 300 && migrated.Thresholds[3].Percent == 10 &&
    migrated.PriceModule && !migrated.VolumeModule && migrated.VolumeFilters.Length == 0 &&
    migrated.Fullscreen == System.Windows.Input.Key.F && migrated.Ruler.Mouse == System.Windows.Input.MouseButton.Middle,
    "v0.1.1 migration");
migrated.Thresholds = migrated.Thresholds.Append(new Threshold(120, true, 9)).ToArray();
migrated.VolumeFilters = [new(300, true, 300)];
Require(Settings.Deserialize(JsonSerializer.Serialize(migrated)).Thresholds.Any(r => r.Seconds == 120) &&
    Settings.Deserialize(JsonSerializer.Serialize(migrated)).VolumeFilters.Length == 1, "custom periods round-trip");

string asset = Updater.Asset;
string url = $"https://github.com/{Updater.Repository}/releases/download/v0.2.0/{asset}";
using var release = JsonDocument.Parse(JsonSerializer.Serialize(new
{
    tag_name = "v0.2.0", draft = false, prerelease = false,
    assets = new[] { new { name = asset, state = "uploaded", browser_download_url = url, size = 10, digest = "sha256:" + new string('a', 64) } }
}));
Require(Updater.Parse(release.RootElement, new(0, 1, 0))?.Download.ToString() == url, "new stable release");
Require(Updater.Parse(release.RootElement, new(0, 2, 0)) == null, "same version");
Console.WriteLine("Listings, volume conditions, chart placement, migration, price and volume windows, eight trade adapters and update metadata passed.");
return 0;

static void Require(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
}
static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
static IEnumerable<byte> Varint(ulong value)
{
    while (value >= 128) { yield return (byte)(value | 128); value >>= 7; }
    yield return (byte)value;
}

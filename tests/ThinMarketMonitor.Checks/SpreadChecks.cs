using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ThinMarketMonitor;

internal static class SpreadChecks
{
    private static readonly Market Btc = new(Venue.Binance, "BTCUSDT", "BTC", "USDT");
    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static void Ensure(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("Spread: " + label);
    }

    public static async Task Run()
    {
        long now = Now;
        double spread = new SpreadQuote(99, 101, now).Percent(now)!.Value;
        Ensure(Math.Abs(spread - 2) < 1e-12, "midpoint formula");
        Ensure(new SpreadQuote(1e308, 1.1e308, now).Percent(now) is > 0 and < 200 &&
            new SpreadQuote(double.Epsilon, double.Epsilon, now).Percent(now) == 0, "extreme finite prices");
        foreach (var quote in new[] { new SpreadQuote(0, 1, now), new(1, 0, now), new(2, 1, now),
            new(double.NaN, 1, now), new(1, double.PositiveInfinity, now), new(1, 2, now - 15_001), new(1, 2, now + 3001) })
            Ensure(quote.Percent(now) == null, "invalid and stale books stay unknown");
        Ensure(new SpreadQuote(1, 1, now).Percent(now) == 0, "a valid locked book has zero spread");
        foreach (var rule in new[] { new SpreadRule(true), new(true, spread), new(true, null, spread), new(true, spread, spread) })
            Ensure(rule.Accepts(spread), "optional inclusive bounds");
        Ensure(!new SpreadRule(true, 3).Accepts(spread) && !new SpreadRule(true, null, 1).Accepts(spread), "both bounds reject");
        Ensure(SpreadRule.TryParse("0,5", "", true, out var parsed) && parsed.Minimum == 0.5 && parsed.Maximum == null &&
            SpreadRule.TryParse("", "0", true, out _), "comma, zero and empty bounds");
        foreach (var pair in new[] { ("-1", ""), ("NaN", "1"), ("0", "Infinity"), ("2", "1"), ("abc", "") })
            Ensure(!SpreadRule.TryParse(pair.Item1, pair.Item2, true, out _), "invalid bounds rejected");
        var saved = Settings.Deserialize(JsonSerializer.Serialize(new Settings { SpreadFilter = new(true, 0.3, 2) }));
        Ensure(saved.SpreadFilter == new SpreadRule(true, 0.3, 2) && !Settings.Deserialize("{}").SpreadFilter.Enabled &&
            !Settings.Deserialize("{\"SpreadFilter\":{\"Enabled\":true,\"Minimum\":2,\"Maximum\":1}}").SpreadFilter.Enabled,
            "persistence and fail-safe migration");
        int reads = 0;
        var off = SpreadFilterMonitor.Apply([Btc], new(), _ => { reads++; return null; }, now);
        Ensure(off.Markets.Length == 1 && reads == 0, "disabled filter never reads quotes");
        Ensure(SpreadFilterMonitor.Apply([Btc], new(true, null, 2), _ => null, now).Pending == 1, "missing is pending");
        Ensure(SpreadFilterMonitor.Apply([Btc with { Contract = new("unknown") }], new(true), _ => null, now).Unsupported == 1,
            "unsupported is distinct from missing");

        var fixtures = new[]
        {
            (Btc, "{\"bidPrice\":99,\"askPrice\":101,\"bidQty\":1,\"askQty\":1}"),
            (Btc with { Contract = new("coinm") }, "{\"bidPrice\":99,\"askPrice\":101,\"bidQty\":1,\"askQty\":1}"),
            (Btc with { Venue = Venue.Bybit }, "{\"bid1Price\":99,\"ask1Price\":101,\"bid1Size\":1,\"ask1Size\":1}"),
            (Btc with { Venue = Venue.Okx }, "{\"bidPx\":99,\"askPx\":101,\"bidSz\":1,\"askSz\":1}"),
            (Btc with { Venue = Venue.Bitget }, "{\"bidPr\":99,\"askPr\":101,\"bidSz\":1,\"askSz\":1}"),
            (Btc with { Venue = Venue.Mexc }, "{\"bidPrice\":99,\"askPrice\":101,\"bidQty\":1,\"askQty\":1}"),
            (Btc with { Venue = Venue.Mexc, Contract = new("contract") }, "{\"bid1\":99,\"ask1\":101}"),
            (Btc with { Venue = Venue.KuCoin }, "{\"buy\":99,\"sell\":101,\"bestBidSize\":1,\"bestAskSize\":1}"),
            (Btc with { Venue = Venue.KuCoin, Contract = new("contract") }, "{\"bestBidPrice\":99,\"bestAskPrice\":101,\"bestBidSize\":1,\"bestAskSize\":1}"),
            (Btc with { Venue = Venue.Gate }, "{\"highest_bid\":99,\"lowest_ask\":101}"),
            (Btc with { Venue = Venue.Gate, Contract = new("delivery/usdt") }, "{\"highest_bid\":99,\"lowest_ask\":101,\"highest_size\":1,\"lowest_size\":1}"),
            (Btc with { Venue = Venue.Alpha }, "{\"b\":99,\"a\":101,\"B\":1,\"A\":1}")
        };
        foreach (var (market, fixture) in fixtures)
        {
            using var json = JsonDocument.Parse(fixture);
            Ensure(Math.Abs(ExchangeApi.DecodeBestQuote(market, json.RootElement, now).Percent(now)!.Value - 2) < 1e-12,
                market.Source.Label + " BBO adapter");
        }
        using (var empty = JsonDocument.Parse("{\"bidPrice\":99,\"askPrice\":101,\"bidQty\":0,\"askQty\":1,\"last\":100}"))
            Ensure(ExchangeApi.DecodeBestQuote(Btc, empty.RootElement, now).Percent(now) == null, "empty side cannot use last");
        using (var trade = JsonDocument.Parse("{\"e\":\"bookTicker\",\"b\":99,\"a\":101,\"B\":1,\"A\":1,\"E\":" + (now - 20_000) + "}"))
            Ensure(SpreadFilterMonitor.DecodeAlpha(Btc with { Venue = Venue.Alpha }, trade.RootElement).Percent(now) == null,
                "Alpha exchange event age is checked");

        using (var cacheHandler = new QuotesHandler { Body = () => "{\"retCode\":0,\"time\":" + Now +
                   ",\"result\":{\"list\":[{\"symbol\":\"BTCUSDT\",\"bid1Price\":99,\"ask1Price\":101,\"bid1Size\":1,\"ask1Size\":1,\"turnover24h\":2000}]}}" })
        using (var cacheHttp = new HttpClient(cacheHandler))
        {
            var cachedApi = new ExchangeApi(cacheHttp);
            var bybit = Btc with { Venue = Venue.Bybit };
            var snapshots = cachedApi.Snapshots([bybit], CancellationToken.None);
            var quotes = cachedApi.BestQuotes([bybit], CancellationToken.None);
            await Task.WhenAll(snapshots, quotes);
            Ensure(cacheHandler.Calls == 1 && snapshots.Result[bybit.Key].Turnover == 2000 &&
                quotes.Result.Count == 1 && quotes.Result[bybit.Key].Percent(Now) != null,
                "volume and spread share one exact batch response");
            cacheHandler.Body = () => "{\"retCode\":0,\"time\":" + (Now - 60_000) +
                ",\"result\":{\"list\":[{\"symbol\":\"BTCUSDT\",\"bid1Price\":99,\"ask1Price\":101,\"bid1Size\":1,\"ask1Size\":1}]}}";
            await Task.Delay(2050);
            Ensure((await cachedApi.BestQuotes([bybit], CancellationToken.None))[bybit.Key].Percent(Now) == null,
                "an old exchange snapshot cannot be refreshed by receiving it again");
        }

        using var handler = new QuotesHandler();
        using var http = new HttpClient(handler);
        var api = new ExchangeApi(http);
        using var monitor = new SpreadFilterMonitor(api, [Btc]);
        await Until(() => monitor.Evaluate([Btc], new(true, null, 1), Now).Markets.Length == 1);
        Ensure(monitor.Quote(Btc with { Symbol = "BTCUSD" }) == null, "exact pair lookup ignores unrelated response symbols");
        handler.Wide = true;
        await Until(() => monitor.Evaluate([Btc], new(true, null, 1), Now).Markets.Length == 0 && monitor.Quote(Btc)?.Percent(Now) > 1);
        handler.Fail = true;
        await Until(() => monitor.Evaluate([Btc], new(true), Now).Pending == 1);
        handler.Fail = false;
        handler.Wide = false;
        await Until(() => monitor.Evaluate([Btc], new(true, null, 1), Now).Markets.Length == 1);
        Ensure(handler.Calls <= 6 && handler.Urls.All(u => u.EndsWith("/ticker/bookTicker", StringComparison.Ordinal)),
            "bounded all-symbol batches keep polling excluded markets and recover from errors");
        Console.WriteLine("Spread formula, bounds, migration, unknown/stale books, adapters, rejection/re-entry and API recovery passed.");
    }

    public static async Task Ui()
    {
        using var handler = new QuotesHandler();
        using var http = new HttpClient(handler);
        var settings = new Settings { Rules = new() { [Venue.Binance] = ListingRule.Require }, PriceModule = false };
        using var window = new MainWindow(false, settings, testHttp: http);
        window.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var catalogs = (Dictionary<MarketSource, Catalog>)typeof(MainWindow).GetField("catalogs", flags)!.GetValue(window)!;
        catalogs[Btc.Source] = new([Btc]);
        void Apply() => typeof(MainWindow).GetMethod("ApplyFilter", flags)!.Invoke(window, [false]);
        Apply();
        await Task.Delay(150);
        Ensure(handler.Calls == 0 && typeof(MainWindow).GetField("spreadFilter", flags)!.GetValue(window) == null,
            "disabled filter starts no producer or requests");
        var overview = JsonSerializer.Serialize(new { settings.OverviewSource, settings.OverviewSpotRules, settings.OverviewFuturesRules });
        ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        window.UpdateLayout();
        var options = (SettingsPanel)typeof(MainWindow).GetField("settingsPanel", flags)!.GetValue(window)!;
        T Control<T>(string name) where T : FrameworkElement => Descendants(options).OfType<T>()
            .Single(e => AutomationProperties.GetName(e) == name);
        var min = Control<TextBox>("Мин. спред, %");
        var max = Control<TextBox>("Макс. спред, %");
        var enabled = Control<CheckBox>("Фильтровать по спреду");
        min.Text = "0";
        max.Text = "1";
        enabled.IsChecked = true;
        enabled.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        await Until(() => handler.Calls == 1);
        await Until(() => ((SpreadFilterMonitor)typeof(MainWindow).GetField("spreadFilter", flags)!.GetValue(window)!).Quote(Btc) != null);
        typeof(MainWindow).GetMethod("RefreshEligibility", flags)!.Invoke(window, null);
        var accepted = (IReadOnlyList<Market>)typeof(MainWindow).GetField("markets", flags)!.GetValue(window)!;
        Ensure(accepted.SequenceEqual(new[] { Btc }), "spread result reaches the signal and picker list");
        Ensure(overview == JsonSerializer.Serialize(new { settings.OverviewSource, settings.OverviewSpotRules, settings.OverviewFuturesRules }),
            "overview keeps its independent source rules");
        Ensure(settings.SpreadFilter == new SpreadRule(true, 0, 1), "UI enables and persists percentage bounds");
        max.Text = "-1";
        Control<Button>("Применить диапазон спреда").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ensure(settings.SpreadFilter.Maximum == 1, "invalid UI edit preserves the applied range");
        max.Text = "1";
        window.UpdateLayout();
        if (Environment.GetEnvironmentVariable("TMM_SPREAD_SCREENSHOT") is { Length: > 0 } path)
        {
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            png.Save(file);
        }
        enabled.IsChecked = false;
        enabled.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Ensure(typeof(MainWindow).GetField("spreadFilter", flags)!.GetValue(window) == null && !settings.SpreadFilter.Enabled,
            "UI disable disposes the producer immediately");
        var panel = (OverviewPanel)typeof(MainWindow).GetField("overviewPanel", flags)!.GetValue(window)!;
        settings.OverviewColumns = [];
        typeof(SettingsPanel).GetMethod("SelectPage", flags)!.Invoke(options, [5]);
        window.UpdateLayout();
        Control<ComboBox>("Тип колонки обзора").SelectedIndex = (int)MetricKind.Spread;
        Ensure(Control<TextBox>("Период колонки, минуты").Visibility == Visibility.Collapsed,
            "a current spread column has no historical period");
        Control<Button>("Добавить колонку обзора").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ensure(settings.OverviewColumns.SequenceEqual(new[] { new OverviewColumn(MetricKind.Spread, 0) }),
            "overview column can be added");
        panel.SetSource([Btc], visible: true);
        var grid = (DataGrid)typeof(OverviewPanel).GetField("table", flags)!.GetValue(panel)!;
        await Until(() => ((OverviewPanel.Row)grid.Items[0]).Number(0) is > 0 and < 1);
        var entry = (OverviewPanel.Row)grid.Items[0];
        Ensure(!entry[0].StartsWith('+') && grid.Columns[1].Header.ToString() == "Спред %",
            "overview shows a live percentage without a change sign");
        settings.OverviewSource = Btc.Source;
        settings.Overview = true;
        typeof(MainWindow).GetMethod("RefreshOverview", flags)!.Invoke(window, null);
        await Until(() => grid.Items.Count > 0 && ((OverviewPanel.Row)grid.Items[0]).Number(0) != null);
        window.UpdateLayout();
        if (Environment.GetEnvironmentVariable("TMM_SPREAD_SCREENSHOT") is { Length: > 0 } overviewPath)
        {
            typeof(MainWindow).GetMethod("CloseDialog", flags)!.Invoke(window, null);
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(overviewPath + ".overview.png");
            png.Save(file);
        }
        settings.SpreadFilter = new(true, 2, 3);
        Apply();
        await Until(() => ((SpreadFilterMonitor)typeof(MainWindow).GetField("spreadFilter", flags)!.GetValue(window)!).Quote(Btc) != null);
        typeof(MainWindow).GetMethod("RefreshEligibility", flags)!.Invoke(window, null);
        Ensure(((IReadOnlyList<Market>)typeof(MainWindow).GetField("markets", flags)!.GetValue(window)!).Count == 0 &&
            grid.Items.Count == 1 && entry.Number(0) != null, "signal spread filtering leaves overview rows independent");
        Control<Button>("Удалить Спред %").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Ensure(settings.OverviewColumns.Length == 0 && typeof(OverviewPanel).GetField("spreadQuotes", flags)!.GetValue(panel) == null,
            "removing the column stops its quote producer");
        window.Close();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        while (!predicate()) await Task.Delay(25, timeout.Token);
    }

    public static async Task<int> Live()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(16) };
        var api = new ExchangeApi(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var catalogs = await Task.WhenAll(MarketSource.All.Select(async source =>
            (Source: source, Catalog: await api.LoadCatalog(source, timeout.Token))));
        var probes = new List<Market>();
        foreach (var (source, catalog) in catalogs)
        {
            if (catalog.Error != null) { Console.WriteLine(source.Label + ": catalog " + catalog.Error); continue; }
            if (source.Venue == Venue.Alpha) { probes.AddRange(catalog.Markets); continue; }
            foreach (var group in catalog.Markets.GroupBy(m => m.Contract?.Product))
                probes.Add(group.FirstOrDefault(m => m.Symbol == "ALPHA_1228USDT") ??
                    group.FirstOrDefault(m => m.Base == "BTC" && m.Quote == "USDT") ??
                    group.FirstOrDefault(m => m.Base == "BTC") ?? group.First());
        }
        using var monitor = new SpreadFilterMonitor(api, probes);
        var groups = probes.GroupBy(SpreadFilterMonitor.BatchKey).ToArray();
        for (int i = 0; i < 40 && groups.Any(g => !g.Any(m => monitor.Quote(m)?.Percent(Now) != null)); i++)
            await Task.Delay(750, timeout.Token);
        int ready = 0;
        foreach (var group in groups)
        {
            var m = group.FirstOrDefault(m => monitor.Quote(m)?.Percent(Now) != null) ?? group.First();
            var quote = monitor.Quote(m);
            var spread = quote?.Percent(Now);
            if (spread != null) ready++;
            Console.WriteLine($"{m.Source.Label} {m.Contract?.Product} {m.Symbol}: " +
                (spread is { } value ? $"{value:F6}% bid={quote!.Value.Bid} ask={quote.Value.Ask}" : "unknown"));
        }
        Console.WriteLine(monitor.Coverage(Now));
        Console.WriteLine($"Fresh spread batches: {ready}/{groups.Length}; catalogs {catalogs.Count(c => c.Catalog.Error == null)}/{catalogs.Length}");
        return ready == groups.Length && catalogs.All(c => c.Catalog.Error == null) ? 0 : 1;
    }

    internal sealed class QuotesHandler : HttpMessageHandler
    {
        public volatile bool Wide, Fail;
        public int Calls;
        public Func<string>? Body;
        public readonly ConcurrentQueue<string> Urls = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Urls.Enqueue(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent(Body?.Invoke() ?? "[{\"symbol\":\"BTCUSDT\",\"bidPrice\":99,\"askPrice\":" + (Wide ? "103" : "99.1") +
                    ",\"bidQty\":1,\"askQty\":1},{\"symbol\":\"BTCUSD\",\"bidPrice\":1,\"askPrice\":900,\"bidQty\":1,\"askQty\":1}]")
            });
        }
    }
}

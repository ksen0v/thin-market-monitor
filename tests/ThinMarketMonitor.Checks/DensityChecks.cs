using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
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
        ListingRulesAndPersistence();
        ChartPriceProjection();

        using var handler = new DepthHandler();
        using var http = new HttpClient(handler);
        using var monitor = new DensityMonitor(new(http));
        var options = new DensityOptions
        {
            Popup = true, Venues = { [Venue.Bybit] = Rule with { MinSeconds = 0 } }
        };
        var other = Market with { Base = "OTHER", Symbol = "OTHERUSDT" };
        var bybitPerp = Market with { Contract = new("linear", Perpetual: true) };
        var binancePerp = bybitPerp with { Venue = Venue.Binance, Contract = new("usdm", Perpetual: true) };
        var catalogs = new Dictionary<MarketSource, Catalog>
        {
            [Market.Source] = new([Market, other]),
            [bybitPerp.Source] = new([bybitPerp]),
            [binancePerp.Source] = new([binancePerp])
        };
        monitor.Configure(options, catalogs);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(23));
        while (!monitor.TryRead(out _)) await Task.Delay(80, timeout.Token);
        while (monitor.Visible().Length < 2) await Task.Delay(80, timeout.Token);
        Require(monitor.Visible().All(entry => entry.Market.Contract == null) && monitor.Status().Single().Markets == 2,
            "configured segment only");
        Require(handler.Queries.Where(q => q.Contains("symbol=TESTUSDT", StringComparison.Ordinal)).Take(2)
            .SequenceEqual(["?category=spot&symbol=TESTUSDT&limit=20",
            "?category=spot&symbol=TESTUSDT&limit=50"]), "start small and request more depth when price range requires it");
        var otherFirstSeen = monitor.ForMarket(other).Single().FirstSeen;
        options.CoinRules[Market.ListingAsset] = Rule.Parameters() with
            { SmallUsd = 950, LargeUsd = 2500, MinSeconds = 0, SmallColor = "#C086E7" };
        monitor.Configure(options, catalogs);
        Require(monitor.ForMarket(Market).Length == 0 && monitor.ForMarket(other).Single().FirstSeen == otherFirstSeen,
            "a per-coin change drops its prior levels while other coins retain their observed age");
        while (monitor.ForMarket(Market).Length == 0) await Task.Delay(80, timeout.Token);
        Require(monitor.ForMarket(Market).Single() is { Large: false, Color: "#C086E7" },
            "the next observations use the coin's thresholds and color");
        options.CoinRules.Clear();
        options.Block(other);
        monitor.Configure(options, catalogs);
        Require(monitor.ForMarket(other).Length == 0, "blacklisting removes the card and chart levels immediately");
        while (monitor.ForMarket(Market).Length == 0) await Task.Delay(80, timeout.Token);
        Require(monitor.Visible().Single() is { Large: true } restoredLevel && restoredLevel.Market == Market,
            "venue thresholds keep applying after the coin override is cleared");

        options.ToggleSource(bybitPerp.Source);
        options.Venues[Venue.Binance] = Rule with { Spot = false, Futures = true, MinSeconds = 0 };
        monitor.Configure(options, catalogs);
        var assets = new HashSet<string>(StringComparer.Ordinal) { Market.ListingAsset };
        while (monitor.Visible(assets).Length < 3) await Task.Delay(80, timeout.Token);
        Require(DensityChartLevel.ForMarket(Market, monitor.Visible(assets)).Select(entry => entry.Level.Market.Source)
            .ToHashSet().SetEquals([Market.Source, bybitPerp.Source, binancePerp.Source]),
            "one chart receives selected spot and futures books across venues");
        options.ToggleSource(bybitPerp.Source);
        monitor.Configure(options, catalogs);
        Require(monitor.Visible(assets).All(entry => entry.Market != bybitPerp),
            "deselecting a market immediately removes its overlay");
        options.Venues.Clear();
        monitor.Configure(options, catalogs);
        Require(monitor.Visible().Length == 0 && monitor.Status().Length == 0, "disabled venue removes levels and scan");
        Console.WriteLine("Density source and listing rules, settings migration, per-coin thresholds, blacklist and adaptive depth passed.");
    }

    private static void ChartPriceProjection()
    {
        var spot = new Market(Venue.Binance, "PEPEUSDT", "PEPE", "USDT");
        var perp = new Market(Venue.Bybit, "1000PEPEUSDT", "PEPE", "USDT",
            new("linear", Perpetual: true, NativeBase: "1000PEPE"));
        var weex = new Market(Venue.Weex, "1000PEPEUSDT", "1000PEPE", "USDT",
            new("linear", Perpetual: true), new(ListingAsset: "PEPE"));
        var hyper = new Market(Venue.Hyperliquid, "kPEPE", "KPEPE", "USDT", new("perp", Perpetual: true),
            new(ListingAsset: "PEPE"));
        var inverse = new Market(Venue.Kraken, "PI_PEPEUSD", "PEPE", "USD",
            new("derivatives", 100, QuantityUnit.Quote, Perpetual: true));
        var unrelated = spot with { Venue = Venue.Hyperliquid, Identifiers = new(ListingAsset: "hyperliquid:unrelated") };
        var untrusted = spot with { Venue = Venue.Lighter, Identifiers = new(TrustedUsdQuote: false) };
        var eur = spot with { Venue = Venue.Kraken, Symbol = "PEPEEUR", Quote = "EUR" };
        var levels = new[] { SampleLevel(spot, .000012, 400), SampleLevel(perp, .012, 700), SampleLevel(weex, .012, 800),
            SampleLevel(hyper, .012, 900), SampleLevel(inverse, .000012, 650), SampleLevel(unrelated, 100, 4000),
            SampleLevel(untrusted, 100, 4000), SampleLevel(eur, 100, 4000) };
        var projected = DensityChartLevel.ForMarket(spot, levels);
        Require(projected.Length == 5 && projected.All(item => Math.Abs(item.Price - .000012) < 1e-12) &&
            projected.Select(item => item.Level).SequenceEqual(levels.Take(5)),
            "use confirmed price denominations and dollar quotes without changing source, amount or side");
        var projectedToPerp = DensityChartLevel.ForMarket(perp, levels);
        Require(projectedToPerp.Length == 5 && projectedToPerp.All(item => Math.Abs(item.Price - .012) < 1e-12) &&
            DensityChartLevel.ForMarket(eur, levels).Single() == new DensityChartLevel(levels[^1], 100),
            "projection also works onto a denominated chart and never assumes an unknown foreign exchange rate");
    }

    private static void ListingRulesAndPersistence()
    {
        var settings = Settings.Deserialize("""
            {"Densities":{"Venues":{"Weex":{"Futures":true,"SmallUsd":200,"LargeUsd":900,"SmallColor":"#123abc"}}}}
            """);
        var options = settings.Densities;
        var weex = new MarketSource(Venue.Weex, Segment.Futures);
        var spot = new MarketSource(Venue.Binance, Segment.Spot);
        var futures = new MarketSource(Venue.Binance, Segment.Futures);
        var pepe = new Market(Venue.Weex, "1000PEPEUSDT", "1000PEPE", "USDT", new("linear", Perpetual: true),
            new(ListingAsset: "PEPE"));
        var floki = pepe with { Symbol = "1000FLOKIUSDT", Base = "1000FLOKI", Identifiers = new(ListingAsset: "FLOKI") };
        var unrelated = pepe with { Symbol = "1000OTHERUSDC", Base = "1000OTHER", Quote = "USDC", Identifiers = null };
        var binanceFloki = new Market(Venue.Binance, "1000FLOKIUSDT", "FLOKI", "USDT",
            new("usdm", NativeBase: "1000FLOKI", Perpetual: true));
        var catalogs = new Dictionary<MarketSource, Catalog>
        {
            [weex] = new([pepe, floki, unrelated]),
            [spot] = new([new(Venue.Binance, "PEPEUSDC", "PEPE", "USDC")]),
            [futures] = new([binanceFloki])
        };
        Require(options.Scans(weex) && !options.Scans(new(Venue.Weex)) &&
            options.Venues[Venue.Weex] is { SmallUsd: 200, LargeUsd: 900, SmallColor: "#123abc" }, "flat v0.11.0 options migrate");
        options.CycleListingRule(spot);
        Require(Filter().Markets.SequenceEqual([pepe]), "required spot listing uses the asset regardless of quote or segment");
        options.CycleListingRule(spot);
        options.CycleListingRule(futures);
        Require(Filter().Markets.SequenceEqual([floki]), "spot exclusion and futures requirement have independent states");
        options.CycleListingRule(futures);
        Require(Filter().Markets.SequenceEqual([unrelated]) && options.NeedsCatalog(futures), "exclude a listing on either marked market");
        catalogs[futures] = new([], "Unavailable");
        var waiting = Filter();
        Require(waiting.Markets.Count == 0 && waiting.Missing.SequenceEqual([futures]), "an unavailable rule catalog remains unknown");
        options.CycleListingRule(futures);
        var unavailableBook = new MarketSource(Venue.Htx, Segment.Futures);
        var partial = ListingFilter.ApplySources(catalogs, options.Rule, [weex, unavailableBook]);
        Require(partial.Markets.SequenceEqual([floki, unrelated]) && partial.Missing.SequenceEqual([unavailableBook]),
            "one unavailable selected book source does not prevent using the other source");

        options.ToggleSource(new(Venue.Weex));
        Require(options.Venues[Venue.Weex] is { Spot: true, Futures: true, SmallUsd: 200, SmallColor: "#123abc" },
            "toggling a source keeps venue parameters");
        Require(options.Block(pepe) && !options.Block(catalogs[spot].Markets[0]) && !settings.SignalBlocked(pepe),
            "density blacklist matches a confirmed base across markets without affecting signals");
        options.CoinRules[floki.ListingAsset] = new(SmallUsd: 222, SmallColor: "#FEDCBA");
        var restored = Settings.Deserialize(JsonSerializer.Serialize(settings)).Densities;
        Require(restored.Blacklist.SequenceEqual(["PEPE"]) && restored.CoinRules["FLOKI"] == options.CoinRules["FLOKI"] &&
            restored.Rule(spot) == ListingRule.Exclude && restored.Scans(new(Venue.Weex)) && restored.Scans(weex),
            "new selection, rule, blacklist and per-coin settings survive a round trip");
        Require(DensityCardKey.For(floki) == DensityCardKey.For(binanceFloki) &&
            DensityCardKey.For(pepe) != DensityCardKey.For(pepe with { Base = "PEPE" }) &&
            DensityCardKey.For(pepe) != DensityCardKey.For(pepe with { Contract = null }) &&
            DensityCardKey.For(pepe) != DensityCardKey.For(pepe with { Quote = "USDC" }),
            "cards group equivalent exchange levels but keep price denominations, quotes and segments separate");

        FilterResult Filter() => ListingFilter.ApplySources(catalogs, options.Rule, [weex]);
    }

    public static async Task Ui()
    {
        var settings = new Settings();
        var options = settings.Densities;
        options.Venues[Market.Venue] = Rule;
        Market? editing = null;
        var opened = new List<Market>();
        int changes = 0;
        var panel = new DensityPanel(options, () => { }, () => { }, market => editing = market,
            market => options.Block(market), opened.Add, () => changes++);
        using var http = new HttpClient(new DepthHandler());
        using var colibri = new ColibriBridge(settings);
        var window = new Window { Width = 1180, Height = 640, Left = -3000, Top = -2000, ShowActivated = false,
            ShowInTaskbar = false, Content = panel, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            var levels = Enumerable.Range(0, 42).SelectMany(i => Enumerable.Range(0, 4).Select(row =>
                SampleLevel(Market with { Base = $"COIN{i:D2}", Symbol = $"COIN{i:D2}USDT" }, 98 + row * .1, 10_000 + i * 1800 + row))).ToArray();
            window.Show();
            Update([levels[12]]);
            await Layout();
            var scroll = panel.Children.OfType<ScrollViewer>().Single();
            var board = (Canvas)scroll.Content;
            DensityCard[] VisibleCards() => board.Children.Cast<DensityCard>().ToArray();
            var lone = VisibleCards().Single();
            var origin = lone.TranslatePoint(new Point(), scroll);
            Require(origin is { X: >= 5 and <= 7, Y: >= 5 and <= 7 },
                "a short board starts at the top left of the actual viewport");
            Update([levels[8], levels[0], levels[12]]);
            await Layout();
            var firstRow = VisibleCards().OrderBy(Canvas.GetLeft).ToArray();
            Require(firstRow.Length == 3 && firstRow[0] == lone && firstRow.All(card => Canvas.GetTop(card) == Canvas.GetTop(lone)) &&
                AutomationProperties.GetName(firstRow[1]) == "COIN00/USDT · Spot",
                "new cards fill the first row from left to right in insertion order");
            Update([levels[8]]);
            await Layout();
            Require(VisibleCards().Single().TranslatePoint(new Point(), scroll) == origin,
                "after disappearances the surviving card returns to the first position");
            Update([]);
            Update(levels);
            await Layout();

            var cards = VisibleCards();
            Require(cards.Length is > 3 and < 42 && cards.Select(Canvas.GetLeft).Distinct().Count() == 3,
                "normal window lays out three columns and only realizes the surrounding visible cards");
            var first = cards.Single(card => AutomationProperties.GetName(card) == "COIN00/USDT · Spot");
            Click(VisualChildren(first).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("Настройки")));
            Require(editing?.Base == "COIN00", "the settings icon passes the coin from the card");
            Click(VisualChildren(first).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("В чёрный")));
            Require(options.Blacklist.SequenceEqual(["COIN00"]), "the card adds to the densities blacklist");
            var terminal = VisualChildren(first).OfType<Button>().Single(button => AutomationProperties.GetName(button).StartsWith("Открыть в Colibri"));
            Click(terminal);
            Require(opened.Count == 0, "the terminal action stays inactive when integration is disabled");
            panel.SetColibriEnabled(true);
            Click(terminal);
            Require(opened.SequenceEqual([levels[0].Market]), "single-market card opens the exact market");
            var secondVenue = levels[0] with { Market = levels[0].Market with { Venue = Venue.Binance } };
            Update([.. levels, secondVenue]);
            await Layout();
            Click(terminal);
            var chooser = terminal.ContextMenu!;
            Require(chooser.Items.Count == 2 && opened.Count == 1, "multiple density markets require an explicit choice");
            chooser.Items.OfType<MenuItem>().Single(item => ((string)item.Header).StartsWith("Binance", StringComparison.Ordinal))
                .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            chooser.IsOpen = false;
            Require(opened.SequenceEqual([levels[0].Market, secondVenue.Market]), "the chosen exchange is sent to the terminal");
            Update(levels);
            await Layout();
            var bybit = VisualChildren(panel).OfType<Button>()
                .Single(button => AutomationProperties.GetName(button) == "Источник плотностей · Bybit");
            var future = bybit.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Futures"));
            future.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Require(future.IsChecked && future.StaysOpenOnClick && options.Venues[Market.Venue] is { Spot: true, Futures: true } && changes == 1,
                "market choices can be toggled independently from the top exchange strip");

            scroll.ScrollToVerticalOffset(335);
            await Layout();
            double start = scroll.VerticalOffset;
            var anchor = VisibleCards().Where(card => Canvas.GetTop(card) <= start &&
                Canvas.GetTop(card) + card.ActualHeight > start).MinBy(Canvas.GetLeft)!;
            double relative = Canvas.GetTop(anchor) - start;
            Update(levels.Select(level => level with { Usd = level.Usd * 2 }).ToArray());
            await Layout();
            Require(VisibleCards().Contains(anchor) && Math.Abs(scroll.VerticalOffset - start) < .5 &&
                Math.Abs(Canvas.GetTop(anchor) - start - relative) < .5, "quote refresh leaves cards and scrolling in place");
            Update([.. levels, levels[0] with { Key = new(BookSide.Ask, 102) }]);
            await Layout();
            Require(VisibleCards().Contains(anchor) && Math.Abs(Canvas.GetTop(anchor) - scroll.VerticalOffset - relative) < .5,
                "structural change above the viewport preserves the visible anchor");
            window.Width = 760;
            await Layout();
            Require(VisibleCards().Select(Canvas.GetLeft).Distinct().Count() == 2 &&
                VisibleCards().All(card => Canvas.GetLeft(card) + card.ActualWidth <= scroll.ViewportWidth),
                "the board reflows when the window shrinks");

            using var dialog = new SettingsPanel(settings, new(http), colibri, _ => { }, () => { }, () => { },
                saveDensities: () => changes++, densityCoin: editing, densityMarkets: () => [Market]);
            window.Content = dialog;
            await Layout();
            var pickers = VisualChildren(dialog).OfType<DensityColorPicker>().Where(item => item.IsVisible).ToArray();
            Require(pickers.Length == 2 && pickers[0].Hex == Rule.SmallColor && pickers[1].Hex == Rule.LargeColor,
                "opening coin preferences inherits both venue swatches");
            var palette = (Popup)typeof(DensityColorPicker).GetMethod("BuildPopup", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(pickers[0], null)!;
            var controls = (StackPanel)((Border)palette.Child).Child;
            Click((Button)((UniformGrid)controls.Children[0]).Children[1]);
            Require(pickers[0].Hex == "#679CF2", "a preset updates the chosen color");
            double[] channels = [18, 202, 120];
            for (int i = 0; i < channels.Length; i++)
            {
                var track = (StackPanel)((DockPanel)controls.Children[i + 1]).Children[1];
                ((Slider)track.Children[0]).Value = channels[i];
            }
            Click(VisibleButton(dialog, "Сохранить для монеты"));
            Require(options.CoinRules["COIN00"].SmallColor == "#12CA78" && options.Venues[Market.Venue].SmallColor == Rule.SmallColor,
                "saving the swatch stores the coin color only");
            Click(VisibleButton(dialog, "Сбросить к настройкам биржи"));
            Require(options.CoinRules.Count == 0 && pickers[0].Hex == Rule.SmallColor, "coin overrides can be reset");
            var blacklist = VisualChildren(dialog).OfType<Button>()
                .Where(button => AutomationProperties.GetName(button) == "Чёрный список").Last();
            Click(blacklist);
            await Layout();
            Click(VisualChildren(dialog).OfType<Button>().Single(button =>
                AutomationProperties.GetName(button) == "Вернуть COIN00 в плотности"));
            Require(options.Blacklist.Length == 0, "the settings blacklist restores the coin");

            var plot = new CandlePlot(() => new(System.Windows.Input.Key.F));
            window.Content = plot;
            plot.Set([new(Start - 60_000, 100, 110, 90, 105), new(Start, 105, 108, 99, 100)], reset: true);
            var bid = SampleLevel(Market, 98, 7_800_000);
            var ask = SampleLevel(Market with { Venue = Venue.Weex, Contract = new("linear", Perpetual: true) }, 100.05, 505_000) with
                { Key = new(BookSide.Ask, 100.05) };
            plot.SetDensityLevels(DensityChartLevel.ForMarket(Market, [bid, ask]));
            await Layout();
            var images = Drawings(VisualTreeHelper.GetDrawing(plot)!).OfType<ImageDrawing>().ToArray();
            Require(images.Length == 2 && images.Any(image => image.ImageSource == Terminal.VenueLogoSource(Venue.Bybit)) &&
                images.Any(image => image.ImageSource == Terminal.VenueLogoSource(Venue.Weex)) &&
                CandlePlot.DensityLabel(bid) == "$7,8M · Spot" && CandlePlot.DensityLabel(ask) == "$505K · Futures",
                "rendered density badges carry their venue logos, segments and compact USD amounts");
            var cluster = new[] { bid with { Usd = 8_700_000 }, ask,
                ask with { Market = ask.Market with { Venue = Venue.Binance }, Usd = 6_000_000 },
                ask with { Market = ask.Market with { Venue = Venue.KuCoin }, Usd = 80_000 },
                ask with { Market = ask.Market with { Venue = Venue.Gate }, Usd = 250_000 } };
            plot.SetDensityLevels(DensityChartLevel.ForMarket(Market, cluster));
            await Layout();
            var drawings = Drawings(VisualTreeHelper.GetDrawing(plot)!).ToArray();
            var labels = drawings.OfType<GlyphRunDrawing>()
                .Select(drawing => new string(drawing.GlyphRun.Characters.ToArray())).ToArray();
            Require(labels.Any(label => label.Contains("$8,7M", StringComparison.Ordinal)) &&
                drawings.OfType<ImageDrawing>().Count() == cluster.Length,
                "changes in size redraw the label and nearby prices can show all their venue badges");
        }
        finally { window.Close(); }
        Console.WriteLine("Density WPF cards, responsive layout, scroll anchoring, per-coin actions and chart badges passed.");

        void Update(DensityLevel[] levels) => panel.Update(levels, [new(Market.Venue, 42, 42, 0, 0, 12, null)], [], false);
        async Task Layout()
        {
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }

    private static DensityLevel SampleLevel(Market market, double price, double usd) =>
        new(market, new(BookSide.Bid, price), usd, 1.4, Start - 35_000, Start, true, "#EFB863");

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static Button VisibleButton(DependencyObject parent, string label) => VisualChildren(parent).OfType<Button>()
        .Single(button => button.IsVisible && Equals(button.Content, label));

    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject node)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            yield return child;
            foreach (var descendant in VisualChildren(child)) yield return descendant;
        }
    }

    private static IEnumerable<Drawing> Drawings(Drawing drawing) => drawing is DrawingGroup group
        ? group.Children.SelectMany(Drawings) : [drawing];

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
            var bids = Side(99.9, -.15);
            var asks = Side(100.1, .15);
            string body = request.RequestUri.Host == "fapi.binance.com"
                ? JsonSerializer.Serialize(new { bids, asks })
                : JsonSerializer.Serialize(new { retCode = 0, result = new { b = bids, a = asks } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Density: " + message);
    }
}

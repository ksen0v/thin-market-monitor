using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using ThinMarketMonitor;

internal static class ChartFeatureChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const long Now = 1_795_387_800_000;
    private static readonly Market Bitcoin = new(Venue.Binance, "BTCUSDT", "BTC", "USDT", new("usdm", Perpetual: true));

    public static void Run()
    {
        var rect = new Rect(0, 0, 100, 40);
        Check(ChartGeometry.Clip(rect, new(-20, 15), new(1, 0), true) == (new Point(0, 15), new Point(100, 15)) &&
            ChartGeometry.Clip(rect, new(20, 10), new(-10, 0), true) == (new Point(20, 10), new Point(0, 10)),
            "horizontal rays preserve the anchor and diagonal rays can travel through their second point to the left");
        Check(ChartGeometry.Clip(rect, new(20, 20), new(30, -6), false)?.End == new Point(50, 14),
            "segments end at the second point");
        var bars = new Candle[] { new(0, 8, 10, 7, 9), new(60_000, 19, 21, 17, 20) };
        Check(ChartGeometry.Snap(new(79_000, 20.7), bars, 60_000) == new ChartPoint(90_000, 21),
            "magnet picks OHLC of the nearest candle in time");

        var tape = new ExecutionTape();
        tape.Add(new(10, 2, 4, "a", LiquidationSide.Long));
        tape.Add(new(10, 2, 4, "a", LiquidationSide.Long));
        tape.Add(new(11, 9, 3, "b", LiquidationSide.Short));
        tape.Add(new(11, 12, 100, "c"));
        tape.Add(new(20, 6, 100, "d"));
        var profile = ExecutionProfile.Calculate(tape.Events, 10, 20, 0, 10, 2);
        Check(profile.First == 10 && profile.Last == 11 && profile.Count == 3 &&
            profile.Buckets[0].Long == 4 && profile.Buckets[1].Short == 3 && profile.Buckets.Sum(bin => bin.Total) == 7,
            "time and price clipping, side separation, and trade ID deduplication use real fills only");

        var live = new LiveCandles(60);
        var old = new Candle(60_000, 8, 10, 7, 9, 150);
        live.AddHistory([old], 100_000);
        var trade = new Trade("BTCUSDT", 109_000, 10, 2, "x");
        live.Add(trade, 20);
        live.Add(trade, 20);
        Check(live.Snapshot()[0] is { QuoteVolume: 150, ObservedVolume: false }, "overlapping REST and live totals are not added");
        live.Add(trade with { Id = "y", Time = 110_000 }, 200);
        Check(live.Snapshot()[0] is { QuoteVolume: 220, ObservedVolume: true }, "observed volume can move ahead of the preceding snapshot");
        live.AddHistory([old with { QuoteVolume = 255 }], 122_000);
        live.Add(trade with { Id = "z" }, 1000);
        Check(live.Snapshot()[0] is { QuoteVolume: 255, ObservedVolume: false }, "the final exchange candle supersedes live volume");
        var inverse = new Market(Venue.Binance, "BTCUSD_PERP", "BTC", "USD", new("coinm", 100, QuantityUnit.Quote, Perpetual: true));
        Check(ChartVolume.TradeAmount(inverse, trade) == 200, "inverse size uses the quote face value once, independent of price");

        var spot = Bitcoin with { Contract = null };
        var perp = Bitcoin with { Venue = Venue.Bybit, Contract = new("linear", Perpetual: true) };
        var another = perp with { Symbol = "BTCOTHER" };
        var usdc = perp with { Symbol = "BTCUSDC", Quote = "USDC" };
        var bybitInverse = perp with { Symbol = "BTCUSD", Quote = "USD", Contract = new("inverse", Unit: QuantityUnit.Quote, Perpetual: true) };
        var matching = ChartFeatures.ComparableContracts(spot, Venue.Bybit, [perp, another, usdc, bybitInverse,
            perp with { Symbol = "SAMEALIAS", Identifiers = new(ListingAsset: "unrelated:BTC") }]);
        Check(matching.Length == 4 && ChartFeatures.ChooseInterestContract(spot, matching, "") == null &&
            ChartFeatures.ChooseInterestContract(spot, [perp, usdc, bybitInverse], "") == perp &&
            ChartFeatures.ChooseInterestContract(spot, [bybitInverse], "") == null &&
            ChartFeatures.ChooseInterestContract(spot, matching, bybitInverse.Key) == bybitInverse &&
            ChartFeatures.ComparableContracts(inverse, Venue.Binance, [Bitcoin]).SequenceEqual([inverse]),
            "exact quote is preferred, different contracts stay individually selectable, and ambiguous or inverse substitutes need explicit selection");

        var rows = ExchangeApi.DecodeInterest(perp, Json("""
            {"retCode":0,"result":{"symbol":"BTCUSDT","category":"linear","list":[
              {"timestamp":"1795387500000","openInterest":"20","singleOpenInterest":"10"},
              {"timestamp":"1795387800000","openInterest":"0","singleOpenInterest":"0"},
              {"timestamp":"1795387800000","openInterest":"123"}]}}
            """), 300, Now);
        Check(rows.SequenceEqual([new(1_795_387_500_000, 10), new(Now, 0)]), "use the documented single-sided OI and preserve a genuine zero");
        var kraken = new Market(Venue.Kraken, "PF_XBTUSD", "BTC", "USD", new("derivatives", Perpetual: true));
        Check(ExchangeApi.DecodeInterest(kraken, Json("""
            {"result":{"timestamp":[1795384800],"data":[["10","14","9","13"]]}}
            """), 3600, Now).Single() == new InterestPoint(1_795_384_800_000, 10),
            "an hourly close is not backdated to the opening timestamp");
        var dollarCandidates = ChartFeatures.ComparableContracts(spot, Venue.Kraken, [kraken]);
        Check(ChartFeatures.ChooseInterestContract(spot, dollarCandidates, "") == kraken &&
            ChartFeatures.ComparableContracts(spot with { Quote = "EUR" }, Venue.Kraken, [kraken]).SequenceEqual([kraken]),
            "a confirmed linear USD contract can be overlaid on a spot chart quoted in USDT or EUR");
        var fill = BinanceLiquidations.Decode(inverse, Json("""
            {"e":"forceOrder","E":1795387800001,"o":{"s":"BTCUSD_PERP","st":2,"S":"SELL","X":"FILLED","p":"8","ap":"10","l":"3","z":"3","T":1795387800000}}
            """));
        Check(fill is { Price: 10, Size: 300, Side: LiquidationSide.Long }, "liquidations use the execution price, side and executed quantity");
        Check(BinanceLiquidations.Decode(Bitcoin, Json("""
            {"e":"forceOrder","o":{"s":"BTCUSDT","S":"BUY","X":"FILLED","p":"8","ap":"10","l":"1","z":"2","T":1795387800000}}
            """)) == null, "averaged multi-fill prices do not become invented price levels");
        var lighter = new Market(Venue.Lighter, "1", "BTC", "USD", new("perp"));
        var events = ExtendedStreams.DecodeTrades(lighter, Json("""
            {"type":"update/trade","channel":"trade:1","trades":[{"market_id":1,"timestamp":1795387800000,"trade_id":3,"price":"10","size":"2"}],
             "liquidation_trades":[{"market_id":1,"timestamp":1795387800000,"trade_id":4,"price":"9","size":"1"}]}
            """));
        Check(events is [{ Liquidated: false }, { Liquidated: true, Price: 9 }], "Lighter trade and liquidation classes are distinguished");
        var gate = new Market(Venue.Gate, "BTC_USDT", "BTC", "USDT", new("futures/usdt"));
        var gateFills = ExchangeApi.DecodeGateExecutions(gate, Json("""
            [{"contract":"BTC_USDT","time":1795387800,"size":"34","order_size":"-34","fill_price":"9","order_price":"8","left":"0"},
             {"contract":"BTC_USDT","time":1795387800,"size":"12","order_size":"12","fill_price":"0","order_price":"8","left":"0"},
             {"contract":"ETH_USDT","time":1795387800,"order_size":"12","fill_price":"9","order_price":"8"}]
            """));
        Check(gateFills is [{ Time: Now, Size: 1, Price: 9, Side: LiquidationSide.Long }],
            "public Gate data contributes executed order events, not position or unfilled order volume");
        var millions = InterestScale.Create([new(1, 2_150_000), new(2, 2_870_000)], 4)!.Value;
        Check(millions.Ticks().Select(millions.Label).SequenceEqual(["2M", "2,2M", "2,4M", "2,6M", "2,8M", "3M"]) &&
            millions.Delta(121_000).StartsWith("+121K") &&
            InterestScale.Create([new(1, double.NaN)], 3) == null &&
            InterestScale.Create([new(1, 0)], 3) is { Low: 0, High: > 0 },
            "OI axes have rounded numeric ticks and distinguish a valid zero from missing data");
        var close = InterestScale.Create([new(1, 2_000_000.01), new(2, 2_000_000.09)], 4)!.Value;
        string[] closeLabels = close.Ticks().Select(close.Label).ToArray();
        Check(closeLabels.Distinct().Count() == closeLabels.Length && close.Delta(-0.08).StartsWith("-0,08"),
            "a narrow range keeps its axis labels distinct and ruler changes measurable");

        var prior = new Settings
        {
            SchemaVersion = 1, Thresholds = [],
            ChartIndicators = new() { [Bitcoin.Key] = new(Volume: false, Interest: new() { [Venue.Binance] = "" }) }
        };
        var migrated = Settings.Deserialize(JsonSerializer.Serialize(prior));
        Check(migrated.ChartIndicators[Bitcoin.Key] is { Volume: true, HasInterest: true } && migrated.Thresholds.Length == 0,
            "opening pre-volume-default settings enables candle volume once while preserving other saved choices");
        Console.WriteLine("Chart drawing geometry, profile arithmetic, live volume joins, OI scale and volume default passed.");
    }

    public static async Task Ui()
    {
        var settings = new Settings { Overview = false, OverviewDrawer = false, Grid = 3 };
        using var requests = new IndicatorRequests();
        using var http = new HttpClient(requests);
        using var window = new MainWindow(false, settings, testHttp: http) { Width = 800, Height = 530 };
        var cell = Field<ChartCell[]>(window, "cells")[0];
        var signals = (Button)window.FindName("SignalsButton");
        var overview = (Button)window.FindName("OverviewButton");
        var preferences = (Button)window.FindName("SettingsButton");
        cell.Assign(Bitcoin with { Contract = Bitcoin.Contract! with { Streaming = false } }, true);
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var navigation = (FrameworkElement)window.FindName("WorkspaceNavigation");
        var bottom = (FrameworkElement)window.FindName("BottomNavigation");
        try
        {
            AssertCenteredNavigation();
            Check(new[] { signals, overview, preferences }.All(b => b.Content is Viewbox && b.IsTabStop &&
                !string.IsNullOrEmpty(AutomationProperties.GetName(b))) && AutomationProperties.GetItemStatus(signals) == "Активный раздел",
                "center navigation has accessible icon-only buttons and an active state");
            window.Width -= 100;
            window.UpdateLayout();
            AssertCenteredNavigation();

            var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var bars = new[] { new Candle(time - 60_000, 10, 12, 9, 11, 50), new Candle(time, 11, 13, 10, 12, 75) };
            var plot = cell.Plot;
            plot.Set(bars);
            typeof(CandlePlot).GetMethod("SetPrice", Private)!.Invoke(plot, [-20d, 40d]);
            var view = typeof(CandlePlot).GetMethod("CurrentView", Private)!.Invoke(plot, null)!;
            var bounds = (Rect)view.GetType().GetProperty("Plot")!.GetValue(view)!;
            Check((double)view.GetType().GetMethod("Price")!.Invoke(view, [bounds.Bottom])! == -20,
                "WPF price view can pan below zero while candles keep their true prices");
            var header = Field<TextBlock>(cell, "label");
            string headerText = string.Concat(header.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
            Check(headerText == "BTC/USDT Binance" && header.ToolTip is string tooltip && tooltip.Contains("BTCUSDT"),
                $"the chart header shows the ticker once and retains the native identifier in its tooltip ({headerText} / {header.ToolTip})");
            typeof(ChartCell).GetMethod("SetChartDetail", Private)!.Invoke(cell, ["", null]);
            Check(Field<TextBlock>(cell, "detail").Visibility == Visibility.Collapsed,
                "healthy charts hide the gesture help strip");
            var button = Field<Button>(cell, "drawingTools");
            var rail = Field<Border>(cell, "drawingToolbar");
            Check(rail.Visibility == Visibility.Collapsed, "drawing tools start collapsed in the signal grid");
            Click(button);
            window.UpdateLayout();
            var tools = Field<Dictionary<DrawingKind, Button>>(cell, "drawingActions");
            Check(rail.IsVisible && tools.Count == 3 && tools.Values.All(b => b.Content is Viewbox) &&
                rail.TranslatePoint(new Point(rail.ActualWidth, 0), cell).X <= plot.TranslatePoint(new Point(), cell).X &&
                rail.CornerRadius.TopLeft > 0 && rail.VerticalAlignment == VerticalAlignment.Center,
                "drawing tools open on the left in a rounded, vertically centered panel");
            Click(tools[DrawingKind.HorizontalRay]);
            view = typeof(CandlePlot).GetMethod("CurrentView", Private)!.Invoke(plot, null)!;
            bounds = (Rect)view.GetType().GetProperty("Plot")!.GetValue(view)!;
            typeof(CandlePlot).GetMethod("DrawingClick", Private)!.Invoke(plot, [new Point(bounds.Left + 10, bounds.Bottom - 3), view]);
            Check(settings.ChartDrawings[Bitcoin.Key] is [{ Start.Price: < 0 }], "the drawing action stores time/price anchors for this market");
            var original = settings.ChartDrawings[Bitcoin.Key][0];
            cell.SetFrame(300);
            Check(Field<ChartDrawing[]>(plot, "drawings").Single() == original &&
                Settings.Deserialize(JsonSerializer.Serialize(settings)).ChartDrawings[Bitcoin.Key].Single() == original,
                "drawing anchors survive timeframe and settings reload");
            var featureMenu = Field<Button>(cell, "indicatorTools");
            Click(featureMenu);
            var volumeToggle = featureMenu.ContextMenu.Items.OfType<MenuItem>().First();
            Check(volumeToggle.IsChecked && Field<TextBlock>(cell, "indicatorHint").Visibility == Visibility.Collapsed,
                "volume is enabled on a new chart without an extra status row");
            ClickMenu(volumeToggle);
            featureMenu.ContextMenu.IsOpen = false;
            Check(!Settings.Deserialize(JsonSerializer.Serialize(settings)).ChartIndicators[Bitcoin.Key].Volume,
                "a user's volume opt-out persists across launches even with no other indicators selected");
            Click(featureMenu);
            ClickMenu(featureMenu.ContextMenu.Items.OfType<MenuItem>().First());
            featureMenu.ContextMenu.IsOpen = false;

            Click(featureMenu);
            ClickMenu(featureMenu.ContextMenu.Items.OfType<MenuItem>().First(item => Equals(item.Header, "Binance")));
            featureMenu.ContextMenu.IsOpen = false;
            await requests.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(settings.ChartIndicators[Bitcoin.Key].Volume && settings.ChartIndicators[Bitcoin.Key].HasInterest &&
                Field<TextBlock>(cell, "indicatorHint").IsVisible && plot.ActualHeight >= 48,
                "chart tools work in a 3×3 layout and start requests on demand");
            Click(button);
            Click(featureMenu);
            ClickMenu(featureMenu.ContextMenu.Items.OfType<MenuItem>().First(item => Equals(item.Header, "Открытый интерес в USD")));
            featureMenu.ContextMenu.IsOpen = false;
            Check(Settings.Deserialize(JsonSerializer.Serialize(settings)).ChartIndicators[Bitcoin.Key].InterestUsd,
                "the chart's USD unit preference persists between sessions");
            Click(featureMenu);
            ClickMenu(featureMenu.ContextMenu.Items.OfType<MenuItem>().First(item => Equals(item.Header, "Lighter")));
            featureMenu.ContextMenu.IsOpen = false;
            var sources = Field<IndicatorDisplay>(plot, "indicators").Interest;
            Check(sources.Select(layer => layer.Venue).SequenceEqual([Venue.Lighter, Venue.Binance]),
                "the newest enabled exchange is stacked above the previous OI pane");
            plot.Set(bars);
            window.UpdateLayout();
            CheckInterestPanels(window, plot, bars);

            Click(preferences);
            Check(AutomationProperties.GetItemStatus(preferences) == "Активный раздел" &&
                ((TextBlock)window.FindName("CatalogStatus")).IsVisible, "bottom settings opens with the network status still visible");
            Click(overview);
            await requests.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(settings.Overview && !settings.OverviewDrawer && !cell.IsVisible &&
                AutomationProperties.GetItemStatus(overview) == "Активный раздел" &&
                Field<Border>(Field<ChartCell>(window, "overviewChart"), "drawingToolbar").IsVisible,
                "Overview expands the drawing tools, cancels unused indicator requests and keeps the drawer state");
            Click(signals);
            Check(!settings.Overview && cell.Choice is { Key: var key, Pinned: true } && key == Bitcoin.Key,
                "switching back restores the signal grid and selection");

            typeof(MainWindow).GetMethod("EnterFullscreen", Private)!.Invoke(window, [cell, false]);
            window.UpdateLayout();
            Check(button.IsVisible && rail.IsVisible && featureMenu.IsVisible && bottom.Visibility == Visibility.Collapsed,
                "fullscreen expands the drawing tools and keeps indicators available");
            typeof(MainWindow).GetMethod("ExitFullscreen", Private)!.Invoke(window, null);
            Check(rail.Visibility == Visibility.Collapsed, "leaving fullscreen restores the compact signal toolbar");
            cell.Assign(Bitcoin with { Symbol = "ETHUSDT", Base = "ETH", Contract = Bitcoin.Contract! with { Streaming = false } }, true);
            Check(Field<ChartDrawing[]>(plot, "drawings").Length == 0 && (Field<IndicatorDisplay?>(plot, "indicators")?.Interest.Length ?? 0) == 0,
                "a different market shows its own annotations and indicator setup");
        }
        finally { window.Close(); }

        void AssertCenteredNavigation()
        {
            double center = navigation.TranslatePoint(new Point(navigation.ActualWidth / 2, 0), bottom).X;
            Check(Math.Abs(center - bottom.ActualWidth / 2) <= 1, "workspace buttons are centered at different window sizes");
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void ClickMenu(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static void CheckInterestPanels(Window window, CandlePlot plot, Candle[] bars)
    {
        InterestLayer binance = new(Venue.Binance, Bitcoin, new("BTC", 300, [new(bars[0].Time, 120_000, 6_100_000), new(bars[1].Time, 141_000, 7_600_000)]));
        InterestLayer lighter = new(Venue.Lighter, null, new("USD", 0, [new(bars[0].Time, 2_100_000, 2_100_000), new(bars[1].Time, 2_600_000, 2_600_000)]));
        var options = ChartIndicatorOptions.Default with { Interest = new() { [Venue.Binance] = "", [Venue.Lighter] = "" }, InterestUsd = true };
        var display = new IndicatorDisplay(Bitcoin, options, null, null, "", "", [lighter, binance]);
        plot.UpdateIndicators(display);
        window.UpdateLayout();
        var view = typeof(CandlePlot).GetMethod("CurrentView", Private)!.Invoke(plot, null)!;
        var panes = ((System.Collections.IEnumerable)typeof(CandlePlot).GetMethod("InterestPanes", Private)!.Invoke(plot, [view])!)
            .Cast<object>().ToArray();
        var graphs = panes.Select(pane => (Rect)pane.GetType().GetProperty("Graph")!.GetValue(pane)!).ToArray();
        Check(graphs.Length == 2 && graphs[0].Bottom < graphs[1].Top && graphs.All(rect => rect.Height >= 20),
            "the two OI series have separately usable panes in a compact 3×3 chart");
        var ruler = typeof(CandlePlot).GetMethod("RulerViewport", Private)!;
        var (upper, upperScale) = Ruler(0, "USD");
        var (_, lowerScale) = Ruler(1, "USD");
        Check(upperScale.Low == 2_000_000 && lowerScale.Low == 6_000_000,
            "both rulers use the USD values of their own series");
        typeof(CandlePlot).GetMethod("SetInterestRange", Private)!.Invoke(plot, [upper, 1_000_000d, 6_000_000d]);
        Check(Ruler(0, "USD").Scale.Low == 1_000_000 && Ruler(1, "USD").Scale == lowerScale,
            "stretching one OI axis uses a separate range");
        var onScale = new Point(graphs[0].Right + 5, Center(0).Y);
        Check((bool)typeof(CandlePlot).GetMethod("StartInterestAxisDrag", Private)!.Invoke(plot, [onScale, view, true])! &&
            Ruler(0, "USD").Scale == upperScale, "a double click on an OI scale restores auto on that pane");
        plot.UpdateIndicators(display with { Options = options with { InterestUsd = false } });
        Check(Ruler(1, "BTC").Scale.High < 200_000, "switching back uses the original contract units");
        plot.UpdateIndicators(display with { Interest = [lighter with { Reading = null }, binance] });
        Check(ruler.Invoke(plot, [Center(0), view]) == null, "an empty OI series never starts a ruler with invented values");

        Point Center(int index) => new(graphs[index].Left + graphs[index].Width / 2, graphs[index].Top + graphs[index].Height / 2);

        (object Axis, InterestScale Scale) Ruler(int index, string unit)
        {
            var target = ruler.Invoke(plot, [Center(index), view]);
            Check(target != null, "ruler accepts points in each OI pane");
            var axis = Field<object>(target!, "Item2", BindingFlags.Public | BindingFlags.Instance);
            var scale = (InterestScale)axis.GetType().GetProperty("Scale")!.GetValue(axis)!;
            var viewport = Field<object>(target!, "Item1", BindingFlags.Public | BindingFlags.Instance);
            Check((string)axis.GetType().GetProperty("Unit")!.GetValue(axis)! == unit &&
                Math.Abs((double)viewport.GetType().GetMethod("Price")!.Invoke(viewport, [graphs[index].Bottom])! - scale.Low) < 1e-5,
                "ruler uses the corresponding pane coordinates and selected units");
            return (axis, scale);
        }
    }

    public static async Task<int> Live()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(14) };
        var api = new ExchangeApi(http);
        using var timer = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var markets = new Market[]
        {
            Bitcoin,
            new(Venue.Binance, "BTCUSD_PERP", "BTC", "USD", new("coinm", 100, QuantityUnit.Quote, Perpetual: true)),
            new(Venue.Bybit, "BTCUSDT", "BTC", "USDT", new("linear", Perpetual: true)),
            new(Venue.Bybit, "BTCUSD", "BTC", "USD", new("inverse", 1, QuantityUnit.Quote, Perpetual: true)),
            new(Venue.Bybit, "BTCPERP", "BTC", "USDC", new("linear", Perpetual: true)),
            new(Venue.Okx, "BTC-USDT-SWAP", "BTC", "USDT", new("SWAP", .01, Perpetual: true)),
            new(Venue.Okx, "BTC-USD-SWAP", "BTC", "USD", new("SWAP", 100, QuantityUnit.Quote, Perpetual: true)),
            new(Venue.Bitget, "BTCUSDT", "BTC", "USDT", new("USDT-FUTURES", Perpetual: true)),
            new(Venue.Bitget, "BTCPERP", "BTC", "USDC", new("USDC-FUTURES", Perpetual: true)),
            new(Venue.Mexc, "BTC_USDT", "BTC", "USDT", new("contract", .0001, Perpetual: true)),
            new(Venue.KuCoin, "XBTUSDTM", "BTC", "USDT", new("contract", .001, Perpetual: true)),
            new(Venue.KuCoin, "XBTUSDM", "BTC", "USD", new("contract", 1, QuantityUnit.Quote, Perpetual: true)),
            new(Venue.Gate, "BTC_USDT", "BTC", "USDT", new("futures/usdt", .0001, Perpetual: true)),
            new(Venue.Gate, "BTC_USD", "BTC", "USD", new("futures/btc", 1, QuantityUnit.Quote, Perpetual: true)),
            new(Venue.Htx, "BTC-USDT", "BTC", "USDT", new("linear", .001, Perpetual: true)),
            new(Venue.Htx, "BTC-USD", "BTC", "USD", new("inverse", 100, QuantityUnit.Quote, Perpetual: true)),
            new(Venue.Htx, "BTC261225", "BTC", "USD", new("delivery", 100, QuantityUnit.Quote)),
            new(Venue.Kraken, "PF_XBTUSD", "BTC", "USD", new("derivatives", Perpetual: true)),
            new(Venue.Kraken, "PI_XBTUSD", "BTC", "USD", new("derivatives", 1, QuantityUnit.Quote, Perpetual: true)),
            new(Venue.Kraken, "FF_XBTUSD_261225", "BTC", "USD", new("derivatives")),
            new(Venue.Weex, "BTCUSDT", "BTC", "USDT", new("linear", Perpetual: true)),
            new(Venue.Lighter, "1", "BTC", "USD", new("perp", Perpetual: true)),
            new(Venue.Hyperliquid, "BTC", "BTC", "USDT", new("perp", Perpetual: true)),
            new(Venue.Hyperliquid, "xyz:XYZ100", "XYZ100", "USD", new("perp", Perpetual: true)),
        };
        var coverage = await Task.WhenAll(markets.Select(async market =>
        {
            var reading = await api.OpenInterest(market, 300, timer.Token);
            Console.WriteLine($"{market.Venue} {market.Symbol}/{market.Contract!.Product}: {reading.Points.Length} OI, {reading.Unit}, period {reading.Period}, " +
                $"first {reading.Points.FirstOrDefault().Time}, last {reading.Points.LastOrDefault().Time}, {reading.Status}");
            return reading.Points.Length > 0;
        }));
        foreach (var market in markets.Where(m => m.Symbol is "PF_XBTUSD" or "1"))
        {
            var trades = await api.RecentTrades(market, timer.Token);
            Console.WriteLine($"{market.Venue} {market.Symbol}: {trades.Length} recent trades, {trades.Count(t => t.Liquidated)} liquidation fills");
        }
        foreach (var market in markets.Where(ExchangeApi.GateExecutedLiquidationsSupported))
        {
            var records = await api.GateExecutedLiquidations(market, timer.Token);
            Console.WriteLine($"Gate {market.Symbol}: {records.Length} executed liquidation order events");
        }
        var daily = await api.OpenInterest(markets[2], 86400, timer.Token);
        Console.WriteLine($"Bybit BTCUSDT daily: {daily.Points.Length} OI, step {daily.Period}");
        var candleFixtures = new[] { Bitcoin with { Contract = null }, markets.Single(m => m.Symbol == "PI_XBTUSD"),
            markets.Single(m => m.Symbol == "1"), markets.Single(m => m is { Venue: Venue.Hyperliquid, Symbol: "BTC" }) };
        var volumeCoverage = await Task.WhenAll(candleFixtures.Select(async market =>
        {
            long before = (api.CandleNow / 60_000 + 1) * 60_000;
            var bars = await api.CandlePage(market, 60, before, 6, timer.Token);
            int supported = bars.Count(bar => ChartVolume.Amount(market, bar).HasValue);
            Console.WriteLine($"{market.Venue} {market.Symbol}: {supported}/{bars.Length} candle volumes in {ChartVolume.Unit(market)}");
            return supported != 0;
        }));
        var stream = new BinanceLiquidations();
        var connections = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sample = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int executions = 0;
        var leases = new[] { "BTC", "ETH", "XRP", "SOL" }.Select(asset => stream.Watch(
            Bitcoin with { Symbol = asset + "USDT", Base = asset },
            _ => { Interlocked.Increment(ref executions); sample.TrySetResult(true); },
            status => { if (status.Contains("поток открыт")) connections.TrySetResult(true); })).ToArray();
        try { await Task.WhenAny(sample.Task, Task.Delay(25_000, timer.Token)); }
        finally { foreach (var lease in leases) lease.Dispose(); }
        Console.WriteLine($"Binance forceOrder: connected={connections.Task.IsCompletedSuccessfully}, exact fills observed={executions}");
        return coverage.All(available => available) && volumeCoverage.All(available => available) && daily.Points.Length > 0 &&
            connections.Task.IsCompletedSuccessfully ? 0 : 1;
    }

    private sealed class IndicatorRequests : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/openInterestHist", StringComparison.Ordinal))
            {
                Started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { Canceled.TrySetResult(); }
            }
            return new(HttpStatusCode.NotFound);
        }
    }

    private static T Field<T>(object owner, string name, BindingFlags flags = Private) =>
        (T)owner.GetType().GetField(name, flags)!.GetValue(owner)!;
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); }
}

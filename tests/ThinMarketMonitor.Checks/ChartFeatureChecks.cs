using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
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
        var startingSizes = new[] { 90d, ChartPaneLayout.InterestMinimum, 100d };
        InterestLayer[] panes = [new(Venue.Binance, null, null), new(Venue.Gate, null, null)];
        var resized = ChartPaneLayout.Resize(startingSizes, [Venue.Binance, Venue.Gate], 1, -40, null);
        double chartHeight = startingSizes.Sum() + panes.Length * ChartPaneLayout.DividerHeight;
        var restoredSizes = ChartPaneLayout.Measure(chartHeight, panes, resized);
        var enlarged = ChartPaneLayout.Measure(chartHeight + 80, panes, resized);
        Check(resized is { Valid: true } && Math.Abs(restoredSizes[0] - 50) < .001 &&
            Math.Abs(restoredSizes[1] - ChartPaneLayout.InterestMinimum) < .001 &&
            Math.Abs(restoredSizes[2] - 140) < .001 && enlarged[2] > restoredSizes[2],
            "resizing past the adjoining pane's minimum adjusts the next pane and proportions work in larger windows");
        CheckPriceAndTimeTicks();
        CheckRulerTurnover();
        CheckOverviewIndicatorSettings();
        CheckCandleRenderingData();
        Console.WriteLine("Chart drawing, grid marks, profiles, live volume joins, ruler turnover and saved indicator options passed.");
    }

    private static void CheckRulerTurnover()
    {
        Check(new[] { 0, .001, 995, 12_500, 3_450_000, 1_230_000_000, 999_999 }
            .Select(value => ChartVolume.CompactUsd(value)).SequenceEqual(["0", "<0,01", "995", "12,5k", "3,45M", "1,23B", "1M"]),
            "ruler turnover abbreviates thousands, millions and billions using the chart's number format");
        const long interval = 60_000;
        Candle[] bars =
        [
            new(Now, 10, 11, 9, 10, 110, 5),
            new(Now + interval, 10, 13, 9, 12, 120),
            new(Now + 2 * interval, 12, 13, 11, 12, BaseVolume: 2),
            new(Now + 3 * interval, 12, 13, 11, 12),
            new(Now + 5 * interval, 12, 13, 11, 12, 14, ObservedVolume: true),
            new(Now + 6 * interval, 12, 13, 11, 12, 0)
        ];
        Check(ChartVolume.MeasureTurnover(Bitcoin, bars, interval, Now + 1, Now + interval + 1) == (230d, false, false) &&
            ChartVolume.MeasureTurnover(Bitcoin with { Quote = "USDC" }, bars, interval, Now + interval + 1, Now + 1) == (230d, false, false),
            "the ruler includes both boundary candles in either direction and prefers quote turnover");
        Check(ChartVolume.MeasureTurnover(Bitcoin, bars, interval, Now + 2 * interval, Now + 2 * interval) == (24d, true, false) &&
            ChartVolume.MeasureTurnover(Bitcoin, bars, interval, Now + 2 * interval, Now + 5 * interval + 1) == (38d, true, true),
            "base-volume estimates and missing or sampled candles are marked separately");
        Check(ChartVolume.MeasureTurnover(Bitcoin, bars, interval, Now + 6 * interval, Now + 6 * interval) == (0d, false, false) &&
            ChartVolume.MeasureTurnover(Bitcoin with { Quote = "EUR" }, bars, interval, Now, Now).Usd == null &&
            ChartVolume.MeasureTurnover(Bitcoin, bars, interval, Now + 8 * interval, Now + 9 * interval).Usd == null,
            "reported zero is distinct from an unsupported quote or a selection without candles");
    }

    private static void CheckOverviewIndicatorSettings()
    {
        var eth = Bitcoin with { Symbol = "ETHUSDT", Base = "ETH" };
        var legacy = new
        {
            SchemaVersion = new Settings().SchemaVersion,
            OverviewChoice = new CellChoice(Bitcoin.Key, false),
            ChartIndicators = new Dictionary<string, ChartIndicatorOptions>
            {
                [Bitcoin.Key] = new(false, Profile: true, Liquidations: true,
                    Interest: new() { [Venue.Binance] = Bitcoin.Key, [Venue.Gate] = "" }, InterestUsd: true)
            }
        };
        var settings = Settings.Deserialize(JsonSerializer.Serialize(legacy));
        using var http = new HttpClient();
        var features = new ChartFeatures(settings, new ExchangeApi(http), _ => null, () => { });
        var shared = features.Options(eth.Key, overview: true);
        Check(shared is { Volume: false, Profile: true, Liquidations: true, InterestUsd: true } &&
            shared.Interest!.Values.SequenceEqual(["", ""]) &&
            features.Options(Bitcoin.Key, overview: true).Interest![Venue.Binance] == Bitcoin.Key,
            "migration shares the saved Overview preferences while keeping the exact OI contract scoped to its market");
        features.SetOptions(eth.Key, true, shared with { Interest = new() { [Venue.Binance] = eth.Key } });
        var reopened = Settings.Deserialize(JsonSerializer.Serialize(settings));
        var restored = new ChartFeatures(reopened, new ExchangeApi(http), _ => null, () => { });
        Check(restored.Options(eth.Key, true).Interest![Venue.Binance] == eth.Key &&
            restored.Options(Bitcoin.Key, true).Interest![Venue.Binance] == Bitcoin.Key &&
            restored.Options(Bitcoin.Key, false).Interest!.ContainsKey(Venue.Gate) &&
            !restored.Options(eth.Key, false).HasDetails,
            "global source removal and both exact choices survive reload without copying settings into Signals");
    }

    private static void CheckPriceAndTimeTicks()
    {
        double wide = ChartAxis.NiceStep(.00223 - .00114, 20);
        double zoomed = ChartAxis.NiceStep(.00194 - .00155, 20);
        Check(Math.Abs(wide - .0001) < 1e-12 && Math.Abs(zoomed - .00002) < 1e-12 &&
            ChartAxis.NumericTicks(.001777, .001824, zoomed).Select(tick => ChartAxis.PriceLabel(tick, zoomed))
                .SequenceEqual(["0,00178", "0,00180", "0,00182"]),
            "price zoom changes to a finer round step and the marks stay on its multiples");
        Check(ChartAxis.NumericTicks(-3.1, 2.99, 2).SequenceEqual([-2d, 0d, 2d]),
            "price marks keep the same origin through zero");

        var halfHourZone = TimeZoneInfo.CreateCustomTimeZone("UTC+05:30", TimeSpan.FromMinutes(330), "UTC+05:30", "UTC+05:30");
        long start = new DateTimeOffset(2026, 11, 25, 3, 4, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        var times = ChartAxis.TimeTicks(start, start + 14_400_000, 60_000, 4, halfHourZone);
        Check(times.Select(mark => mark.Label).SequenceEqual(["09:00", "10:00", "11:00", "12:00"]) &&
            times.SequenceEqual(ChartAxis.TimeTicks(start + 660_000, start + 15_060_000, 60_000, 4, halfHourZone)),
            "clock grid follows local round hours and keeps the same timestamps when the chart pans");
        var begin = new DateTimeOffset(2026, 1, 17, 0, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 5, 22, 0, 0, 0, TimeSpan.Zero);
        var months = ChartAxis.TimeTicks(begin.ToUnixTimeMilliseconds(), end.ToUnixTimeMilliseconds(), 86_400_000, 4, TimeZoneInfo.Utc);
        Check(months.Select(mark => mark.Label).SequenceEqual(["Mar'26", "May'26"]) &&
            months.All(mark => DateTimeOffset.FromUnixTimeMilliseconds(mark.Time).Day == 1),
            "multi-month marks land on calendar boundaries");
        var moment = new DateTimeOffset(2026, 11, 23, 13, 41, 15, TimeSpan.FromHours(3));
        Check(ChartAxis.DateLabel(moment) == "Mon 23 Nov'26 13:41" && ChartAxis.DateLabel(moment, true) == "Mon 23 Nov'26 13:41:15",
            "timestamp labels match the requested English date format and retain seconds on short timeframes");
    }

    private static void CheckCandleRenderingData()
    {
        var oi = InterestCandle.Build([new(60_000, 12), new(90_000, 11), new(120_000, 18), new(145_000, 14),
            new(240_000, 8)], 60_000, 65_000);
        Check(oi is [{ Open: 12, High: 12, Low: 11, Close: 11 }, { Open: 11, High: 18, Low: 11, Close: 14 },
            { Time: 240_000, Open: 8, Close: 8 }], "OI candles contain actual sampled extrema and leave missing periods empty");
        var pixels = new CandlePixels();
        pixels.Reset(.2, 2);
        pixels.Add(new(1.01, 10, 12, 9, 11, 3, false));
        pixels.Add(new(1.22, 11, 15, 8, 13, 7, true));
        pixels.Add(new(1.51, 13, 14, 12, 14, null, false));
        Check(pixels.Columns is [{ Open: 10, High: 15, Low: 8, Close: 13, Volume: 10, Observed: true }, { Volume: null }]
            && pixels.Known == 2 && pixels.Total == 3, "dense display columns preserve actual extrema, open, close and volume");
        pixels.Reset(.15, 1);
        for (int i = 0; i < 6000; i++) pixels.Add(new(i * .15, 1, 2, .5, 1, null, false));
        Check(pixels.Total == 6000 && pixels.Columns.Count <= 901, "far zoom limits the rendered bars to device pixels");
        var live = new LiveCandles(60);
        live.Add(new("BTCUSDT", 61_000, 2));
        var display = live.DisplaySnapshot();
        var retained = live.Snapshot();
        live.Add(new("BTCUSDT", 62_000, 3));
        Check(ReferenceEquals(display, live.DisplaySnapshot()) && display[0].Close == 3 && retained[0].Close == 2,
            "a live tick replaces only the updated bar in the display while independent snapshots stay immutable");
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
            AssertNavigationLayout();
            Check(new[] { signals, overview, preferences }.All(b => b.Content is Viewbox && b.IsTabStop &&
                !string.IsNullOrEmpty(AutomationProperties.GetName(b))) && AutomationProperties.GetItemStatus(signals) == "Активный раздел",
                "center navigation has accessible icon-only buttons and an active state");
            window.Width -= 100;
            window.UpdateLayout();
            AssertNavigationLayout();

            var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var bars = new[] { new Candle(time - 60_000, 10, 12, 9, 11, 50), new Candle(time, 11, 13, 10, 12, 75) };
            var plot = cell.Plot;
            plot.Set(bars);
            var cursor = CheckCrosshair(plot);
            SetCrosshair(plot, new Point(plot.ActualWidth - 2, cursor.Y));
            Check(!HasCrosshair(plot), "the crosshair hides over the price scale");
            SetCrosshair(plot, cursor);
            plot.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
            Check(!HasCrosshair(plot), "leaving a signal chart clears the crosshair");
            CheckChartGridPan(plot);
            plot.Set(bars, reset: true);
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
            var binanceItem = featureMenu.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Binance"));
            Check(binanceItem.Icon is Image && !featureMenu.ContextMenu.Items.OfType<MenuItem>()
                .Any(item => Equals(item.Header, "Открытый интерес в USD")), "the indicator menu shows source logos and leaves currency to the panes");
            ClickMenu(binanceItem);
            featureMenu.ContextMenu.IsOpen = false;
            await requests.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(settings.ChartIndicators[Bitcoin.Key].Volume && settings.ChartIndicators[Bitcoin.Key].HasInterest &&
                !Field<TextBlock>(cell, "indicatorHint").IsVisible && plot.ActualHeight >= 48,
                "chart tools work in a 3×3 layout and start requests on demand");
            Click(button);
            var surface = Field<ChartSurface>(cell, "chartSurface");
            var currency = PaneButton(surface, "Единицы OI · Binance");
            var style = PaneButton(surface, "Вид графика OI · Binance");
            Check(AutomationProperties.GetItemStatus(style) == "Свечи", "new OI panes start with candles enabled");
            var initialRun = Field<CancellationTokenSource>(cell, "indicatorRun");
            Click(currency);
            Click(style);
            Check(Settings.Deserialize(JsonSerializer.Serialize(settings)).ChartIndicators[Bitcoin.Key].PresentationFor(Venue.Binance)
                is { Usd: true, Candles: false } && AutomationProperties.GetItemStatus(style) == "Линия" &&
                ReferenceEquals(initialRun, Field<CancellationTokenSource>(cell, "indicatorRun")),
                "a pane toggles currency and plot style without restarting data requests and saves the selection");
            Click(style);
            Click(featureMenu);
            ClickMenu(featureMenu.ContextMenu.Items.OfType<MenuItem>().First(item => Equals(item.Header, "Lighter")));
            featureMenu.ContextMenu.IsOpen = false;
            var sources = Field<IndicatorDisplay>(plot, "indicators").Interest;
            Check(sources.Select(layer => layer.Venue).SequenceEqual([Venue.Lighter, Venue.Binance]),
                "the newest enabled exchange is stacked above the previous OI pane");
            Check(AutomationProperties.GetItemStatus(PaneButton(surface, "Единицы OI · Lighter")) == "Единицы контракта",
                "other OI panes keep their own unit setting");
            var run = Field<CancellationTokenSource>(cell, "indicatorRun");
            var less = PaneButton(surface, "Опустить панель OI · Lighter");
            Check(less.IsEnabled && !PaneButton(surface, "Поднять панель OI · Lighter").IsEnabled,
                "the first pane offers the down control");
            Click(less);
            var reopenedSettings = Settings.Deserialize(JsonSerializer.Serialize(settings));
            Check(reopenedSettings.ChartIndicators[Bitcoin.Key].Interest!.Keys.Reverse().SequenceEqual([Venue.Binance, Venue.Lighter]) &&
                Field<IndicatorDisplay>(plot, "indicators").Interest[0].Venue == Venue.Binance &&
                ReferenceEquals(run, Field<CancellationTokenSource>(cell, "indicatorRun")),
                "reordering the OI panes persists the order within the chart without restarting subscriptions");
            Click(PaneButton(surface, "Поднять панель OI · Lighter"));
            plot.Set(bars);
            window.UpdateLayout();
            CheckInterestPanels(window, plot, bars);
            CheckPaneResize(window, plot, surface, settings);

            Click(PaneButton(surface, "Закрыть панель OI · Lighter"));
            Check(settings.ChartIndicators[Bitcoin.Key].Interest!.Keys.SequenceEqual([Venue.Binance]) &&
                Field<IndicatorDisplay>(plot, "indicators").Interest is [{ Venue: Venue.Binance }] &&
                settings.ChartPaneWeights.ContainsKey(Bitcoin.Key),
                "the close button removes just that OI pane and remembers its layout settings for reopening");

            Click(preferences);
            Check(AutomationProperties.GetItemStatus(preferences) == "Активный раздел", "bottom navigation marks the settings as active");
            CheckDiagnostics(window, settings);
            Click(overview);
            await requests.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var overviewChart = Field<ChartCell>(window, "overviewChart");
            Check(settings.Overview && !settings.OverviewDrawer && !cell.IsVisible &&
                AutomationProperties.GetItemStatus(overview) == "Активный раздел" &&
                Field<Border>(overviewChart, "drawingToolbar").IsVisible,
                "Overview expands the drawing tools, cancels unused indicator requests and keeps the drawer state");
            Check(HeaderButtons(cell).Any(button => Equals(button.Content, "Выбрать")) &&
                HeaderButtons(cell).Contains(Field<Button>(cell, "pin")) &&
                !HeaderButtons(overviewChart).Any(button => Equals(button.Content, "Выбрать")) &&
                !HeaderButtons(overviewChart).Contains(Field<Button>(overviewChart, "pin")),
                "choose and pin are present in Signals only");
            CheckQuickSources(window, settings);
            var overviewEth = new Market(Venue.Gate, "ETH_USDT", "ETH", "USDT", new("futures/usdt", Streaming: false, Perpetual: true));
            overviewChart.Assign(overviewEth, false);
            overviewChart.Plot.Set(bars);
            CheckPriceLabelsOnMove(overviewChart.Plot, bars);
            CheckCrosshair(overviewChart.Plot);
            var overviewTools = Field<Button>(overviewChart, "indicatorTools");
            ToggleOverview("Объём свечей");
            Check(Field<IndicatorDisplay?>(overviewChart.Plot, "indicators") == null,
                "the candle volume overlay can be disabled in Overview");
            var overviewView = typeof(CandlePlot).GetMethod("CurrentView", Private)!.Invoke(overviewChart.Plot, null)!;
            Check((string)typeof(CandlePlot).GetMethod("RulerTurnover", Private)!.Invoke(overviewChart.Plot,
                [overviewView, CandleCenter(bars[0]), CandleCenter(bars[1])])! == "Оборот 125 USD",
                "ruler turnover uses the visible selection even when the volume overlay is disabled");
            ToggleOverview("Профиль по сделкам");
            ToggleOverview("Binance");
            Click(PaneButton(Field<ChartSurface>(overviewChart, "chartSurface"), "Единицы OI · Binance"));
            ToggleOverview("Lighter");
            overviewChart.Assign(Bitcoin with { Contract = Bitcoin.Contract! with { Streaming = false } }, false);
            var overviewDisplay = Field<IndicatorDisplay>(overviewChart.Plot, "indicators");
            Check(overviewDisplay.Market.Key == Bitcoin.Key && overviewDisplay.Options is { Volume: false, Profile: true } &&
                overviewDisplay.Options.PresentationFor(Venue.Binance).Usd && !overviewDisplay.Options.PresentationFor(Venue.Lighter).Usd &&
                overviewDisplay.Interest.Select(layer => layer.Venue).SequenceEqual([Venue.Lighter, Venue.Binance]) &&
                settings.ChartIndicators[Bitcoin.Key] is { Volume: true, Profile: false },
                "Overview settings follow the next instrument without affecting a Signals chart of the same market");
            Click(PaneButton(Field<ChartSurface>(overviewChart, "chartSurface"), "Закрыть панель OI · Lighter"));
            overviewChart.Assign(overviewEth, false);
            overviewChart.Plot.Set(bars);
            Check(Field<IndicatorDisplay>(overviewChart.Plot, "indicators").Interest is [{ Venue: Venue.Binance }],
                "closing an OI panel applies to other instruments in Overview");
            CheckCrosshair(overviewChart.Plot);
            Click(signals);
            Check(!HasCrosshair(overviewChart.Plot), "hiding a chart clears its cursor overlay");
            Check(!settings.Overview && cell.Choice is { Key: var key, Pinned: true } && key == Bitcoin.Key,
                "switching back restores the signal grid and selection");

            typeof(MainWindow).GetMethod("EnterFullscreen", Private)!.Invoke(window, [cell, false]);
            window.UpdateLayout();
            Check(button.IsVisible && rail.IsVisible && featureMenu.IsVisible && bottom.Visibility == Visibility.Collapsed,
                "fullscreen expands the drawing tools and keeps indicators available");
            plot.Set(bars);
            CheckCrosshair(plot);
            typeof(MainWindow).GetMethod("ExitFullscreen", Private)!.Invoke(window, null);
            Check(rail.Visibility == Visibility.Collapsed && !HasCrosshair(plot),
                "leaving fullscreen restores the compact toolbar and clears the old crosshair");
            cell.Assign(Bitcoin with { Symbol = "ETHUSDT", Base = "ETH", Contract = Bitcoin.Contract! with { Streaming = false } }, true);
            Check(Field<ChartDrawing[]>(plot, "drawings").Length == 0 && (Field<IndicatorDisplay?>(plot, "indicators")?.Interest.Length ?? 0) == 0,
                "a different market shows its own annotations and indicator setup");

            void ToggleOverview(string caption)
            {
                Click(overviewTools);
                ClickMenu(overviewTools.ContextMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, caption)));
                overviewTools.ContextMenu.IsOpen = false;
            }
            Point CandleCenter(Candle candle) => new((double)overviewView.GetType().GetMethod("X")!
                .Invoke(overviewView, [(double)candle.Time + 30_000])!, 20);
        }
        finally { window.Close(); }

        void AssertNavigationLayout()
        {
            double center = navigation.TranslatePoint(new Point(navigation.ActualWidth / 2, 0), bottom).X;
            Check(Math.Abs(center - bottom.ActualWidth / 2) <= 1, "workspace buttons are centered at different window sizes");
            foreach (var button in new[] { signals, overview, preferences })
            {
                var shape = (System.Windows.Shapes.Path)((Viewbox)button.Content).Child;
                var bounds = shape.TransformToAncestor(button).TransformBounds(shape.Data.Bounds);
                Check(bounds.Width >= 16 && Math.Abs(bounds.Width - bounds.Height) <= .5 &&
                    Math.Abs(bounds.Left + bounds.Width / 2 - button.ActualWidth / 2) <= 1 &&
                    Math.Abs(bounds.Top + bounds.Height / 2 - button.ActualHeight / 2) <= 1,
                    "workspace icons keep their proportions and are centered in the buttons");
            }
        }
        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void ClickMenu(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static Button PaneButton(ChartSurface surface, string name) => surface.Children.OfType<StackPanel>()
        .SelectMany(panel => panel.Children.OfType<Button>()).Single(button => AutomationProperties.GetName(button) == name);

    private static Button[] HeaderButtons(ChartCell chart) => ((DockPanel)Field<Border>(chart, "headerFrame").Child)
        .Children.OfType<StackPanel>().SelectMany(panel => panel.Children.OfType<Button>()).ToArray();

    private static void CheckDiagnostics(MainWindow window, Settings settings)
    {
        window.UpdateLayout();
        var panel = Field<SettingsPanel>(window, "settingsPanel");
        var nav = Field<Button[]>(panel, "navigation");
        var pages = Field<FrameworkElement[]>(panel, "pages");
        var mode = ((StackPanel)pages[10]).Children.OfType<CheckBox>().Single();
        var formula = Field<List<TextBlock>>(panel, "diagnosticCopy").Single(block => block.Text.StartsWith("Спред ="));
        var catalog = (TextBlock)window.FindName("CatalogStatus");
        var log = Field<TextBlock>(panel, "result");
        var integration = (Border)window.FindName("IntegrationStatusHost");
        var colibri = Field<ColibriBridge>(window, "colibri");
        var report = typeof(ColibriBridge).GetMethod("Report", Private)!;
        var overview = Field<OverviewPanel>(window, "overviewPanel");
        var overviewStatus = Field<TextBlock>(overview, "status");
        overview.SetSource([Bitcoin], false, details: "Техническая причина");
        settings.ColibriEnabled = true;
        report.Invoke(colibri, ["Стакан переключён", false]);
        typeof(MainWindow).GetMethod("ShowCatalogStatus", Private)!.Invoke(window, ["26 монет после фильтра", "Детали источника", null]);
        Check(GroupOf(nav[0]) == "Сигналы" && GroupOf(nav[5]) == "Обзор" && GroupOf(nav[6]) == "Общие" &&
            pages[0].IsVisible && formula.Visibility == Visibility.Collapsed && !catalog.IsVisible && !integration.IsVisible &&
            overviewStatus.Visibility == Visibility.Collapsed && overviewStatus.ToolTip == null && !log.IsVisible,
            "normal mode hides routine diagnostics and settings are grouped by workspace");

        nav[10].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Toggle(true);
        nav[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(settings.DebugMode && Settings.Deserialize(JsonSerializer.Serialize(settings)).DebugMode &&
            catalog.IsVisible && catalog.Text == "26 монет после фильтра" &&
            ((TextBlock)window.FindName("StreamStatus")).IsVisible && integration.IsVisible && log.IsVisible && formula.IsVisible &&
            overviewStatus.Visibility == Visibility.Visible && Equals(overviewStatus.ToolTip, "Техническая причина"),
            "the settings switch immediately restores diagnostics and survives reload");
        nav[5].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!log.IsVisible, "the Overview settings do not display diagnostic counters from Signals");
        nav[10].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Toggle(false);
        report.Invoke(colibri, ["Точное соответствие не найдено", true]);
        Check(!settings.DebugMode && !catalog.IsVisible && integration.IsVisible && formula.Visibility == Visibility.Collapsed,
            "turning diagnostics off preserves actionable integration errors");
        settings.ColibriEnabled = false;
        report.Invoke(colibri, ["", false]);

        void Toggle(bool enabled)
        {
            mode.IsChecked = enabled;
            mode.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            window.UpdateLayout();
        }
        static string GroupOf(Button button) => AutomationProperties.GetName(((FrameworkElement)button.Parent).Parent);
    }

    private static void CheckQuickSources(MainWindow window, Settings settings)
    {
        var bar = (WrapPanel)window.FindName("OverviewQuickSources");
        Check(bar.Children.OfType<Button>().Select(item => (Venue)item.Tag)
            .SequenceEqual([Venue.Binance, Venue.Okx, Venue.Bybit, Venue.Gate, Venue.Mexc]), "the quick row initially has five major exchanges");
        var overview = Field<OverviewPanel>(window, "overviewPanel");
        overview.SelectSource(new(Venue.KuCoin, Segment.Futures));
        var button = bar.Children.OfType<Button>().Single(item => Equals(item.Tag, Venue.Bybit));
        settings.OverviewFuturesRules[Venue.Gate] = ListingRule.Exclude;
        settings.OverviewSpotRules[Venue.Htx] = ListingRule.Require;
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(settings.OverviewSource == new MarketSource(Venue.Bybit, Segment.Futures)
            && AutomationProperties.GetItemStatus(button) == "Текущий источник"
            && settings.OverviewFuturesRules[Venue.Gate] == ListingRule.Exclude && settings.OverviewSpotRules[Venue.Htx] == ListingRule.Require,
            "a quick button changes source in the current segment and preserves both sets of listing rules");
        overview.SelectSource(new(Venue.Kraken, Segment.Futures));
        overview.SelectSource(new(Venue.Bybit, Segment.Futures));
        var reopened = Settings.Deserialize(JsonSerializer.Serialize(settings));
        Check(reopened.QuickOverviewVenues().First() == Venue.Bybit && reopened.QuickOverviewVenues().Contains(Venue.Kraken),
            "source selection in either menu affects the persisted top five");
    }

    private static void CheckPriceLabelsOnMove(CandlePlot plot, Candle[] bars)
    {
        typeof(CandlePlot).GetMethod("SetPrice", Private)!.Invoke(plot, [8d, 8d]);
        (Point Origin, string Text)[]? baseline = null;
        foreach (double price in new[] { 12.4, 12.01, 12, 11.99, 11.6, 12.04 })
        {
            plot.Set([bars[0], bars[1] with { Close = price }]);
            plot.UpdateLayout();
            var view = typeof(CandlePlot).GetMethod("CurrentView", Private)!.Invoke(plot, null)!;
            var graph = (Rect)view.GetType().GetProperty("Plot")!.GetValue(view)!;
            var current = Drawings(VisualTreeHelper.GetDrawing(plot)).ToArray();
            var grid = current.OfType<GlyphRunDrawing>()
                .Where(run => run.GlyphRun.BaselineOrigin.X > graph.Right && run.GlyphRun.BaselineOrigin.Y <= graph.Bottom)
                .Select(run => (run.GlyphRun.BaselineOrigin, string.Join(",", run.GlyphRun.GlyphIndices))).ToArray();
            var badge = current.OfType<GeometryDrawing>().Select(item => item.Geometry).OfType<RectangleGeometry>()
                .Single(rect => Math.Abs(rect.Rect.Right - graph.Right + 1) < .001).Rect;
            Check(grid.Length >= 3 && badge.Right < graph.Right && (baseline == null || grid.SequenceEqual(baseline)),
                "grid labels and the market-price badge remain visible when consecutive live ticks cross a grid value");
            baseline ??= grid;
        }
        plot.Set(bars, reset: true);
    }

    private static Point CheckCrosshair(CandlePlot plot, Rect? pane = null)
    {
        plot.UpdateLayout();
        var view = typeof(CandlePlot).GetMethod("CurrentView", Private)!.Invoke(plot, null)!;
        var graph = pane ?? (Rect)view.GetType().GetProperty("Plot")!.GetValue(view)!;
        var cursor = new Point(graph.Left + graph.Width / 3, graph.Top + graph.Height * .6);
        foreach (var at in new[] { cursor, new Point(cursor.X + 5, cursor.Y + 3) })
        {
            SetCrosshair(plot, at);
            var drawings = Drawings(CrosshairVisual(plot).Drawing).ToArray();
            var lines = Lines(drawings).ToArray();
            var badges = drawings.OfType<GeometryDrawing>().Select(item => item.Geometry).OfType<RectangleGeometry>().ToArray();
            Check(lines.Any(line => line.StartPoint == at && line.EndPoint == new Point(graph.Left, at.Y)) &&
                lines.Any(line => line.StartPoint == at && line.EndPoint.X == at.X && line.EndPoint.Y < at.Y) &&
                badges.Any(badge => badge.Rect.Bottom == plot.ActualHeight && badge.Rect.Left <= at.X && at.X <= badge.Rect.Right) &&
                badges.Any(badge => badge.Rect.Left == graph.Right && badge.Rect.Top <= at.Y && at.Y <= badge.Rect.Bottom) &&
                drawings.OfType<GlyphRunDrawing>().Count() >= 2 &&
                plot.IsArrangeValid, "both guides follow the cursor without invalidating layout of the plot");
        }
        return cursor;
    }

    private static void CheckChartGridPan(CandlePlot plot)
    {
        var viewMethod = typeof(CandlePlot).GetMethod("CurrentView", Private)!;
        var moveMethod = typeof(CandlePlot).GetMethod("MoveGesture", Private)!;
        var stepMethod = typeof(CandlePlot).GetMethod("PriceGridStep", BindingFlags.Static | BindingFlags.NonPublic)!;
        var before = viewMethod.Invoke(plot, null)!;
        var type = before.GetType();
        var graph = (Rect)type.GetProperty("Plot")!.GetValue(before)!;
        var anchor = new Point(graph.Left + graph.Width / 2, graph.Top + graph.Height / 2);
        double step = (double)stepMethod.Invoke(null, [before])!;
        double low = (double)type.GetProperty("Low")!.GetValue(before)!;
        double high = (double)type.GetProperty("High")!.GetValue(before)!;
        double level = ChartAxis.NumericTicks(low, high, step).MinBy(tick => Math.Abs(tick - (low + high) / 2));
        var ticks = (ChartTimeTick[])typeof(CandlePlot).GetMethod("TimeGridTicks", Private)!.Invoke(plot, [before])!;
        long time = ticks[^1].Time;
        Check(GridLine(X(before), true) && GridLine(Y(before), false), "the plot draws the price and time grid at the round scale marks");
        var marketBadge = Drawings(VisualTreeHelper.GetDrawing(plot)).OfType<GeometryDrawing>()
            .Select(item => item.Geometry).OfType<RectangleGeometry>().Single(rect => Math.Abs(rect.Rect.Right - graph.Right + 1) < .001).Rect;
        SetCrosshair(plot, new Point(anchor.X, marketBadge.Top + marketBadge.Height / 2));
        var cursorBadges = Drawings(CrosshairVisual(plot).Drawing).OfType<GeometryDrawing>()
            .Select(item => item.Geometry).OfType<RectangleGeometry>().Select(rect => rect.Rect).ToArray();
        Check(cursorBadges.Any(rect => rect.Left == graph.Right && !rect.IntersectsWith(marketBadge)),
            $"cursor and live-price labels remain readable when their price levels coincide ({graph}, {marketBadge}, {string.Join(" | ", cursorBadges)})");

        double dx = -.12 * graph.Width, dy = .12 * graph.Height;
        Move("Pan", before, anchor, new Point(anchor.X + dx, anchor.Y + dy));
        var after = viewMethod.Invoke(plot, null)!;
        Check(Math.Abs(X(after) - X(before) - dx) < .001 && Math.Abs(Y(after) - Y(before) - dy) < .001 &&
            GridLine(X(after), true) && GridLine(Y(after), false) && (double)stepMethod.Invoke(null, [after])! == step,
            "dragging the plot moves both sets of grid lines by the same distance without rescaling");

        SetCrosshair(plot, new Point(anchor.X, graph.Top + .27 * graph.Height));
        string oldLabel = GlyphSignature();
        Move("Price", after, new Point(graph.Right + 6, anchor.Y), new Point(graph.Right + 6, anchor.Y - .60 * graph.Height));
        var zoomed = viewMethod.Invoke(plot, null)!;
        Check((double)stepMethod.Invoke(null, [zoomed])! < step && GlyphSignature() != oldLabel,
            "vertical scaling picks a finer step and updates the value under a stationary cursor");

        double X(object view) => (double)type.GetMethod("X")!.Invoke(view, [(double)time])!;
        double Y(object view) => (double)type.GetMethod("Y")!.Invoke(view, [level])!;
        bool GridLine(double position, bool vertical)
        {
            var start = vertical ? new Point(position, graph.Top) : new Point(graph.Left, position);
            var end = vertical ? new Point(position, graph.Bottom) : new Point(graph.Right, position);
            return Drawings(VisualTreeHelper.GetDrawing(plot)).OfType<GeometryDrawing>().Any(item =>
                item.Geometry is LineGeometry line && (line.StartPoint - start).Length < .001 && (line.EndPoint - end).Length < .001);
        }
        void Move(string action, object view, Point from, Point to)
        {
            var mode = Enum.Parse(typeof(CandlePlot).GetNestedType("Operation", BindingFlags.NonPublic)!, action);
            var drag = typeof(CandlePlot).GetNestedType("Drag", BindingFlags.NonPublic)!.GetConstructors().Single()
                .Invoke([mode, from, view, (double)CandlePlot.DefaultVisibleBars, MouseButton.Left, null]);
            moveMethod.Invoke(plot, [drag, to]);
            plot.UpdateLayout();
        }
        string GlyphSignature() => string.Join(",", Drawings(CrosshairVisual(plot).Drawing).OfType<GlyphRunDrawing>()
            .SelectMany(run => run.GlyphRun.GlyphIndices));
    }

    private static IEnumerable<Drawing> Drawings(Drawing? drawing) => drawing switch
    {
        DrawingGroup group => group.Children.SelectMany(Drawings),
        null => [],
        _ => [drawing]
    };

    private static IEnumerable<LineGeometry> Lines(IEnumerable<Drawing> drawings) => drawings.OfType<GeometryDrawing>()
        .Where(item => item.Pen?.DashStyle.Dashes.Count > 0).Select(item => item.Geometry).OfType<LineGeometry>();

    private static DrawingVisual CrosshairVisual(CandlePlot plot) => (DrawingVisual)VisualTreeHelper.GetChild(plot, 0);
    private static bool HasCrosshair(CandlePlot plot) => CrosshairVisual(plot).Drawing is { Children.Count: > 0 };
    private static void SetCrosshair(CandlePlot plot, Point? at) =>
        typeof(CandlePlot).GetMethod("SetCrosshairPosition", Private)!.Invoke(plot, [at]);

    private static void CheckPaneResize(Window window, CandlePlot plot, ChartSurface surface, Settings settings)
    {
        window.UpdateLayout();
        var grip = surface.Children.OfType<Thumb>().Single(thumb => AutomationProperties.GetName(thumb) == "Высота панели OI · Lighter");
        var original = plot.MeasurePanes(plot.ActualHeight);
        double y = grip.TranslatePoint(new Point(), surface).Y;
        Check(grip.IsVisible && grip.ActualHeight == ChartPaneLayout.DividerHeight &&
            Math.Abs(y - (7 + original[0])) < 1, "the draggable separator occupies the geometry between price and the first OI pane");
        Begin();
        Drag(-21);
        var preview = plot.MeasurePanes(plot.ActualHeight);
        Check(preview[1] > original[1] + 15 && preview[0] < original[0] - 15 &&
            !settings.ChartPaneWeights.ContainsKey(Bitcoin.Key), "dragging previews the height without writing settings");
        End(canceled: false);
        var loaded = Settings.Deserialize(JsonSerializer.Serialize(settings)).ChartPaneWeights[Bitcoin.Key];
        Check(loaded.Interest.ContainsKey(Venue.Lighter) &&
            Math.Abs(ChartPaneLayout.Measure(plot.ActualHeight - 24,
                Field<IndicatorDisplay>(plot, "indicators").Interest, loaded)[1] - preview[1]) < .001,
            "the finished drag restores the same height after a settings reload");
        Begin();
        Drag(15);
        End(canceled: true);
        Check(Math.Abs(plot.MeasurePanes(plot.ActualHeight)[1] - preview[1]) < .001,
            "cancelling a drag restores the last saved size");

        void Begin() => grip.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        void End(bool canceled) => grip.RaiseEvent(new DragCompletedEventArgs(0, 0, canceled) { RoutedEvent = Thumb.DragCompletedEvent });
        void Drag(double delta)
        {
            var gesture = Field<object>(surface, "resize");
            double start = (double)gesture.GetType().GetProperty("StartY")!.GetValue(gesture)!;
            typeof(ChartSurface).GetMethod("PreviewResize", Private)!.Invoke(surface, [grip, start + delta]);
        }
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
        var at = CheckCrosshair(plot, graphs[1]);
        SetCrosshair(plot, new Point(at.X, (graphs[0].Bottom + graphs[1].Top) / 2));
        Check(!HasCrosshair(plot), "the chart crosshair hides between OI panes");
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

        var priceView = view;
        var pan = ruler.Invoke(plot, [Center(0), view])!;
        var interestView = Field<object>(pan, "Item1", BindingFlags.Public | BindingFlags.Instance);
        var mode = Enum.Parse(typeof(CandlePlot).GetNestedType("Operation", BindingFlags.NonPublic)!, "InterestPan");
        var drag = typeof(CandlePlot).GetNestedType("Drag", BindingFlags.NonPublic)!.GetConstructors().Single()
            .Invoke([mode, Center(0), interestView, (double)CandlePlot.DefaultVisibleBars, MouseButton.Left, upper]);
        var move = typeof(CandlePlot).GetMethod("MoveGesture", Private)!;
        var priorScale = Ruler(0, "USD").Scale;
        double dy = -graphs[0].Height * 4;
        double dx = -graphs[0].Width * .05;
        move.Invoke(plot, [drag, Center(0) + new Vector(dx, dy)]);
        view = typeof(CandlePlot).GetMethod("CurrentView", Private)!.Invoke(plot, null)!;
        var viewType = view.GetType();
        var shifted = Ruler(0, "USD").Scale;
        Check(shifted.Low < priorScale.Low && Math.Abs((shifted.High - shifted.Low) - (priorScale.High - priorScale.Low)) < .01 &&
            Ruler(1, "USD").Scale == lowerScale && Equals(viewType.GetProperty("Low")!.GetValue(view), viewType.GetProperty("Low")!.GetValue(priceView)) &&
            (double)viewType.GetProperty("End")!.GetValue(view)! > (double)viewType.GetProperty("End")!.GetValue(priceView)!,
            "dragging an OI pane moves its value range and the common time axis while preserving the other vertical scales");
        move.Invoke(plot, [drag, Center(0)]);
        view = typeof(CandlePlot).GetMethod("CurrentView", Private)!.Invoke(plot, null)!;
        Check(Math.Abs(Ruler(0, "USD").Scale.Low - priorScale.Low) < .01, "dragging back restores the initial OI range");
        typeof(CandlePlot).GetMethod("StartInterestAxisDrag", Private)!.Invoke(plot, [onScale, view, true]);
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

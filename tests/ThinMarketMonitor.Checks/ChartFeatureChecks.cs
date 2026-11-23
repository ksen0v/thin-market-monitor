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
        var wrongQuote = perp with { Symbol = "BTCUSDC", Quote = "USDC" };
        Check(ChartFeatures.ComparableContracts(spot, Venue.Bybit, [perp, another, wrongQuote, perp with
            { Symbol = "BTCUSD", Contract = new("inverse", Unit: QuantityUnit.Quote, Perpetual: true) }]).Length == 2 &&
            ChartFeatures.ComparableContracts(inverse, Venue.Binance, [Bitcoin]).SequenceEqual([inverse]),
            "only exact quote, derivative kind and confirmed asset matches are candidates, and ambiguity remains explicit");

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
        Console.WriteLine("Chart drawing geometry, profile arithmetic, live volume joins, unit mapping and OI/liquidation sources passed.");
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
        try
        {
            var time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var bars = new[] { new Candle(time - 60_000, 10, 12, 9, 11, 50), new Candle(time, 11, 13, 10, 12, 75) };
            var plot = cell.Plot;
            plot.Set(bars);
            typeof(CandlePlot).GetMethod("SetPrice", Private)!.Invoke(plot, [-20d, 40d]);
            var view = typeof(CandlePlot).GetMethod("CurrentView", Private)!.Invoke(plot, null)!;
            var bounds = (Rect)view.GetType().GetProperty("Plot")!.GetValue(view)!;
            Check((double)view.GetType().GetMethod("Price")!.Invoke(view, [bounds.Bottom])! == -20,
                "WPF price view can pan below zero while candles keep their true prices");
            var button = Field<Button>(cell, "drawingTools");
            Click(button);
            var horizontal = button.ContextMenu.Items.OfType<MenuItem>().First();
            ClickMenu(horizontal);
            button.ContextMenu.IsOpen = false;
            typeof(CandlePlot).GetMethod("DrawingClick", Private)!.Invoke(plot, [new Point(bounds.Left + 10, bounds.Bottom - 3), view]);
            Check(settings.ChartDrawings[Bitcoin.Key] is [{ Start.Price: < 0 }], "a real drawing menu stores time/price anchors for this market");
            var original = settings.ChartDrawings[Bitcoin.Key][0];
            cell.SetFrame(300);
            Check(Field<ChartDrawing[]>(plot, "drawings").Single() == original &&
                Settings.Deserialize(JsonSerializer.Serialize(settings)).ChartDrawings[Bitcoin.Key].Single() == original,
                "drawing anchors survive timeframe and settings reload");
            var featureMenu = Field<Button>(cell, "indicatorTools");
            Click(featureMenu);
            ClickMenu(featureMenu.ContextMenu.Items.OfType<MenuItem>().First());
            featureMenu.ContextMenu.IsOpen = false;
            Click(featureMenu);
            ClickMenu(featureMenu.ContextMenu.Items.OfType<MenuItem>().First(item => Equals(item.Header, "Binance")));
            featureMenu.ContextMenu.IsOpen = false;
            await requests.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(settings.ChartIndicators[Bitcoin.Key].Volume && settings.ChartIndicators[Bitcoin.Key].HasInterest &&
                cell.HasIndicatorLayout && plot.ActualHeight >= 48, "chart tools work in a 3×3 layout and start requests on demand");

            Click(preferences);
            Check(AutomationProperties.GetItemStatus(preferences) == "Активный раздел" &&
                ((TextBlock)window.FindName("CatalogStatus")).IsVisible, "bottom settings opens with the network status still visible");
            Click(overview);
            await requests.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(settings.Overview && !settings.OverviewDrawer && !cell.IsVisible &&
                AutomationProperties.GetItemStatus(overview) == "Активный раздел", "changing workspace cancels unused indicator requests and keeps the drawer state");
            Click(signals);
            Check(!settings.Overview && cell.Choice is { Key: var key, Pinned: true } && key == Bitcoin.Key,
                "switching back restores the signal grid and selection");

            typeof(MainWindow).GetMethod("EnterFullscreen", Private)!.Invoke(window, [cell, false]);
            window.UpdateLayout();
            Check(button.IsVisible && featureMenu.IsVisible &&
                ((FrameworkElement)window.FindName("BottomNavigation")).Visibility == Visibility.Collapsed, "both tools remain available in fullscreen");
            typeof(MainWindow).GetMethod("ExitFullscreen", Private)!.Invoke(window, null);
            cell.Assign(Bitcoin with { Symbol = "ETHUSDT", Base = "ETH", Contract = Bitcoin.Contract! with { Streaming = false } }, true);
            Check(Field<ChartDrawing[]>(plot, "drawings").Length == 0 && !cell.HasIndicatorLayout, "a different market shows its own annotations and indicator setup");
        }
        finally { window.Close(); }

        static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void ClickMenu(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
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

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Private)!.GetValue(owner)!;
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static void Check(bool value, string description) { if (!value) throw new InvalidOperationException(description); }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThinMarketMonitor;

internal static class VolumeGrowthChecks
{
    private const long Start = 1_700_000_000_000;
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Market Market = new(Venue.Binance, "BTCUSDT", "BTC", "USDT");
    private static long Time(int seconds) => Start + seconds * 1000L;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Volume growth: " + message);
    }
    private static void Near(double actual, double expected, string message) =>
        Check(Math.Abs(actual - expected) <= Math.Max(1e-10, Math.Abs(expected) * 1e-10), message);
    private static VolumeImpulse Detector(int window = 15, int reference = 3600)
    {
        var detector = new VolumeImpulse([new(window, true, 100)], reference);
        detector.ResetCoverage(Start);
        return detector;
    }
    private static void Add(VolumeImpulse detector, int second, double amount, string id, int? received = null) =>
        detector.Observe(Market, new(Market.Symbol, Time(second), 1, amount, id), Time(received ?? second));

    public static void Run()
    {
        var sparse = Detector();
        Add(sparse, 0, 999_999, "excluded-left-edge");
        Add(sparse, 3600, 2, "base-right-edge");
        Add(sparse, 3615, 50, "current-right-edge");
        Add(sparse, 3616, 999_999, "after-evaluation-time");
        Check(sparse.Reading(15, Time(3620)).Phase == VolumePhase.Waiting, "incomplete preceding coverage is unknown");
        var reading = sparse.Reading(15, Time(3621));
        Near(reading.Current!.Value, 50, "current is (t-W,t]");
        Near(reading.Average!.Value, 2d * 15 / 3600, "$2/hour yields $0.008333 per 15 seconds");
        Near(reading.Growth!.Value, 599900, "$50 on the sparse baseline gives +599900%");
        Check(sparse.Evaluate(Market, Time(3621), true) is { Module: SignalModule.Volume, Seconds: 15, CurrentTurnover: 50 } signal &&
            Math.Abs(signal.Value - 599900) < 1e-6 && signal.Time == Time(3615), "signal carries percentage and event-time window");

        var twice = Detector(15, 60);
        Add(twice, 60, 100, "prior");
        Add(twice, 75, 50, "current");
        Near(twice.Reading(15, Time(81)).Average!.Value, 25, "baseline scales to the current window duration");
        Near(twice.Evaluate(Market, Time(81), true)!.Value, 100, "+100% means exactly twice average");
        Check(twice.Evaluate(Market, Time(82), true) == null, "same episode stays latched");

        var quiet = Detector();
        Check(quiet.Reading(15, Time(3621)) is { Phase: VolumePhase.ZeroBaseline, Growth: null } && quiet.Evaluate(Market, Time(3621), true) == null,
            "continuous silence is confirmed zero; zero divided by zero has no percentage");
        Add(quiet, 3615, 50, "first-trade");
        Check(quiet.Reading(15, Time(3621)) is { Phase: VolumePhase.ZeroBaseline, Current: 50 } &&
            quiet.Evaluate(Market, Time(3622), true) == null, "positive current with zero base never produces infinity");
        quiet.ResetCoverage(null);
        quiet.ProcessingGap(Time(100));
        Check(!quiet.HasCoverage(Time(10000)), "disconnect differs from confirmed quiet trading");
        quiet.ResetCoverage(Time(100));
        Add(quiet, 60, 1000, "replay");
        Check(quiet.Reading(15, Time(3720)).Phase == VolumePhase.Waiting &&
            quiet.Reading(15, Time(3721)).Phase == VolumePhase.ZeroBaseline, "reconnection requires all base and window seconds again");

        var outOfOrder = Detector(15, 60);
        Add(outOfOrder, 60, 100, "base");
        Add(outOfOrder, 72, 25, "buy", 73);
        Add(outOfOrder, 70, 25, "sell", 73);
        Add(outOfOrder, 72, 25, "buy", 73);
        Near(outOfOrder.Reading(15, Time(81)).Current!.Value, 50, "out-of-order buys and sells sum once, with ID deduplication");
        Add(outOfOrder, 75, 1, "too-late", 81);
        Check(outOfOrder.Reading(15, Time(82)).Phase == VolumePhase.Waiting, "late unknown trades invalidate completeness");
        var invalid = Detector(15, 60);
        invalid.Observe(Market, new(Market.Symbol, Time(10), 1, 0, "missing-size"), Time(10));
        Check(invalid.Reading(15, Time(81)).Phase == VolumePhase.Waiting, "uncountable trades invalidate coverage");

        var episodes = Detector(15, 60);
        var signals = new List<Signal>();
        for (int second = 1; second <= 201; second++)
        {
            Add(episodes, second, second is >= 61 and <= 75 or >= 181 and <= 195 ? 2 : 1, second.ToString());
            if (episodes.Evaluate(Market, Time(second), true) is { } next) signals.Add(next);
        }
        Check(signals.Count == 2 && signals[0].Time == Time(75) && signals[1].Time == Time(195),
            "new episode can fire after growth falls below hysteresis and the 30s cooldown");
        var gated = Detector(15, 60);
        Add(gated, 60, 100, "base"); Add(gated, 75, 50, "current");
        Check(gated.Evaluate(Market, Time(81), false) == null && gated.Evaluate(Market, Time(82), true) != null,
            "excluded markets retain measurements but cannot signal");
        foreach (int window in new[] { 1, 15, 60, 120, 300, 900, 3600 })
        {
            var independent = new VolumeImpulse([new(15, false, 100), new(window, true, 100)], 60);
            independent.ResetCoverage(Start);
            Add(independent, 60, 60, "base"); Add(independent, 60 + window, window * 2, "current");
            Check(independent.Evaluate(Market, Time(66 + window), true)?.Seconds == window,
                $"independent existing/custom {window}s window and a reference shorter than the window");
        }
        var overflow = Detector(15, 60);
        Add(overflow, 1, double.MaxValue, "large-1"); Add(overflow, 1, double.MaxValue, "large-2");
        Check(overflow.Reading(15, Time(81)).Phase == VolumePhase.Waiting, "non-finite totals cannot create a signal");
        var overload = Detector(15, 60);
        for (int i = 0; i <= VolumeImpulse.MaxRecentIds; i++) Add(overload, 1, 1, i.ToString());
        Check(overload.Reading(15, Time(81)).Phase == VolumePhase.Waiting, "bounded ID memory invalidates coverage on overload");

        Migration();
        SubscriptionContinuity();
        var rolling = new RollingVolume(7200);
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 100_000; i++) { rolling.Add(i, 1); rolling.Sum(i + 1, 3600); }
        Check(rolling.AllocatedBytes <= 131072 && rolling.Sum(100000, 3600) == 3600, "bounded ring/tree storage over long runs");
        Console.WriteLine($"Volume growth formula, boundaries, coverage, episodes, migration and subscription continuity passed; 100k updates/queries {timer.ElapsedMilliseconds} ms, {rolling.AllocatedBytes} bytes/market.");
    }

    private static void Migration()
    {
        var legacy = Settings.Deserialize("""{"VolumeModule":true,"VolumeThresholds":[{"Seconds":15,"Enabled":false,"Usd":800},{"Seconds":120,"Enabled":true,"Usd":9999},{"Seconds":3600,"Enabled":true,"Usd":1}],"VolumeFilters":[{"Seconds":300,"Enabled":true,"Usd":300}]}""");
        Check(!legacy.VolumeModule && legacy.VolumePercentMigrationPending && legacy.VolumeThresholds == null &&
            legacy.VolumeGrowthThresholds.Single(r => r.Seconds == 120) == new Threshold(120, true, 100) &&
            legacy.VolumeGrowthThresholds.Single(r => r.Seconds == 15) == new Threshold(15, false, 100) &&
            legacy.VolumeGrowthThresholds.Any(r => r.Seconds == 3600) && legacy.VolumeFilters[0].Usd == 300,
            "old USD thresholds never become percentages; windows, flags and global USD filter survive");
        legacy.VolumeModule = true; legacy.VolumePercentMigrationPending = false;
        legacy.VolumeGrowthThresholds = legacy.VolumeGrowthThresholds.Select(r => r.Seconds == 120 ? r with { Percent = 599900 } : r).ToArray();
        var roundTrip = Settings.Deserialize(JsonSerializer.Serialize(legacy));
        Check(roundTrip.VolumeModule && !roundTrip.VolumePercentMigrationPending &&
            roundTrip.VolumeGrowthThresholds.Single(r => r.Seconds == 120).Percent == 599900 &&
            !JsonSerializer.Serialize(roundTrip).Contains("\"VolumeThresholds\""), "new settings round-trip without repeated migration");
        var full = Settings.Deserialize(JsonSerializer.Serialize(new { VolumeModule = true,
            VolumeThresholds = Enumerable.Range(100, 24).Select(s => new VolumeRule(s, s % 2 == 0, 1234)).ToArray() }));
        var reloaded = Settings.Deserialize(JsonSerializer.Serialize(full));
        Check(Enumerable.Range(100, 24).All(s => reloaded.VolumeGrowthThresholds.Any(r => r.Seconds == s && r.Enabled == (s % 2 == 0))),
            "all 24 existing custom rows survive migration and the next save");
        Check(Settings.Deserialize("{}").VolumeReferenceSeconds == 3600 &&
            Settings.Deserialize("{\"VolumeReferenceSeconds\":0}").VolumeReferenceSeconds == 3600 &&
            Settings.Deserialize("{\"VolumeReferenceSeconds\":60}").VolumeReferenceSeconds == 60, "reference defaults and validation");
        var fresh = Settings.Deserialize("{}");
        Check(fresh.Thresholds.SequenceEqual(Settings.Defaults) && fresh.VolumeGrowthThresholds.SequenceEqual(Settings.DefaultVolumeGrowth),
            "a fresh profile still gets all four starting periods for each module");
    }

    private static void SubscriptionContinuity()
    {
        using var http = new HttpClient();
        using var streams = new MarketStreams(new ExchangeApi(http), [Market], [], [new(15, true, 100)], [Market], startFeeds: false);
        var volumes = Field<ConcurrentDictionary<string, VolumeImpulse>>(streams, "volumes");
        var original = volumes[Market.Key];
        original.ResetCoverage(Start); Add(original, 3600, 2, "base");
        var other = Market with { Base = "ETH", Symbol = "ETHUSDT" };
        streams.UpdateMarkets([Market, other]);
        streams.SetEligible([other]);
        Check(ReferenceEquals(original, volumes[Market.Key]) && original.Reading(15, Time(3621)).Phase == VolumePhase.Ready,
            "catalog additions and eligibility changes retain existing hour coverage");
        streams.UpdateMarkets([other]); streams.UpdateMarkets([Market, other]);
        Check(!ReferenceEquals(original, volumes[Market.Key]) && volumes[Market.Key].Readiness(Time(5000)) == VolumePhase.Waiting,
            "a removed/re-added market requires fresh coverage");
        using var shared = new MarketStreams(new ExchangeApi(http), [Market, other], [], [new(15, true, 100)], [Market], startFeeds: false);
        int owner = Field<ConcurrentDictionary<int, FeedStatus>>(shared, "feeds").Keys.Single();
        shared.UpdateMarkets([other]); shared.UpdateMarkets([Market, other]);
        var owned = typeof(MarketStreams).GetMethod("Owned", Flags)!.Invoke(shared, [Market.Key, owner]);
        Check(ReferenceEquals(owned, Field<ConcurrentDictionary<string, VolumeImpulse>>(shared, "volumes")[Market.Key]),
            "re-added market in a retained disconnected feed receives a new coverage owner");
    }

    public static async Task<int> Live()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var api = new ExchangeApi(http);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125));
        var catalogs = await Task.WhenAll(MarketSource.All.Select(async source =>
            (Source: source, Catalog: await api.LoadCatalog(source, timeout.Token))));
        var probes = new List<Market>();
        foreach (var (source, catalog) in catalogs)
        {
            if (catalog.Error != null) { Console.WriteLine(source.Label + ": " + catalog.Error); continue; }
            foreach (var product in catalog.Markets.Where(m => m.Streaming && UsdQuotes.Supported(m)).GroupBy(m => m.Contract?.Product))
                probes.Add(product.FirstOrDefault(m => m.Base == "BTC" && m.Quote == "USDT") ??
                    product.FirstOrDefault(m => m.Base == "BTC") ??
                    product.FirstOrDefault(m => m.Symbol == "ALPHA_140USDT") ?? product.First());
        }
        using var streams = new MarketStreams(api, probes, [], [new(15, true, 100)], probes, referenceSeconds: 60);
        Console.WriteLine($"Live volume probe: {probes.Count} spot/futures product groups; 60s reference + 15s current + 6s settlement.");
        var volumes = Field<ConcurrentDictionary<string, VolumeImpulse>>(streams, "volumes");
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(1000, timeout.Token);
            if (i is 14 or 44 or 74)
                Console.WriteLine($"{i + 1}s: {streams.Status().Count(s => s.Connected)}/{streams.Status().Length} feeds; " +
                    $"{streams.VolumeReadyCount(probes, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())}/{probes.Count} ready");
            if (i >= 82 && probes.All(m => volumes.TryGetValue(m.Key, out var detector) &&
                    detector.Readiness(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) != VolumePhase.Waiting)) break;
        }
        bool passed = catalogs.All(c => c.Catalog.Error == null);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var market in probes)
        {
            var reading = volumes[market.Key].Reading(15, now);
            bool sized = streams.HasSizedTrade(market, now);
            Console.WriteLine($"{market.Source.Label} {market.Contract?.Product} {market.Symbol}: {reading.Phase}, " +
                $"current={reading.Current:G6}, average={reading.Average:G6}, growth=" +
                (reading.Growth is { } growth ? $"{growth:G6}%" : "undefined") + $", fresh sized trade={sized}");
            passed &= reading.Phase != VolumePhase.Waiting;
        }
        return passed ? 0 : 1;
    }

    public static void Ui()
    {
        var settings = Settings.Deserialize("""{"VolumeModule":true,"VolumeThresholds":[{"Seconds":120,"Enabled":false,"Usd":5000}]}""");
        using var http = new HttpClient();
        using var colibri = new ColibriBridge(settings);
        int changes = 0;
        using var panel = new SettingsPanel(settings, new Updater(http), colibri, _ => changes++, () => { }, () => { });
        var window = new Window { Width = 760, Height = 750, Content = panel, Background = Terminal.Brush("Shell") };
        window.Show();
        void Page(int index) { typeof(SettingsPanel).GetMethod("SelectPage", Flags)!.Invoke(panel, [index]); window.UpdateLayout(); }
        T Find<T>(string name) where T : FrameworkElement => Descendants(panel).OfType<T>().Single(e => e.IsVisible && AutomationProperties.GetName(e) == name);
        Page(1);
        Check(Descendants(panel).OfType<TextBlock>().Any(t => t.IsVisible && t.Text.Contains("новые стартовые пороги")), "migration notice is visible");
        Find<Button>("Импульс объёма").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(settings.VolumeModule && !settings.VolumePercentMigrationPending, "user explicitly enables migrated percent module");
        Page(3);
        var baseline = Find<TextBox>("База импульса объёма, минуты");
        baseline.Text = "0"; Find<Button>("Применить базу импульса объёма").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(settings.VolumeReferenceSeconds == 3600, "invalid reference edit is not applied");
        baseline.Text = "30"; Find<Button>("Применить базу импульса объёма").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var threshold = Find<TextBox>("Порог за 2 мин, %");
        threshold.Text = "599900";
        threshold.RaiseEvent(new System.Windows.Input.KeyboardFocusChangedEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, 0, threshold, baseline)
            { RoutedEvent = UIElement.LostKeyboardFocusEvent });
        Check(settings.VolumeReferenceSeconds == 1800 && settings.VolumeGrowthThresholds.Single(r => r.Seconds == 120).Percent == 599900 && changes >= 3,
            "percent threshold and reference UI apply independently");
        if (Environment.GetEnvironmentVariable("TMM_VOLUME_SCREENSHOT") is { Length: > 0 } path)
        {
            window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path); png.Save(file);
        }
        var cell = new ChartCell(new ExchangeApi(http), 1, () => { }, () => { }, () => 1, () => new(System.Windows.Input.Key.None), () => [60]);
        cell.Assign(Market, false, new(Market, Time(75), 15, 100, SignalModule.Volume, 50, 25, 3600));
        string tooltip = Field<TextBlock>(cell, "label").ToolTip.ToString()!;
        Check(tooltip.Contains("100") && tooltip.Contains("50") && tooltip.Contains("25") && tooltip.Contains("без текущего окна"),
            "signal tooltip exposes percentage, current and average turnover");

        RemoveDefaults(2, Settings.Defaults);
        Check(settings.Thresholds.Length == 0 && settings.VolumeGrowthThresholds.Length == 5,
            "price defaults are removable without touching volume periods");
        RemoveDefaults(3, Settings.DefaultVolumeGrowth);
        var withoutDefaults = Settings.Deserialize(JsonSerializer.Serialize(settings));
        Check(withoutDefaults.Thresholds.Length == 0 && withoutDefaults.VolumeGrowthThresholds is [{ Seconds: 120, Percent: 599900 }],
            "removed defaults do not return after loading, and a custom volume period survives");
        Find<Button>("Удалить период 2 мин").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var withoutPeriods = Settings.Deserialize(JsonSerializer.Serialize(settings));
        Check(withoutPeriods.Thresholds.Length == 0 && withoutPeriods.VolumeGrowthThresholds.Length == 0 &&
            withoutPeriods.PriceModule && withoutPeriods.VolumeModule, "a saved empty list stays empty for both enabled modules");
        AddPeriod(3, "60");
        AddPeriod(2, "15");
        var restored = Settings.Deserialize(JsonSerializer.Serialize(settings));
        Check(restored.Thresholds is [{ Seconds: 15, Enabled: true }] &&
            restored.VolumeGrowthThresholds is [{ Seconds: 60, Enabled: true }], "a previously removed default period can be re-added to either module");
        window.Close();

        void RemoveDefaults(int page, IEnumerable<Threshold> defaults)
        {
            Page(page);
            foreach (var rule in defaults)
            {
                Find<Button>($"Удалить период {MainWindow.WindowName(rule.Seconds)}").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.UpdateLayout();
            }
        }

        void AddPeriod(int page, string seconds)
        {
            Page(page);
            Find<TextBox>("Период в секундах").Text = seconds;
            Descendants(panel).OfType<Button>().Single(button => button.IsVisible && Equals(button.Content, "Добавить период"))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Flags)!.GetValue(owner)!;
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

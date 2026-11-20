using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThinMarketMonitor;

internal static class BlacklistChecks
{
    private static readonly Market Btc = new(Venue.Binance, "BTCUSDT", "BTC", "USDT");
    private static readonly Market Eth = new(Venue.Binance, "ETHUSDT", "ETH", "USDT");
    private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException("Blacklist: " + message);
    }

    public static void Run()
    {
        var settings = Settings.Deserialize("{\"SignalBlacklist\":[\" btc \",\"BTC\",\"\",null,\"eth\"]}");
        Check(settings.SignalBlacklist.SequenceEqual(new[] { "BTC", "ETH" }), "migration normalizes duplicates and empty entries");
        Check(Settings.Deserialize("{}").SignalBlacklist.Length == 0 &&
            Settings.Deserialize("{\"SignalBlacklist\":null}").SignalBlacklist.Length == 0, "older settings start with an empty list");
        Check(settings.SignalBlocked(Btc) && settings.SignalBlocked(Btc with { Venue = Venue.KuCoin,
            Symbol = "XBTUSDTM", Base = ExchangeApi.Underlying(Venue.KuCoin, "XBT"), Contract = new("contract") }) &&
            settings.SignalBlocked(Btc with { Symbol = "BTCUSDC", Quote = "USDC" }), "coin exclusions span venues, segments and pairs");
        Check(!settings.SignalBlocked(Btc with { Symbol = "WBTCUSDT", Base = "WBTC" }) && !settings.BlockSignals(Btc),
            "similar tickers stay distinct and repeated additions are idempotent");
        var restored = Settings.Deserialize(JsonSerializer.Serialize(settings));
        Check(restored.SignalBlocked(Btc) && restored.SignalBlocked(Eth), "exclusions survive settings persistence");
    }

    public static async Task Ui()
    {
        using var handler = new SpreadChecks.QuotesHandler();
        using var http = new System.Net.Http.HttpClient(handler);
        var settings = new Settings { Grid = 3, Rules = new() { [Venue.Binance] = ListingRule.Require }, PriceModule = false,
            OverviewSource = Btc.Source, OverviewColumns = [] };
        using var window = new MainWindow(false, settings, testHttp: http);
        window.Show();
        var catalogs = Field<Dictionary<MarketSource, Catalog>>(window, "catalogs");
        var future = Btc with { Venue = Venue.Bybit, Contract = new("linear") };
        catalogs[Btc.Source] = new([Btc, Eth]);
        catalogs[future.Source] = new([future]);
        typeof(MainWindow).GetMethod("ApplyFilter", Flags)!.Invoke(window, [false]);
        var cells = Field<ChartCell[]>(window, "cells");
        foreach (var cell in cells)
        {
            cell.Activate(false);
            Check(Field<Button>(cell, "blacklist").Visibility == Visibility.Visible && !Field<Button>(cell, "blacklist").IsEnabled,
                "all nine signal cells have a disabled button when empty");
        }
        var overview = Field<ChartCell>(window, "overviewChart");
        Check(Field<Button>(overview, "blacklist").Visibility == Visibility.Collapsed, "overview does not expose a signals-only action");
        cells[0].Assign(Btc, pinned: true);
        cells[1].Assign(future, pinned: true);
        cells[2].Assign(Eth, pinned: true);
        var block = Field<Button>(cells[0], "blacklist");
        Check(block.IsEnabled && AutomationProperties.GetName(block).Contains("BTC"), "assigned coin is named in the button");
        Screenshot(window, "-cells.png");
        typeof(MainWindow).GetMethod("EnterFullscreen", Flags)!.Invoke(window, [cells[0], false]);
        block.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(settings.SignalBlocked(Btc) && !settings.SignalBlocked(Eth), "cell action persists the selected coin exclusion");
        Check(cells[0].Choice == null && cells[1].Choice == null && !cells[0].HasMarket && !cells[1].HasMarket &&
            cells[2].Choice?.Key == Eth.Key && Field<ChartCell?>(window, "fullscreenCell") == null,
            "all excluded cells including pinned and fullscreen cells clear; other coins remain");
        Check(Field<IReadOnlyList<Market>>(window, "candidates").SequenceEqual(new[] { Eth }) &&
            Field<IReadOnlyList<Market>>(window, "markets").SequenceEqual(new[] { Eth }) && settings.Cells[0] == null,
            "excluded coins leave candidate producers, eligible markets and saved chart choices");
        typeof(MainWindow).GetMethod("Place", Flags)!.Invoke(window,
            [new Signal(Btc, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 15, 10)]);
        Check(cells.All(c => c.Instrument?.Base != "BTC"), "queued or late signals cannot resurrect the excluded coin");
        typeof(MainWindow).GetMethod("PickMarket", Flags)!.Invoke(window, [0]);
        var picker = Field<MarketPicker>(window, "picker");
        Check(Field<IReadOnlyList<Market>>(picker, "markets").SequenceEqual(new[] { Eth }), "manual signal picker excludes the coin");
        typeof(MainWindow).GetMethod("CloseDialog", Flags)!.Invoke(window, null);
        var panel = Field<OverviewPanel>(window, "overviewPanel");
        panel.SetSource(ListingFilter.Apply(catalogs, settings.OverviewRule, settings.OverviewSource).Markets, visible: false);
        Check(Field<DataGrid>(panel, "table").Items.Cast<OverviewPanel.Row>().Any(r => r.Market.Base == "BTC"),
            "independent overview still includes the excluded coin");

        ((Button)window.FindName("SettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var options = Field<SettingsPanel>(window, "settingsPanel");
        typeof(SettingsPanel).GetMethod("SelectPage", Flags)!.Invoke(options, [8]);
        window.UpdateLayout();
        Screenshot(window, "-settings.png");
        var restore = Descendants(options).OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Вернуть BTC в сигналы");
        restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!settings.SignalBlocked(Btc) && Field<IReadOnlyList<Market>>(window, "markets").Any(m => m.Base == "BTC"),
            "settings action restores eligibility immediately");
        Check(cells[0].Choice == null && cells[1].Choice == null, "restoring eligibility leaves cleared cells ready for future signals");
        window.Close();
        await Task.Yield();
        Console.WriteLine("Signal blacklist persistence, nine cell actions, pinned/fullscreen clearing, picker, late signals and restoration passed.");
    }

    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Flags)!.GetValue(owner)!;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var item in Descendants(child)) yield return item;
        }
    }

    private static void Screenshot(Window window, string suffix)
    {
        if (Environment.GetEnvironmentVariable("TMM_BLACKLIST_SCREENSHOT") is not { Length: > 0 } prefix) return;
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(prefix + suffix);
        png.Save(file);
    }
}

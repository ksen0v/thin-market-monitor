using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ThinMarketMonitor;

internal static class PriceAlertChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Market Spot = new(Venue.KuCoin, "BTW-USDT", "BTW", "USDT");
    private static readonly Market Futures = new(Venue.KuCoin, "BTWUSDTM", "BTW", "USDT", new("contract"));

    internal static void Run()
    {
        const long now = 1_795_644_000_000;
        var book = new PriceAlertBook([]);
        Require(book.Add(Futures, .91, .9, now) && !book.Add(Futures, .91, .9, now) &&
            !book.Add(Futures, double.NaN, null, now) && book.Add(Futures, .89, .9, now), "valid levels are unique per market and price");
        var saved = book.Drain()!.Value.Levels;
        var restored = Settings.Deserialize(JsonSerializer.Serialize(new Settings
        {
            PriceAlerts = saved, PriceAlertPopup = false, PriceAlertSound = true, PriceAlertTone = SignalTone.Double
        }));
        Require(restored.PriceAlerts.SequenceEqual(saved) && !restored.PriceAlertPopup && restored.PriceAlertSound &&
            restored.PriceAlertTone == SignalTone.Double, "levels and dedicated channels survive settings persistence");
        book = new(restored.PriceAlerts);
        Observe(Spot, .95, now + 1);
        Observe(Futures with { Venue = Venue.Gate }, .95, now + 1);
        Observe(Futures, .95, now - 1);
        Observe(Futures, .905, now + 1);
        Require(book.Drain() == null, "other exchanges, other segments and pre-arm or same-side trades cannot trigger a level");
        Observe(Futures, .91, now + 2);
        Observe(Futures, .92, now + 3);
        Observe(Futures, .885, now + 4);
        var reached = book.Drain()!.Value;
        Require(reached.Hits.Select(hit => hit.Alert.ReachedPrice).SequenceEqual([.91, .885]) &&
            reached.Levels.All(alert => !alert.Active) && !book.Watches(Futures.Key),
            "a touch and a downward crossing both record the actual trade price and fire once");

        var id = saved[0].Id;
        book.SetEnabled(id, true, now + 10);
        book.SetEnabled(id, false, now + 11);
        Observe(Futures, .91, now + 12);
        Require(book.Drain()!.Value.Hits.Length == 0, "paused alerts ignore trades");
        book.SetEnabled(id, true, now + 13);
        Observe(Futures, .92, now + 14);
        Require(book.Drain()!.Value is { Hits.Length: 0 }, "rearming starts from the first new trade");
        Observe(Futures, .905, now + 16);
        Require(book.Drain()!.Value.Hits.Single().Alert.Id == id, "an alert can be rearmed for the opposite crossing");
        book.Remove(id);
        Require(book.Drain()!.Value.Levels.All(alert => alert.Id != id), "deleting a level removes it from persisted state");
        Console.WriteLine("Price alerts exact market identity, crossings, pause, rearming, removal and persistence passed.");

        void Observe(Market market, double price, long time) =>
            book.Observe(market, new(market.Symbol, time, price, Id: time.ToString()), now + 20);
    }

    internal static async Task<Button> Ui(MainWindow window, Settings settings, Market futures)
    {
        settings.PriceAlertPopup = false;
        var signal = Field<ChartCell[]>(window, "cells")[0];
        var overview = Field<ChartCell>(window, "overviewChart");
        Navigate("SignalsButton");
        await Place(signal, Spot, .27);
        Navigate("OverviewButton");
        var reached = await Place(overview, futures, .38);
        Invoke(window, "EnterFullscreen", overview, false);
        await Place(overview, futures, .59);
        Invoke(window, "ExitFullscreen");
        overview.Clear();
        Navigate("DensityButton");
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var book = Field<PriceAlertBook>(window, "priceAlerts");
        book.Observe(futures, new(futures.Symbol, now, reached.Price, Id: "exact-touch"), now);
        Invoke(window, "DrainPriceAlerts");
        Require(settings.PriceAlerts.Single(alert => alert.Id == reached.Id) is { Active: false, ReachedPrice: var price } &&
            price == reached.Price && settings.DensityWorkspace, "a closed chart can trigger in another workspace with popups off");
        Navigate("SignalsButton");
        var open = (Button)window.FindName("PriceAlertColibriButton");
        Require(open.IsVisible && open.IsEnabled && open.Tag is PriceAlert alert && alert.Id == reached.Id,
            "the exact terminal action is available in Signals even when price alert popups are off");
        Click((Button)window.FindName("PriceAlertsButton"));
        var options = Field<SettingsPanel>(window, "settingsPanel");
        var alertRows = Field<StackPanel>(options, "priceAlertRows");
        var spot = settings.PriceAlerts.Single(alert => alert.MarketKey == Spot.Key);
        Click(RowAction("Приостановить · BTW/USDT"));
        Require(!settings.PriceAlerts.Single(alert => alert.Id == spot.Id).Active, "settings can pause a saved alert");
        Click(RowAction("Удалить уровень · BTW/USDT"));
        Require(settings.PriceAlerts.All(alert => alert.Id != spot.Id), "settings can remove a saved alert");
        Invoke(window, "CloseDialog");
        await Channels(futures, reached with { Enabled = false, TriggeredAt = now, ReachedPrice = reached.Price });
        Console.WriteLine("Price alert buttons across Overview, Signals and fullscreen, background crossing and independent channels passed.");
        return open;

        void Navigate(string button) => Click((Button)window.FindName(button));

        Button RowAction(string label) => alertRows.Children.OfType<Border>().Select(row => (DockPanel)row.Child)
            .SelectMany(row => row.Children.OfType<StackPanel>()).SelectMany(panel => panel.Children.OfType<Button>())
            .Single(button => Equals(button.ToolTip, label));

        async Task<PriceAlert> Place(ChartCell cell, Market market, double y)
        {
            cell.Activate(false);
            cell.Assign(market, pinned: true);
            long bar = ExchangeApi.CandleStart(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 60_000);
            cell.Plot.Set([new(bar - 120_000, 95, 110, 90, 100), new(bar, 100, 110, 90, 100)]);
            Require(cell.Plot.CandleGapLabel(bar - 60_000) == "не загружено", "unavailable history is marked as unloaded");
            cell.Plot.MarkCandleCoverage(bar - 120_000, bar + 60_000);
            Require(cell.Plot.CandleGapLabel(bar - 60_000) == "нет свечи" && cell.Plot.CandleGapLabel(bar) == null,
                "an inspected gap is distinct from unavailable history and an actual candle");
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var view = Invoke(cell.Plot, "CurrentView")!;
            var plot = (Rect)view.GetType().GetProperty("Plot")!.GetValue(view)!;
            Invoke(cell.Plot, "SetCrosshairPosition", new Point(plot.Left + plot.Width / 2, plot.Top + plot.Height * y));
            var surface = Field<ChartSurface>(cell, "chartSurface");
            var add = Field<Button>(surface, "addAlert");
            var target = Field<(Rect Bounds, double Price)?>(surface, "alertTarget");
            Require(add.IsVisible && target is { } selected && selected.Bounds.Right < plot.Right &&
                selected.Bounds.Right >= plot.Right - 2, "the real plus button is reachable on the chart's right edge");
            var existing = settings.PriceAlerts.Select(alert => alert.Id).ToHashSet();
            Click(add);
            var level = settings.PriceAlerts.Single(alert => !existing.Contains(alert.Id));
            Require(level.MarketKey == market.Key && level.Source == market.Source && level.Price == target!.Value.Price,
                "clicking plus installs the displayed price for the exact market");
            Invoke(cell.Plot, "SetCrosshairPosition", (object?)null);
            Require(!add.IsVisible, "the plus button hides with the crosshair");
            return level;
        }
    }

    private static async Task Channels(Market market, PriceAlert alert)
    {
        var settings = new Settings { PriceAlertPopup = false, PriceAlertSound = true, PriceAlertTone = SignalTone.Double };
        var shown = new List<SignalNotice>();
        var tones = new ConcurrentQueue<SignalTone>();
        using var notices = new SignalNotifications(settings, Dispatcher.CurrentDispatcher,
            notice => { if (notice != null) shown.Add(notice); }, tones.Enqueue);
        var hit = new PriceAlertHit(alert, market);
        notices.PublishPriceAlert(hit);
        for (int i = 0; tones.IsEmpty && i < 80; i++) await Task.Delay(25);
        Require(tones.SequenceEqual([SignalTone.Double]) && shown.Count == 0, "separate price alert sound works with its popup off");
        settings.PriceAlertSound = false;
        settings.PriceAlertPopup = true;
        notices.SettingsChanged();
        notices.PublishPriceAlert(hit);
        Require(shown.Single() is { AlertMarket: var instrument, Details: var details } && instrument == market &&
            details.Contains(PriceAlert.Format(alert.Price), StringComparison.Ordinal),
            "the popup contains the reached price and the exact market for Colibri");

        Market? opened = null;
        using var popup = new SignalPopupWindow(instrument => opened = instrument, () => true);
        Invoke(popup, "UpdateNotice", shown[0]);
        var open = Field<Button>(popup, "openTerminal");
        Require(open.Visibility == Visibility.Visible, "price alert popup exposes the terminal action");
        Click(open);
        Require(opened == market, "the popup hands the exact triggered market to Colibri");
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, args);

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Price alerts: " + description);
    }
}

using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        book = new([]);
        Require(book.Add(Futures, .91, .9, now) && book.Add(Futures, .89, .9, now), "reference levels for moving a price alert");
        var levels = book.Drain()!.Value.Levels;
        id = levels[0].Id;
        Observe(Futures, .902, now + 2);
        Require(!book.Move(id, .89, null, now + 4) && !book.Move(id, 0, null, now + 4) &&
            book.Move(id, .88, .85, now + 4), "moving rejects duplicates and keeps the same active ID");
        book.Remove(levels[1].Id);
        Observe(Futures, .87, now + 3);
        Observe(Futures, .9, now + 5);
        var moved = book.Drain()!.Value;
        Require(moved.Hits.Length == 0 && moved.Levels.Single(alert => alert.Id == id) is
            { Price: .88, ArmedAt: now + 4, ReferencePrice: .902, Active: true },
            "moving resets the arm time and uses the latest observed trade as its baseline");
        Observe(Futures, .88, now + 6);
        Require(book.Drain()!.Value.Hits.Any(hit => hit.Alert.Id == id) && !book.Move(id, .87, null, now + 7),
            "the moved level only triggers at its new price and a consumed level cannot be moved");
        Console.WriteLine("Price alerts exact market identity, crossings, movement, rearming, removal and persistence passed.");

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
        reached = MoveLevel(overview.Plot, reached);
        Invoke(window, "EnterFullscreen", overview, false);
        var delete = await Place(overview, futures, .59);
        SelectLine(overview.Plot, delete);
        Invoke(overview.Plot, "ReleasePriceAlert");
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(overview.Plot)!, Environment.TickCount, Key.Delete)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        overview.Plot.RaiseEvent(key);
        Require(key.Handled && settings.PriceAlerts.All(level => level.Id != delete.Id) &&
            settings.PriceAlerts.Any(level => level.Id == reached.Id), "Delete removes only the selected price alert");
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
        Console.WriteLine("Price alert buttons, price badges, drag, Delete, background crossing and independent channels passed.");
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
            var bounds = target!.Value.Bounds;
            var pointer = new Point(bounds.Left + bounds.Width / 2, bounds.Bottom - 2);
            Invoke(surface, "TrackAlertPointer", pointer);
            var tracked = Field<(Rect Bounds, double Price)?>(surface, "alertTarget");
            Require(Field<Point?>(cell.Plot, "crosshairPosition") == pointer && tracked is { } moved &&
                moved.Price != target.Value.Price && moved.Bounds.Contains(pointer) && add.IsVisible,
                "the crosshair and level keep tracking while the pointer is over the plus button");
            var existing = settings.PriceAlerts.Select(alert => alert.Id).ToHashSet();
            Click(add);
            var level = settings.PriceAlerts.Single(alert => !existing.Contains(alert.Id));
            Require(level.MarketKey == market.Key && level.Source == market.Source && level.Price == tracked!.Value.Price,
                "clicking plus installs the displayed price for the exact market");
            Invoke(cell.Plot, "SetCrosshairPosition", (object?)null);
            Require(!add.IsVisible, "the plus button hides with the crosshair");
            return level;
        }

        PriceAlert MoveLevel(CandlePlot plot, PriceAlert initial)
        {
            var (_, bounds) = SelectLine(plot, initial);
            var view = Invoke(plot, "CurrentView")!;
            Invoke(plot, "MovePriceAlert", view, bounds.Height * .13);
            var preview = Field<double?>(plot, "priceAlertPreview");
            plot.InvalidateVisual();
            window.UpdateLayout();
            var marks = Descendants(VisualTreeHelper.GetDrawing(plot)).OfType<GlyphRunDrawing>()
                .Where(item => item.GlyphRun.BaselineOrigin.X > bounds.Right)
                .Select(item => new string(item.GlyphRun.Characters.ToArray()));
            Require(preview is { } price && marks.Contains(PriceAlert.Format(price)) &&
                settings.PriceAlerts.Single(alert => alert.Id == initial.Id).Price == initial.Price,
                "dragging previews the updated badge while the live level is saved only on release");
            Invoke(plot, "ReleasePriceAlert");
            var saved = settings.PriceAlerts.Single(alert => alert.Id == initial.Id);
            Require(saved.Price == preview && saved.Price != initial.Price && saved.Source == initial.Source && saved.Active,
                "the alert is moved in place on the same exact market");
            return saved;
        }

        static (Point Point, Rect Bounds) SelectLine(CandlePlot plot, PriceAlert alert)
        {
            var view = Invoke(plot, "CurrentView")!;
            var bounds = (Rect)view.GetType().GetProperty("Plot")!.GetValue(view)!;
            double y = (double)view.GetType().GetMethod("Y")!.Invoke(view, [alert.Price])!;
            var point = new Point(bounds.Left + bounds.Width * .4, y);
            Require(Invoke(plot, "SelectPriceAlert", point, view, false) is true, "the dashed line is selectable");
            return (point, bounds);
        }

        static IEnumerable<Drawing> Descendants(Drawing drawing) => drawing is DrawingGroup group
            ? group.Children.SelectMany(Descendants) : [drawing];
    }

    private static async Task Channels(Market market, PriceAlert alert)
    {
        var settings = new Settings { PriceAlertPopup = false, PriceAlertSound = true, PriceAlertTone = SignalTone.Double,
            Audio = new() { Master = 80, PriceAlert = 45 } };
        var shown = new List<SignalNotice>();
        var tones = new ConcurrentQueue<(SignalTone Tone, int Volume)>();
        using var notices = new SignalNotifications(settings, Dispatcher.CurrentDispatcher,
            notice => { if (notice != null) shown.Add(notice); }, (tone, volume, _) => tones.Enqueue((tone, volume)));
        var hit = new PriceAlertHit(alert, market);
        notices.PublishPriceAlert(hit);
        for (int i = 0; tones.IsEmpty && i < 80; i++) await Task.Delay(25);
        Require(tones.SequenceEqual([(SignalTone.Double, 36)]) && shown.Count == 0,
            "the independent price alert sound uses its mixer level with its popup off");
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

using System.Collections.Concurrent;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ThinMarketMonitor;

internal static class SignalNotificationChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Market Market = new(Venue.Weex, "TESTUSDT", "TEST", "USDT", new("linear"));

    public static async Task Run()
    {
        var settings = Settings.Deserialize("{}");
        Check(!settings.SignalPopup && !settings.SignalSound && !settings.SignalVoice, "all three channels default to off");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var price = new Signal(Market, now, 15, 5.126, LastPrice: 0.00001234);
        var volume = new Signal(Market, now, 60, 234.56, SignalModule.Volume, 12453.6, 3722.41, 3600);
        var volumeNotice = SignalNotice.From(volume);
        Check(volumeNotice.Title.Contains("TESTUSDT") && volumeNotice.Title.Contains("объёма") &&
            volumeNotice.Details.Contains("WEEX · Futures") && volumeNotice.Details.Contains("+234,56% к среднему за 1 мин") &&
            Numbers(volumeNotice.Details).Contains("12453,60 USD") && Numbers(volumeNotice.Details).Contains("3722,41 USD") &&
            volumeNotice.Details.Contains("предыдущие 1 ч"), "volume uses the confirmed turnover, moving baseline and growth");
        var priceNotice = SignalNotice.From(price);
        Check(priceNotice.Title.Contains("цены") && priceNotice.Details.Contains("+5,13% за 15 с") &&
            priceNotice.Details.Contains("1,234E-05 USDT") && priceNotice.Speech.Contains("15 секунд"), "price contains real change, trade price and window");

        var sounds = new ConcurrentQueue<string>();
        var spoken = new ConcurrentQueue<string>();
        var shown = new List<SignalNotice>();
        int active = 0, maxActive = 0;
        using var notifications = new SignalNotifications(settings, Dispatcher.CurrentDispatcher,
            notice => { if (notice != null) shown.Add(notice); },
            () => { sounds.Enqueue("sound"); throw new InvalidOperationException("Test output failure"); },
            (text, ct) =>
            {
                maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
                spoken.Enqueue(text);
                ct.WaitHandle.WaitOne(70);
                Interlocked.Decrement(ref active);
            });
        notifications.Publish(price);
        Check(shown.Count == 0 && sounds.IsEmpty && spoken.IsEmpty, "disabled channels produce nothing");
        settings.SignalSound = true;
        settings.SignalVoice = true;
        notifications.Publish(price with { Time = now - 30_000 });
        Check(sounds.IsEmpty, "old events are discarded before reaching any channel");
        notifications.Publish(price);
        notifications.Publish(price);
        await Until(() => spoken.Count == 1);
        Check(sounds.Count == 1 && spoken.Count == 1 && shown.Count == 0, "audio only, failed system sound and duplicate event");
        settings.SignalSound = false;
        notifications.SettingsChanged();
        notifications.Publish(volume);
        notifications.Publish(price with { Time = now + 1 });
        await Until(() => spoken.Count == 3);
        Check(maxActive == 1 && spoken.ElementAt(1).Contains("объёма") && spoken.ElementAt(2).Contains("цены") &&
            sounds.Count == 1, "volume and price queue in sequence, playback never overlaps");
        settings.SignalVoice = false;
        notifications.SettingsChanged();
        settings.SignalSound = true;
        notifications.Publish(volume with { Time = now + 1 });
        await Until(() => sounds.Count == 2);
        Check(spoken.Count == 3 && shown.Count == 0, "sound remains independent from voice and popup");

        settings.SignalSound = false;
        settings.SignalPopup = true;
        settings.Overview = true;
        using var http = new HttpClient();
        using var window = new MainWindow(false, settings, testHttp: http, testNotifications: notifications);
        var cells = (ChartCell[])typeof(MainWindow).GetField("cells", Instance)!.GetValue(window)!;
        for (int i = 0; i < settings.Grid * settings.Grid; i++)
            cells[i].Assign(Market with { Symbol = $"PIN{i}USDT", Base = $"PIN{i}" }, pinned: true);
        var choices = cells.Select(cell => cell.Choice).ToArray();
        var place = typeof(MainWindow).GetMethod("Place", Instance)!;
        place.Invoke(window, [volume with { Time = now + 2 }]);
        place.Invoke(window, [volume with { Time = now + 2 }]);
        Check(shown.Count == 1 && shown[0].Details == volumeNotice.Details &&
            cells.Select(cell => cell.Choice).SequenceEqual(choices) && settings.Overview && settings.OverviewChoice == null,
            "a confirmed event shows once in Overview while every signal cell is pinned");
        settings.SignalPopup = false;
        notifications.SettingsChanged();
        settings.BlockSignals(Market);
        settings.SignalPopup = true;
        place.Invoke(window, [price with { Time = now + 3 }]);
        Check(shown.Count == 1, "blacklisted events do not notify");

        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            window.Show();
            window.WindowState = WindowState.Minimized;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var previouslyFocused = Keyboard.FocusedElement;
            using var popup = new SignalPopupWindow();
            popup.Show(priceNotice);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var visible = (Window)typeof(SignalPopupWindow).GetField("window", Instance)!.GetValue(popup)!;
            Check(visible.IsVisible && !visible.IsActive && visible.Topmost && visible.Owner == null &&
                !visible.ShowInTaskbar && ReferenceEquals(Keyboard.FocusedElement, previouslyFocused) &&
                window.WindowState == WindowState.Minimized, "desktop notifications stay visible without activating the minimized app");
            popup.Show(null);
            Check(!visible.IsVisible, "popup is hidden when the timer expires or its channel is disabled");
            window.Close();
        }
        Console.WriteLine("Signal notification channels, exact payloads, freshness, deduplication, sequencing, and pinned/Overview routing passed.");
    }

    private static string Numbers(string text) => text.Replace("\u00a0", "").Replace("\u202f", "").Replace(" ", "").Replace("USD", " USD");

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static void Check(bool condition, string detail)
    {
        if (!condition) throw new InvalidOperationException(detail);
    }
}

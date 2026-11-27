using System.Collections.Concurrent;
using System.Media;
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

internal static class SignalNotificationChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Market Market = new(Venue.Weex, "TESTUSDT", "TEST", "USDT", new("linear"));

    public static async Task Run()
    {
        var settings = Settings.Deserialize("{}");
        Check(!settings.SignalPopup && !settings.SignalSound && !settings.SignalVoice, "all three channels default to off");
        Check(settings.PriceSignalTone == SignalTone.System && settings.VolumeSignalTone == SignalTone.System, "existing settings retain their system sound");
        settings.PriceSignalTone = SignalTone.Bell;
        settings.VolumeSignalTone = SignalTone.Double;
        settings.DensityTone = SignalTone.Soft;
        settings.Audio = new() { Master = 80, Price = 50, Volume = 80, Density = 90, Voice = 25 };
        var restored = Settings.Deserialize(JsonSerializer.Serialize(settings));
        Check(restored.PriceSignalTone == SignalTone.Bell && restored.VolumeSignalTone == SignalTone.Double &&
            restored.DensityTone == SignalTone.Soft && restored.Audio is { Master: 80, Voice: 25, Density: 90 } &&
            Settings.Deserialize("""{"Audio":{"Master":200,"Voice":-5}}""").Audio is { Master: 100, Voice: 0 } &&
            Settings.Deserialize("""{"PriceSignalTone":99}""").PriceSignalTone == SignalTone.System, "per-event sounds persist and invalid values fall back");
        CheckWaveforms();
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
        Check(RussianVoicePack.Preview == "би ти си ю эс ди ти на Гейт спот. Импульс 5 процентов за 15 секунд." &&
            SignalSpeech.Format(price with { Value = 1.56, Seconds = 1 }).Contains("2 процента за 1 секунду") &&
            volumeNotice.Speech.EndsWith("Рост объёма 235 процентов за 1 минуту.") &&
            !volumeNotice.Speech.Contains('·'), "speech names the market and summarizes the event without interface labels");
        CheckSpeechTemplates(price, volume);
        CheckSpeechEditor();
        await CheckMixerPage();
        await CheckSignalCooldown();

        var sounds = new ConcurrentQueue<SignalTone>();
        var effectLevels = new ConcurrentQueue<int>();
        var voiceLevels = new ConcurrentQueue<int>();
        var spoken = new ConcurrentQueue<string>();
        var shown = new List<SignalNotice>();
        int active = 0, maxActive = 0;
        using var notifications = new SignalNotifications(settings, Dispatcher.CurrentDispatcher,
            notice => { if (notice != null) shown.Add(notice); },
            (tone, gain, _) =>
            {
                effectLevels.Enqueue(gain);
                sounds.Enqueue(tone);
                throw new InvalidOperationException("Test output failure");
            },
            (text, gain, ct) =>
            {
                maxActive = Math.Max(maxActive, Interlocked.Increment(ref active));
                voiceLevels.Enqueue(gain);
                spoken.Enqueue(text);
                ct.WaitHandle.WaitOne(70);
                Interlocked.Decrement(ref active);
            });
        notifications.Publish(price);
        Check(shown.Count == 0 && sounds.IsEmpty && spoken.IsEmpty, "disabled channels produce nothing");
        notifications.PreviewSound(SignalTone.Soft, 75);
        await Until(() => sounds.Count == 1);
        Check(sounds.SequenceEqual(new[] { SignalTone.Soft }) && effectLevels.Single() == 60 && shown.Count == 0 && spoken.IsEmpty,
            "preview applies the master and channel levels even with notification channels disabled");
        notifications.PreviewVoice("Пример несохранённого текста.");
        await Until(() => spoken.Count == 1);
        Check(spoken.Single() == "Пример несохранённого текста." && voiceLevels.Single() == 20 && shown.Count == 0,
            "speech preview uses its own level with notification channels disabled");
        spoken.Clear();
        sounds.Clear();
        effectLevels.Clear();
        voiceLevels.Clear();
        settings.SignalSound = true;
        settings.SignalVoice = true;
        settings.PriceSpeech = new("Изменение {процент}.", 1);
        settings.VolumeSpeech = new("Оборот {оборот}.", 2);
        notifications.Publish(price with { Time = now - 30_000 });
        Check(sounds.IsEmpty, "old events are discarded before reaching any channel");
        notifications.Publish(price);
        notifications.Publish(price);
        await Until(() => spoken.Count == 1);
        Check(sounds.SequenceEqual(new[] { SignalTone.Bell }) && spoken.Single() == "Изменение 5,1 процента." && shown.Count == 0,
            "a price event uses its preset once and sound failure leaves speech working");
        settings.SignalSound = false;
        notifications.SettingsChanged();
        notifications.Publish(volume);
        notifications.Publish(price with { Time = now + 1 });
        await Until(() => spoken.Count == 3);
        Check(maxActive == 1 && spoken.ElementAt(1) == "Оборот 12453,6 доллара." && spoken.ElementAt(2) == spoken.First() &&
            sounds.Count == 1, "volume and price queue in sequence, playback never overlaps");
        settings.SignalVoice = false;
        notifications.SettingsChanged();
        settings.SignalSound = true;
        notifications.Publish(volume with { Time = now + 1 });
        await Until(() => sounds.Count == 2);
        Check(spoken.Count == 3 && shown.Count == 0 && sounds.SequenceEqual(new[] { SignalTone.Bell, SignalTone.Double }),
            "the volume event uses its own preset independently of speech and popup");
        Check(effectLevels.SequenceEqual([40, 64]) && voiceLevels.SequenceEqual([20, 20, 20]),
            "the master and individual gains are composed for each event without affecting the voice gain");
        settings.Densities.Sound = true;
        notifications.PublishDensity(new(Market, new(BookSide.Ask, 123), 50_000, 1, now, now, true, "#EFB863"));
        await Until(() => sounds.Count == 3);
        Check(sounds.Last() == SignalTone.Soft && effectLevels.Last() == 72, "book density has its own melody and channel");
        settings.Audio.Master = 0;
        notifications.PreviewSound(SignalTone.Double);
        notifications.PreviewVoice("Тихо.");
        await Task.Delay(250);
        Check(sounds.Count == 3 && voiceLevels.Count == 3, "the master slider at zero silences all previews");
        settings.Audio.Master = 80;

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
            var workArea = SystemParameters.WorkArea;
            double top = visible.Top, left = visible.Left;
            Check(Math.Abs(top - workArea.Top - 16) < 1 && Math.Abs(left + visible.ActualWidth - workArea.Right + 16) < 1,
                $"notifications appear at the top right of the work area ({left}, {top})");
            popup.Show(null);
            Check(!visible.IsVisible, "popup is hidden when the timer expires or its channel is disabled");
            popup.Show(volumeNotice);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(visible.IsVisible && visible.Top == top && visible.Left == left && !visible.IsActive,
                "a second notification of another kind reopens at the same position without taking focus");
            window.Close();
        }
        Console.WriteLine("Sound mixer, dark settings dropdowns, speech, notifications and pinned/Overview routing passed.");
    }

    private static async Task CheckSignalCooldown()
    {
        var settings = Settings.Deserialize("""{"SignalCooldownMinutes":5}""");
        Check(settings.SignalCooldownMinutes == 5 && Settings.Deserialize("{}").SignalCooldownMinutes == 0 &&
            Settings.Deserialize("""{"SignalCooldownMinutes":1441}""").SignalCooldownMinutes == 0,
            "repeat delay settings are persistent and opt-in");
        settings.SignalSound = true;
        settings.PriceSignalTone = SignalTone.Bell;
        settings.VolumeSignalTone = SignalTone.Double;
        var clock = new ManualSignalClock();
        var sounds = new ConcurrentQueue<SignalTone>();
        using var http = new HttpClient(new NoMarketData());
        using var notifications = new SignalNotifications(settings, Dispatcher.CurrentDispatcher, _ => { }, (tone, _, _) => sounds.Enqueue(tone));
        using var window = new MainWindow(false, settings, testHttp: http, testNotifications: notifications, signalClock: clock);
        var cells = (ChartCell[])typeof(MainWindow).GetField("cells", Instance)!.GetValue(window)!;
        var place = typeof(MainWindow).GetMethod("Place", Instance)!;
        var spot = new Market(Venue.Gate, "PEPE_USDC", "PEPE", "USDC");
        var future = new Market(Venue.Weex, "1000PEPEUSDT", "1000PEPE", "USDT", new("linear"), new(ListingAsset: "PEPE"));
        var independent = new Market(Venue.Hyperliquid, "@148", "PEPE", "USDC", Identifiers: new(ListingAsset: "hyperliquid:distinct-pepe"));
        var other = new Market(Venue.Gate, "BTC_USDC", "BTC", "USDC");
        long eventTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        Route(spot);
        Check(Placed(spot), "the first signal is placed immediately");
        clock.Advance(TimeSpan.FromMinutes(2));
        Route(future, SignalModule.Volume);
        Check(!Placed(future), "the same confirmed coin is suppressed across venues, quotes, segments and modules");
        Route(independent);
        Route(other);
        Check(Placed(independent) && Placed(other), "different coins and separate known identities are independent");
        await Until(() => sounds.Count == 3);

        clock.Advance(TimeSpan.FromMinutes(3) - TimeSpan.FromTicks(1));
        Route(future, SignalModule.Volume);
        Check(!Placed(future), "the entire selected delay is enforced");
        clock.Advance(TimeSpan.FromTicks(1));
        Route(future, SignalModule.Volume);
        Check(Placed(future), "the next impulse passes at the boundary and dropped signals do not extend the pause");
        await Until(() => sounds.Count == 4);
        Check(sounds.SequenceEqual(new[] { SignalTone.Bell, SignalTone.Bell, SignalTone.Bell, SignalTone.Double }),
            "suppressed impulses do not notify");

        settings.SignalCooldownMinutes = 0;
        Route(future, SignalModule.Volume);
        await Until(() => sounds.Count == 5);
        Check(sounds.Last() == SignalTone.Double, "turning off the delay takes effect without recreating signal feeds");

        void Route(Market market, SignalModule module = SignalModule.Price) =>
            place.Invoke(window, [new Signal(market, eventTime++, 30, 7, module)]);
        bool Placed(Market market) => cells.Any(cell => cell.Choice?.Key == market.Key);
    }

    private sealed class ManualSignalClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public void Advance(TimeSpan amount) => ticks += amount.Ticks;
    }

    private sealed class NoMarketData : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private static void CheckSpeechTemplates(Signal price, Signal volume)
    {
        var about = new SignalSpeechOptions("{ПРОЦЕНТ}. {рынок} {биржа}.");
        Check(SignalSpeech.Format(price, about) == "5 процентов. фьючерсы Уикс." &&
            SignalSpeech.Format(price, new("Текст без полей.")) == "Текст без полей." &&
            SignalSpeech.Format(price, new("{период}", 0)) != SignalSpeech.Format(price with { Seconds = 27 }, new("{период}", 0)),
            "templates speak only the supplied text and substitute the actual event values each time");
        var percent = new SignalSpeechOptions("{процент}");
        var example = price with { Value = 15.94 };
        Check(SignalSpeech.Format(example, percent) == "16 процентов" &&
            SignalSpeech.Format(example, percent with { PercentDecimals = 1 }) == "15,9 процента" &&
            SignalSpeech.Format(example, percent with { PercentDecimals = 2 }) == "15,94 процента" &&
            SignalSpeech.Format(example with { Value = 0.3 }, percent) == "меньше одного процента",
            "rounding is selectable and whole-percent speech does not report a real small impulse as zero");
        Check(SignalSpeech.Format(volume, new("{оборот}. Средний {средний_оборот} за {период_среднего}.")) ==
            "12453,6 доллара. Средний 3722,41 доллара за 1 час." &&
            SignalSpeech.Format(price with { LastPrice = null }, new("{цена}")) == "нет данных",
            "optional fields use available confirmation data");
        foreach (string invalid in new[] { " ", "{цена", "}}", "{{тикер}}", "{чужое_поле}", "{оборот}" })
        {
            Check(!SignalSpeech.TryFormat(price, new(invalid), out _, out var error) && error != null &&
                SignalSpeech.Format(price, new(invalid)) == SignalSpeech.Format(price), "invalid fields have inline validation and safe runtime defaults");
        }
        var alpha = price with { Market = new(Venue.Alpha, "ALPHA_123USDT", "ETH", "USDT") };
        Check(SignalSpeech.Format(alpha, new("{площадка}. {Тикер}.")) == "Бинанс Альфа. эфир ю эс ди ти.",
            "the speech fields use friendly names and actual assets for Binance Alpha");
    }

    private static void CheckSpeechEditor()
    {
        var settings = Settings.Deserialize("{}");
        int saved = 0;
        string? played = null;
        var editor = new SpeechTemplateEditor(settings, () => saved++, value => played = value);
        var template = Control<TextBox>("template");
        var modules = Control<ComboBox>("modules");
        var precision = Control<ComboBox>("precision");
        var fields = Control<ComboBox>("fields");
        var preview = Control<TextBlock>("example");
        var listen = Control<Button>("listen");
        var apply = Control<Button>("apply");
        Check(!listen.IsEnabled && !apply.IsEnabled && preview.Text.Contains("16 процентов"), "the text preview works without an installed voice");
        editor.CanPreview = true;
        template.Text = "Сигнал: X";
        template.Select(8, 1);
        fields.SelectedItem = fields.Items.Cast<SignalSpeech.Field>().Single(field => field.Name == "процент");
        Click("insert");
        precision.SelectedIndex = 1;
        Click("listen");
        Check(template.Text == "Сигнал: {процент}" && played == "Сигнал: 15,9 процента" &&
            settings.PriceSpeech == SignalSpeechOptions.Default && saved == 0, "insertion replaces the selected text and playback uses the unsaved draft");
        modules.SelectedIndex = (int)SignalModule.Volume;
        Check(fields.Items.Cast<SignalSpeech.Field>().Any(f => f.Name == "оборот") &&
            fields.Items.Cast<SignalSpeech.Field>().All(f => f.Name != "цена"), "the field picker reflects the signal kind");
        template.Text = "{цена}";
        Check(!listen.IsEnabled && !apply.IsEnabled && preview.Text.Contains("недоступно"), "invalid drafts cannot be saved or played");
        modules.SelectedIndex = (int)SignalModule.Price;
        Check(template.Text == "Сигнал: {процент}" && precision.SelectedIndex == 1 &&
            !editor.TrySavePending() && modules.SelectedIndex == (int)SignalModule.Volume && saved == 0,
            "switching modules preserves edits and Done returns to the invalid draft");
        template.Text = "{оборот}.";
        precision.SelectedIndex = 2;
        Check(editor.TrySavePending() && saved == 1, "Done saves both valid drafts together");
        var restored = Settings.Deserialize(JsonSerializer.Serialize(settings));
        Check(restored.PriceSpeech == new SignalSpeechOptions("Сигнал: {процент}", 1) &&
            restored.VolumeSpeech == new SignalSpeechOptions("{оборот}.", 2), "each template and rounding preference survives reload");
        modules.SelectedIndex = (int)SignalModule.Price;
        Click("reset");
        Click("apply");
        Check(saved == 2 && settings.PriceSpeech == SignalSpeechOptions.Default && settings.VolumeSpeech == restored.VolumeSpeech,
            "reset restores only the current event's defaults");

        T Control<T>(string name) => (T)typeof(SpeechTemplateEditor).GetField(name, Instance)!.GetValue(editor)!;
        void Click(string button) => Control<Button>(button).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }

    private static void CheckWaveforms()
    {
        var samples = SignalSound.Options.Where(option => option.Tone != SignalTone.System).Select(option =>
        {
            using var wave = SignalSound.CreateWave(option.Tone);
            using var player = new SoundPlayer(wave);
            player.Load();
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(wave.ToArray()));
        }).ToArray();
        Check(samples.Length >= 3 && samples.Distinct().Count() == samples.Length, "each custom sound is a distinct loadable wave");
        using var original = SignalSound.CreateWave(SignalTone.Bell);
        using var scaled = SignalSound.VolumeAdjustedWave(original, 25);
        var before = original.ToArray();
        var after = scaled!.ToArray();
        Check(before.AsSpan(0, 44).SequenceEqual(after.AsSpan(0, 44)) &&
            Enumerable.Range(0, (before.Length - 44) / 2).All(index =>
                BitConverter.ToInt16(after, 44 + index * 2) == BitConverter.ToInt16(before, 44 + index * 2) / 4),
            "the voice and system-wave volume path scales 16-bit PCM samples while preserving the wave header");
    }

    private static async Task CheckMixerPage()
    {
        var settings = new Settings();
        using var client = new HttpClient(new NoMarketData());
        using var colibri = new ColibriBridge(settings);
        var previewed = new List<(SignalTone Tone, int Level)>();
        int saved = 0;
        using var panel = new SettingsPanel(settings, new(client), colibri, _ => { }, () => { }, () => { },
            saveNotifications: () => saved++, previewSound: (tone, gain) => previewed.Add((tone, gain)));
        var window = new Window
        {
            Width = 745, Height = 650, Left = -3000, Top = -2000, Content = panel, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual
        };
        try
        {
            window.Show();
            window.UpdateLayout();
            var sections = Visuals(panel).OfType<StackPanel>().Where(item => AutomationProperties.GetName(item) == "Общие").Single();
            var general = Visuals(sections).OfType<Button>().ToDictionary(AutomationProperties.GetName);
            Check(general.ContainsKey("Уведомления") && general.ContainsKey("Уровни цены") && general.ContainsKey("Звуки"),
                "notifications and sound controls are under the shared settings group");
            general["Звуки"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var sliders = Visuals(panel).OfType<Slider>().Where(item => item.IsVisible).ToDictionary(AutomationProperties.GetName);
            Check(sliders.Count == 6, "the mixer presents a master level, four event levels, and independent voice level");
            sliders["Общая громкость"].Value = 70;
            sliders["Громкость · Голос"].Value = 25;
            sliders["Громкость · Импульс цены"].Value = 84;
            var list = Visuals(panel).OfType<ComboBox>().Single(item => item.IsVisible &&
                AutomationProperties.GetName(item) == "Мелодия · Импульс цены");
            list.SelectedValue = SignalTone.Double;
            var play = Visuals(panel).OfType<Button>().Single(item => item.IsVisible &&
                AutomationProperties.GetName(item) == "Прослушать · Импульс цены");
            play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(previewed.SequenceEqual([(SignalTone.Double, 84)]) && settings.Audio is { Master: 70, Voice: 25, Price: 84 } &&
                settings.PriceSignalTone == SignalTone.Double && saved > 0, "mixer controls update and preview the selected settings");
            CheckDarkMenu(list);
            general["Уведомления"].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            foreach (var control in Visuals(panel).OfType<ComboBox>().Where(control => control.IsVisible))
                CheckDarkMenu(control);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        finally { window.Close(); }

        static void CheckDarkMenu(ComboBox list)
        {
            list.ApplyTemplate();
            Check(list.Template.FindName("PART_Popup", list) is Popup { Child: Border border } &&
                border.Background == Terminal.Brush("Surface") &&
                list.ItemContainerStyle == (Style)Application.Current.FindResource("TerminalComboItem"),
                "the dropdown surface and its selectable rows use the shared terminal style");
        }

        static IEnumerable<DependencyObject> Visuals(DependencyObject node)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                yield return child;
                foreach (var item in Visuals(child)) yield return item;
            }
        }
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

using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ThinMarketMonitor;

internal sealed record SignalNotice(string Ticker, string Kind, string Details, string Speech, long Time, SignalModule? Module,
    Market? AlertMarket = null)
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public string Title => $"{Ticker} · {Kind}";

    public static SignalNotice From(Signal signal, SignalSpeechOptions? speech = null)
    {
        bool volume = signal.Module == SignalModule.Volume;
        string type = volume ? "Импульс объёма" : "Импульс цены";
        string window = MainWindow.WindowName(signal.Seconds);
        string percent = signal.Value.ToString("+0.00;-0.00;0.00", Russian);
        string change = volume ? $"{percent}% к среднему за {window}" : $"{percent}% за {window}";
        string details = $"{signal.Market.Source.Label}\n{change}";
        if (volume && signal.CurrentTurnover is { } current && signal.AverageTurnover is { } average)
        {
            details += $"\nОборот {Money(current)} USD · средний {Money(average)} USD";
            if (signal.ReferenceSeconds is { } reference) details += $"\nБаза · предыдущие {MainWindow.WindowName(reference)}";
        }
        if (!volume && signal.LastPrice is { } price && double.IsFinite(price))
            details += $"\nЦена {price.ToString("G12", Russian)} {signal.Market.Quote}";
        return new(signal.Market.Ticker, type, details, SignalSpeech.Format(signal, speech), signal.Time, signal.Module);
    }

    private static string Money(double value) => value.ToString("N2", Russian);

    public static SignalNotice From(DensityLevel level)
    {
        string details = $"{level.Market.Source.Label} · {level.SideLabel}\n" +
            $"≈ {Money(level.Usd)} USD · цена {level.Key.Price.ToString("G12", Russian)} {level.Market.Quote}\n" +
            $"{level.Distance.ToString("F2", Russian)}% от середины спреда";
        return new(level.Market.Ticker, "Плотность стакана", details, "", level.Seen, null);
    }

    public static SignalNotice From(PriceAlertHit hit)
    {
        var alert = hit.Alert;
        string details = $"{hit.Market.Source.Label}\nЦена {PriceAlert.Format(alert.ReachedPrice!.Value)} {alert.Quote}" +
            $" · уровень {PriceAlert.Format(alert.Price)}";
        return new(hit.Market.Ticker, "Ценовое оповещение", details, "", alert.TriggeredAt!.Value, null, hit.Market);
    }
}

internal sealed class SignalNotifications : IDisposable
{
    private const int MaxPending = 4;
    private const long MaximumAge = 20_000;
    private readonly Settings settings;
    private readonly TimeProvider clock;
    private readonly DispatcherTimer popupTimer;
    private readonly Action<SignalNotice?> showPopup;
    private readonly Action<SignalTone, int, CancellationToken> playSound;
    private readonly Action<string, int, CancellationToken> speak;
    private readonly SignalPopupWindow? popup;
    private readonly CancellationTokenSource stop = new();
    private readonly Queue<SignalNotice> visual = new(), audio = new();
    private readonly Dictionary<(string Market, SignalModule Module), long> last = [];
    private long pruneAt;
    private Task? audioWorker;
    private CancellationTokenSource? activeVoice;
    private (SignalTone Tone, int Volume)? preview;
    private SignalNotice? shown;
    private string? voicePreview;
    private bool disposed;

    public SignalNotifications(Settings settings, Dispatcher dispatcher, Action<SignalNotice?>? showPopup = null,
        Action<SignalTone, int, CancellationToken>? playSound = null, Action<string, int, CancellationToken>? speak = null,
        TimeProvider? clock = null,
        Action<Market>? openAlert = null)
    {
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
        this.playSound = playSound ?? SignalSound.Play;
        this.speak = speak ?? WindowsSignalVoice.Speak;
        popup = showPopup == null ? new SignalPopupWindow(openAlert, () => settings.ColibriEnabled) : null;
        this.showPopup = showPopup ?? popup!.Show;
        popupTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(5) };
        popupTimer.Tick += (_, _) => ShowNext();
    }

    public void Publish(Signal signal)
    {
        if (disposed || !(settings.SignalPopup || settings.SignalSound || settings.SignalVoice)) return;
        long now = Now;
        if (!Fresh(signal.Time, now)) return;
        if (now >= pruneAt)
        {
            foreach (var entry in last.Where(x => !Fresh(x.Value, now)).ToArray()) last.Remove(entry.Key);
            pruneAt = now + MaximumAge;
        }
        var key = (signal.Market.Key, signal.Module);
        if (last.TryGetValue(key, out long previous) && signal.Time <= previous) return;
        last[key] = signal.Time;

        Publish(SignalNotice.From(signal, settings.SpeechFor(signal.Module)));
    }

    public void PublishDensity(DensityLevel level)
    {
        if (disposed || !Fresh(level.Seen, Now) || !(settings.Densities.Popup || settings.Densities.Sound)) return;
        Publish(SignalNotice.From(level));
    }

    public void PublishPriceAlert(PriceAlertHit hit)
    {
        if (disposed || !Fresh(hit.Alert.TriggeredAt!.Value, Now)) return;
        Publish(SignalNotice.From(hit));
    }

    private void Publish(SignalNotice notice)
    {
        if (PopupEnabled(notice))
        {
            Enqueue(visual, notice);
            if (!popupTimer.IsEnabled) ShowNext();
        }
        if (SoundEnabled(notice) || VoiceEnabled(notice))
        {
            Enqueue(audio, notice);
            StartAudio();
        }
    }

    public void PreviewSound(SignalTone tone, int channelVolume = 100)
    {
        if (disposed) return;
        preview = (tone, settings.Audio.Apply(channelVolume));
        StartAudio();
    }

    public void PreviewVoice(string phrase)
    {
        if (disposed || string.IsNullOrWhiteSpace(phrase)) return;
        voicePreview = phrase;
        StartAudio();
    }

    private void StartAudio()
    {
        if (audioWorker is not { IsCompleted: false }) audioWorker = PlayPending();
    }

    public void SettingsChanged()
    {
        if (!settings.SignalPopup && !settings.Densities.Popup && !settings.PriceAlertPopup)
            visual.Clear();
        if (shown != null && !PopupEnabled(shown))
        {
            ShowNext();
        }
        if (!settings.SignalSound && !settings.SignalVoice && !settings.Densities.Sound && !settings.PriceAlertSound) audio.Clear();
        if (!settings.SignalVoice) activeVoice?.Cancel();
        popup?.UpdateAction();
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static bool Fresh(long time, long now) => time >= now - MaximumAge && time <= now + 3000;
    private bool PopupEnabled(SignalNotice notice) => notice.AlertMarket != null ? settings.PriceAlertPopup :
        notice.Module == null ? settings.Densities.Popup : settings.SignalPopup;
    private bool SoundEnabled(SignalNotice notice) => notice.AlertMarket != null ? settings.PriceAlertSound :
        notice.Module == null ? settings.Densities.Sound : settings.SignalSound;
    private bool VoiceEnabled(SignalNotice notice) => notice.Module != null && settings.SignalVoice;

    private static void Enqueue(Queue<SignalNotice> queue, SignalNotice notice)
    {
        if (queue.Count == MaxPending) queue.Dequeue();
        queue.Enqueue(notice);
    }

    private void ShowNext()
    {
        popupTimer.Stop();
        while (visual.TryDequeue(out var notice))
        {
            if (!PopupEnabled(notice) || !Fresh(notice.Time, Now)) continue;
            showPopup(shown = notice);
            popupTimer.Start();
            return;
        }
        showPopup(shown = null);
    }

    private async Task PlayPending()
    {
        while (!disposed)
        {
            if (preview is { } audition)
            {
                preview = null;
                var token = stop.Token;
                await Task.Run(() =>
                {
                    if (audition.Volume > 0 && !token.IsCancellationRequested)
                        TryPlay(() => playSound(audition.Tone, audition.Volume, token));
                });
                continue;
            }
            if (voicePreview is { } phrase)
            {
                voicePreview = null;
                var token = stop.Token;
                int volume = settings.Audio.Apply(settings.Audio.Voice);
                await Task.Run(() =>
                {
                    if (volume > 0 && !token.IsCancellationRequested) TryPlay(() => speak(phrase, volume, token));
                });
                continue;
            }
            if (!audio.TryDequeue(out var notice)) return;
            var mix = settings.Audio;
            var (tone, channelVolume) = notice.AlertMarket != null ? (settings.PriceAlertTone, mix.PriceAlert) : notice.Module switch
            {
                SignalModule.Volume => (settings.VolumeSignalTone, mix.Volume),
                SignalModule.Price => (settings.PriceSignalTone, mix.Price),
                _ => (settings.DensityTone, mix.Density)
            };
            int soundVolume = SoundEnabled(notice) ? mix.Apply(channelVolume) : 0;
            int voiceVolume = VoiceEnabled(notice) ? mix.Apply(mix.Voice) : 0;
            if (soundVolume == 0 && voiceVolume == 0 || !Fresh(notice.Time, Now)) continue;
            var ct = stop.Token;
            using var voiceStop = voiceVolume > 0 ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
            activeVoice = voiceStop;
            await Task.Run(async () =>
            {
                if (ct.IsCancellationRequested) return;
                if (soundVolume > 0)
                {
                    TryPlay(() => playSound(tone, soundVolume, ct));
                    try { await Task.Delay(150, ct); }
                    catch (OperationCanceledException) { return; }
                }
                if (voiceStop is { IsCancellationRequested: false } && Fresh(notice.Time, Now))
                    TryPlay(() => speak(notice.Speech, voiceVolume, voiceStop.Token));
            });
            activeVoice = null;
        }
    }

    private static void TryPlay(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            System.Diagnostics.Trace.TraceWarning("Signal audio unavailable: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        popupTimer.Stop();
        visual.Clear();
        audio.Clear();
        stop.Cancel();
        if (audioWorker is { IsCompleted: false })
            _ = audioWorker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        else stop.Dispose();
        popup?.Dispose();
    }
}

internal static class WindowsSignalVoice
{
    public static bool RussianVoiceAvailable()
    {
        try { return UseRussianVoice(null, 100, CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    public static void Speak(string message, int volume, CancellationToken stop)
    {
        if (volume > 0) UseRussianVoice(message, volume, stop);
    }

    private static bool UseRussianVoice(string? message, int volume, CancellationToken stop)
    {
        object? voice = null, voices = null, token = null;
        try
        {
            if (Type.GetTypeFromProgID("SAPI.SpVoice") is not { } type) return false;
            voice = Activator.CreateInstance(type)!;
            string language = CultureInfo.GetCultureInfo("ru-RU").LCID.ToString("X", CultureInfo.InvariantCulture);
            voices = ((dynamic)voice).GetVoices($"Language={language}", "");
            if (((dynamic)voices).Count == 0) return false;
            if (message == null || stop.IsCancellationRequested) return true;
            token = ((dynamic)voices).Item(0);
            dynamic speech = voice;
            speech.Voice = token;
            speech.Rate = 2;
            speech.Volume = volume;
            const int asynchronous = 1, purge = 2, plainText = 16;
            speech.Speak(message, asynchronous | plainText);
            while (!speech.WaitUntilDone(100))
                if (stop.IsCancellationRequested)
                {
                    speech.Speak("", asynchronous | purge | plainText);
                    break;
                }
            return true;
        }
        finally
        {
            if (token != null) Marshal.FinalReleaseComObject(token);
            if (voices != null) Marshal.FinalReleaseComObject(voices);
            if (voice != null) Marshal.FinalReleaseComObject(voice);
        }
    }
}

internal sealed class SignalPopupWindow(Action<Market>? openAlert = null, Func<bool>? canOpenAlert = null) : IDisposable
{
    private const double Inset = 16;
    private Window? window;
    private TextBlock? ticker, kind, details;
    private Border? badge;
    private Button? openTerminal;
    private Market? alertMarket;

    public void Show(SignalNotice? notice)
    {
        if (notice == null)
        {
            alertMarket = null;
            UpdateAction();
            window?.Hide();
            return;
        }
        UpdateNotice(notice);
        var workArea = SystemParameters.WorkArea;
        window!.Width = Math.Clamp(workArea.Width - Inset * 2, 1, 380);
        window.Left = workArea.Right - window.Width - Inset;
        window.Top = workArea.Top + Inset;
        if (!window.IsVisible) window.Show();
    }

    private void UpdateNotice(SignalNotice notice)
    {
        alertMarket = notice.AlertMarket;
        window ??= CreateWindow();
        ticker!.Text = notice.Ticker;
        kind!.Text = notice.Kind;
        details!.Text = notice.Details;
        bool volume = notice.Module == SignalModule.Volume;
        kind.Foreground = Terminal.Brush(volume ? "Rising" : "Accent");
        badge!.Background = Terminal.Brush(volume ? "RequiredSurface" : "AccentSurface");
        badge.Child = Terminal.Icon(alertMarket != null ? "bell" : notice.Module == null ? "density-nav" : volume ? "volume" : "activity");
        System.Windows.Documents.TextElement.SetForeground(badge.Child, kind.Foreground);
        UpdateAction();
    }

    private Window CreateWindow()
    {
        ticker = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        kind = Terminal.Copy("");
        details = Terminal.Copy("", muted: false);
        var caption = new StackPanel { Margin = new Thickness(10, 0, 5, 0) };
        caption.Children.Add(ticker);
        caption.Children.Add(kind);

        var close = Terminal.IconButton("close", "Закрыть уведомление");
        close.Width = close.Height = 25;
        close.Focusable = false;
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Click += (_, _) => window?.Hide();
        badge = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Top };
        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(badge, Dock.Left);
        header.Children.Add(close);
        header.Children.Add(badge);
        header.Children.Add(caption);
        var content = new StackPanel();
        content.Children.Add(header);
        var separator = Terminal.Rule(new Thickness(0, 1, 0, 0));
        separator.Margin = new Thickness(0, 9, 0, 8);
        content.Children.Add(separator);
        content.Children.Add(details);
        var terminalLabel = new StackPanel { Orientation = Orientation.Horizontal };
        terminalLabel.Children.Add(Terminal.AssetLogo("colibri", 15));
        terminalLabel.Children.Add(new TextBlock { Text = "Открыть в Colibri", Margin = new Thickness(6, 0, 0, 0) });
        openTerminal = new Button
        {
            Content = terminalLabel, MinHeight = 26, Focusable = false, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 9, 0, 0), Visibility = Visibility.Collapsed
        };
        System.Windows.Automation.AutomationProperties.SetName(openTerminal, "Открыть в Colibri");
        openTerminal.Click += (_, _) =>
        {
            if (alertMarket is not { } market || openAlert == null || canOpenAlert?.Invoke() == false) return;
            openAlert(market);
            window?.Hide();
        };
        content.Children.Add(openTerminal);
        var view = new Window
        {
            Title = "Thin Market Monitor · Сигнал", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, Focusable = false, AllowsTransparency = true,
            Background = Brushes.Transparent, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Border { Child = content, Background = Terminal.Brush("Surface"), BorderBrush = Terminal.Brush("Outline"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 11, 13, 12) }
        };
        view.SourceInitialized += (_, _) => ((HwndSource)PresentationSource.FromVisual(view)).AddHook(NoActivation);
        return view;
    }

    internal void UpdateAction()
    {
        if (openTerminal == null) return;
        openTerminal.Visibility = alertMarket != null && openAlert != null && canOpenAlert?.Invoke() != false
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private static IntPtr NoActivation(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int mouseActivate = 0x0021, doNotActivate = 3;
        if (message != mouseActivate) return IntPtr.Zero;
        handled = true;
        return new IntPtr(doNotActivate);
    }

    public void Dispose() => window?.Close();
}

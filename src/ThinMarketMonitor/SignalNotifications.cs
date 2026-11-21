using System.Globalization;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ThinMarketMonitor;

internal sealed record SignalNotice(string Title, string Details, string Speech, long Time)
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public static SignalNotice From(Signal signal)
    {
        bool volume = signal.Module == SignalModule.Volume;
        string type = volume ? "Импульс объёма" : "Импульс цены";
        string title = $"{signal.Market.Ticker} · {type}";
        string window = MainWindow.WindowName(signal.Seconds);
        string percent = signal.Value.ToString("+0.00;-0.00;0.00", Russian);
        string change = volume ? $"{percent}% к среднему за {window}" : $"{percent}% за {window}";
        string details = $"{signal.Market.Source.Label}\n{change}";
        string baseline = volume ? " к среднему" : "";
        string speech = $"{type}. {signal.Market.Ticker}. {signal.Market.Source.Label}. Рост {signal.Value.ToString("0.00", Russian)} процента{baseline}. Окно {SpokenWindow(signal.Seconds)}.";
        if (volume && signal.CurrentTurnover is { } current && signal.AverageTurnover is { } average)
        {
            details += $"\nОборот {Money(current)} USD · средний {Money(average)} USD";
            if (signal.ReferenceSeconds is { } reference) details += $"\nБаза · предыдущие {MainWindow.WindowName(reference)}";
            speech += $" Оборот {Money(current)} долларов.";
        }
        if (!volume && signal.LastPrice is { } price && double.IsFinite(price))
            details += $"\nЦена {price.ToString("G12", Russian)} {signal.Market.Quote}";
        return new(title, details, speech, signal.Time);
    }

    private static string Money(double value) => value.ToString("N2", Russian);

    private static string SpokenWindow(int seconds)
    {
        if (seconds % 3600 == 0) return Duration(seconds / 3600, "час", "часа", "часов");
        if (seconds % 60 == 0) return Duration(seconds / 60, "минута", "минуты", "минут");
        return Duration(seconds, "секунда", "секунды", "секунд");

        static string Duration(int value, string one, string few, string many) =>
            $"{value} {(value % 100 is >= 11 and <= 14 ? many : (value % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many })}";
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
    private readonly Action playSound;
    private readonly Action<string, CancellationToken> speak;
    private readonly SignalPopupWindow? popup;
    private readonly CancellationTokenSource stop = new();
    private readonly Queue<SignalNotice> visual = new(), audio = new();
    private readonly Dictionary<(string Market, SignalModule Module), long> last = [];
    private long pruneAt;
    private Task? audioWorker;
    private CancellationTokenSource? activeVoice;
    private bool disposed;

    public SignalNotifications(Settings settings, Dispatcher dispatcher, Action<SignalNotice?>? showPopup = null,
        Action? playSound = null, Action<string, CancellationToken>? speak = null, TimeProvider? clock = null)
    {
        this.settings = settings;
        this.clock = clock ?? TimeProvider.System;
        this.playSound = playSound ?? SystemSounds.Asterisk.Play;
        this.speak = speak ?? WindowsSignalVoice.Speak;
        popup = showPopup == null ? new SignalPopupWindow() : null;
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

        var notice = SignalNotice.From(signal);
        if (settings.SignalPopup)
        {
            Enqueue(visual, notice);
            if (!popupTimer.IsEnabled) ShowNext();
        }
        if (settings.SignalSound || settings.SignalVoice)
        {
            Enqueue(audio, notice);
            if (audioWorker is not { IsCompleted: false }) audioWorker = PlayPending();
        }
    }

    public void SettingsChanged()
    {
        if (!settings.SignalPopup && popupTimer.IsEnabled)
        {
            visual.Clear();
            popupTimer.Stop();
            showPopup(null);
        }
        if (!settings.SignalSound && !settings.SignalVoice) audio.Clear();
        if (!settings.SignalVoice) activeVoice?.Cancel();
    }

    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();
    private static bool Fresh(long time, long now) => time >= now - MaximumAge && time <= now + 3000;

    private static void Enqueue(Queue<SignalNotice> queue, SignalNotice notice)
    {
        if (queue.Count == MaxPending) queue.Dequeue();
        queue.Enqueue(notice);
    }

    private void ShowNext()
    {
        popupTimer.Stop();
        while (settings.SignalPopup && visual.TryDequeue(out var notice))
        {
            if (!Fresh(notice.Time, Now)) continue;
            showPopup(notice);
            popupTimer.Start();
            return;
        }
        showPopup(null);
    }

    private async Task PlayPending()
    {
        while (!disposed && audio.TryDequeue(out var notice))
        {
            bool sound = settings.SignalSound, voice = settings.SignalVoice;
            if ((!sound && !voice) || !Fresh(notice.Time, Now)) continue;
            var ct = stop.Token;
            using var voiceStop = voice ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
            activeVoice = voiceStop;
            await Task.Run(async () =>
            {
                if (ct.IsCancellationRequested) return;
                if (sound)
                {
                    TryPlay(playSound);
                    try { await Task.Delay(450, ct); }
                    catch (OperationCanceledException) { return; }
                }
                if (voiceStop is { IsCancellationRequested: false } && Fresh(notice.Time, Now))
                    TryPlay(() => speak(notice.Speech, voiceStop.Token));
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
        try { return UseRussianVoice(null, CancellationToken.None); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
    }

    public static void Speak(string message, CancellationToken stop) => UseRussianVoice(message, stop);

    private static bool UseRussianVoice(string? message, CancellationToken stop)
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

internal sealed class SignalPopupWindow : IDisposable
{
    private Window? window;
    private TextBlock? title, details;

    public void Show(SignalNotice? notice)
    {
        if (notice == null)
        {
            window?.Hide();
            return;
        }
        window ??= CreateWindow();
        title!.Text = notice.Title;
        details!.Text = notice.Details;
        window.Opacity = 0;
        window.Show();
        window.UpdateLayout();
        var workArea = SystemParameters.WorkArea;
        window.Left = workArea.Right - window.ActualWidth - 16;
        window.Top = workArea.Bottom - window.ActualHeight - 16;
        window.Opacity = 1;
    }

    private Window CreateWindow()
    {
        title = Terminal.Heading("");
        title.TextWrapping = TextWrapping.Wrap;
        title.Foreground = Terminal.Brush("Accent");
        details = Terminal.Copy("", muted: false);
        var content = new StackPanel();
        content.Children.Add(title);
        content.Children.Add(details);
        var view = new Window
        {
            Title = "Thin Market Monitor · Сигнал", WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true, Focusable = false,
            Width = 378, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new Border { Child = content, Background = Terminal.Brush("Surface"), BorderBrush = Terminal.Brush("Accent"),
                BorderThickness = new Thickness(3, 1, 1, 1), Padding = new Thickness(13, 10, 13, 10) }
        };
        view.SourceInitialized += (_, _) => ((HwndSource)PresentationSource.FromVisual(view)).AddHook(NoActivation);
        return view;
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

using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Collections.Concurrent;

namespace ThinMarketMonitor;

internal sealed partial class ChartCell : Border
{
    private readonly ExchangeApi api;
    private readonly Func<long> nextOrder;
    private readonly CandlePlot plot;
    private readonly ChartSurface chartSurface;
    private readonly ScrollViewer chartViewport;
    private readonly DockPanel layout = new();
    private readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly StackPanel frameBar = new() { Orientation = Orientation.Horizontal };
    private readonly Border headerFrame = Terminal.Rule(new Thickness(0, 0, 0, 1));
    private readonly ContentControl signalIcon = new() { Margin = new Thickness(0, 0, 5, 0) };
    private readonly Func<int[]> frameButtons;
    private readonly Button pin = Terminal.IconButton("pin-off", "Закрепить график");
    private readonly Button clear = Terminal.IconButton("close", "Очистить ячейку");
    private readonly Button blacklist = Terminal.IconButton("ban", "В чёрный список сигналов");
    private readonly Button colibri = Terminal.IconButton("external", "Открыть в Colibri");
    private CancellationTokenSource? refresh;
    private Market? market;
    private bool active;
    private CellChoice? choice;
    private Signal? latestSignal;
    private int frame = 60;
    private bool olderRequested;
    private bool autoAssigned;
    private bool debugMode;
    public int Frame => frame;
    public Market? Instrument => market;
    public event Action<int>? FrameChanged;
    public event Action<Market>? OpenInColibri;
    public event Action<Market>? BlacklistRequested;

    public CellChoice? Choice
    {
        get => choice;
        set { choice = value; if (value != null && Frames.All.Contains(value.Frame)) frame = value.Frame; UpdateHeader(); UpdateFrames(); }
    }
    public long Order => Choice?.Order ?? 0;
    public double PlotHeight { set => plot.Height = value; }
    public bool HasMarket => market != null;
    public bool IsInteracting => plot.IsMouseCaptured || chartSurface.IsResizing;
    public CandlePlot Plot => plot;

    public ChartCell(ExchangeApi api, int number, Action? choose, Action save, Func<long> nextOrder, Func<ChartGesture> gesture,
        Func<int[]> frameButtons, ChartFeatures? features = null)
    {
        this.api = api;
        overview = number == 0;
        this.nextOrder = nextOrder;
        this.frameButtons = frameButtons;
        plot = new CandlePlot(gesture) { Focusable = true };
        chartSurface = new(plot, MoveInterestPane, CloseInterestPane, SavePaneWeights,
            venue => ChangeInterestPresentation(venue, mode => mode with { Usd = !mode.Usd }),
            venue => ChangeInterestPresentation(venue, mode => mode with { Candles = !mode.Candles }));
        KeyboardNavigation.SetIsTabStop(plot, false);
        plot.OlderRequested += () => olderRequested = true;
        Margin = new Thickness(0, 0, 1, 1);
        Padding = new Thickness(0, 0, 0, 4);
        Background = Terminal.Brush("Surface");
        detail.Foreground = Terminal.Brush("Muted");
        detail.MinHeight = 15;
        detail.Margin = new Thickness(7, 4, 7, 0);
        ToolTipService.SetIsEnabled(detail, false);
        Child = layout;
        var header = new DockPanel { Margin = new Thickness(7, 2, 7, 2), MinHeight = 24 };
        headerFrame.Child = header;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        Button? select = null;
        if (choose != null)
        {
            select = new Button
            {
                Content = "Выбрать", Style = Terminal.Style("IconButton"), Padding = new Thickness(6, 1, 6, 1),
                Background = Terminal.Brush("Control"), BorderBrush = Terminal.Brush("Outline"), FontSize = 11
            };
            AutomationProperties.SetName(select, $"Выбрать инструмент · ячейка {number}");
            select.Click += (_, _) => choose();
            actions.Children.Add(select);
        }
        pin.Click += (_, _) => { SetPinned(Choice?.Pinned != true); save(); };
        clear.Click += (_, _) =>
        {
            Clear();
            save();
            if (select?.Focus() != true) plot.Focus();
        };
        colibri.Visibility = Visibility.Collapsed;
        colibri.Margin = new Thickness(2, 0, 0, 0);
        colibri.Click += (_, _) => { if (market != null) OpenInColibri?.Invoke(market); };
        colibri.Content = Terminal.AssetLogo("colibri", 17);
        actions.Children.Add(colibri);
        blacklist.Visibility = number > 0 ? Visibility.Visible : Visibility.Collapsed;
        blacklist.Margin = new Thickness(2, 0, 0, 0);
        blacklist.Click += (_, _) => { if (market != null) BlacklistRequested?.Invoke(market); };
        actions.Children.Add(blacklist);
        pin.Margin = clear.Margin = new Thickness(2, 0, 0, 0);
        if (number > 0) actions.Children.Add(pin);
        actions.Children.Add(clear);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        DockPanel.SetDock(signalIcon, Dock.Left);
        header.Children.Add(signalIcon);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 7, 0);
        header.Children.Add(label);
        DockPanel.SetDock(headerFrame, Dock.Top);
        var commandBar = new DockPanel();
        var chartBody = new DockPanel();
        DockPanel.SetDock(commandBar, Dock.Top);
        ConfigureChartTools(commandBar, chartBody, features);
        commandBar.Children.Add(frameBar);
        DockPanel.SetDock(detail, Dock.Bottom);
        layout.Children.Add(headerFrame);
        layout.Children.Add(commandBar);
        layout.Children.Add(detail);
        DockPanel.SetDock(indicatorHint, Dock.Bottom);
        layout.Children.Add(indicatorHint);
        chartViewport = new ScrollViewer
        {
            Content = chartSurface, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false
        };
        chartBody.Children.Add(chartViewport);
        layout.Children.Add(chartBody);
        UpdateHeader();
        UpdateFrames();
    }

    public void Activate(bool visible)
    {
        if (active == visible) return;
        active = visible;
        ResetFeed();
    }

    internal void CancelGestures()
    {
        chartSurface.CancelResize();
        plot.CancelGesture();
    }

    public void SetColibriEnabled(bool enabled)
    {
        colibri.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        colibri.IsEnabled = market != null;
    }

    public void Assign(Market selected, bool pinned, Signal? signal = null)
    {
        market = selected;
        latestSignal = signal;
        autoAssigned = signal != null;
        Choice = new(selected.Key, pinned, nextOrder(), frame);
        ResetFeed();
        if (signal != null)
        {
            var tint = new SolidColorBrush(((SolidColorBrush)Terminal.Brush("AccentSurface")).Color);
            headerFrame.Background = tint;
            tint.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(
                ((SolidColorBrush)Terminal.Brush("Surface")).Color, TimeSpan.FromMilliseconds(650)));
        }
    }

    public void Clear()
    {
        market = null;
        latestSignal = null;
        autoAssigned = false;
        Choice = null;
        ResetFeed();
    }

    public void ShowSignal(Signal signal)
    {
        if (!autoAssigned) return;
        latestSignal = signal;
        if (market != null) UpdateHeader();
    }

    private string? InstrumentTooltip() => latestSignal is not { } s ? market?.Label :
        $"{market?.Label}\n{(s.Module == SignalModule.Price ? "Импульс цены" : "Импульс объёма")} · " +
        (s.Module == SignalModule.Price ? $"+{s.Value:F2}%" : $"+{s.Value:N2}%") + $" за {MainWindow.WindowName(s.Seconds)}" +
        (s.Module == SignalModule.Volume ? $"\nОборот сейчас: {s.CurrentTurnover:G6} USD · средний за такое же окно: {s.AverageTurnover:G6} USD" +
            (debugMode ? $"\nБаза: предыдущие {MainWindow.WindowName(s.ReferenceSeconds ?? 3600)} без текущего окна. {VolumeImpulse.SettlementSeconds} с на доставку сделок." : "") : "");

    public void Resolve(Market? resolved)
    {
        if (market == resolved) return;
        market = resolved;
        ResetFeed();
    }

    private void SetPinned(bool pinned)
    {
        if (Choice == null) return;
        Choice = Choice with { Pinned = pinned };
    }

    private void UpdatePin()
    {
        clear.IsEnabled = pin.IsEnabled = Choice != null;
        bool pinned = Choice?.Pinned == true;
        pin.Content = Terminal.Icon(pinned ? "pin" : "pin-off");
        pin.Foreground = Terminal.Brush(pinned ? "Accent" : "Text");
        pin.Background = pinned ? Terminal.Brush("AccentSurface") : Brushes.Transparent;
        string caption = pinned ? "Снять закрепление" : "Закрепить график";
        pin.ToolTip = caption;
        AutomationProperties.SetName(pin, caption);
        AutomationProperties.SetItemStatus(pin, pinned ? "Закреплён" : "Не закреплён");
    }

    private void UpdateHeader()
    {
        label.Inlines.Clear();
        label.Foreground = Terminal.Brush(market == null ? "Muted" : "Accent");
        label.FontWeight = market == null ? FontWeights.Normal : FontWeights.SemiBold;
        if (market == null)
        {
            label.Text = Choice?.Key ?? "Свободная ячейка";
        }
        else
        {
            label.Inlines.Add($"{market.Base}/{market.Quote}");
            label.Inlines.Add(new System.Windows.Documents.Run(" " + market.Venue.Label())
            {
                Foreground = Terminal.Brush("Muted"), FontWeight = FontWeights.Normal, FontSize = 11
            });
        }
        label.ToolTip = InstrumentTooltip() ?? Choice?.Key;
        signalIcon.Visibility = latestSignal == null ? Visibility.Collapsed : Visibility.Visible;
        signalIcon.Content = latestSignal == null ? null : Terminal.Icon(latestSignal.Module == SignalModule.Price ? "activity" : "volume");
        signalIcon.Foreground = Terminal.Brush(latestSignal?.Module == SignalModule.Volume ? "Rising" : "Accent");
        signalIcon.ToolTip = latestSignal == null ? null : InstrumentTooltip();
        if (latestSignal is { Module: SignalModule.Volume } volumeSignal)
            label.Inlines.Add(new System.Windows.Documents.Run($"  +{volumeSignal.Value:N0}%")
                { Foreground = Terminal.Brush("Rising"), FontWeight = FontWeights.SemiBold });
        UpdatePin();
        colibri.IsEnabled = market != null;
        SetChartDetail(market == null && Choice != null ? "Инструмент вне доступного списка" : "");
        blacklist.IsEnabled = market != null;
        string blockLabel = market == null ? "В чёрный список сигналов" : $"В чёрный список сигналов · {market.Base}";
        blacklist.ToolTip = blockLabel;
        AutomationProperties.SetName(blacklist, blockLabel);
        UpdateChartToolState();
        AutomationProperties.SetName(plot, market == null ? "" : $"Свечи {Frames.Label(frame)} · {market.Label}");
    }

    private void SetChartDetail(string text, string? hint = null)
    {
        detail.Text = text;
        detail.ToolTip = hint;
        detail.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void SetDebugMode(bool enabled)
    {
        if (debugMode == enabled) return;
        debugMode = enabled;
        ToolTipService.SetIsEnabled(detail, enabled);
        label.ToolTip = InstrumentTooltip() ?? Choice?.Key;
        signalIcon.ToolTip = latestSignal == null ? null : InstrumentTooltip();
        indicatorHint.Text = "";
        indicatorHint.ToolTip = null;
        if (indicatorTools != null) indicatorTools.ToolTip = "Индикаторы графика";
        UpdateIndicatorHint(active && IsVisible && market != null);
        plot.SetDebugMode(enabled);
    }

    private void ResetFeed()
    {
        refresh?.Cancel();
        refresh = null;
        olderRequested = false;
        plot.Frame = frame;
        plot.Instrument = market;
        plot.SetDensityLevels([]);
        plot.Set([], reset: true);
        UpdateHeader();
        ResetChartTools();
        if (!active || market == null) return;
        refresh = new();
        _ = Load(market, frame, refresh);
    }

    public void CycleFrame(int direction) => SetFrame(Frames.All[Math.Clamp(Array.IndexOf(Frames.All, frame) + direction, 0, Frames.All.Length - 1)]);

    public void SetFrame(int value)
    {
        if (value == frame || !Frames.All.Contains(value)) return;
        frame = value;
        if (choice != null) choice = choice with { Frame = value };
        UpdateFrames();
        ResetFeed();
        FrameChanged?.Invoke(value);
    }

    public void UpdateFrames()
    {
        frameBar.Children.Clear();
        var shortcuts = frameButtons();
        foreach (int value in shortcuts)
            Add(value, frameBar);
        var more = Terminal.IconButton("chevron", "Все таймфреймы");
        if (!shortcuts.Contains(frame))
        {
            more.Content = Terminal.LabelWithIcon("chevron", Frames.Label(frame), 3);
            more.Foreground = Terminal.Brush("Accent");
        }
        Terminal.AttachMenu(more, Frames.All.Select(value =>
        {
            var option = new MenuItem { Header = Frames.Label(value), IsChecked = value == frame };
            option.Click += (_, _) => SetFrame(value);
            return option;
        }), 90);
        frameBar.Children.Add(more);

        void Add(int value, Panel target)
        {
            var button = new Button { Content = Frames.Label(value), Style = Terminal.Style("IconButton"), MinHeight = 20,
                Foreground = Terminal.Brush(value == frame ? "Accent" : "Muted") };
            AutomationProperties.SetName(button, $"Таймфрейм {Frames.Label(value)}");
            button.Click += (_, _) => SetFrame(value);
            target.Children.Add(button);
        }
    }

    private async Task Load(Market instrument, int tf, CancellationTokenSource source)
    {
        var ct = source.Token;
        long offset = ExchangeApi.CandleOffset(instrument, tf);
        var candles = new LiveCandles(tf, offset, ChartVolume.UsesBase(instrument));
        bool rulerTurnover = UsdQuotes.Supported(instrument);
        long liveStart = api.CandleNow;
        var live = new ConcurrentQueue<Trade>();
        bool native = tf >= 60 || ExchangeApi.NativeSeconds(instrument);
        int queuedTrades = 0;
        bool finished = false;
        int emptyPages = 0;
        var coverage = (Text: "", Diagnostic: (string?)null);
        long nextPage = Boundary(tf, offset);
        long nextTick = 0;
        bool historyLoaded = false;
        var refreshPlot = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        refreshPlot.Tick += (_, _) =>
        {
            if (ct.IsCancellationRequested || live.IsEmpty) return;
            int pending = Math.Min(Volatile.Read(ref queuedTrades), 10000);
            for (int i = 0; i < pending && live.TryDequeue(out var trade); i++)
            {
                Interlocked.Decrement(ref queuedTrades);
                candles.Add(trade, (rulerTurnover || indicatorOptions.Volume) && IsVisible && (native || trade.Time >= liveStart)
                    ? ChartVolume.TradeAmount(instrument, trade) : double.NaN);
                ObserveChartTrade(instrument, trade);
            }
            Draw();
        };
        SetChartDetail("Загрузка свечей…");
        try
        {
            if (instrument.Streaming)
            {
                refreshPlot.Start();
                _ = Task.Run(() => StreamTrades(instrument, trade =>
                {
                    if (Interlocked.Increment(ref queuedTrades) <= 10000) live.Enqueue(trade);
                    else Interlocked.Decrement(ref queuedTrades);
                }, ct));
            }
            if (!native) await LoadTradeHistory();
            while (!ct.IsCancellationRequested)
            {
                bool loadingOlder = false;
                try
                {
                    if (native && olderRequested && !finished && historyLoaded)
                    {
                        loadingOlder = true;
                        olderRequested = false;
                        int remaining = plot.MissingHistoryBars(nextPage);
                        bool found = await Page(nextPage, Math.Clamp(remaining, 180, tf < 60 ? 180 : 480), prepend: true);
                        if (found)
                        {
                            emptyPages = 0;
                            olderRequested |= plot.MissingHistoryBars(nextPage) > 0;
                        }
                        else if (++emptyPages >= 3)
                        {
                            finished = true;
                            coverage = ("Конец доступной истории", "Три предыдущих участка без свечей.");
                        }
                        else
                        {
                            coverage = ("На предыдущем участке свечей нет", null);
                            olderRequested |= plot.MissingHistoryBars(nextPage) > 0;
                        }
                        loadingOlder = false;
                    }
                    if (native && Environment.TickCount64 >= nextTick)
                    {
                        nextTick = Environment.TickCount64 + (tf < 60 ? 4000 : 12000);
                        bool initial = !historyLoaded;
                        bool found = await Page(Boundary(tf, offset), initial ? CandlePlot.InitialHistoryBars(tf) : 4, prepend: false);
                        if (initial && found && tf < 60) olderRequested |= plot.MissingHistoryBars(nextPage) > 0;
                    }
                    if (native && candles.Count >= LiveCandles.MaxBars)
                    {
                        finished = true;
                        coverage = ("Конец загруженной истории", $"Локальный предел {LiveCandles.MaxBars:N0} свечей.");
                    }
                    SetChartDetail(coverage.Text.Length > 0 ? coverage.Text : candles.Count == 0 ? "Биржа не вернула свечи" : "",
                        coverage.Diagnostic);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    if (loadingOlder) olderRequested = true;
                    SetChartDetail(loadingOlder ? "История слева не загрузилась" :
                        !historyLoaded ? "История не загрузилась полностью" : "Свечи не обновились",
                        $"Повтор через 12 с\n{ExchangeApi.ConnectionProblem(ex) ?? ex.Message}");
                    await Task.Delay(12000, ct);
                }
                await Task.Delay(native ? 300 : 450, ct);
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or HttpRequestException) { }
        finally { refreshPlot.Stop(); source.Dispose(); }

        async Task<bool> Page(long before, int count, bool prepend)
        {
            if (before <= 0) { finished = true; return false; }
            long requestedAt = api.CandleNow;
            var partial = new List<Candle>();
            Candle[] result;
            try
            {
                result = await api.CandlePage(instrument, tf, before, count, ct, partial.AddRange);
            }
            catch (Exception) when (!ct.IsCancellationRequested && partial.Count > 0)
            {
                candles.AddHistory(partial, requestedAt);
                Draw();
                throw;
            }
            ct.ThrowIfCancellationRequested();
            if (prepend || !historyLoaded) nextPage = result.Length > 0 ? result[0].Time : before - tf * count * 1000L;
            if (result.Length > 0)
            {
                historyLoaded = true;
                if (prepend || !finished && emptyPages == 0) coverage = ("", null);
                candles.AddHistory(result, requestedAt);
                Draw();
            }
            return result.Length > 0;
        }

        void Draw() => plot.Set(candles.DisplaySnapshot());

        async Task LoadTradeHistory()
        {
            coverage = ("Доступна короткая история", "Свечи построены из недавних сделок REST и WebSocket.");
            try
            {
                var trades = features != null && indicatorOptions.Profile ? await features.Recent(instrument, ct) : await api.RecentTrades(instrument, ct);
                ct.ThrowIfCancellationRequested();
                foreach (var trade in trades) candles.Add(trade, (rulerTurnover || indicatorOptions.Volume) && IsVisible && trade.Time < liveStart
                    ? ChartVolume.TradeAmount(instrument, trade) : double.NaN);
                Draw();
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                coverage = ("Доступны только новые сделки", ExchangeApi.ConnectionProblem(ex) ?? ex.Message);
            }
        }
    }

    private long Boundary(int tf, long offset)
    {
        long interval = tf * 1000L;
        return ExchangeApi.CandleStart(api.CandleNow, interval, offset) + interval;
    }

    private async Task StreamTrades(Market selected, Action<Trade> publish, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await MarketStreams.Open(api, [selected], ct, () => { }, t =>
                    {
                        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        if (!ct.IsCancellationRequested && t.Symbol.Equals(selected.Symbol, StringComparison.OrdinalIgnoreCase) &&
                            t.Time >= now - 5000 && t.Time <= now + 3000) publish(t);
                    }, chart: true);
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                await Task.Delay(5000, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }
}

internal sealed partial class CandlePlot : FrameworkElement
{
    private const double Left = 7, Top = 7, TimeAxisHeight = 17;
    internal const int DefaultVisibleBars = 220;
    internal const int MaxVisibleBars = 6000;
    private const double LatestFraction = .8;
    public int Frame { get; set; } = 60;
    internal Market? Instrument { private get; set; }
    private long BarTime => Frame * 1000L;
    public event Action? OlderRequested;
    private readonly Brush background = Terminal.Brush("Plot");
    private readonly Brush up = Terminal.Brush("Rising");
    private readonly Brush down = Terminal.Brush("Falling");
    private readonly Brush axis = Terminal.Brush("Muted");
    private readonly Brush accent = Terminal.Brush("Accent");
    private readonly Pen gridLine = new(Terminal.Brush("GridLine"), 1);
    private readonly Pen upLine = new(Terminal.Brush("Rising"), 1);
    private readonly Pen downLine = new(Terminal.Brush("Falling"), 1);
    private readonly Pen rulerLine = new(Terminal.Brush("Accent"), 1);
    private readonly Pen lastPriceLine = new(Terminal.Brush("Accent"), 1) { DashStyle = DashStyles.Dash };
    private readonly Brush onAccent = Terminal.Brush("OnAccent");
    private readonly Func<ChartGesture> gesture;
    private readonly Typeface typeface = new(new FontFamily("Cascadia Mono, Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private Candle[] candles = [];
    private double visible = DefaultVisibleBars;
    private double? manualEnd;
    private (double Low, double High)? fixedPrice;
    private Drag? drag;
    private Point rulerEnd;
    private enum Operation { Pan, InterestPan, Time, Price, InterestPrice, Ruler }
    private sealed record Drag(Operation Mode, Point Start, View View, double Visible, MouseButton? Button, InterestAxis? Interest);
    private readonly record struct View(Rect Plot, double Start, double End, double Low, double High, double[]? PaneHeights = null)
    {
        public double Span => High - Low;
        public double Price(double y) => High - (y - Plot.Top) / Plot.Height * Span;
        public double Time(double x) => Start + (x - Plot.Left) / Plot.Width * (End - Start);
        public double X(double time) => Plot.Left + (time - Start) / (End - Start) * Plot.Width;
        public double Y(double price) => Plot.Top + (High - price) / Span * Plot.Height;
        public Point Bound(Point point) => new(Math.Clamp(point.X, Plot.Left, Plot.Right), Math.Clamp(point.Y, Plot.Top, Plot.Bottom));
    }

    public CandlePlot(Func<ChartGesture> gesture)
    {
        this.gesture = gesture;
        AddVisualChild(crosshair);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) SetCrosshairPosition(null);
        };
    }

    public void Set(Candle[] value, bool reset = false)
    {
        candles = value;
        if (reset) Reset();
        else
        {
            if (manualEnd is { } end && candles.Length > 0) manualEnd = ClampEnd(end, visible);
            InvalidateVisual();
        }
    }

    public void Reset()
    {
        CancelDrawing();
        selectedDrawing = null;
        CancelGesture();
        visible = DefaultVisibleBars;
        manualEnd = null;
        fixedPrice = null;
        interestRanges.Clear();
        lastInterestWindow = null;
        InvalidateVisual();
    }

    internal static double PanEnd(double end, double dragFraction, double count, long barTime) =>
        end - dragFraction * count * barTime;

    internal static int InitialHistoryBars(int frame) => frame < 60 ? 100 : DefaultVisibleBars;

    internal static double FollowEnd(long latest, double count, long barTime) =>
        latest + (.5 + (1 - LatestFraction) * count) * barTime;

    public int MissingHistoryBars(long oldestRequested)
    {
        if (candles.Length == 0) return 0;
        double start = (manualEnd ?? LiveEnd(visible)) - visible * BarTime;
        return (int)Math.Clamp(Math.Ceiling((oldestRequested - start) / BarTime), 0, MaxVisibleBars);
    }

    private View? CurrentView()
    {
        if (ActualWidth < 100 || ActualHeight < 48 || candles.Length == 0) return null;
        double right = ActualWidth - (ActualWidth < 160 ? 46 : 70);
        var paneHeights = indicators is { Interest.Length: > 0 } ? MeasurePanes(ActualHeight) : null;
        var rect = new Rect(Left, Top, right - Left, paneHeights?[0] ?? ActualHeight - Top - TimeAxisHeight);
        double end = manualEnd ?? LiveEnd(visible);
        double start = end - visible * BarTime;
        if (fixedPrice is { } range) return new(rect, start, end, range.Low, range.High, paneHeights);
        double low = double.MaxValue, high = 0;
        foreach (var c in VisibleCandles(start, end))
        {
            low = Math.Min(low, c.Low);
            high = Math.Max(high, c.High);
        }
        if (high == 0)
        {
            var nearest = end > candles[^1].Time ? candles[^1] : candles[0];
            low = nearest.Low;
            high = nearest.High;
        }
        double margin = Math.Max((high - low) * 0.08, high * 0.00015);
        return new(rect, start, end, low - margin, high + margin, paneHeights);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var currentView = CurrentView();
        if (currentView is not { } view)
        {
            UpdateCrosshairAxes(null, 0, Rect.Empty, []);
            SummarizeIndicators(null);
            return;
        }
        var plot = view.Plot;
        double step = plot.Width * BarTime / (view.End - view.Start);
        double priceStep = PriceGridStep(view);
        var timeTicks = TimeGridTicks(view);
        double lastPrice = candles[^1].Close;
        double priceY = view.Y(lastPrice);
        FormattedText? priceLabel = double.IsFinite(priceY) && priceY >= plot.Top && priceY <= plot.Bottom
            ? Text(lastPrice.ToString("G9", Russian), onAccent) : null;
        Rect priceBadge = Rect.Empty;
        if (priceLabel != null)
        {
            double height = Math.Min(plot.Height, priceLabel.Height + 4);
            double width = Math.Min(ActualWidth - plot.Right - 4, plot.Width - 2);
            priceBadge = new Rect(plot.Right - width - 1, Math.Clamp(priceY - height / 2, plot.Top, plot.Bottom - height),
                width, height);
        }
        DrawPriceGrid(dc, view, priceStep);
        DrawTimeGrid(dc, view, plot, timeTicks);
        DrawTimeScale(dc, view, timeTicks);

        dc.PushClip(new RectangleGeometry(plot));
        DrawDensityRays(dc, view);
        candlePixels.Reset(step, VisualTreeHelper.GetDpi(this).DpiScaleX);
        Market? volumeMarket = indicators is { Options.Volume: true } indicator ? indicator.Market : null;
        foreach (var c in VisibleCandles(view.Start, view.End))
        {
            candlePixels.Add(new(view.X(c.Time + BarTime / 2d), c.Open, c.High, c.Low, c.Close,
                volumeMarket == null ? null : ChartVolume.Amount(volumeMarket, c), c.ObservedVolume));
        }
        var volumeBars = DrawCandleVolume(dc, view);
        DrawCandleSeries(dc, view);
        var (volume, liquidations) = DrawProfiles(dc, view);
        DrawAnnotations(dc, view);
        if (volumeBars.Known > 0 && indicators is { } data && plot.Width > 95 && plot.Height > 31)
            DrawText(dc, $"V · {ChartVolume.Unit(data.Market)}", plot.Left + 4, plot.Bottom - 14);
        dc.Pop();
        var interestScales = DrawInterest(dc, view, timeTicks);
        SummarizeIndicators(view, volumeBars, volume, liquidations);
        if (priceLabel != null)
        {
            dc.DrawLine(lastPriceLine, new Point(plot.Left, priceY), new Point(plot.Right, priceY));
            dc.DrawRectangle(accent, null, priceBadge);
            dc.PushClip(new RectangleGeometry(priceBadge));
            double available = priceBadge.Width - 6;
            if (priceLabel.Width > available)
                priceLabel = Text(lastPrice.ToString("G6", Russian), onAccent);
            FitAxisLabel(priceLabel, available);
            dc.DrawText(priceLabel, new Point(priceBadge.Right - priceLabel.Width - 3, priceBadge.Top + (priceBadge.Height - priceLabel.Height) / 2));
            dc.Pop();
        }
        if (drag is { Mode: Operation.Ruler } measure) DrawRuler(dc, measure);
        UpdateCrosshairAxes(view, priceStep, priceBadge, interestScales);
    }

    private void DrawRuler(DrawingContext dc, Drag measure)
    {
        var view = measure.View;
        var a = view.Bound(measure.Start);
        var b = view.Bound(rulerEnd);
        double first = view.Price(a.Y), last = view.Price(b.Y);
        double change = PriceChange(first, last);
        var interest = measure.Interest;
        Brush color = interest == null ? accent : InterestBrush(interest.Venue);
        Pen line = interest == null ? rulerLine : new(color, 1);
        dc.DrawLine(line, a, new Point(b.X, a.Y));
        dc.DrawLine(line, new Point(b.X, a.Y), b);
        dc.DrawEllipse(color, null, a, 3, 3);
        dc.DrawEllipse(color, null, b, 3, 3);
        string percent = double.IsFinite(change) ? change.ToString("+0.00;-0.00;0.00", Russian) + "%" : "—";
        string text = interest == null
            ? $"{ChartAxis.PriceLabel(first, view.Span / 100)} → {ChartAxis.PriceLabel(last, view.Span / 100)}   {percent}"
            : $"{(interest.Estimated ? "≈ " : "")}{interest.Scale.Delta(last - first)} {interest.Unit}   {percent}";
        if (interest == null) text += "\n" + RulerTurnover(view, a, b);
        var formatted = Text(text, color);
        double fit = Math.Min((ActualWidth - view.Plot.Left - 12) / formatted.Width, (view.Plot.Height - 6) / formatted.Height);
        if (fit < 1) formatted.SetFontSize(Math.Max(7, 11 * fit));
        double x = Math.Clamp(b.X + 7, view.Plot.Left, Math.Max(view.Plot.Left, ActualWidth - formatted.Width - 14));
        double y = Math.Clamp(b.Y - formatted.Height - 9, view.Plot.Top, Math.Max(view.Plot.Top, view.Plot.Bottom - formatted.Height - 6));
        dc.DrawRectangle(background, line, new Rect(x, y, formatted.Width + 10, formatted.Height + 6));
        dc.DrawText(formatted, new Point(x + 5, y + 3));
    }

    private string RulerTurnover(View view, Point start, Point end)
    {
        if (Instrument == null) return "Оборот USD —";
        double a = view.Time(start.X), b = view.Time(end.X);
        var subset = VisibleCandles(Math.Min(a, b), Math.Max(a, b), includeEnd: true);
        var (total, estimated, partial) = ChartVolume.MeasureTurnover(Instrument, subset, BarTime, a, b);
        if (total is not { } usd) return "Оборот USD —";
        string amount = ChartVolume.CompactUsd(usd);
        return $"Оборот {(estimated ? "≈ " : "")}{amount} USD{(partial ? " · частично" : "")}";
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (drag != null || CurrentView() is not { } view) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && indicators?.Interest.Length > 1)
        {
            SetCrosshairPosition(null);
            return;
        }
        double fraction = Math.Clamp((e.GetPosition(this).X - Left) / view.Plot.Width, 0, 1);
        double count = ClampCount(visible * Math.Pow(e.Delta > 0 ? .78 : 1.28, Math.Abs(e.Delta) / 120d));
        if (manualEnd.HasValue)
            manualEnd = ClampEnd(view.End + (count - visible) * (1 - fraction) * BarTime, count);
        visible = count;
        RequestOlder(e.Delta < 0);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (drag != null || CurrentView() is not { } view) return;
        Point at = e.GetPosition(this);
        bool inPlot = view.Plot.Contains(at);
        if (inPlot && e.ChangedButton == MouseButton.Left && DrawingClick(at, view))
        {
            if (IsMouseCaptured) SetCrosshairPosition(null);
            Focus();
            e.Handled = true;
            return;
        }
        if (gesture().Mouse == e.ChangedButton && BeginRulerAt(at, view, e.ChangedButton))
        {
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (StartInterestAxisDrag(at, view, reset: e.ClickCount == 2)) { e.Handled = true; return; }
        if (inPlot) Start(Operation.Pan, at, view, MouseButton.Left);
        else if (InterestPaneAt(at, view, axisOnly: false) is { } pane)
        {
            var interest = GetInterestAxis(pane, view);
            Start(Operation.InterestPan, at, interest == null ? view with { Plot = pane.Graph } : InterestView(view, pane, interest.Scale),
                MouseButton.Left, interest);
        }
        else if (at.Y >= view.Plot.Bottom && at.X <= view.Plot.Right) Start(Operation.Time, at, view, MouseButton.Left);
        else if (at.X >= view.Plot.Right && at.Y >= view.Plot.Top && at.Y <= view.Plot.Bottom)
        {
            if (e.ClickCount == 2)
            {
                fixedPrice = null;
                InvalidateVisual();
            }
            else Start(Operation.Price, at, view, MouseButton.Left);
        }
        else return;
        e.Handled = true;
    }

    public bool BeginKeyboardRuler()
    {
        if (drag != null || !IsMouseOver || CurrentView() is not { } view) return false;
        return BeginRulerAt(Mouse.GetPosition(this), view, null);
    }

    private bool BeginRulerAt(Point at, View view, MouseButton? button) =>
        RulerViewport(at, view) is { } target && Start(Operation.Ruler, at, target.View, button, target.Interest);

    private (View View, InterestAxis? Interest)? RulerViewport(Point at, View view)
    {
        if (view.Plot.Contains(at)) return (view, null);
        if (InterestPaneAt(at, view, axisOnly: false) is not { } pane ||
            GetInterestAxis(pane, view) is not { } interest) return null;
        return (InterestView(view, pane, interest.Scale), interest);
    }

    public void EndKeyboardRuler()
    {
        if (drag is not { Mode: Operation.Ruler, Button: null }) return;
        CancelGesture();
        if (IsMouseOver) SetCrosshairPosition(Mouse.GetPosition(this));
    }

    internal static double PriceChange(double start, double end) =>
        start > 0 && double.IsFinite(start) && double.IsFinite(end) ? (end / start - 1) * 100 : double.NaN;

    private bool Start(Operation mode, Point at, View view, MouseButton? button, InterestAxis? interest = null)
    {
        if (!CaptureMouse()) return false;
        drag = new(mode, at, view, visible, button, interest);
        rulerEnd = at;
        SetCrosshairPosition(null);
        InvalidateVisual();
        return true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (drag is not { } move)
        {
            SetCrosshairPosition(IsMouseCaptured ? null : point);
            MoveDrawing(point);
            return;
        }
        MoveGesture(move, point);
    }

    private void MoveGesture(Drag move, Point point)
    {
        var view = move.View;
        double dx = (point.X - move.Start.X) / view.Plot.Width;
        double dy = (point.Y - move.Start.Y) / view.Plot.Height;
        switch (move.Mode)
        {
            case Operation.Ruler:
                rulerEnd = view.Bound(point);
                break;
            case Operation.Pan or Operation.InterestPan:
                if (Math.Abs(point.X - move.Start.X) > 1 || manualEnd.HasValue)
                    manualEnd = ClampEnd(PanEnd(view.End, dx, move.Visible, BarTime), move.Visible);
                RequestOlder(dx > 0);
                bool moved = Math.Abs(point.X - move.Start.X) > 1 || Math.Abs(point.Y - move.Start.Y) > 4;
                if (move.Interest is { } axis)
                {
                    if (moved || interestRanges.ContainsKey(InterestRangeKey(axis)))
                        SetInterestRange(axis, view.Low + dy * view.Span, view.Span);
                }
                else if (move.Mode == Operation.Pan && (moved || fixedPrice.HasValue))
                    SetPrice(view.Low + dy * view.Span, view.Span);
                break;
            case Operation.Time:
                visible = ClampCount(move.Visible * Math.Exp(Math.Clamp(dx * 2.3, -5, 5)));
                double anchor = Math.Clamp((move.Start.X - view.Plot.Left) / view.Plot.Width, 0, 1);
                if (manualEnd.HasValue)
                    manualEnd = ClampEnd(view.End + (visible - move.Visible) * (1 - anchor) * BarTime, visible);
                RequestOlder(dx > 0);
                break;
            case Operation.Price or Operation.InterestPrice:
                double span = view.Span * Math.Exp(Math.Clamp(dy * 2.3, -5, 5));
                double fromBottom = 1 - Math.Clamp((move.Start.Y - view.Plot.Top) / view.Plot.Height, 0, 1);
                double low = view.Price(move.Start.Y) - fromBottom * span;
                if (move.Interest is { } interest) SetInterestRange(interest, low, span);
                else SetPrice(low, span);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !ReleaseDrawing())
        {
            if (drag?.Button != e.ChangedButton) return;
            CancelGesture();
        }
        if (IsMouseOver) SetCrosshairPosition(e.GetPosition(this));
        e.Handled = true;
    }

    public void CancelGesture()
    {
        drag = null;
        movingAnchor = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        SetCrosshairPosition(null);
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        drag = null;
        movingAnchor = null;
        SetCrosshairPosition(null);
        InvalidateVisual();
    }

    private void SetPrice(double low, double span)
    {
        if (double.IsFinite(low + span) && span > 0 && low + span > low)
            fixedPrice = (low, low + span);
    }

    private static double ClampCount(double count) => Math.Clamp(count, 8, MaxVisibleBars);
    private double LiveEnd(double count) => FollowEnd(candles[^1].Time, count, BarTime);
    private double ClampEnd(double value, double count) => Math.Clamp(value,
        Math.Min(LiveEnd(count), candles[0].Time + count * BarTime), DateTimeOffset.MaxValue.AddDays(-2).ToUnixTimeMilliseconds());
    private void RequestOlder(bool backwards)
    {
        if (backwards && (manualEnd ?? LiveEnd(visible)) - visible * BarTime <= candles[0].Time + 6 * BarTime)
            OlderRequested?.Invoke();
    }

    private void DrawText(DrawingContext dc, string text, double x, double y) => dc.DrawText(Text(text, axis), new Point(x, y));

    private FormattedText Text(string text, Brush foreground) => new(text, Russian, FlowDirection.LeftToRight,
        typeface, 11, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}

using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Collections.Concurrent;

namespace ThinMarketMonitor;

internal sealed class ChartCell : Border
{
    private readonly ExchangeApi api;
    private readonly Func<long> nextOrder;
    private readonly CandlePlot plot;
    private readonly DockPanel layout = new();
    private readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly StackPanel frameBar = new() { Orientation = Orientation.Horizontal };
    private readonly Border headerFrame = Terminal.Rule(new Thickness(0, 0, 0, 1));
    private readonly ContentControl signalIcon = new() { Margin = new Thickness(0, 0, 5, 0) };
    private readonly Func<int[]> frameButtons;
    private readonly Button pin = Terminal.IconButton("pin-off", "Закрепить график");
    private readonly Button clear = Terminal.IconButton("close", "Очистить ячейку");
    private CancellationTokenSource? refresh;
    private Market? market;
    private bool active;
    private CellChoice? choice;
    private Signal? latestSignal;
    private int frame = 60;
    private bool olderRequested;
    private bool autoAssigned;
    public int Frame => frame;
    public Market? Instrument => market;
    public event Action<int>? FrameChanged;

    public CellChoice? Choice
    {
        get => choice;
        set { choice = value; if (value != null && Frames.All.Contains(value.Frame)) frame = value.Frame; UpdateHeader(); UpdateFrames(); }
    }
    public long Order => Choice?.Order ?? 0;
    public double PlotHeight { set => plot.Height = value; }
    public bool HasMarket => market != null;
    public bool IsInteracting => plot.IsMouseCaptured;
    public CandlePlot Plot => plot;

    public ChartCell(ExchangeApi api, int number, Action choose, Action save, Func<long> nextOrder, Func<ChartGesture> gesture,
        Func<int[]> frameButtons)
    {
        this.api = api;
        this.nextOrder = nextOrder;
        this.frameButtons = frameButtons;
        plot = new CandlePlot(gesture);
        plot.OlderRequested += () => olderRequested = true;
        Margin = new Thickness(0, 0, 1, 1);
        Padding = new Thickness(0, 0, 0, 4);
        Background = Terminal.Brush("Surface");
        detail.Foreground = Terminal.Brush("Muted");
        detail.MinHeight = 15;
        detail.Margin = new Thickness(7, 4, 7, 0);
        Child = layout;
        var header = new DockPanel { Margin = new Thickness(7, 2, 7, 2), MinHeight = 24 };
        headerFrame.Child = header;
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var select = new Button
        {
            Content = "Выбрать", Style = Terminal.Style("IconButton"), Padding = new Thickness(6, 1, 6, 1),
            Background = Terminal.Brush("Control"), BorderBrush = Terminal.Brush("Outline"), FontSize = 11
        };
        AutomationProperties.SetName(select, $"Выбрать инструмент · ячейка {number}");
        var home = Terminal.IconButton("latest", "К последним свечам");
        select.Click += (_, _) => choose();
        home.Click += (_, _) => plot.Reset();
        pin.Click += (_, _) => { SetPinned(Choice?.Pinned != true); save(); };
        clear.Click += (_, _) =>
        {
            Choice = null;
            market = null;
            latestSignal = null;
            ResetFeed();
            save();
            select.Focus();
        };
        actions.Children.Add(select);
        pin.Margin = home.Margin = clear.Margin = new Thickness(2, 0, 0, 0);
        actions.Children.Add(pin);
        actions.Children.Add(home);
        actions.Children.Add(clear);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        DockPanel.SetDock(signalIcon, Dock.Left);
        header.Children.Add(signalIcon);
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 7, 0);
        header.Children.Add(label);
        DockPanel.SetDock(headerFrame, Dock.Top);
        DockPanel.SetDock(frameBar, Dock.Top);
        DockPanel.SetDock(detail, Dock.Bottom);
        layout.Children.Add(headerFrame);
        layout.Children.Add(frameBar);
        layout.Children.Add(detail);
        layout.Children.Add(plot);
        UpdateHeader();
        UpdateFrames();
    }

    public void Activate(bool visible)
    {
        if (active == visible) return;
        active = visible;
        ResetFeed();
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

    public void ShowSignal(Signal signal)
    {
        if (!autoAssigned) return;
        latestSignal = signal;
        if (market != null) UpdateHeader();
    }

    private string? InstrumentTooltip() => latestSignal is not { } s ? market?.Label :
        $"{market?.Label}\n{(s.Module == SignalModule.Price ? "Импульс цены" : "Импульс объёма")} · " +
        (s.Module == SignalModule.Price ? $"+{s.Value:F2}%" : $"{s.Value:N2} USD") + $" за {MainWindow.WindowName(s.Seconds)}";

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
            label.Inlines.Add(new System.Windows.Documents.Run(" " + market.Venue.Label() +
                (market.Venue == Venue.Alpha || market.Contract != null ? $" · {market.Symbol}" : ""))
            {
                Foreground = Terminal.Brush("Muted"), FontWeight = FontWeights.Normal, FontSize = 11
            });
        }
        label.ToolTip = InstrumentTooltip() ?? Choice?.Key;
        signalIcon.Visibility = latestSignal == null ? Visibility.Collapsed : Visibility.Visible;
        signalIcon.Content = latestSignal == null ? null : Terminal.Icon(latestSignal.Module == SignalModule.Price ? "activity" : "volume");
        signalIcon.Foreground = Terminal.Brush(latestSignal?.Module == SignalModule.Volume ? "Rising" : "Accent");
        signalIcon.ToolTip = latestSignal == null ? null : InstrumentTooltip();
        UpdatePin();
        detail.Text = market == null && Choice != null ? "Инструмент вне доступного списка" : "";
        detail.ToolTip = null;
        AutomationProperties.SetName(plot, market == null ? "" : $"Свечи {Frames.Label(frame)} · {market.Label}");
    }

    private void ResetFeed()
    {
        refresh?.Cancel();
        refresh = null;
        olderRequested = false;
        plot.Frame = frame;
        plot.Set([], reset: true);
        UpdateHeader();
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
        foreach (int value in frameButtons())
            Add(value, frameBar);
        var more = new Button { Content = frameButtons().Contains(frame) ? "▾" : Frames.Label(frame) + " ▾",
            Style = Terminal.Style("IconButton"), ToolTip = "Все таймфреймы" };
        AutomationProperties.SetName(more, "Все таймфреймы");
        var menu = new ContextMenu { Background = Terminal.Brush("Surface"), Foreground = Terminal.Brush("Text") };
        foreach (int value in Frames.All)
        {
            var item = new MenuItem { Header = Frames.Label(value), IsCheckable = true, IsChecked = value == frame };
            item.Click += (_, _) => SetFrame(value);
            menu.Items.Add(item);
        }
        more.ContextMenu = menu;
        more.Click += (_, _) => { menu.PlacementTarget = more; menu.IsOpen = true; };
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
        var candles = new SortedDictionary<long, Candle>();
        var live = new ConcurrentQueue<Trade>();
        bool native = tf >= 60 || ExchangeApi.NativeSeconds(instrument);
        bool finished = false;
        int emptyPages = 0;
        string coverage = "";
        long nextPage = Boundary(tf);
        long nextTick = 0;
        detail.Text = "Загрузка свечей…";
        try
        {
            if (!native) await LoadSeconds(instrument, tf, ct);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (native && olderRequested && !finished && candles.Count > 0)
                    {
                        olderRequested = false;
                        bool found = await Page(nextPage, 180, prepend: true);
                        if (found) emptyPages = 0;
                        else if (++emptyPages >= 3)
                        {
                            finished = true;
                            coverage = "Конец доступной истории · три участка без свечей";
                        }
                        else coverage = "На предыдущем участке свечей нет · повторите движение влево";
                    }
                    if (Environment.TickCount64 >= nextTick)
                    {
                        nextTick = Environment.TickCount64 + (tf < 60 ? 4000 : 12000);
                        if (native) await Page(Boundary(tf), candles.Count == 0 ? (tf < 60 ? 100 : 180) : 4, prepend: false);
                        else UpdateTrades();
                    }
                    else if (!native) UpdateTrades();
                    if (native && candles.Count >= 12000) { finished = true; coverage = "Достигнут локальный предел 12 000 свечей"; }
                    detail.Text = $"{Frames.Label(tf)} · " + (coverage.Length > 0 ? coverage :
                        candles.Count == 0 ? "Биржа не вернула свечи" : "Колесо — масштаб · ЛКМ — сдвиг · шкалы — растяжение");
                    detail.ToolTip = null;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    detail.Text = candles.Count == 0 ? "Свечи недоступны · повтор через 12 с" : "Свечи не обновились · повтор через 12 с";
                    detail.ToolTip = ex is OperationCanceledException ? "Тайм-аут" : ex.Message;
                    await Task.Delay(12000, ct);
                }
                await Task.Delay(native ? 300 : 450, ct);
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or HttpRequestException) { }
        finally { source.Dispose(); }

        async Task<bool> Page(long before, int count, bool prepend)
        {
            if (before <= 0) { finished = true; return false; }
            var result = await api.CandlePage(instrument, tf, before, count, ct);
            ct.ThrowIfCancellationRequested();
            if (prepend || candles.Count == 0) nextPage = result.Length > 0 ? result[0].Time : before - tf * count * 1000L;
            foreach (var c in result) candles[c.Time] = c;
            while (candles.Count > 12000) candles.Remove(candles.Keys.First());
            if (result.Length > 0)
            {
                if (prepend || !finished && emptyPages == 0) coverage = "";
                plot.Set(candles.Values.ToArray());
            }
            return result.Length > 0;
        }

        void UpdateTrades()
        {
            var batch = new List<Trade>();
            while (live.TryDequeue(out var trade)) batch.Add(trade);
            if (batch.Count == 0) return;
            foreach (var c in ExchangeApi.TradeBars(batch, tf))
                candles[c.Time] = candles.TryGetValue(c.Time, out var old)
                    ? new(c.Time, old.Open, Math.Max(old.High, c.High), Math.Min(old.Low, c.Low), c.Close) : c;
            while (candles.Count > 12000) candles.Remove(candles.Keys.First());
            plot.Set(candles.Values.ToArray());
        }

        async Task LoadSeconds(Market selected, int seconds, CancellationToken token)
        {
            coverage = "Сделки REST/WS · более ранняя история биржей не предоставляется";
            try
            {
                foreach (var c in ExchangeApi.TradeBars(await api.RecentTrades(selected, token), seconds)) candles[c.Time] = c;
                token.ThrowIfCancellationRequested();
                plot.Set(candles.Values.ToArray());
            }
            catch (Exception) when (!token.IsCancellationRequested) { coverage = "История сделок недоступна · только новые сделки"; }
            if (selected.Streaming) _ = StreamSeconds(selected, live, token);
        }
    }

    private static long Boundary(int tf) => (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / (tf * 1000L) + 1) * tf * 1000L;

    private async Task StreamSeconds(Market selected, ConcurrentQueue<Trade> queue, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await MarketStreams.Open(api, [selected], ct, () => { }, t =>
                    {
                        if (!ct.IsCancellationRequested && t.Symbol.Equals(selected.Symbol, StringComparison.OrdinalIgnoreCase) &&
                            t.Time >= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000 && queue.Count < 10000) queue.Enqueue(t);
                    });
                }
                catch (Exception) when (!ct.IsCancellationRequested) { }
                await Task.Delay(5000, ct);
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
    }
}

internal sealed class CandlePlot(Func<ChartGesture> gesture) : FrameworkElement
{
    private const double Left = 7, Top = 7;
    public int Frame { get; set; } = 60;
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
    private readonly Typeface typeface = new(new FontFamily("Cascadia Mono, Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private Candle[] candles = [];
    private double visible = 55;
    private double offset;
    private (double Low, double High)? fixedPrice;
    private Drag? drag;
    private Point rulerEnd;
    private enum Operation { Pan, Time, Price, Ruler }
    private sealed record Drag(Operation Mode, Point Start, View View, double Visible, double Offset, MouseButton? Button);
    private readonly record struct View(Rect Plot, double Start, double End, double Low, double High)
    {
        public double Span => High - Low;
        public double Price(double y) => Math.Max(0, High - (y - Plot.Top) / Plot.Height * Span);
        public double Y(double price) => Plot.Top + (High - price) / Span * Plot.Height;
        public Point Bound(Point point) => new(Math.Clamp(point.X, Plot.Left, Plot.Right), Math.Clamp(point.Y, Plot.Top, Plot.Bottom));
    }

    public void Set(Candle[] value, bool reset = false)
    {
        if (!reset && offset > 0 && candles.Length > 0 && value.Length > 0)
            offset += Math.Max(0, (value[^1].Time - candles[^1].Time) / (double)BarTime);
        candles = value;
        if (reset) Reset();
        else
        {
            offset = ClampOffset(offset, visible);
            InvalidateVisual();
        }
    }

    public void Reset()
    {
        CancelGesture();
        visible = 55;
        offset = 0;
        fixedPrice = null;
        InvalidateVisual();
    }

    private View? CurrentView()
    {
        if (ActualWidth < 100 || ActualHeight < 48 || candles.Length == 0) return null;
        double right = ActualWidth - (ActualWidth < 160 ? 46 : 70);
        var rect = new Rect(Left, Top, right - Left, ActualHeight - Top - 17);
        double end = candles[^1].Time + BarTime - offset * BarTime;
        double start = end - visible * BarTime;
        double low = double.MaxValue, high = 0;
        foreach (var c in candles)
        {
            if (c.Time + BarTime <= start || c.Time >= end) continue;
            low = Math.Min(low, c.Low);
            high = Math.Max(high, c.High);
        }
        if (high == 0) { low = candles[0].Low; high = candles[0].High; }
        double margin = Math.Max((high - low) * 0.08, high * 0.00015);
        return new(rect, start, end, fixedPrice?.Low ?? Math.Max(0, low - margin), fixedPrice?.High ?? high + margin);
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (CurrentView() is not { } view) return;
        var plot = view.Plot;
        double step = plot.Width * BarTime / (view.End - view.Start);
        double body = Math.Max(1, step * 0.67);
        int rows = ActualHeight < 105 ? 3 : 5;
        int columns = ActualWidth > 325 ? 4 : ActualWidth > 190 ? 3 : 2;
        for (int i = 0; i < rows; i++)
        {
            double fraction = (double)i / (rows - 1);
            double y = plot.Top + plot.Height * fraction;
            dc.DrawLine(gridLine, new Point(plot.Left, y), new Point(plot.Right, y));
            DrawText(dc, FormatPrice(view.High - view.Span * fraction, view.Span / (rows - 1)), plot.Right + 5, y - 6);
        }
        for (int i = 0; i < columns; i++)
        {
            double fraction = (double)i / (columns - 1);
            double x = plot.Left + plot.Width * fraction;
            dc.DrawLine(gridLine, new Point(x, plot.Top), new Point(x, plot.Bottom));
            long time = (long)(view.Start + fraction * (view.End - view.Start));
            DrawText(dc, DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime().ToString(Frame < 60 ? "HH:mm:ss" : Frame >= 3600 ? "dd.MM HH:mm" : "HH:mm"),
                x, ActualHeight - 15, i == 0 ? 0 : i == columns - 1 ? 1 : 0.5);
        }

        dc.PushClip(new RectangleGeometry(plot));
        foreach (var c in candles)
        {
            if (c.Time + BarTime <= view.Start || c.Time >= view.End) continue;
            double x = plot.Left + ((c.Time - view.Start) / (double)BarTime + 0.5) * step;
            bool rising = c.Close >= c.Open;
            dc.DrawLine(rising ? upLine : downLine, new Point(x, view.Y(c.High)), new Point(x, view.Y(c.Low)));
            dc.DrawRectangle(rising ? up : down, null,
                new Rect(x - body / 2, Math.Min(view.Y(c.Open), view.Y(c.Close)), body, Math.Max(1, Math.Abs(view.Y(c.Close) - view.Y(c.Open)))));
        }
        dc.Pop();
        if (drag is { Mode: Operation.Ruler } measure) DrawRuler(dc, measure);
    }

    private void DrawRuler(DrawingContext dc, Drag measure)
    {
        var view = measure.View;
        var a = view.Bound(measure.Start);
        var b = view.Bound(rulerEnd);
        double first = view.Price(a.Y), last = view.Price(b.Y);
        double change = PriceChange(first, last);
        dc.DrawLine(rulerLine, a, new Point(b.X, a.Y));
        dc.DrawLine(rulerLine, new Point(b.X, a.Y), b);
        dc.DrawEllipse(accent, null, a, 3, 3);
        dc.DrawEllipse(accent, null, b, 3, 3);
        string percent = double.IsFinite(change) ? change.ToString("+0.00;-0.00;0.00", Russian) + "%" : "—";
        string text = $"{FormatPrice(first, view.Span / 100)} → {FormatPrice(last, view.Span / 100)}   {percent}";
        var formatted = Text(text, accent);
        double x = Math.Clamp(b.X + 7, view.Plot.Left, Math.Max(view.Plot.Left, ActualWidth - formatted.Width - 14));
        double y = Math.Clamp(b.Y - formatted.Height - 9, view.Plot.Top, Math.Max(view.Plot.Top, view.Plot.Bottom - formatted.Height - 6));
        dc.DrawRectangle(background, rulerLine, new Rect(x, y, formatted.Width + 10, formatted.Height + 6));
        dc.DrawText(formatted, new Point(x + 5, y + 3));
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (drag != null || CurrentView() is not { } view) return;
        double fraction = Math.Clamp((e.GetPosition(this).X - Left) / view.Plot.Width, 0, 1);
        double count = ClampCount(visible * Math.Pow(e.Delta > 0 ? .78 : 1.28, Math.Abs(e.Delta) / 120d));
        offset = ClampOffset(offset + (offset > 0 || fraction < .87 ? (visible - count) * (1 - fraction) : 0), count);
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
        if (inPlot && gesture().Mouse == e.ChangedButton)
        {
            Start(Operation.Ruler, at, view, e.ChangedButton);
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2 && inPlot) { Reset(); e.Handled = true; return; }
        if (inPlot) Start(Operation.Pan, at, view, MouseButton.Left);
        else if (at.Y >= view.Plot.Bottom && at.X <= view.Plot.Right) Start(Operation.Time, at, view, MouseButton.Left);
        else if (at.X >= view.Plot.Right && at.Y <= view.Plot.Bottom) Start(Operation.Price, at, view, MouseButton.Left);
        else return;
        e.Handled = true;
    }

    public bool BeginKeyboardRuler()
    {
        if (drag != null || !IsMouseOver || CurrentView() is not { } view) return false;
        var at = Mouse.GetPosition(this);
        if (!view.Plot.Contains(at)) return false;
        Start(Operation.Ruler, at, view, null);
        return true;
    }

    public void EndKeyboardRuler()
    {
        if (drag is { Mode: Operation.Ruler, Button: null }) CancelGesture();
    }

    internal static double PriceChange(double start, double end) =>
        start > 0 && double.IsFinite(start) && double.IsFinite(end) ? (end / start - 1) * 100 : double.NaN;

    private void Start(Operation mode, Point at, View view, MouseButton? button)
    {
        if (!CaptureMouse()) return;
        drag = new(mode, at, view, visible, offset, button);
        rulerEnd = at;
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag is not { } move) return;
        Point point = e.GetPosition(this);
        var view = move.View;
        double dx = (point.X - move.Start.X) / view.Plot.Width;
        double dy = (point.Y - move.Start.Y) / view.Plot.Height;
        switch (move.Mode)
        {
            case Operation.Ruler:
                rulerEnd = view.Bound(point);
                break;
            case Operation.Pan:
                offset = ClampOffset(move.Offset + dx * move.Visible, move.Visible);
                RequestOlder(dx > 0);
                SetPrice(view.Low + dy * view.Span, view.Span);
                break;
            case Operation.Time:
                visible = ClampCount(move.Visible * Math.Exp(Math.Clamp(dx * 2.3, -5, 5)));
                double anchor = Math.Clamp((move.Start.X - view.Plot.Left) / view.Plot.Width, 0, 1);
                offset = ClampOffset(move.Offset + (move.Visible - visible) * (1 - anchor), visible);
                RequestOlder(dx > 0);
                break;
            case Operation.Price:
                double span = view.Span * Math.Exp(Math.Clamp(dy * 2.3, -5, 5));
                double fromBottom = 1 - Math.Clamp((move.Start.Y - view.Plot.Top) / view.Plot.Height, 0, 1);
                SetPrice(view.Price(move.Start.Y) - fromBottom * span, span);
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (drag?.Button != e.ChangedButton) return;
        CancelGesture();
        e.Handled = true;
    }

    public void CancelGesture()
    {
        drag = null;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        drag = null;
        InvalidateVisual();
    }

    private void SetPrice(double low, double span)
    {
        low = Math.Max(0, low);
        if (double.IsFinite(low + span) && span > 0 && low + span > low) fixedPrice = (low, low + span);
    }

    private double ClampCount(double count)
    {
        double max = Math.Clamp(HistoryBars, 8, 1600);
        return Math.Clamp(count, Math.Min(8, max), max);
    }
    private double ClampOffset(double value, double count) => Math.Clamp(value, 0, Math.Max(0, HistoryBars - count));
    private double HistoryBars => candles.Length == 0 ? 0 : (candles[^1].Time - candles[0].Time) / (double)BarTime + 1;
    private void RequestOlder(bool backwards)
    {
        if (backwards && offset + visible >= HistoryBars - 6) OlderRequested?.Invoke();
    }

    private void DrawText(DrawingContext dc, string text, double x, double y, double anchor = 0)
    {
        var formatted = Text(text, axis);
        dc.DrawText(formatted, new Point(x - formatted.Width * anchor, y));
    }
    private FormattedText Text(string text, Brush foreground) => new(text, Russian, FlowDirection.LeftToRight,
        typeface, 11, foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string FormatPrice(double value, double tick)
    {
        if (value == 0) return "0";
        if (value < 0.00001 || value >= 10_000_000) return value.ToString("0.###e-0", Russian);
        int precision = value >= 100 ? 0 : value >= 0.01 ? 4 : 5;
        precision = Math.Max(precision, Math.Clamp((int)Math.Ceiling(-Math.Log10(tick)), 0, 8));
        return value.ToString($"F{precision}", Russian);
    }
}

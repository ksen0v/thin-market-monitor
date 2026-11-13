using System.Globalization;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed class ChartCell : Border
{
    private readonly ExchangeApi api;
    private readonly Func<long> nextOrder;
    private readonly CandlePlot plot;
    private readonly DockPanel layout = new();
    private readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly Button pin = Terminal.IconButton("pin-off", "Закрепить график");
    private readonly Button clear = Terminal.IconButton("close", "Очистить ячейку");
    private CancellationTokenSource? refresh;
    private Market? market;
    private bool active;
    private CellChoice? choice;
    private Signal? latestSignal;

    public CellChoice? Choice
    {
        get => choice;
        set { choice = value; UpdateHeader(); }
    }
    public long Order => Choice?.Order ?? 0;
    public double PlotHeight { set => plot.Height = value; }
    public bool HasMarket => market != null;
    public bool IsInteracting => plot.IsMouseCaptured;
    public CandlePlot Plot => plot;

    public ChartCell(ExchangeApi api, int number, Action choose, Action save, Func<long> nextOrder, Func<ChartGesture> gesture)
    {
        this.api = api;
        this.nextOrder = nextOrder;
        plot = new CandlePlot(gesture);
        Margin = new Thickness(0, 0, 1, 1);
        Padding = new Thickness(0, 0, 0, 4);
        Background = Terminal.Brush("Surface");
        detail.Foreground = Terminal.Brush("Muted");
        detail.MinHeight = 15;
        detail.Margin = new Thickness(7, 4, 7, 0);
        Child = layout;
        var headerFrame = Terminal.Rule(new Thickness(0, 0, 0, 1));
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
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 7, 0);
        header.Children.Add(label);
        DockPanel.SetDock(headerFrame, Dock.Top);
        DockPanel.SetDock(detail, Dock.Bottom);
        layout.Children.Add(headerFrame);
        layout.Children.Add(detail);
        layout.Children.Add(plot);
        UpdateHeader();
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
        Choice = new(selected.Key, pinned, nextOrder());
        ResetFeed();
    }

    public void ShowSignal(Signal signal)
    {
        latestSignal = signal;
        if (market != null) label.ToolTip = InstrumentTooltip();
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
                (market.Venue == Venue.Alpha ? $" · {market.Symbol}" : ""))
            {
                Foreground = Terminal.Brush("Muted"), FontWeight = FontWeights.Normal, FontSize = 11
            });
        }
        label.ToolTip = InstrumentTooltip() ?? Choice?.Key;
        UpdatePin();
        detail.Text = market == null && Choice != null ? "Инструмент вне доступного списка" : "";
        detail.ToolTip = null;
        AutomationProperties.SetName(plot, market == null ? "" : $"Минутные свечи {market.Label}");
    }

    private void ResetFeed()
    {
        refresh?.Cancel();
        refresh = null;
        plot.Set([], reset: true);
        UpdateHeader();
        if (!active || market == null) return;
        refresh = new();
        _ = Load(market, refresh);
    }

    private async Task Load(Market instrument, CancellationTokenSource source)
    {
        var ct = source.Token;
        var candles = new SortedDictionary<long, Candle>();
        detail.Text = "Загрузка минутных свечей…";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var next = await api.Candles(instrument, candles.Count > 0, ct);
                    ct.ThrowIfCancellationRequested();
                    foreach (var candle in next) candles[candle.Time] = candle;
                    while (candles.Count > 240) candles.Remove(candles.Keys.First());
                    plot.Set(candles.Values.ToArray());
                    detail.Text = candles.Count == 0 ? "Биржа не вернула свечи" : "1 мин · колесо — масштаб · ЛКМ — сдвиг · шкалы — растяжение";
                    detail.ToolTip = null;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    detail.Text = candles.Count == 0 ? "Свечи недоступны · повтор через 12 с" : "Свечи не обновились · повтор через 12 с";
                    detail.ToolTip = ex is OperationCanceledException ? "Тайм-аут" : ex.Message;
                }
                await Task.Delay(12_000, ct);
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is OperationCanceledException or ObjectDisposedException or HttpRequestException) { }
        finally { source.Dispose(); }
    }
}

internal sealed class CandlePlot(Func<ChartGesture> gesture) : FrameworkElement
{
    private const double Left = 7, Top = 7;
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
            offset += Math.Max(0, (value[^1].Time - candles[^1].Time) / 60_000d);
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
        double end = candles[^1].Time + 60_000 - offset * 60_000;
        double start = end - visible * 60_000;
        double low = double.MaxValue, high = 0;
        foreach (var c in candles)
        {
            if (c.Time + 60_000 <= start || c.Time >= end) continue;
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
        double step = plot.Width * 60_000 / (view.End - view.Start);
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
            DrawText(dc, DateTimeOffset.FromUnixTimeMilliseconds(time).ToLocalTime().ToString("HH:mm"),
                x, ActualHeight - 15, i == 0 ? 0 : i == columns - 1 ? 1 : 0.5);
        }

        dc.PushClip(new RectangleGeometry(plot));
        foreach (var c in candles)
        {
            if (c.Time + 60_000 <= view.Start || c.Time >= view.End) continue;
            double x = plot.Left + ((c.Time - view.Start) / 60_000d + 0.5) * step;
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
                SetPrice(view.Low + dy * view.Span, view.Span);
                break;
            case Operation.Time:
                visible = ClampCount(move.Visible * Math.Exp(Math.Clamp(dx * 2.3, -5, 5)));
                double anchor = Math.Clamp((move.Start.X - view.Plot.Left) / view.Plot.Width, 0, 1);
                offset = ClampOffset(move.Offset + (move.Visible - visible) * (1 - anchor), visible);
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
        double max = Math.Clamp(HistoryMinutes, 8, 10080);
        return Math.Clamp(count, Math.Min(8, max), max);
    }
    private double ClampOffset(double value, double count) => Math.Clamp(value, 0, Math.Max(0, HistoryMinutes - count));
    private double HistoryMinutes => candles.Length == 0 ? 0 : (candles[^1].Time - candles[0].Time) / 60_000d + 1;

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

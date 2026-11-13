using System.Globalization;
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
    private readonly CandlePlot plot = new();
    private readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly Button pin = Terminal.IconButton("pin-off", "Закрепить график");
    private readonly Button clear = Terminal.IconButton("close", "Очистить ячейку");
    private CancellationTokenSource? refresh;
    private Market? market;
    private bool active;
    private CellChoice? choice;

    public CellChoice? Choice
    {
        get => choice;
        set { choice = value; UpdateHeader(); }
    }
    public long Order => Choice?.Order ?? 0;
    public double PlotHeight { set => plot.Height = value; }

    public ChartCell(ExchangeApi api, int number, Action choose, Action save, Func<long> nextOrder)
    {
        this.api = api;
        this.nextOrder = nextOrder;
        Margin = new Thickness(0, 0, 1, 1);
        Padding = new Thickness(0, 0, 0, 4);
        Background = Terminal.Brush("Surface");
        detail.Foreground = Terminal.Brush("Muted");
        detail.MinHeight = 15;
        detail.Margin = new Thickness(7, 4, 7, 0);
        var layout = new StackPanel();
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
        layout.Children.Add(headerFrame);
        layout.Children.Add(plot);
        layout.Children.Add(detail);
        UpdateHeader();
    }

    public void Activate(bool visible)
    {
        if (active == visible) return;
        active = visible;
        ResetFeed();
    }

    public void Assign(Market selected, bool pinned)
    {
        market = selected;
        Choice = new(selected.Key, pinned, nextOrder());
        ResetFeed();
    }

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
        label.ToolTip = market?.Label ?? Choice?.Key;
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
                    detail.Text = candles.Count == 0 ? "Биржа не вернула свечи" : "1 мин · колесо — масштаб · перетаскивание — история";
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { source.Dispose(); }
    }
}

internal sealed class CandlePlot : FrameworkElement
{
    private readonly Brush background = Terminal.Brush("Plot");
    private readonly Brush up = Terminal.Brush("Rising");
    private readonly Brush down = Terminal.Brush("Falling");
    private readonly Brush axis = Terminal.Brush("Muted");
    private readonly Pen gridLine = new(Terminal.Brush("GridLine"), 1);
    private readonly Pen upLine = new(Terminal.Brush("Rising"), 1);
    private readonly Pen downLine = new(Terminal.Brush("Falling"), 1);
    private readonly Typeface typeface = new(new FontFamily("Cascadia Mono, Consolas"),
        FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    private Candle[] candles = [];
    private int visible = 55;
    private int offset;
    private Point? drag;
    private int dragOffset;

    public void Set(Candle[] value, bool reset = false)
    {
        if (!reset && offset > 0 && candles.Length > 0)
            offset += value.Count(c => c.Time > candles[^1].Time);
        candles = value;
        if (reset) Reset();
        else
        {
            offset = Math.Clamp(offset, 0, Math.Max(0, candles.Length - visible));
            InvalidateVisual();
        }
    }

    public void Reset()
    {
        visible = 55;
        offset = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
        if (width < 100 || height < 48 || candles.Length == 0) return;

        int end = candles.Length - offset;
        int begin = Math.Max(0, end - visible);
        double low = double.MaxValue, high = double.MinValue;
        for (int i = begin; i < end; i++)
        {
            low = Math.Min(low, candles[i].Low);
            high = Math.Max(high, candles[i].High);
        }
        double margin = Math.Max((high - low) * 0.08, high * 0.00015);
        low = Math.Max(0, low - margin);
        high += margin;
        const double left = 7, top = 7;
        double plotWidth = ChartWidth(width), plotHeight = height - top - 17;
        double right = left + plotWidth;
        long start = candles[begin].Time, finish = candles[end - 1].Time;
        double step = plotWidth * 60_000 / Math.Max(60_000, finish - start + 60_000);
        double body = Math.Max(1, step * 0.67);
        double Y(double p) => top + (high - p) / (high - low) * plotHeight;

        int rows = height < 105 ? 3 : 5;
        int columns = width > 325 ? 4 : width > 190 ? 3 : 2;
        int precision = high >= 100 ? 0 : high >= 0.01 ? 4 : 5;
        double tickSpacing = (high - low) / (rows - 1);
        precision = Math.Max(precision, Math.Clamp((int)Math.Ceiling(-Math.Log10(tickSpacing)), 0, 8));
        for (int i = 0; i < rows; i++)
        {
            double fraction = (double)i / (rows - 1);
            double y = top + plotHeight * fraction;
            dc.DrawLine(gridLine, new Point(left, y), new Point(right, y));
            double price = high - (high - low) * fraction;
            string tick = price is > 0 and < 0.00001 ? price.ToString("0.##e-0", Russian) : price.ToString($"F{precision}", Russian);
            DrawText(dc, tick, right + 5, y - 6);
        }
        for (int i = 0; i < columns; i++)
        {
            double fraction = (double)i / (columns - 1);
            double x = left + plotWidth * fraction;
            dc.DrawLine(gridLine, new Point(x, top), new Point(x, top + plotHeight));
            long time = start + (long)(fraction * (finish - start));
            DrawText(dc, Time(time), x, height - 15, i == 0 ? 0 : i == columns - 1 ? 1 : 0.5);
        }

        for (int i = begin; i < end; i++)
        {
            var c = candles[i];
            double x = left + ((c.Time - start) / 60_000d + 0.5) * step;
            bool rising = c.Close >= c.Open;
            dc.DrawLine(rising ? upLine : downLine, new Point(x, Y(c.High)), new Point(x, Y(c.Low)));
            dc.DrawRectangle(rising ? up : down, null,
                new Rect(x - body / 2, Math.Min(Y(c.Open), Y(c.Close)), body, Math.Max(1, Math.Abs(Y(c.Close) - Y(c.Open)))));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (candles.Length < 3) return;
        double fraction = Math.Clamp((e.GetPosition(this).X - 7) / ChartWidth(ActualWidth), 0, 1);
        int oldCount = Math.Min(visible, candles.Length - offset);
        int newCount = Math.Clamp((int)Math.Round(oldCount * (e.Delta > 0 ? 0.78 : 1.28)),
            Math.Min(8, candles.Length), candles.Length);
        if (offset > 0 || fraction < 0.87)
            offset = Math.Clamp(offset + (int)Math.Round((oldCount - Math.Min(newCount, candles.Length)) * (1 - fraction)),
                0, Math.Max(0, candles.Length - newCount));
        visible = newCount;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Reset(); return; }
        if (candles.Length == 0) return;
        drag = e.GetPosition(this);
        dragOffset = offset;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag == null || e.LeftButton != MouseButtonState.Pressed) return;
        double movement = (e.GetPosition(this).X - drag.Value.X) / ChartWidth(ActualWidth);
        offset = Math.Clamp(dragOffset + (int)Math.Round(movement * visible), 0, Math.Max(0, candles.Length - visible));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        drag = null;
        ReleaseMouseCapture();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) => drag = null;

    private void DrawText(DrawingContext dc, string text, double x, double y, double anchor = 0)
    {
        var formatted = new FormattedText(text, Russian, FlowDirection.LeftToRight,
            typeface, 11, axis, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x - formatted.Width * anchor, y));
    }

    private static double ChartWidth(double width) => Math.Max(1, width - 7 - (width < 160 ? 46 : 58));
    private static string Time(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("HH:mm");
}

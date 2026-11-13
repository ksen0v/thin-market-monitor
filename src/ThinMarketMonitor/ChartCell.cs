using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed class ChartCell : Border
{
    private readonly ExchangeApi api;
    private readonly Func<Market?> choose;
    private readonly Action save;
    private readonly Func<long> nextOrder;
    private readonly CandlePlot plot = new();
    private readonly TextBlock label = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock detail = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly Button pin = new() { Padding = new Thickness(6, 2, 6, 2) };
    private readonly Button clear = new() { Content = "×", Padding = new Thickness(6, 2, 6, 2), ToolTip = "Очистить ячейку" };
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

    public ChartCell(ExchangeApi api, Func<Market?> choose, Action save, Func<long> nextOrder)
    {
        this.api = api;
        this.choose = choose;
        this.save = save;
        this.nextOrder = nextOrder;
        Margin = new Thickness(3);
        Padding = new Thickness(8, 6, 8, 5);
        Background = (Brush)Application.Current.FindResource("Surface");
        BorderBrush = (Brush)Application.Current.FindResource("Outline");
        BorderThickness = new Thickness(1);
        detail.Foreground = (Brush)Application.Current.FindResource("Muted");
        var layout = new DockPanel();
        Child = layout;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var select = new Button { Content = "Выбрать", Padding = new Thickness(6, 2, 6, 2) };
        var home = new Button { Content = "↦", Padding = new Thickness(6, 2, 6, 2), ToolTip = "К последним свечам" };
        select.Click += (_, _) => { if (this.choose() is { } picked) { Assign(picked, true); this.save(); } };
        home.Click += (_, _) => plot.Reset();
        pin.Click += (_, _) => { SetPinned(Choice?.Pinned != true); this.save(); };
        clear.Click += (_, _) => { Choice = null; market = null; ResetFeed(); this.save(); };
        actions.Children.Add(select);
        actions.Children.Add(pin);
        actions.Children.Add(home);
        actions.Children.Add(clear);
        DockPanel.SetDock(actions, Dock.Right);
        header.Children.Add(actions);
        label.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(label);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        DockPanel.SetDock(detail, Dock.Bottom);
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

    public void SetPinned(bool pinned)
    {
        if (Choice == null) return;
        Choice = Choice with { Pinned = pinned };
    }

    private void UpdateHeader()
    {
        label.Text = market?.Label ?? Choice?.Key ?? "Свободная ячейка";
        label.ToolTip = label.Text;
        clear.IsEnabled = pin.IsEnabled = Choice != null;
        pin.Content = Choice?.Pinned == true ? "Снять" : "Закрепить";
        pin.ToolTip = Choice?.Pinned == true ? "Снять закрепление" : "Закрепить график";
        detail.Text = market == null && Choice != null ? "Инструмент вне доступного списка" : "";
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
    private static readonly Brush Up = Brush("#52CEAF");
    private static readonly Brush Down = Brush("#F1848B");
    private static readonly Brush Axis = Brush("#A3B3C8");
    private static readonly Pen GridLine = Pen("#29364B");
    private static readonly Pen UpLine = Pen("#52CEAF");
    private static readonly Pen DownLine = Pen("#F1848B");
    private readonly Typeface typeface = new("Segoe UI");
    private Candle[] candles = [];
    private int visible = 55;
    private int offset;
    private Point? drag;
    private int dragOffset;

    public void Set(Candle[] value, bool reset = false)
    {
        if (!reset && offset > 0 && candles.Length > 0 && value.Length > 0)
            offset += (int)Math.Max(0, (value[^1].Time - candles[^1].Time) / 60_000);
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
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        if (width < 100 || height < 48 || candles.Length == 0) return;

        int end = candles.Length - offset;
        int begin = Math.Max(0, end - visible);
        int count = end - begin;
        double low = double.MaxValue, high = double.MinValue;
        for (int i = begin; i < end; i++)
        {
            low = Math.Min(low, candles[i].Low);
            high = Math.Max(high, candles[i].High);
        }
        double margin = Math.Max((high - low) * 0.09, high * 0.00015);
        low = Math.Max(0, low - margin);
        high += margin;
        double plotWidth = width - 76;
        double plotHeight = height - 18;
        long startTime = candles[begin].Time;
        long span = Math.Max(60_000, candles[end - 1].Time - startTime + 60_000);
        double step = plotWidth * 60_000 / span;
        double body = Math.Max(1, step * 0.68);
        double Y(double p) => (high - p) / (high - low) * plotHeight;

        for (int i = 0; i <= 4; i++)
        {
            double y = 1 + (plotHeight - 2) * i / 4;
            dc.DrawLine(GridLine, new Point(0, y), new Point(plotWidth, y));
            DrawText(dc, Price(high - (high - low) * i / 4), plotWidth + 5, Math.Clamp(y - 6, 0, plotHeight - 12));
        }
        for (int i = begin; i < end; i++)
        {
            var c = candles[i];
            double x = ((c.Time - startTime) / 60_000d + 0.5) * step;
            bool up = c.Close >= c.Open;
            dc.DrawLine(up ? UpLine : DownLine, new Point(x, Y(c.High)), new Point(x, Y(c.Low)));
            dc.DrawRectangle(up ? Up : Down, null,
                new Rect(x - body / 2, Math.Min(Y(c.Open), Y(c.Close)), body, Math.Max(1, Math.Abs(Y(c.Close) - Y(c.Open)))));
        }
        DrawText(dc, Time(candles[begin].Time), 0, plotHeight + 3);
        if (count > 1) DrawText(dc, Time(candles[end - 1].Time), Math.Max(32, plotWidth - 32), plotHeight + 3);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (candles.Length < 3) return;
        double fraction = Math.Clamp(e.GetPosition(this).X / Math.Max(1, ActualWidth - 76), 0, 1);
        int oldCount = Math.Min(visible, candles.Length - offset);
        int newCount = Math.Clamp((int)Math.Round(visible * (e.Delta > 0 ? 0.78 : 1.28)), 8, 240);
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
        drag = e.GetPosition(this);
        dragOffset = offset;
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (drag == null || e.LeftButton != MouseButtonState.Pressed) return;
        double movement = (e.GetPosition(this).X - drag.Value.X) / Math.Max(1, ActualWidth - 76);
        offset = Math.Clamp(dragOffset + (int)Math.Round(movement * visible), 0, Math.Max(0, candles.Length - visible));
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        drag = null;
        ReleaseMouseCapture();
    }

    private void DrawText(DrawingContext dc, string text, double x, double y)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, 10, Axis, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(formatted, new Point(x, y));
    }

    private static string Time(long value) => DateTimeOffset.FromUnixTimeMilliseconds(value).ToLocalTime().ToString("HH:mm");
    private static string Price(double value) => value < 0.00001 ? value.ToString("0.####e-0") : value.ToString("0.########");
    private static SolidColorBrush Brush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
    private static Pen Pen(string color)
    {
        var pen = new Pen(Brush(color), 1);
        pen.Freeze();
        return pen;
    }
}

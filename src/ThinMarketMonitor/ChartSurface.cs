using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ThinMarketMonitor;

internal sealed class ChartSurface : Panel
{
    private const double ButtonWidth = 22;
    internal const double AlertButtonSize = 19;
    internal const double ControlsWidth = 5 * ButtonWidth;
    private readonly CandlePlot plot;
    private readonly Action<Venue, int> move;
    private readonly Action<Venue> close;
    private readonly Action<ChartPaneWeights?> save;
    private readonly Action<Venue> toggleCurrency;
    private readonly Action<Venue> toggleStyle;
    private readonly Dictionary<Venue, PaneControls> controls = [];
    private string? marketKey;
    private Venue[] order = [];
    private ChartPaneWeights? weights, preview;
    private ResizeGesture? resize;
    private readonly Button addAlert;
    private readonly TranslateTransform alertPosition = new();
    private (Rect Bounds, double Price)? alertTarget;

    private sealed record PaneControls(Thumb Divider, StackPanel Buttons, Button Up, Button Down, Button Currency, Button Style);
    private sealed record ResizeGesture(Thumb Divider, int Boundary, double StartY, double[] Heights);

    internal ChartSurface(CandlePlot plot, Action<Venue, int> move, Action<Venue> close, Action<ChartPaneWeights?> save,
        Action<Venue> toggleCurrency, Action<Venue> toggleStyle, Action<double> placeAlert)
    {
        this.plot = plot;
        this.move = move;
        this.close = close;
        this.save = save;
        this.toggleCurrency = toggleCurrency;
        this.toggleStyle = toggleStyle;
        Children.Add(plot);
        addAlert = new Button
        {
            Content = "+", Style = Terminal.Style("TerminalButton"), Width = AlertButtonSize, Height = AlertButtonSize,
            MinHeight = 0, Padding = new Thickness(0), Background = Terminal.Brush("AccentSurface"),
            Foreground = Terminal.Brush("Accent"), Visibility = Visibility.Hidden, RenderTransform = alertPosition,
            ToolTip = "Поставить ценовое оповещение"
        };
        AutomationProperties.SetName(addAlert, "Поставить ценовое оповещение");
        addAlert.Click += (_, _) =>
        {
            if (alertTarget is { } target) placeAlert(target.Price);
        };
        Children.Add(addAlert);
        plot.AlertTargetChanged += SetAlertTarget;
        plot.KeepCrosshairOnAlert = () => alertTarget is { } target && target.Bounds.Contains(Mouse.GetPosition(plot));
        MouseLeave += (_, _) => plot.HideCrosshair();
        plot.InterestPanesChanged += UpdatePanes;
    }

    private void SetAlertTarget((Rect Bounds, double Price)? target)
    {
        if (alertTarget == target) return;
        alertTarget = target;
        addAlert.Visibility = target == null ? Visibility.Hidden : Visibility.Visible;
        if (target is not { } current) return;
        alertPosition.X = current.Bounds.X;
        alertPosition.Y = current.Bounds.Y;
    }

    internal bool IsResizing => resize != null;
    internal void CancelResize() => resize?.Divider.CancelDrag();
    internal static bool IsPaneDivider(IInputElement? element) =>
        element is Thumb grip && grip.Style == Terminal.Style("ChartPaneDivider");

    internal void RestoreWeights(ChartPaneWeights? value)
    {
        CancelResize();
        weights = value;
        ShowWeights(value);
    }

    private void ShowWeights(ChartPaneWeights? value)
    {
        plot.SetPaneWeights(value);
        InvalidateArrange();
    }

    private void CommitWeights(ChartPaneWeights? value)
    {
        RestoreWeights(value);
        save(value);
    }

    private void UpdatePanes(string? key, InterestLayer[] layers, ChartIndicatorOptions options)
    {
        if (key == marketKey && order.Length == layers.Length &&
            !order.Where((venue, index) => venue != layers[index].Venue).Any())
        {
            UpdatePresentation(options);
            return;
        }
        CancelResize();
        marketKey = key;
        order = layers.Select(layer => layer.Venue).ToArray();
        foreach (var removed in controls.Keys.Except(order).ToArray())
        {
            var pane = controls[removed];
            bool focused = pane.Buttons.IsKeyboardFocusWithin || pane.Divider.IsKeyboardFocusWithin;
            Children.Remove(pane.Divider);
            Children.Remove(pane.Buttons);
            controls.Remove(removed);
            if (focused) plot.Focus();
        }
        for (int index = 0; index < order.Length; index++)
        {
            var venue = order[index];
            if (!controls.TryGetValue(venue, out var pane)) controls.Add(venue, pane = CreateControls(venue));
            pane.Up.IsEnabled = index > 0;
            pane.Down.IsEnabled = index < order.Length - 1;
            KeyboardNavigation.SetTabIndex(pane.Divider, index * 2);
            KeyboardNavigation.SetTabIndex(pane.Buttons, index * 2 + 1);
        }
        UpdatePresentation(options);
        InvalidateMeasure();
    }

    private void UpdatePresentation(ChartIndicatorOptions options)
    {
        foreach (var (venue, pane) in controls)
        {
            var presentation = options.PresentationFor(venue);
            pane.Currency.Foreground = Terminal.Brush(presentation.Usd ? "Accent" : "Text");
            pane.Currency.ToolTip = presentation.Usd ? "Показать в единицах контракта" : "Показать в USD";
            AutomationProperties.SetItemStatus(pane.Currency, presentation.Usd ? "USD" : "Единицы контракта");
            if (pane.Style.Tag is bool shown && shown == presentation.Candles) continue;
            pane.Style.Tag = presentation.Candles;
            pane.Style.Content = Terminal.Icon(presentation.Candles ? "candles" : "line-chart");
            pane.Style.ToolTip = presentation.Candles ? "Свечи по замерам OI · переключить на линию" : "Переключить на свечи по замерам OI";
            AutomationProperties.SetItemStatus(pane.Style, presentation.Candles ? "Свечи" : "Линия");
        }
    }

    private PaneControls CreateControls(Venue venue)
    {
        string source = venue.Label();
        var divider = new Thumb
        {
            Style = Terminal.Style("ChartPaneDivider"), Cursor = Cursors.SizeNS, Focusable = true,
            ToolTip = "Потяните, чтобы изменить высоту. Двойной щелчок возвращает автоматические размеры."
        };
        AutomationProperties.SetName(divider, $"Высота панели OI · {source}");
        divider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount != 2) return;
            if (weights != null) CommitWeights(null);
            e.Handled = true;
        };
        divider.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down)) return;
            var changed = ChartPaneLayout.Resize(plot.MeasurePanes(ActualHeight), order, Array.IndexOf(order, venue),
                e.Key == Key.Up ? -12 : 12, weights);
            if (changed != null) CommitWeights(changed);
            e.Handled = true;
        };
        divider.DragStarted += (_, _) =>
        {
            plot.CancelGesture();
            resize = new(divider, Array.IndexOf(order, venue), Mouse.GetPosition(this).Y, plot.MeasurePanes(ActualHeight));
        };
        divider.DragDelta += (_, _) => PreviewResize(divider, Mouse.GetPosition(this).Y);
        divider.DragCompleted += (_, e) =>
        {
            if (resize?.Divider != divider) return;
            var result = preview;
            preview = null;
            resize = null;
            if (!e.Canceled && result != null) CommitWeights(result);
            else ShowWeights(weights);
        };

        Button up = ActionButton("chevron-up", $"Поднять панель OI · {source}", () => move(venue, -1));
        Button down = ActionButton("chevron", $"Опустить панель OI · {source}", () => move(venue, 1));
        Button remove = ActionButton("close", $"Закрыть панель OI · {source}", () => close(venue));
        Button currency = ActionButton("dollar", $"Единицы OI · {source}", () => toggleCurrency(venue));
        Button style = ActionButton("candles", $"Вид графика OI · {source}", () => toggleStyle(venue));
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(style);
        actions.Children.Add(currency);
        actions.Children.Add(up);
        actions.Children.Add(down);
        actions.Children.Add(remove);
        Children.Add(divider);
        Children.Add(actions);
        return new(divider, actions, up, down, currency, style);
    }

    private void PreviewResize(Thumb divider, double y)
    {
        if (resize is not { } drag || drag.Divider != divider) return;
        preview = ChartPaneLayout.Resize(drag.Heights, order, drag.Boundary, y - drag.StartY, weights);
        ShowWeights(preview ?? weights);
    }

    private static Button ActionButton(string icon, string title, Action action)
    {
        var button = Terminal.IconButton(icon, title);
        button.Width = button.MinWidth = ButtonWidth;
        button.Height = ChartPaneLayout.HeaderHeight;
        button.MinHeight = 0;
        button.Padding = new Thickness(2, 0, 2, 0);
        button.Click += (_, _) => action();
        return button;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        plot.Measure(availableSize);
        addAlert.Measure(new(AlertButtonSize, AlertButtonSize));
        foreach (var pane in controls.Values)
        {
            pane.Divider.Measure(new(availableSize.Width, ChartPaneLayout.DividerHeight));
            pane.Buttons.Measure(new(ControlsWidth, ChartPaneLayout.HeaderHeight));
        }
        return plot.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        plot.Arrange(new Rect(finalSize));
        addAlert.Arrange(new Rect(0, 0, AlertButtonSize, AlertButtonSize));
        if (order.Length == 0) return finalSize;
        int index = 0;
        foreach (var bounds in CandlePlot.PaneDividers(finalSize.Width, plot.MeasurePanes(finalSize.Height)))
        {
            var pane = controls[order[index++]];
            pane.Divider.Arrange(bounds);
            pane.Buttons.Arrange(new Rect(Math.Max(0, finalSize.Width - ControlsWidth - 3), bounds.Bottom,
                ControlsWidth, ChartPaneLayout.HeaderHeight));
        }
        return finalSize;
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ThinMarketMonitor;

internal sealed class ChartSurface : Panel
{
    private const double ButtonWidth = 22;
    internal const double ControlsWidth = 3 * ButtonWidth;
    private readonly CandlePlot plot;
    private readonly Action<Venue, int> move;
    private readonly Action<Venue> close;
    private readonly Action<ChartPaneWeights?> save;
    private readonly Dictionary<Venue, PaneControls> controls = [];
    private string? marketKey;
    private Venue[] order = [];
    private ChartPaneWeights? weights, preview;
    private ResizeGesture? resize;

    private sealed record PaneControls(Thumb Divider, StackPanel Buttons, Button Up, Button Down);
    private sealed record ResizeGesture(Thumb Divider, int Boundary, double StartY, double[] Heights);

    internal ChartSurface(CandlePlot plot, Action<Venue, int> move, Action<Venue> close, Action<ChartPaneWeights?> save)
    {
        this.plot = plot;
        this.move = move;
        this.close = close;
        this.save = save;
        Children.Add(plot);
        plot.InterestPanesChanged += UpdatePanes;
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

    private void UpdatePanes(string? key, InterestLayer[] layers)
    {
        if (key == marketKey && order.Length == layers.Length &&
            !order.Where((venue, index) => venue != layers[index].Venue).Any()) return;
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
        InvalidateMeasure();
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
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(up);
        actions.Children.Add(down);
        actions.Children.Add(remove);
        Children.Add(divider);
        Children.Add(actions);
        return new(divider, actions, up, down);
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

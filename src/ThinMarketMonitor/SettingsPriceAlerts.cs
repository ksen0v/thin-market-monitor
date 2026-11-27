using System.Windows;
using System.Windows.Controls;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel
{
    private readonly StackPanel priceAlertRows = new();
    private Action<Guid, bool>? setPriceAlert;
    private Action<Guid>? removePriceAlert;
    private Action<PriceAlert>? openPriceAlert;

    private StackPanel PriceAlertOptions(Action<Guid, bool>? set, Action<Guid>? remove, Action<PriceAlert>? open)
    {
        setPriceAlert = set;
        removePriceAlert = remove;
        openPriceAlert = open;
        var panel = new StackPanel();
        panel.Children.Add(Terminal.Heading("Ценовые оповещения"));
        var separator = Terminal.Rule(new Thickness(0, 1, 0, 0));
        separator.Margin = new Thickness(0, 14, 0, 10);
        panel.Children.Add(separator);
        panel.Children.Add(priceAlertRows);
        panel.IsVisibleChanged += (_, _) => { if (panel.IsVisible) RefreshPriceAlertRows(); };
        RefreshPriceAlertRows();
        return panel;
    }

    internal void RefreshPriceAlertRows()
    {
        priceAlertRows.Children.Clear();
        if (settings.PriceAlerts.Length == 0)
        {
            priceAlertRows.Children.Add(Terminal.Copy("Нажмите «+» рядом с ценой указателя на графике."));
            return;
        }
        foreach (var alert in settings.PriceAlerts.OrderByDescending(alert => alert.TriggeredAt ?? alert.ArmedAt))
            priceAlertRows.Children.Add(AlertRow(alert));
    }

    private FrameworkElement AlertRow(PriceAlert alert)
    {
        var row = new DockPanel { Margin = new Thickness(5, 6, 3, 6) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (alert.TriggeredAt.HasValue && settings.ColibriEnabled && openPriceAlert != null)
        {
            var terminal = Terminal.IconButton("external", $"Открыть {alert.Ticker} в Colibri");
            terminal.Content = Terminal.AssetLogo("colibri", 16);
            terminal.Click += (_, _) => openPriceAlert(alert);
            actions.Children.Add(terminal);
        }
        var toggle = Terminal.IconButton(alert.Active ? "pause" : "play",
            $"{(alert.Active ? "Приостановить" : "Включить")} · {alert.Ticker}");
        toggle.IsEnabled = setPriceAlert != null;
        toggle.Click += (_, _) => setPriceAlert?.Invoke(alert.Id, !alert.Active);
        actions.Children.Add(toggle);
        var remove = Terminal.IconButton("close", $"Удалить уровень · {alert.Ticker}");
        remove.IsEnabled = removePriceAlert != null;
        remove.Click += (_, _) => removePriceAlert?.Invoke(alert.Id);
        actions.Children.Add(remove);
        DockPanel.SetDock(actions, Dock.Right);
        row.Children.Add(actions);

        var info = new StackPanel();
        var heading = Terminal.Copy($"{alert.Ticker} · {PriceAlert.Format(alert.Price)} {alert.Quote}", muted: false);
        heading.FontWeight = FontWeights.SemiBold;
        info.Children.Add(heading);
        var state = Terminal.Copy(alert.ReachedPrice is { } reached
            ? $"{alert.Source.Label} · сработало по {PriceAlert.Format(reached)} {alert.Quote}"
            : $"{alert.Source.Label} · {(alert.Active ? "Активно" : "На паузе")}");
        if (alert.TriggeredAt.HasValue) state.Foreground = Terminal.Brush("Accent");
        info.Children.Add(state);
        row.Children.Add(info);
        var border = Terminal.Rule(new Thickness(0, 0, 0, 1));
        border.Child = row;
        return border;
    }
}

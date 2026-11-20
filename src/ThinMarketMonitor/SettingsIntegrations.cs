using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ThinMarketMonitor;

internal sealed partial class SettingsPanel
{
    private StackPanel Integrations()
    {
        var panel = new StackPanel();
        var terminal = new StackPanel { Orientation = Orientation.Horizontal };
        terminal.Children.Add(Terminal.AssetLogo("colibri", 24));
        var heading = Terminal.Heading("Colibri");
        heading.Margin = new Thickness(8, 0, 0, 0);
        terminal.Children.Add(heading);
        panel.Children.Add(terminal);
        var enabled = new CheckBox { Content = "Включить интеграцию", IsChecked = settings.ColibriEnabled,
            Margin = new Thickness(0, 5, 0, 12) };
        enabled.Click += (_, _) =>
        {
            settings.ColibriEnabled = enabled.IsChecked == true;
            changed(false);
        };
        panel.Children.Add(enabled);
        panel.Children.Add(Terminal.Copy("В Colibri включите «Настройки → Программа → Local API». Скопируйте постоянный ID нужного стакана кнопкой ⧉ на его панели."));
        panel.Children.Add(Terminal.Copy("ID стакана", false));
        var slot = new TextBox { Text = settings.ColibriSlot, Margin = new Thickness(0, 5, 0, 8), MaxLength = 64 };
        AutomationProperties.SetName(slot, "Постоянный ID стакана Colibri");
        slot.TextChanged += (_, _) =>
        {
            string value = slot.Text.Trim();
            settings.ColibriSlot = Guid.TryParse(value, out var id) ? id.ToString("N") : value;
            changed(false);
        };
        panel.Children.Add(slot);
        var verify = new Button { Content = "Проверить подключение", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8) };
        verify.Click += async (_, _) =>
        {
            verify.IsEnabled = false;
            try { await colibri.Check(stop.Token); }
            finally { verify.IsEnabled = true; }
        };
        panel.Children.Add(verify);
        colibriStatus.Margin = new Thickness(0, 0, 0, 12);
        panel.Children.Add(colibriStatus);
        colibri.StatusChanged += ShowColibriStatus;
        ShowColibriStatus();
        panel.Children.Add(Terminal.Copy("В «Сигналах» нажмите «Открыть в Colibri» в заголовке графика. В «Обзоре» включите переключатель Colibri над таблицей для синхронизации при выборе инструмента."));
        var separator = Terminal.Rule(new Thickness(0, 1, 0, 0));
        separator.Margin = new Thickness(0, 15, 0, 12);
        panel.Children.Add(separator);
        foreach (string name in new[] { "Vataga", "Tiger" })
            panel.Children.Add(new CheckBox { Content = $"{name} · пока недоступно", IsEnabled = false,
                Margin = new Thickness(0, 5, 0, 5) });
        return panel;
    }

    private void ShowColibriStatus()
    {
        colibriStatus.Text = colibri.Status;
        colibriStatus.Foreground = Terminal.Brush(colibri.HasError ? "Falling" : "Muted");
    }
}
